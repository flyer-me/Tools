using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using DiskWriteTracker.Core.Analysis;

namespace DiskWriteTracker.Core.Export;

/// <summary>
/// 报表数据契约。序列化后内嵌在 HTML 的 &lt;script id="dwt-data"&gt; 里，由 report.js 读取并渲染。
/// 键名刻意缩短以控制体积（rows 是体积主体，1 条约 150 字节）。
/// 短键映射：d=日期 k=磁盘标识 s=起始累计 e=结束累计 x=日增量 m=方法 q=数据质量 g=缺失天数 n=样本数
/// </summary>
public sealed class ReportData
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("generatedAt")] public string GeneratedAt { get; init; } = string.Empty;
    [JsonPropertyName("generatedAtUtc")] public string GeneratedAtUtc { get; init; } = string.Empty;
    [JsonPropertyName("engineVersion")] public string EngineVersion { get; init; } = string.Empty;
    [JsonPropertyName("dataDirectory")] public string DataDirectory { get; init; } = string.Empty;
    [JsonPropertyName("today")] public string Today { get; init; } = string.Empty;
    [JsonPropertyName("yesterday")] public string Yesterday { get; init; } = string.Empty;
    [JsonPropertyName("range")] public ReportRange Range { get; init; } = new();
    [JsonPropertyName("dict")] public ReportDict Dict { get; init; } = new();
    [JsonPropertyName("cards")] public ReportCards Cards { get; init; } = new();
    [JsonPropertyName("disks")] public List<ReportDisk> Disks { get; init; } = [];
    [JsonPropertyName("rows")] public List<ReportRow> Rows { get; init; } = [];
    [JsonPropertyName("dataQuality")] public ReportQuality DataQuality { get; init; } = new();
    [JsonPropertyName("config")] public ReportConfigView Config { get; init; } = new();

    /// <summary>从日统计与磁盘汇总构造报表数据。所有数值都来自已有聚合结果，此处不做二次计算。</summary>
    public static ReportData From(
        IReadOnlyList<DailyStat> stats,
        IReadOnlyList<DiskDailySummary> summaries,
        string dataDirectory,
        string rangeLabel,
        int rangeDays,
        DateTimeOffset generatedAt,
        ReportConfigView? config = null)
    {
        string today = generatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string yesterday = generatedAt.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string sevenAgo = generatedAt.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // 柱/线/表格共用的行集合：按日期升序、同日内按磁盘标识排序，前端再按需重排
        var rows = stats
            .OrderBy(s => s.LocalDate, StringComparer.Ordinal)
            .ThenBy(s => s.Model, StringComparer.Ordinal)
            .Select(s => new ReportRow
            {
                D = s.LocalDate,
                K = s.DiskKey,
                S = s.StartBytes,
                E = s.EndBytes,
                X = s.DeltaBytes,
                M = s.Method,
                Q = s.Quality,
                G = s.GapDays,
                N = s.SampleCount,
                Note = s.Note ?? string.Empty
            })
            .ToList();

        // 磁盘清单：颜色按出现顺序固定，使同一台机器多次生成的配色一致
        string[] palette = ["#2f6fed", "#e8710a", "#1a9e6f", "#c0341d", "#7b4bd3", "#0f8fa8"];
        var disks = summaries
            .OrderBy(s => s.Model, StringComparer.Ordinal)
            .Select((s, i) => new ReportDisk
            {
                Key = s.DiskKey,
                Model = s.Model,
                Serial = s.SerialNumber,
                Bus = s.BusType,
                Color = palette[i % palette.Length],
                CumulativeBytes = s.CumulativeBytes,
                TodayBytes = s.TodayBytes,
                YesterdayBytes = s.YesterdayBytes,
                Last7Bytes = s.Last7DaysBytes,
                DaysWithSamples = s.DaysWithSamples,
                AnomalyDays = s.AnomalyDays,
                Quality = s.Quality,
                PercentageUsed = s.PercentageUsed,
                TemperatureC = s.TemperatureC,
                PowerOnHours = s.PowerOnHours
            })
            .ToList();

        long todayBytes = rows.Where(r => r.D == today).Sum(r => r.X);
        long yesterdayBytes = rows.Where(r => r.D == yesterday).Sum(r => r.X);
        long last7Bytes = rows.Where(r => string.CompareOrdinal(r.D, sevenAgo) >= 0
                                          && string.CompareOrdinal(r.D, today) <= 0).Sum(r => r.X);
        long cumulative = disks.Sum(d => d.CumulativeBytes ?? 0);

        int anomalyDisks = disks.Count(d =>
            d.AnomalyDays > 0 || d.Quality is "anomaly" or "missing");

        var dates = rows.Select(r => r.D).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
        string from = dates.Count > 0
            ? dates[0]
            : generatedAt.AddDays(-(rangeDays - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string to = dates.Count > 0 ? dates[^1] : today;

        return new ReportData
        {
            GeneratedAt = generatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            GeneratedAtUtc = generatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            EngineVersion = typeof(ReportData).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(ReportData).Assembly.GetName().Version?.ToString()
                ?? "0.0.0",
            DataDirectory = dataDirectory,
            Today = today,
            Yesterday = yesterday,
            Range = new ReportRange { Days = rangeDays, From = from, To = to, Label = rangeLabel },
            Cards = new ReportCards
            {
                TodayBytes = todayBytes,
                YesterdayBytes = yesterdayBytes,
                Last7Bytes = last7Bytes,
                CumulativeBytes = cumulative,
                AnomalyDisks = anomalyDisks
            },
            Disks = disks,
            Rows = rows,
            DataQuality = BuildQuality(rows, dates),
            Config = config ?? new ReportConfigView()
        };
    }

    private static ReportQuality BuildQuality(List<ReportRow> rows, List<string> dates)
    {
        var notes = new List<string>();

        int anomalyDays = rows.Count(r => r.Q == "anomaly" && r.M != "source_switch");
        int missingDays = rows.Count(r => r.Q == "missing");
        int sourceSwitches = rows.Count(r => r.M == "source_switch");
        int gapFilled = rows.Count(r => r.M == "gap_filled");
        int clamped = rows.Count(r => r.M == "clamped");

        if (gapFilled > 0)
            notes.Add($"有 {gapFilled} 条跨期累计：中间缺日，增量是两次有效读数之差，不是严格单日值。");
        if (clamped > 0)
            notes.Add($"有 {clamped} 条计数器回退：累计值变小，已按 0 处理并在备注中标明原因。");
        if (sourceSwitches > 0)
            notes.Add($"有 {sourceSwitches} 条来源切换：读取通道变化，当日不做差值以免误判。");
        if (missingDays > 0)
            notes.Add($"有 {missingDays} 条缺失：当日无有效读数，表格保留但增量为 0。");

        if (notes.Count == 0 && rows.Count > 0)
            notes.Add("区间内所有日增量的数据质量均为可靠，未发现回退、缺日或来源切换。");

        return new ReportQuality
        {
            SampleCount = rows.Sum(r => r.N),
            DayCount = dates.Count,
            FirstDate = dates.Count > 0 ? dates[0] : string.Empty,
            LastDate = dates.Count > 0 ? dates[^1] : string.Empty,
            MissingDays = missingDays,
            AnomalyDays = anomalyDays,
            SourceSwitches = sourceSwitches,
            GapFilled = gapFilled,
            Clamped = clamped,
            Notes = notes
        };
    }
}

public sealed class ReportRange
{
    [JsonPropertyName("days")] public int Days { get; init; }
    [JsonPropertyName("from")] public string From { get; init; } = string.Empty;
    [JsonPropertyName("to")] public string To { get; init; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
}

public sealed class ReportDict
{
    [JsonPropertyName("quality")] public Dictionary<string, string> Quality { get; init; } = new()
    {
        ["reliable"] = "可靠",
        ["fallback"] = "回退",
        ["low_confidence"] = "低可信",
        ["anomaly"] = "异常",
        ["missing"] = "缺失"
    };

    [JsonPropertyName("method")] public Dictionary<string, string> Method { get; init; } = new()
    {
        ["baseline"] = "建立基线",
        ["counter_diff"] = "相邻日差值",
        ["source_switch"] = "来源切换",
        ["gap_filled"] = "跨期累计",
        ["clamped"] = "回退钳制"
    };
}

public sealed class ReportCards
{
    [JsonPropertyName("todayBytes")] public long TodayBytes { get; init; }
    [JsonPropertyName("yesterdayBytes")] public long YesterdayBytes { get; init; }
    [JsonPropertyName("last7Bytes")] public long Last7Bytes { get; init; }
    [JsonPropertyName("cumulativeBytes")] public long CumulativeBytes { get; init; }
    [JsonPropertyName("anomalyDisks")] public int AnomalyDisks { get; init; }
}

public sealed class ReportDisk
{
    [JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
    [JsonPropertyName("model")] public string Model { get; init; } = string.Empty;
    [JsonPropertyName("serial")] public string Serial { get; init; } = string.Empty;
    [JsonPropertyName("bus")] public string Bus { get; init; } = string.Empty;
    [JsonPropertyName("color")] public string Color { get; init; } = "#2f6fed";
    [JsonPropertyName("cumulativeBytes")] public long? CumulativeBytes { get; init; }
    [JsonPropertyName("todayBytes")] public long TodayBytes { get; init; }
    [JsonPropertyName("yesterdayBytes")] public long YesterdayBytes { get; init; }
    [JsonPropertyName("last7Bytes")] public long Last7Bytes { get; init; }
    [JsonPropertyName("daysWithSamples")] public int DaysWithSamples { get; init; }
    [JsonPropertyName("anomalyDays")] public int AnomalyDays { get; init; }
    [JsonPropertyName("quality")] public string Quality { get; init; } = "missing";
    [JsonPropertyName("percentageUsed")] public int? PercentageUsed { get; init; }
    [JsonPropertyName("temperatureC")] public double? TemperatureC { get; init; }
    [JsonPropertyName("powerOnHours")] public long? PowerOnHours { get; init; }
}

public sealed class ReportRow
{
    [JsonPropertyName("d")] public string D { get; init; } = string.Empty;
    [JsonPropertyName("k")] public string K { get; init; } = string.Empty;
    [JsonPropertyName("s")] public long? S { get; init; }
    [JsonPropertyName("e")] public long? E { get; init; }
    [JsonPropertyName("x")] public long X { get; init; }
    [JsonPropertyName("m")] public string M { get; init; } = string.Empty;
    [JsonPropertyName("q")] public string Q { get; init; } = string.Empty;
    [JsonPropertyName("g")] public int G { get; init; }
    [JsonPropertyName("n")] public int N { get; init; }
    [JsonPropertyName("note")] public string Note { get; init; } = string.Empty;
}

public sealed class ReportQuality
{
    [JsonPropertyName("sampleCount")] public int SampleCount { get; init; }
    [JsonPropertyName("dayCount")] public int DayCount { get; init; }
    [JsonPropertyName("firstDate")] public string FirstDate { get; init; } = string.Empty;
    [JsonPropertyName("lastDate")] public string LastDate { get; init; } = string.Empty;
    [JsonPropertyName("missingDays")] public int MissingDays { get; init; }
    [JsonPropertyName("anomalyDays")] public int AnomalyDays { get; init; }
    [JsonPropertyName("sourceSwitches")] public int SourceSwitches { get; init; }
    [JsonPropertyName("gapFilled")] public int GapFilled { get; init; }
    [JsonPropertyName("clamped")] public int Clamped { get; init; }
    [JsonPropertyName("notes")] public List<string> Notes { get; init; } = [];
}

/// <summary>报表页脚展示的运行信息。只读快照，修改请用启动器菜单。</summary>
public sealed class ReportConfigView
{
    [JsonPropertyName("dailyTime")] public string DailyTime { get; init; } = "00:05";
    [JsonPropertyName("retentionDays")] public int RetentionDays { get; init; } = 3650;
    [JsonPropertyName("timeoutSeconds")] public int TimeoutSeconds { get; init; } = 30;
    [JsonPropertyName("taskName")] public string TaskName { get; init; } = "DiskWriteTracker_DailySample";
    [JsonPropertyName("logLevel")] public string LogLevel { get; init; } = "INFO";
    [JsonPropertyName("portable")] public bool Portable { get; init; }
}
