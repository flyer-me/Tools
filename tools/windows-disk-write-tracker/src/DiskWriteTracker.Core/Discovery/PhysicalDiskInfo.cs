namespace DiskWriteTracker.Core.Discovery;

/// <summary>单块物理盘的身份信息。全部来自 IOCTL，不依赖 WMI。</summary>
public sealed record PhysicalDiskInfo(
    int DriveNumber,
    string Model,
    string SerialNumber,
    string Firmware,
    string BusType,
    long? SizeBytes,
    string AccessLevel)
{
    /// <summary>Windows 对 NVMe 常返回合成序列号（形如 0000_0000_..._XXXX.），不可作为稳定标识。</summary>
    public bool HasSyntheticSerial => LooksSynthetic(SerialNumber);

    internal static bool LooksSynthetic(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial))
            return true;

        string trimmed = serial.Trim();
        if (trimmed.Length < 8)
            return true;

        // 形如 0000_0000_0000_0000_707C_1856_F808_51A8.
        if (trimmed.Count(c => c == '_') >= 4)
            return true;

        // 全为 0 或全为同一个字符
        return trimmed.Distinct().Count() <= 2;
    }
}
