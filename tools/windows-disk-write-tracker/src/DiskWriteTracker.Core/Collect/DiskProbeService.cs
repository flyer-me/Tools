using DiskWriteTracker.Core.Discovery;
using DiskWriteTracker.Core.Interop;
using DiskWriteTracker.Core.Units;

namespace DiskWriteTracker.Core.Collect;

/// <summary>一次采样中单块盘的读数。</summary>
public sealed record DiskProbeResult
{
    public required int DriveNumber { get; init; }
    public required string Model { get; init; }
    public required string SerialNumber { get; init; }
    public required string Firmware { get; init; }
    public required string BusType { get; init; }
    public long? SizeBytes { get; init; }

    /// <summary>实际取得的句柄权限：readwrite / read / none。决定哪些 IOCTL 可用。</summary>
    public string AccessLevel { get; init; } = "none";

    public bool HasReadWriteAccess => string.Equals(AccessLevel, "readwrite", StringComparison.OrdinalIgnoreCase);

    /// <summary>数据来源，如 nvme.healthlog / ata.smart。</summary>
    public required string Source { get; init; }
    public string? AttributeRef { get; init; }
    public long? RawValue { get; init; }
    public string? RawUnit { get; init; }
    public long UnitScale { get; init; }
    public long? TotalBytes { get; init; }

    public string Quality { get; init; } = "missing";
    public string? Note { get; init; }

    /// <summary>由 累计字节 ÷ 已用寿命 反推出的隐含标称 TBW（仅 SATA 闸门提供）。</summary>
    public double? ImpliedTbwTb { get; init; }

    public double? TemperatureCelsius { get; init; }
    public long? PowerOnHours { get; init; }
    public long? PowerCycles { get; init; }
    public int? PercentageUsed { get; init; }
    public long? MediaErrors { get; init; }
}

/// <summary>
/// 依次尝试通道 A（NVMe）与通道 B（SATA），产出硬件累计写入值。
/// 全部为只读操作，不写盘、不联网。
/// </summary>
public static class DiskProbeService
{
    public static IReadOnlyList<DiskProbeResult> ProbeAll()
    {
        var results = new List<DiskProbeResult>();

        foreach (var disk in DiskEnumerator.Enumerate())
            results.Add(Probe(disk));

        return results;
    }

    private static DiskProbeResult Probe(PhysicalDiskInfo disk)
    {
        var handle = NativeMethods.OpenPhysicalDrive(disk.DriveNumber, out DeviceAccess access, out int openError);
        if (handle is null)
        {
            return Failure(disk, access, "open_failed", $"无法打开 PhysicalDrive{disk.DriveNumber}，Win32 错误 {openError}");
        }

        using (handle)
        {
            bool isNvme = string.Equals(disk.BusType, "NVMe", StringComparison.OrdinalIgnoreCase);

            return isNvme ? ProbeNvme(disk, handle, access) : ProbeAta(disk, handle, access);
        }
    }

    private static DiskProbeResult ProbeNvme(PhysicalDiskInfo disk, Microsoft.Win32.SafeHandles.SafeFileHandle handle, DeviceAccess access)
    {
        if (!Nvme.TryReadHealthLog(handle, out NvmeHealthLog health, out int healthError))
        {
            return Failure(disk, access, "nvme_healthlog_failed",
                $"IOCTL_STORAGE_QUERY_PROPERTY 读取 NVMe 健康日志失败，Win32 错误 {healthError}");
        }

        // 优先用 Identify Controller 的真实序列号，替换 Windows 的合成串
        string serial = disk.SerialNumber;
        string model = disk.Model;
        string firmware = disk.Firmware;

        if (Nvme.TryReadIdentifyController(handle, out NvmeIdentify identify, out _))
        {
            if (!string.IsNullOrWhiteSpace(identify.SerialNumber))
                serial = identify.SerialNumber;

            if (!string.IsNullOrWhiteSpace(identify.ModelNumber))
                model = identify.ModelNumber;

            if (!string.IsNullOrWhiteSpace(identify.FirmwareRevision))
                firmware = identify.FirmwareRevision;
        }

        long? bytes = health.DataUnitsWritten > (ulong)(long.MaxValue / Nvme.BytesPerDataUnit)
            ? null
            : (long)health.DataUnitsWritten * Nvme.BytesPerDataUnit;

        return new DiskProbeResult
        {
            DriveNumber = disk.DriveNumber,
            Model = model,
            SerialNumber = serial,
            Firmware = firmware,
            BusType = disk.BusType,
            SizeBytes = disk.SizeBytes,
            AccessLevel = access.ToString().ToLowerInvariant(),
            Source = "nvme.healthlog",
            AttributeRef = "healthlog[0x02].data_units_written",
            RawValue = (long)Math.Min(health.DataUnitsWritten, long.MaxValue),
            RawUnit = "data_unit_512000",
            UnitScale = Nvme.BytesPerDataUnit,
            TotalBytes = bytes,
            Quality = "reliable",
            Note = $"log page 0x02 (propertyId={health.PropertyIdUsed})",
            TemperatureCelsius = health.TemperatureKelvin >= 273 ? health.TemperatureKelvin - 273.15 : health.TemperatureKelvin,
            PowerOnHours = (long)Math.Min(health.PowerOnHours, long.MaxValue),
            PowerCycles = (long)Math.Min(health.PowerCycles, long.MaxValue),
            PercentageUsed = health.PercentageUsed,
            MediaErrors = (long)Math.Min(health.MediaErrors, long.MaxValue)
        };
    }

    private static DiskProbeResult ProbeAta(PhysicalDiskInfo disk, Microsoft.Win32.SafeHandles.SafeFileHandle handle, DeviceAccess access)
    {
        if (!AtaSmart.TryReadAttributes(handle, out AtaAttributeTable table, out AtaTransport transport, out int smartError))
        {
            return Failure(disk, access, "ata_smart_failed",
                $"SMART_RCV_DRIVE_DATA 读取属性表失败，Win32 错误 {smartError}（虚拟盘/RAID 卡后常见）");
        }

        double? temperature = table.TryGet(194, out AtaAttribute temp) ? temp.RawLowByte : null;
        long? powerOnHours = table.TryGet(9, out AtaAttribute poh) ? (long)Math.Min(poh.Raw, long.MaxValue) : null;
        long? powerCycles = table.TryGet(12, out AtaAttribute cycles) ? (long)Math.Min(cycles.Raw, long.MaxValue) : null;

        // 202 Percent_Lifetime_Remain: MX500 上 value=90 / raw=10，raw 即"已用百分比"
        int? percentUsed = table.TryGet(202, out AtaAttribute life) ? life.RawLowByte : null;

        if (!table.TryGetWrittenLba(out byte attrId, out ulong rawLba))
        {
            return Failure(disk, access, "written_lba_absent",
                "属性表中未找到 246/241/233，该盘不提供累计写入计数") with
            {
                TemperatureCelsius = temperature,
                PowerOnHours = powerOnHours,
                PowerCycles = powerCycles,
                PercentageUsed = percentUsed
            };
        }

        int sectorSize = NativeMethods.TryGetBytesPerSector(handle) ?? 512;
        var decision = UnitResolver.Resolve((long)Math.Min(rawLba, long.MaxValue), sectorSize, percentUsed, declaredTbwTb: null);

        long? bytes = decision.Scale > 0 && rawLba <= (ulong)(long.MaxValue / decision.Scale)
            ? (long)rawLba * decision.Scale
            : null;

        return new DiskProbeResult
        {
            DriveNumber = disk.DriveNumber,
            Model = disk.Model,
            SerialNumber = disk.SerialNumber,
            Firmware = disk.Firmware,
            BusType = disk.BusType,
            SizeBytes = disk.SizeBytes,
            AccessLevel = access.ToString().ToLowerInvariant(),
            Source = transport == AtaTransport.PredictFailure ? "ata.predict_failure" : "ata.smart_rcv",
            AttributeRef = $"smart.attr{attrId}",
            RawValue = (long)Math.Min(rawLba, long.MaxValue),
            RawUnit = decision.Scale == 1 ? "bytes" : "lba" + decision.Scale,
            UnitScale = decision.Scale,
            TotalBytes = bytes,
            Quality = decision.Quality,
            Note = $"属性 {attrId}；{decision.Reason}",
            ImpliedTbwTb = decision.ImpliedTbwTb,
            TemperatureCelsius = temperature,
            PowerOnHours = powerOnHours,
            PowerCycles = powerCycles,
            PercentageUsed = percentUsed
        };
    }

    private static DiskProbeResult Failure(PhysicalDiskInfo disk, DeviceAccess access, string source, string note) => new()
    {
        DriveNumber = disk.DriveNumber,
        Model = disk.Model,
        SerialNumber = disk.SerialNumber,
        Firmware = disk.Firmware,
        BusType = disk.BusType,
        SizeBytes = disk.SizeBytes,
        AccessLevel = access.ToString().ToLowerInvariant(),
        Source = source,
        Quality = "missing",
        Note = note
    };
}
