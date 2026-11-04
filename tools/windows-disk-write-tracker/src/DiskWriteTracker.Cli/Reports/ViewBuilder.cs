using DiskWriteTracker.Core.Analysis;
using DiskWriteTracker.Core.Store;

namespace DiskWriteTracker.Cli.Reports;

/// <summary>
/// 把原始样本折成"某段日期内的日统计 + 每盘汇总"。
/// 命令行与菜单共用同一份口径，避免两处各写一套过滤条件。
/// </summary>
internal static class ViewBuilder
{
    /// <summary>
    /// 窗口起点（含）。日期口径只在这里定义一次：命令入口、日统计、导出若各算各的，
    /// 跨零点就会一个用昨天一个用今天，窗口边界错开一天。
    /// </summary>
    public static string WindowStart(int days)
        => DateOnly.FromDateTime(DateTime.Now).AddDays(-days + 1).ToString("yyyy-MM-dd");

    public static (IReadOnlyList<DailyStat> Stats, IReadOnlyList<DiskDailySummary> Summaries) Build(
        IReadOnlyList<SampleRecord> samples,
        int days)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        string from = WindowStart(days);

        var allStats = DailyAggregator.Aggregate(samples);
        var stats = allStats.Where(s => string.CompareOrdinal(s.LocalDate, from) >= 0).ToList();

        var relevantKeys = stats.Select(s => s.DiskKey).ToHashSet(StringComparer.Ordinal);
        var summaries = DailyAggregator.Summarize(samples, stats, today)
            .Where(s => relevantKeys.Contains(s.DiskKey))
            .ToList();

        return (stats, summaries);
    }
}
