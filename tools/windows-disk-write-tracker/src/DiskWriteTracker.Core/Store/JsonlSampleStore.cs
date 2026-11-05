using System.Text;

namespace DiskWriteTracker.Core.Store;

/// <summary>
/// 零依赖存储：按日追加 JSONL。
/// 单文件按本地日期切分，便于人工排查与按日清理。
/// </summary>
public sealed class JsonlSampleStore
{
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

        // 跨进程写锁：计划任务与手动采样可能同时启动，无锁追加会写出交错行。
        // 持锁后一次性写完整块，中途不检查取消。
        using var writeLock = FileLock.Acquire(
            Path.Combine(_directory, ".write.lock"), FileLock.DefaultTimeout);

        foreach (var group in byDate)
        {
            if (!DateOnly.TryParseExact(group.Key, "yyyy-MM-dd", out DateOnly date))
                continue;

            string path = FilePathFor(date);
            var sb = new StringBuilder();

            foreach (var record in group)
            {
                // 极简 JSON 手写：字段固定、无嵌套，避免引入序列化开销
                sb.Append('{');
                AppendString(sb, "runId", record.RunId); sb.Append(',');
                AppendString(sb, "capturedUtc", record.CapturedUtc); sb.Append(',');
                AppendString(sb, "capturedLocal", record.CapturedLocal); sb.Append(',');
                AppendString(sb, "localDate", record.LocalDate); sb.Append(',');
                sb.Append("\"tzOffsetMinutes\":").Append(record.TzOffsetMinutes).Append(',');
                AppendString(sb, "diskKey", record.DiskKey); sb.Append(',');
                sb.Append("\"driveNumber\":").Append(record.DriveNumber).Append(',');
                AppendString(sb, "model", record.Model); sb.Append(',');
                AppendString(sb, "serialNumber", record.SerialNumber); sb.Append(',');
                AppendString(sb, "firmware", record.Firmware); sb.Append(',');
                AppendString(sb, "busType", record.BusType); sb.Append(',');
                AppendString(sb, "source", record.Source); sb.Append(',');
                AppendNullableString(sb, "attributeRef", record.AttributeRef); sb.Append(',');
                AppendNullableNumber(sb, "rawValue", record.RawValue); sb.Append(',');
                AppendNullableString(sb, "rawUnit", record.RawUnit); sb.Append(',');
                sb.Append("\"unitScale\":").Append(record.UnitScale).Append(',');
                AppendNullableNumber(sb, "bytes", record.Bytes); sb.Append(',');
                AppendString(sb, "quality", record.Quality); sb.Append(',');
                AppendNullableString(sb, "note", record.Note); sb.Append(',');
                AppendNullableDouble(sb, "temperatureC", record.TemperatureC); sb.Append(',');
                AppendNullableNumber(sb, "powerOnHours", record.PowerOnHours); sb.Append(',');
                AppendNullableNumber(sb, "powerCycles", record.PowerCycles); sb.Append(',');
                AppendNullableNumber(sb, "percentageUsed", record.PercentageUsed);
                sb.Append('}').Append('\n');
            }

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
    ///
    /// 为什么必须有前驱：日增量 = 当天最后一条 − 前一天最后一条。窗口第一天如果只读到窗口内的样本，
    /// 它就退化成"基线日"，那一天的写入量凭空消失。多读一天是错的做法 —— 第 1 天要看的是
    /// "严格早于窗口起点的最后一条"，不是"窗口起点前一天的最后一条"：中间缺了三天时，
    /// 多了那三天，前一天根本没有记录。
    ///
    /// 往回最多扫 <see cref="MaxPredecessorScanDays"/> 天。一块盘常年不在线时，
    /// 再多扫也只是把开文件数刷上去，拿不到东西；超过就放弃，返回的仍是窗口内那部分。
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
            // 窗口内一条都没有，说明链路本身断了 —— 这时补前驱只会让人以为"有数据"
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

            // 每块盘只要这个文件里最后一条有值的：往前扫时第一个碰到有值的日子就是最近的前驱
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

    /// <summary>只数非空行，不解析 JSON。doctor 与菜单统计要的是"有多少条"，不是"内容是什么"。</summary>
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
        // 必须用共享模式打开：写者持 FileShare.Read 追加时，默认的独占读会让两边互锁
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

    private static SampleRecord? TryParse(string line)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var root = doc.RootElement;

            return new SampleRecord
            {
                RunId = GetString(root, "runId") ?? string.Empty,
                CapturedUtc = GetString(root, "capturedUtc") ?? string.Empty,
                CapturedLocal = GetString(root, "capturedLocal") ?? string.Empty,
                LocalDate = GetString(root, "localDate") ?? string.Empty,
                TzOffsetMinutes = root.GetProperty("tzOffsetMinutes").GetInt32(),
                DiskKey = GetString(root, "diskKey") ?? string.Empty,
                DriveNumber = root.GetProperty("driveNumber").GetInt32(),
                Model = GetString(root, "model") ?? string.Empty,
                SerialNumber = GetString(root, "serialNumber") ?? string.Empty,
                Firmware = GetString(root, "firmware") ?? string.Empty,
                BusType = GetString(root, "busType") ?? string.Empty,
                Source = GetString(root, "source") ?? string.Empty,
                AttributeRef = GetString(root, "attributeRef"),
                RawValue = GetLong(root, "rawValue"),
                RawUnit = GetString(root, "rawUnit"),
                UnitScale = root.GetProperty("unitScale").GetInt64(),
                Bytes = GetLong(root, "bytes"),
                Quality = GetString(root, "quality") ?? "missing",
                Note = GetString(root, "note"),
                TemperatureC = GetDouble(root, "temperatureC"),
                PowerOnHours = GetLong(root, "powerOnHours"),
                PowerCycles = GetLong(root, "powerCycles"),
                PercentageUsed = (int?)GetLong(root, "percentageUsed")
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private static string? GetString(System.Text.Json.JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

    private static long? GetLong(System.Text.Json.JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetInt64() : null;

    private static double? GetDouble(System.Text.Json.JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDouble() : null;

    private static void AppendString(StringBuilder sb, string name, string value)
        => sb.Append('"').Append(name).Append("\":\"").Append(Escape(value)).Append('"');

    private static void AppendNullableString(StringBuilder sb, string name, string? value)
    {
        sb.Append('"').Append(name).Append("\":");
        sb.Append(value is null ? "null" : "\"" + Escape(value) + "\"");
    }

    private static void AppendNullableNumber(StringBuilder sb, string name, long? value)
        => sb.Append('"').Append(name).Append("\":").Append(value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null");

    private static void AppendNullableDouble(StringBuilder sb, string name, double? value)
        => sb.Append('"').Append(name).Append("\":").Append(value?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "null");

    private static string Escape(string value)
    {
        bool needsEscape = false;
        foreach (char c in value)
        {
            if (c is '"' or '\\' or '\n' or '\r' or '\t')
            {
                needsEscape = true;
                break;
            }
        }

        if (!needsEscape)
            return value;

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }
}
