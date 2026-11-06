using DiskWriteTracker.Core.Analysis;
using DiskWriteTracker.Core.Store;
using Xunit;

namespace DiskWriteTracker.Core.Tests;

public class DailyAggregatorTests
{
    private static List<DailyStat> Aggregate(params SampleRecord[] samples) =>
        DailyAggregator.Aggregate(samples).ToList();

    [Fact]
    public void 首日样本只建基线()
    {
        var stats = Aggregate(TestData.Sample("2026-01-01", 1000));

        var stat = Assert.Single(stats);
        Assert.Equal("baseline", stat.Method);
        Assert.Equal(0, stat.DeltaBytes);
        Assert.Null(stat.StartBytes);
        Assert.Equal(1000, stat.EndBytes);
    }

    [Fact]
    public void 相邻日差值为当日增量()
    {
        var stats = Aggregate(
            TestData.Sample("2026-01-01", 1000),
            TestData.Sample("2026-01-02", 3000));

        Assert.Equal(2, stats.Count);
        var day2 = stats.Single(s => s.LocalDate == "2026-01-02");
        Assert.Equal("counter_diff", day2.Method);
        Assert.Equal(2000, day2.DeltaBytes);
        Assert.Equal(1000, day2.StartBytes);
        Assert.Equal(3000, day2.EndBytes);
    }

    [Fact]
    public void 同日多条样本取最后一条有效值()
    {
        var stats = Aggregate(
            TestData.Sample("2026-01-01", 1000, capturedUtc: "2026-01-01T01:00:00Z"),
            TestData.Sample("2026-01-01", 2500, capturedUtc: "2026-01-01T23:00:00Z"),
            TestData.Sample("2026-01-02", 3000));

        var day1 = stats.Single(s => s.LocalDate == "2026-01-01");
        var day2 = stats.Single(s => s.LocalDate == "2026-01-02");
        Assert.Equal(2500, day1.EndBytes);
        Assert.Equal(500, day2.DeltaBytes);
    }

    [Fact]
    public void 跨缺日周期累计并标注()
    {
        var stats = Aggregate(
            TestData.Sample("2026-01-01", 1000),
            TestData.Sample("2026-01-05", 513000));

        var day5 = stats.Single(s => s.LocalDate == "2026-01-05");
        Assert.Equal("gap_filled", day5.Method);
        Assert.Equal(512000, day5.DeltaBytes);
        Assert.Equal(3, day5.GapDays);
        Assert.Equal("low_confidence", day5.Quality);
    }

    [Fact]
    public void 计数器回退钳制为零并标异常()
    {
        var stats = Aggregate(
            TestData.Sample("2026-01-01", 2000),
            TestData.Sample("2026-01-02", 1500));

        var day2 = stats.Single(s => s.LocalDate == "2026-01-02");
        Assert.Equal("clamped", day2.Method);
        Assert.Equal(0, day2.DeltaBytes);
        Assert.Equal("anomaly", day2.Quality);
    }

    [Fact]
    public void 数据来源切换不做跨源相减()
    {
        var stats = Aggregate(
            TestData.Sample("2026-01-01", 1000, source: "ata.predict_failure"),
            TestData.Sample("2026-01-02", 9000, source: "nvme.healthlog"));

        var day2 = stats.Single(s => s.LocalDate == "2026-01-02");
        Assert.Equal("source_switch", day2.Method);
        Assert.Equal(0, day2.DeltaBytes);
        Assert.Equal("low_confidence", day2.Quality);
    }

    [Fact]
    public void 当日无有效读数记为缺失()
    {
        var stats = Aggregate(
            TestData.Sample("2026-01-01", 1000),
            TestData.Sample("2026-01-02", null));

        var day2 = stats.Single(s => s.LocalDate == "2026-01-02");
        Assert.Equal("gap_filled", day2.Method);
        Assert.Equal("missing", day2.Quality);
        Assert.Equal(0, day2.DeltaBytes);
    }

    [Fact]
    public void 多块盘互不串扰()
    {
        var stats = Aggregate(
            TestData.Sample("2026-01-01", 1000, diskKey: "A|S1"),
            TestData.Sample("2026-01-02", 4000, diskKey: "A|S1"),
            TestData.Sample("2026-01-02", 500, diskKey: "B|S2"));

        var a = stats.Single(s => s.DiskKey == "A|S1" && s.LocalDate == "2026-01-02");
        var b = stats.Single(s => s.DiskKey == "B|S2" && s.LocalDate == "2026-01-02");
        Assert.Equal(3000, a.DeltaBytes);
        Assert.Equal("baseline", b.Method);
    }

    [Fact]
    public void 大数差值不丢精度()
    {
        var stats = Aggregate(
            TestData.Sample("2026-01-01", 100_000_000_000),
            TestData.Sample("2026-01-02", 202_400_000_000),
            TestData.Sample("2026-01-05", 714_400_000_000));

        Assert.Equal(102_400_000_000, stats.Single(s => s.LocalDate == "2026-01-02").DeltaBytes);
        Assert.Equal(512_000_000_000, stats.Single(s => s.LocalDate == "2026-01-05").DeltaBytes);
        Assert.All(stats, s => Assert.True(s.DeltaBytes >= 0));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public void 缺日天数按间隔计(int gap, int expected)
    {
        var stats = Aggregate(
            TestData.Sample("2026-01-01", 1000),
            TestData.Sample($"2026-01-0{2 + gap}", 5000));

        var last = stats.Single(s => s.Method == "gap_filled");
        Assert.Equal(expected, last.GapDays);
        Assert.Equal("low_confidence", last.Quality);
    }
}
