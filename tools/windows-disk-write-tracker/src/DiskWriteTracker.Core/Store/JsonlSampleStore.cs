using System.Text;

namespace DiskWriteTracker.Core.Store;

/// <summary>
/// M1 零依赖存储：按日追加 JSONL。M2 迁移到 SQLite 后，JSONL 保留为审计副本。
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
        // 持锁后一次性写完整块，中途不检查取消，保证不会留下半行。
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
        foreach (SampleFile file in SampleFile.Enumerate(_directory))
            ReadInto(file.Path, result);

        SortByCapturedUtc(result);
        return result;
    }

    /// <summary>
    /// 只读窗口内的文件，外加窗口之前的前驱样本。
    ///
    /// 前驱不能简单取"前一天"：窗口前一天该盘可能读取失败（bytes=null），真实前驱在更早。
    /// 少读一条，DailyAggregator 就会把窗口首日当成 baseline，一整天的写入量静默变成 0。
    /// 所以规则是：继续回读更早的文件，直到窗口里出现的每块盘都拿到至少一条有效窗口前样本。
    ///
    /// 代价上限是 ReadAll：某块盘若从无有效读数，会一直回读到最早的文件，
    /// 也就是退化成改造前的全量读取，不可能更慢。
    ///
    /// 返回结果**包含**回读进来的前驱行，调用方按日期过滤即可（日统计与报表本来就按窗口过滤）。
    /// </summary>
    public IReadOnlyList<SampleRecord> ReadForWindow(int days, DateOnly today)
    {
        if (days <= 0)
            return ReadAll();

        var result = new List<SampleRecord>();
        List<SampleFile> files = SampleFile.Enumerate(_directory);
        if (files.Count == 0)
            return result;

        DateOnly from = today.AddDays(-(days - 1));

        int start = 0;
        while (start < files.Count && files[start].Date < from)
            start++;

        // 窗口完全落在最新样本之后（改过系统时间，或只查最近一天而样本更早）：窗口内没有数据
        if (start >= files.Count)
            return result;

        for (int i = start; i < files.Count; i++)
            ReadInto(files[i].Path, result);

        var disks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in result)
            disks.Add(record.DiskKey);

        // 窗口内所有文件都晚于 from，因此前驱只能来自更早的文件；逐文件回读、边读边记已覆盖的盘。
        var covered = new HashSet<string>(StringComparer.Ordinal);
        for (int i = start - 1; i >= 0 && covered.Count < disks.Count; i--)
        {
            int before = result.Count;
            ReadInto(files[i].Path, result);

            for (int j = before; j < result.Count; j++)
            {
                if (result[j].Bytes.HasValue)
                    covered.Add(result[j].DiskKey);
            }
        }

        SortByCapturedUtc(result);
        return result;
    }

    /// <summary>只数非空行，不解析 JSON。doctor 与菜单只要一个数量。</summary>
    public int CountLines()
    {
        int count = 0;

        foreach (SampleFile file in SampleFile.Enumerate(_directory))
        {
            try
            {
                foreach (string line in File.ReadLines(file.Path))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        count++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件被占用就跳过：少几条不影响诊断，不值得让 doctor 失败
            }
        }

        return count;
    }

    private static void ReadInto(string path, List<SampleRecord> into)
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
                into.Add(record);
        }
    }

    private static void SortByCapturedUtc(List<SampleRecord> records)
        => records.Sort(static (a, b) => string.CompareOrdinal(a.CapturedUtc, b.CapturedUtc));

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
