using System.Globalization;
using System.Text;

namespace DiskWriteTracker.Core.Store;

/// <summary>
/// 一条落盘样本。字段设计与 plan 中 samples 表一一对应，便于 M2 迁移到 SQLite。
/// </summary>
public sealed record SampleRecord
{
    public required string RunId { get; init; }
    public required string CapturedUtc { get; init; }
    public required string CapturedLocal { get; init; }
    public required string LocalDate { get; init; }
    public required int TzOffsetMinutes { get; init; }

    public required string DiskKey { get; init; }
    public required int DriveNumber { get; init; }
    public required string Model { get; init; }
    public required string SerialNumber { get; init; }
    public required string Firmware { get; init; }
    public required string BusType { get; init; }

    public required string Source { get; init; }
    public string? AttributeRef { get; init; }
    public long? RawValue { get; init; }
    public string? RawUnit { get; init; }
    public long UnitScale { get; init; }
    public long? Bytes { get; init; }
    public required string Quality { get; init; }
    public string? Note { get; init; }

    public double? TemperatureC { get; init; }
    public long? PowerOnHours { get; init; }
    public long? PowerCycles { get; init; }
    public int? PercentageUsed { get; init; }

    /// <summary>型号+序列号归一化后的稳定标识。序列号缺失或为合成串时退到型号+容量。</summary>
    public static string BuildDiskKey(string model, string serial, long? sizeBytes, bool serialIsSynthetic)
    {
        string m = Normalize(model);

        if (!serialIsSynthetic && !string.IsNullOrWhiteSpace(serial))
            return m + "|" + Normalize(serial);

        // 合成串不可作身份：退到型号+容量，并标记，避免与真实序列号混淆
        string size = sizeBytes.HasValue ? sizeBytes.Value.ToString(CultureInfo.InvariantCulture) : "0";
        return m + "|size:" + size;
    }

    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "UNKNOWN";

        var sb = new StringBuilder(value.Length);
        foreach (char c in value.Trim().ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
                sb.Append(c);
            else if (c is ' ')
                sb.Append('_');
        }

        return sb.Length == 0 ? "UNKNOWN" : sb.ToString();
    }
}
