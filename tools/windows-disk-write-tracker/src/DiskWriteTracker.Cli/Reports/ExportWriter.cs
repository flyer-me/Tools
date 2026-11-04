using System.Globalization;
using DiskWriteTracker.Core.Export;
using DiskWriteTracker.Core.Store;

namespace DiskWriteTracker.Cli.Reports;

internal sealed record ExportOutcome(string DailyFile, string SamplesFile, int DailyRows, int SampleRows);

/// <summary>
/// CSV 导出。固定输出 daily.csv 与 samples.csv 两份，命令行与菜单共用。
/// </summary>
internal static class ExportWriter
{
    private static readonly string[] DailyHeader =
    [
        "日期", "磁盘标识", "型号", "序列号", "总线",
        "日增量(bytes)", "日增量", "起始累计(bytes)", "结束累计(bytes)", "方法", "数据质量", "备注"
    ];

    private static readonly string[] SampleHeader =
    [
        "采样时间(本地)", "自然日", "磁盘标识", "型号", "序列号", "来源",
        "原始值", "单位", "换算系数", "累计(bytes)", "累计", "数据质量", "备注"
    ];

    public static ExportOutcome WriteCsv(
        IReadOnlyList<SampleRecord> samples, int days, string directory, bool allSamples = false)
    {
        Directory.CreateDirectory(directory);

        var (stats, _) = ViewBuilder.Build(samples, days);
        string from = ViewBuilder.WindowStart(days);

        var rows = new List<string[]> { DailyHeader };

        foreach (var s in stats.OrderBy(x => x.LocalDate, StringComparer.Ordinal).ThenBy(x => x.DiskKey, StringComparer.Ordinal))
        {
            rows.Add([
                s.LocalDate,
                s.DiskKey,
                s.Model,
                s.SerialNumber,
                s.BusType,
                s.DeltaBytes.ToString(CultureInfo.InvariantCulture),
                CsvExporter.FormatBytes(s.DeltaBytes),
                s.StartBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                s.EndBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                s.Method,
                s.Quality,
                s.Note ?? string.Empty
            ]);
        }

        string dailyFile = Path.Combine(directory, "daily.csv");
        CsvExporter.WriteFile(dailyFile, rows);

        var sampleRows = new List<string[]> { SampleHeader };

        foreach (var s in samples.OrderBy(x => x.CapturedUtc, StringComparer.Ordinal))
        {
            // 窗口读取会带回窗口前的前驱样本（算首日增量用），明细里不该出现
            if (!allSamples && string.CompareOrdinal(s.LocalDate, from) < 0)
                continue;

            sampleRows.Add([
                s.CapturedLocal,
                s.LocalDate,
                s.DiskKey,
                s.Model,
                s.SerialNumber,
                s.Source,
                s.RawValue?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                s.RawUnit ?? string.Empty,
                s.UnitScale.ToString(CultureInfo.InvariantCulture),
                s.Bytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                CsvExporter.FormatBytes(s.Bytes),
                s.Quality,
                s.Note ?? string.Empty
            ]);
        }

        string samplesFile = Path.Combine(directory, "samples.csv");
        CsvExporter.WriteFile(samplesFile, sampleRows);

        return new ExportOutcome(dailyFile, samplesFile, rows.Count - 1, sampleRows.Count - 1);
    }
}
