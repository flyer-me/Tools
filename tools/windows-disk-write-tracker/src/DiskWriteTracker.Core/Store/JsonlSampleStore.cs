using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DiskWriteTracker.Core.Store;

/// <summary>
/// 零依赖存储：按日追加 JSONL，单文件按本地日期切分，便于人工排查与按日清理。
/// </summary>
public sealed class JsonlSampleStore
{
    /// <summary>中文与特殊字符原样写入，保持文件可直接人工阅读。</summary>
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = StoreJsonContext.Default,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _directory;

    public JsonlSampleStore(string dataDirectory)
    {
        _directory = dataDirectory;
    }

    public string Directory => _directory;

    public string FilePathFor(DateOnly date)
        => Path.Combine(_directory, $"samples-{date:yyyyMMdd}.jsonl");

    public int Append(IEnumerable<SampleRecord> records)
    {
        var byDate = records
            .GroupBy(r => r.LocalDate, StringComparer.Ordinal)
            .ToList();

        int written = 0;
        System.IO.Directory.CreateDirectory(_directory);

        if (byDate.Count == 0)
            return 0;

        // 计划任务与手动采样可能同时启动，无锁追加会写出交错行
        using var writeLock = FileLock.Acquire(
            Path.Combine(_directory, ".write.lock"), FileLock.DefaultTimeout);

        foreach (var group in byDate)
        {
            if (!DateOnly.TryParseExact(group.Key, "yyyy-MM-dd", out DateOnly date))
                continue;

            string path = FilePathFor(date);
            var sb = new StringBuilder();

            foreach (var record in group)
                sb.Append(JsonSerializer.Serialize(record, LineOptions)).Append('\n');

            var bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            written += group.Count();
        }

        return written;
    }

    /// <summary>读取全部历史样本（按时间升序）。数据量级为每日数条，可全量载入。</summary>
    public IReadOnlyList<SampleRecord> ReadAll()
    {
        var result = new List<SampleRecord>();
        foreach (var file in SampleFile.Enumerate(_directory))
            ReadInto(file.Path, result);

        SortByCapturedUtc(result);
        return result;
    }

    /// <summary>
    /// 只读窗口内的文件，外加每块盘在窗口起点之前最近的一条有用样本（前驱）。
    /// 前驱用于计算窗口第一天的增量；缺了它，首日会退化成基线日，增量按 0 计。
    /// 取"严格早于窗口起点的最后一条"：中间缺采样时起点前一天没有记录。
    ///
    /// 往回最多扫 <see cref="MaxPredecessorScanDays"/> 天：一块盘常年不在线时再多扫也只是
    /// 白开文件，超过就放弃，返回窗口内那部分。
    /// </summary>
    /// <param name="days">窗口天数。&lt;= 0 表示全量。</param>
    /// <param name="today">"今天"的日期，由调用方注入以便测试。</param>
    public IReadOnlyList<SampleRecord> ReadForWindow(int days, DateOnly today)
    {
        if (days <= 0)
            return ReadAll();

        var files = SampleFile.Enumerate(_directory);
        if (files.Count == 0)
            return [];

        DateOnly start = today.AddDays(-days + 1);
        var result = new List<SampleRecord>();

        foreach (var file in files)
        {
            if (file.Date >= start)
                ReadInto(file.Path, result);
        }

        if (result.Count == 0)
        {
            // 窗口内一条都没有，链路本身断了，补前驱只会制造"有数据"的假象
            return result;
        }

        var need = result
            .Where(r => r.Bytes.HasValue)
            .Select(r => r.DiskKey)
            .ToHashSet(StringComparer.Ordinal);

        int scanned = 0;
        foreach (var file in files.Where(f => f.Date < start).OrderByDescending(f => f.Date))
        {
            if (need.Count == 0 || scanned >= MaxPredecessorScanDays)
                break;

            scanned++;

            var probe = new List<SampleRecord>();
            ReadInto(file.Path, probe);

            // 每块盘只取该文件里最后一条有值的：往前扫时第一个碰到有值的日子就是最近的前驱
            foreach (var group in probe
                         .Where(r => r.Bytes.HasValue && need.Contains(r.DiskKey))
                         .GroupBy(r => r.DiskKey, StringComparer.Ordinal))
            {
                SampleRecord last = group.OrderByDescending(r => r.CapturedUtc, StringComparer.Ordinal).First();
                result.Add(last);
                need.Remove(last.DiskKey);
            }
        }

        SortByCapturedUtc(result);
        return result;
    }

    /// <summary>只数非空行，不解析 JSON。doctor 与菜单要的是"有多少条"，不是"内容是什么"。</summary>
    public int CountLines()
    {
        int count = 0;
        foreach (var file in SampleFile.Enumerate(_directory))
        {
            try
            {
                using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, new UTF8Encoding(false),
                    detectEncodingFromByteOrderMarks: true);

                while (reader.ReadLine() is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        count++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到就跳过，统计数字少一点比抛异常好
            }
        }

        return count;
    }

    /// <summary>往回补前驱最多扫这么多天。</summary>
    public const int MaxPredecessorScanDays = 90;

    private static void ReadInto(string path, List<SampleRecord> target)
    {
        // 共享模式打开：写者持 FileShare.Read 追加时，独占读会让两边互锁
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var record = TryParse(line);
            if (record is not null)
                target.Add(record);
        }
    }

    private static void SortByCapturedUtc(List<SampleRecord> records)
        => records.Sort((a, b) => string.CompareOrdinal(a.CapturedUtc, b.CapturedUtc));

    /// <summary>
    /// 解析一行。必填字段缺失或类型不符时返回 null，该行跳过；
    /// 旧版手写序列化产出的行字段名相同，可直接读取。
    /// </summary>
    private static SampleRecord? TryParse(string line)
    {
        try
        {
            var record = JsonSerializer.Deserialize(line, StoreJsonContext.Default.SampleRecord);
            if (record is null)
                return null;

            // 这三个字段是日增量计算的最小依赖，缺任何一个的行没有使用价值
            if (string.IsNullOrEmpty(record.CapturedUtc) ||
                string.IsNullOrEmpty(record.DiskKey) ||
                string.IsNullOrEmpty(record.LocalDate))
                return null;

            return record;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
