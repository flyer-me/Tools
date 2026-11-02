using DiskWriteTracker.Core.Store;

namespace DiskWriteTracker.Core.Analysis;

/// <summary>某盘某自然日的写入增量。</summary>
public sealed record DailyStat
{
    public required string LocalDate { get; init; }
    public required string DiskKey { get; init; }
    public required string Model { get; init; }
    public required string SerialNumber { get; init; }
    public required string BusType { get; init; }

    public long? StartBytes { get; init; }
    public long? EndBytes { get; init; }
    public required long DeltaBytes { get; init; }

    /// <summary>baseline / counter_diff / source_switch / gap_filled / clamped</summary>
    public required string Method { get; init; }

    public required string Quality { get; init; }
    public int GapDays { get; init; }
    public int SampleCount { get; init; }
    public string? Note { get; init; }
}

/// <summary>单盘跨多日的汇总视图。</summary>
public sealed record DiskDailySummary
{
    public required string DiskKey { get; init; }
    public required string Model { get; init; }
    public required string SerialNumber { get; init; }
    public required string BusType { get; init; }
    public long? CumulativeBytes { get; init; }
    public long TodayBytes { get; init; }
    public long YesterdayBytes { get; init; }
    public long Last7DaysBytes { get; init; }
    public int DaysWithSamples { get; init; }
    public int AnomalyDays { get; init; }
    public required string Quality { get; init; }
    public int? PercentageUsed { get; init; }
    public double? TemperatureC { get; init; }
    public long? PowerOnHours { get; init; }
}

public static class DailyAggregator
{
    private static readonly string[] QualityRank = ["reliable", "fallback", "low_confidence", "anomaly", "missing"];

    public static string Worse(string a, string b) => Rank(a) >= Rank(b) ? a : b;

    private static int Rank(string quality)
    {
        int i = Array.IndexOf(QualityRank, quality);
        return i < 0 ? QualityRank.Length - 1 : i;
    }

    /// <summary>
    /// 每盘每日一条。首日只建基线；跨源不相减；计数器回退不出负数。
    /// </summary>
    public static IReadOnlyList<DailyStat> Aggregate(IReadOnlyList<SampleRecord> samples)
    {
        var result = new List<DailyStat>();

        foreach (var diskGroup in samples.GroupBy(s => s.DiskKey).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var ordered = diskGroup.OrderBy(s => s.CapturedUtc, StringComparer.Ordinal).ToList();
            var dates = ordered.Select(s => s.LocalDate).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();

            foreach (string date in dates)
            {
                var daySamples = ordered.Where(s => s.LocalDate == date).ToList();

                // 当日最后一条有效样本（有字节值）
                var current = daySamples.LastOrDefault(s => s.Bytes.HasValue);

                if (current is null)
                {
                    result.Add(new DailyStat
                    {
                        LocalDate = date,
                        DiskKey = diskGroup.Key,
                        Model = daySamples[^1].Model,
                        SerialNumber = daySamples[^1].SerialNumber,
                        BusType = daySamples[^1].BusType,
                        DeltaBytes = 0,
                        Method = "gap_filled",
                        Quality = "missing",
                        SampleCount = daySamples.Count,
                        Note = "当日无有效读数"
                    });
                    continue;
                }

                // 前一自然日的最后一条有效样本（严格早于当日的日期，不是当日更早的那条）
                // 若取"当日上一条"，日增量就只覆盖最后一个采样区间，会明显偏小。
                var previous = ordered
                    .Where(s => s.Bytes.HasValue && string.CompareOrdinal(s.LocalDate, date) < 0)
                    .LastOrDefault();

                if (previous is null)
                {
                    result.Add(new DailyStat
                    {
                        LocalDate = date,
                        DiskKey = diskGroup.Key,
                        Model = current.Model,
                        SerialNumber = current.SerialNumber,
                        BusType = current.BusType,
                        StartBytes = null,
                        EndBytes = current.Bytes,
                        DeltaBytes = 0,
                        Method = "baseline",
                        Quality = current.Quality,
                        SampleCount = daySamples.Count,
                        Note = "首次采样，仅建立基线"
                    });
                    continue;
                }

                int gapDays = DaysBetween(previous.LocalDate, date) - 1;
                var notes = new List<string>();

                if (!string.Equals(previous.Source, current.Source, StringComparison.Ordinal))
                {
                    result.Add(new DailyStat
                    {
                        LocalDate = date,
                        DiskKey = diskGroup.Key,
                        Model = current.Model,
                        SerialNumber = current.SerialNumber,
                        BusType = current.BusType,
                        StartBytes = previous.Bytes,
                        EndBytes = current.Bytes,
                        DeltaBytes = 0,
                        Method = "source_switch",
                        Quality = "low_confidence",
                        GapDays = Math.Max(0, gapDays),
                        SampleCount = daySamples.Count,
                        Note = $"数据来源切换（{previous.Source} → {current.Source}），不做跨源相减"
                    });
                    continue;
                }

                long rawDelta = current.Bytes!.Value - previous.Bytes!.Value;
                long delta;
                string method;
                string quality;

                if (rawDelta < 0)
                {
                    delta = 0;
                    method = "clamped";
                    quality = "anomaly";
                    notes.Add("计数器回退，可能换盘/固件更新/计数重置");
                }
                else
                {
                    delta = rawDelta;
                    method = gapDays > 0 ? "gap_filled" : "counter_diff";
                    quality = Worse(previous.Quality, current.Quality);

                    if (gapDays > 0)
                    {
                        quality = Worse(quality, "low_confidence");
                        notes.Add($"中间缺失 {gapDays} 天，增量为跨期累计");
                    }
                }

                result.Add(new DailyStat
                {
                    LocalDate = date,
                    DiskKey = diskGroup.Key,
                    Model = current.Model,
                    SerialNumber = current.SerialNumber,
                    BusType = current.BusType,
                    StartBytes = previous.Bytes,
                    EndBytes = current.Bytes,
                    DeltaBytes = delta,
                    Method = method,
                    Quality = quality,
                    GapDays = Math.Max(0, gapDays),
                    SampleCount = daySamples.Count,
                    Note = notes.Count == 0 ? null : string.Join("；", notes)
                });
            }
        }

        return result;
    }

    /// <summary>每盘一行汇总，供报表顶部卡片使用。</summary>
    public static IReadOnlyList<DiskDailySummary> Summarize(
        IReadOnlyList<SampleRecord> samples,
        IReadOnlyList<DailyStat> stats,
        DateOnly today)
    {
        var result = new List<DiskDailySummary>();
        string todayKey = today.ToString("yyyy-MM-dd");
        string yesterdayKey = today.AddDays(-1).ToString("yyyy-MM-dd");
        var last7 = Enumerable.Range(0, 7).Select(i => today.AddDays(-i).ToString("yyyy-MM-dd")).ToHashSet(StringComparer.Ordinal);

        foreach (var group in stats.GroupBy(s => s.DiskKey).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var forDisk = group.OrderBy(s => s.LocalDate, StringComparer.Ordinal).ToList();
            var latestSample = samples
                .Where(s => s.DiskKey == group.Key && s.Bytes.HasValue)
                .OrderBy(s => s.CapturedUtc, StringComparer.Ordinal)
                .LastOrDefault();

            result.Add(new DiskDailySummary
            {
                DiskKey = group.Key,
                Model = forDisk[^1].Model,
                SerialNumber = forDisk[^1].SerialNumber,
                BusType = forDisk[^1].BusType,
                CumulativeBytes = latestSample?.Bytes,
                TodayBytes = forDisk.Where(s => s.LocalDate == todayKey).Sum(s => s.DeltaBytes),
                YesterdayBytes = forDisk.Where(s => s.LocalDate == yesterdayKey).Sum(s => s.DeltaBytes),
                Last7DaysBytes = forDisk.Where(s => last7.Contains(s.LocalDate)).Sum(s => s.DeltaBytes),
                DaysWithSamples = forDisk.Count(s => s.SampleCount > 0),
                AnomalyDays = forDisk.Count(s => s.Quality is "anomaly" or "missing"),
                Quality = forDisk[^1].Quality,
                PercentageUsed = latestSample?.PercentageUsed,
                TemperatureC = latestSample?.TemperatureC,
                PowerOnHours = latestSample?.PowerOnHours
            });
        }

        return result;
    }

    private static int DaysBetween(string fromDate, string toDate)
    {
        if (!DateOnly.TryParseExact(fromDate, "yyyy-MM-dd", out var from) ||
            !DateOnly.TryParseExact(toDate, "yyyy-MM-dd", out var to))
            return 0;

        return to.DayNumber - from.DayNumber;
    }
}
