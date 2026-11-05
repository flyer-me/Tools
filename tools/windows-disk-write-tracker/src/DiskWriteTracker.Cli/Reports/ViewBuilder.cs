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
    /// "最近 N 天"的起点。全仓库只此一处定义：这个口径一旦在两处各写一遍，
    /// 差一格（-N 还是 -N+1）会让报表与 CSV 相差整整一天。
    /// </summary>
    public static DateOnly WindowStart(int days, DateOnly today) => today.AddDays(-days + 1);

    public static string WindowStartText(int days, DateOnly today)
        => WindowStart(days, today).ToString("yyyy-MM-dd");

    public static (IReadOnlyList<DailyStat> Stats, IReadOnlyList<DiskDailySummary> Summaries) Build(
        IReadOnlyList<SampleRecord> samples,
        int days)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        string from = WindowStartText(days, today);

        var allStats = DailyAggregator.Aggregate(samples);
        var stats = allStats.Where(s => string.CompareOrdinal(s.LocalDate, from) >= 0).ToList();

        var relevantKeys = stats.Select(s => s.DiskKey).ToHashSet(StringComparer.Ordinal);
        var summaries = DailyAggregator.Summarize(samples, stats, today)
            .Where(s => relevantKeys.Contains(s.DiskKey))
            .ToList();

        return (stats, summaries);
    }
}
