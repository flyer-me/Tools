using DiskWriteTracker.Core.Store;

namespace DiskWriteTracker.Core.Tests;

/// <summary>样本工厂。默认字段取最常见的真实形态。</summary>
internal static class TestData
{
    public static SampleRecord Sample(
        string localDate,
        long? bytes,
        string? capturedUtc = null,
        string quality = "reliable",
        string source = "ata.predict_failure",
        string diskKey = "TEST|DISK",
        string model = "TEST DISK") => new()
    {
        RunId = "run-" + localDate,
        CapturedUtc = capturedUtc ?? localDate + "T12:00:00Z",
        CapturedLocal = localDate + "T20:00:00",
        LocalDate = localDate,
        TzOffsetMinutes = 480,
        DiskKey = diskKey,
        DriveNumber = 0,
        Model = model,
        SerialNumber = "SN1",
        Firmware = "F1",
        BusType = "SATA",
        Source = source,
        UnitScale = 512,
        Bytes = bytes,
        Quality = quality
    };
}
