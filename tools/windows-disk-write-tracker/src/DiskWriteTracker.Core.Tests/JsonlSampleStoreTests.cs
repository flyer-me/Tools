using System.Text;
using DiskWriteTracker.Core.Analysis;
using DiskWriteTracker.Core.Store;
using Xunit;

namespace DiskWriteTracker.Core.Tests;

public class JsonlSampleStoreTests : IDisposable
{
    private readonly string _dir;

    public JsonlSampleStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dwt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private JsonlSampleStore Store => new(_dir);

    [Fact]
    public void 写入后读回字段一致()
    {
        var record = TestData.Sample("2026-01-01", 987654321, quality: "fallback") with
        {
            AttributeRef = "smart.attr246",
            RawValue = 1929008,
            RawUnit = "lba512",
            Note = "中文备注",
            TemperatureC = 33.5,
            PowerOnHours = 3632,
            PowerCycles = 2932,
            PercentageUsed = 1
        };

        Assert.Equal(1, Store.Append([record]));

        var read = Assert.Single(Store.ReadAll());
        Assert.Equal(record.RunId, read.RunId);
        Assert.Equal(record.CapturedUtc, read.CapturedUtc);
        Assert.Equal(record.CapturedLocal, read.CapturedLocal);
        Assert.Equal(record.LocalDate, read.LocalDate);
        Assert.Equal(record.TzOffsetMinutes, read.TzOffsetMinutes);
        Assert.Equal(record.DiskKey, read.DiskKey);
        Assert.Equal(record.DriveNumber, read.DriveNumber);
        Assert.Equal(record.Model, read.Model);
        Assert.Equal(record.SerialNumber, read.SerialNumber);
        Assert.Equal(record.Firmware, read.Firmware);
        Assert.Equal(record.BusType, read.BusType);
        Assert.Equal(record.Source, read.Source);
        Assert.Equal(record.AttributeRef, read.AttributeRef);
        Assert.Equal(record.RawValue, read.RawValue);
        Assert.Equal(record.RawUnit, read.RawUnit);
        Assert.Equal(record.UnitScale, read.UnitScale);
        Assert.Equal(record.Bytes, read.Bytes);
        Assert.Equal(record.Quality, read.Quality);
        Assert.Equal(record.Note, read.Note);
        Assert.Equal(record.TemperatureC, read.TemperatureC);
        Assert.Equal(record.PowerOnHours, read.PowerOnHours);
        Assert.Equal(record.PowerCycles, read.PowerCycles);
        Assert.Equal(record.PercentageUsed, read.PercentageUsed);
    }

    [Fact]
    public void 按自然日切分文件()
    {
        Store.Append([
            TestData.Sample("2026-01-01", 100),
            TestData.Sample("2026-01-02", 200)
        ]);

        Assert.True(File.Exists(Path.Combine(_dir, "samples-20260101.jsonl")));
        Assert.True(File.Exists(Path.Combine(_dir, "samples-20260102.jsonl")));
        Assert.Equal(2, Store.ReadAll().Count);
    }

    [Fact]
    public void 中文与特殊字符原样落盘()
    {
        Store.Append([TestData.Sample("2026-01-01", 1, model: "型号\"引号\"磁盘")]);

        string line = File.ReadAllText(Path.Combine(_dir, "samples-20260101.jsonl"));
        Assert.DoesNotContain("\\u", line);
        Assert.Contains("型号", line);
    }

    [Fact]
    public void 旧版手写格式的行可读()
    {
        // 旧手写格式：字段名相同，可空字段显式写 null
        File.WriteAllBytes(Path.Combine(_dir, "samples-20260101.jsonl"), Encoding.UTF8.GetBytes(
            "{\"runId\":\"r1\",\"capturedUtc\":\"2026-01-01T12:00:00Z\",\"capturedLocal\":\"l\",\"localDate\":\"2026-01-01\","
            + "\"tzOffsetMinutes\":480,\"diskKey\":\"OLD|DISK\",\"driveNumber\":0,\"model\":\"OLD\",\"serialNumber\":\"S\","
            + "\"firmware\":\"f\",\"busType\":\"SATA\",\"source\":\"ata.predict_failure\",\"attributeRef\":null,"
            + "\"rawValue\":null,\"rawUnit\":null,\"unitScale\":512,\"bytes\":4096,\"quality\":\"reliable\",\"note\":null,"
            + "\"temperatureC\":null,\"powerOnHours\":null,\"powerCycles\":null,\"percentageUsed\":null}\n"));

        var read = Assert.Single(Store.ReadAll());
        Assert.Equal("OLD|DISK", read.DiskKey);
        Assert.Equal(4096, read.Bytes);
        Assert.Null(read.Note);
    }

    [Fact]
    public void 缺关键字的行被跳过()
    {
        Store.Append([TestData.Sample("2026-01-01", 7)]);

        // 合法行前后各插一行坏数据
        string path = Path.Combine(_dir, "samples-20260101.jsonl");
        string good = File.ReadAllText(path);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(
            "{\"runId\":\"r1\",\"capturedUtc\":\"\",\"capturedLocal\":\"l\",\"localDate\":\"2026-01-01\","
            + "\"tzOffsetMinutes\":480,\"diskKey\":\"K\",\"driveNumber\":0,\"model\":\"M\",\"serialNumber\":\"S\","
            + "\"firmware\":\"f\",\"busType\":\"SATA\",\"source\":\"ata\",\"unitScale\":1,\"bytes\":1,\"quality\":\"reliable\"}\n"
            + "不是 JSON 的一行\n"
            + good));

        var read = Assert.Single(Store.ReadAll());
        Assert.Equal(7, read.Bytes);
    }

    [Fact]
    public void 窗口读取带回前驱避免首日退化成基线()
    {
        Store.Append([
            TestData.Sample("2026-01-01", 1000),
            TestData.Sample("2026-01-10", 5000)
        ]);

        var windowed = Store.ReadForWindow(1, new DateOnly(2026, 1, 10));

        // 窄窗口必须带回前驱，否则首日退化成基线，增量变 0
        Assert.Equal(2, windowed.Count);

        var stats = DailyAggregator.Aggregate(windowed);
        var day10 = stats.Single(s => s.LocalDate == "2026-01-10");
        Assert.Equal("gap_filled", day10.Method);
        Assert.Equal(4000, day10.DeltaBytes);
    }

    [Fact]
    public void 窗口与全量在重叠日期上结果一致()
    {
        Store.Append([
            TestData.Sample("2026-01-01", 1000),
            TestData.Sample("2026-01-05", 3000),
            TestData.Sample("2026-01-06", 4000)
        ]);

        var full = DailyAggregator.Aggregate(Store.ReadAll()).ToDictionary(s => s.LocalDate);
        var windowed = DailyAggregator.Aggregate(Store.ReadForWindow(2, new DateOnly(2026, 1, 6)));

        foreach (var stat in windowed)
        {
            Assert.Equal(full[stat.LocalDate].DeltaBytes, stat.DeltaBytes);
            Assert.Equal(full[stat.LocalDate].Method, stat.Method);
            Assert.Equal(full[stat.LocalDate].Quality, stat.Quality);
        }

        Assert.Contains(windowed, s => s.LocalDate == "2026-01-06");
    }

    [Fact]
    public void 窗口完全落在数据之外时返回空()
    {
        Store.Append([TestData.Sample("2026-01-01", 1000)]);

        Assert.Empty(Store.ReadForWindow(2, new DateOnly(2026, 3, 1)));
    }
}
