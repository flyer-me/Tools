using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskWriteTracker.Core.Interop;

/// <summary>通道 A：NVMe SMART/Health 日志（Log Page 0x02）与 Identify Controller。</summary>
internal static class Nvme
{
    /// <summary>NVMe 规范：Data Units 为 1000 × 512 字节。</summary>
    internal const long BytesPerDataUnit = 512_000L;

    internal const byte HealthLogPageId = 0x02;
    internal const uint CnsIdentifyController = 0x01;

    internal static bool TryReadHealthLog(SafeFileHandle handle, out NvmeHealthLog log, out int win32Error)
    {
        log = default;
        win32Error = 0;

        foreach (uint propertyId in new[] { StorageQuery.StorageDeviceProtocolSpecificProperty, StorageQuery.StorageAdapterProtocolSpecificProperty })
        {
            if (!StorageQuery.TryProtocolQuery(handle, propertyId, StorageQuery.ProtocolTypeNvme,
                    StorageQuery.NvmeDataTypeLogPage, HealthLogPageId, 0, 512, out byte[] data, out win32Error))
                continue;

            if (data.Length < 160)
            {
                win32Error = 0;
                return false;
            }

            log = new NvmeHealthLog(
                CriticalWarning: data[0],
                TemperatureKelvin: BitConverter.ToUInt16(data, 1),
                AvailableSpare: data[3],
                AvailableSpareThreshold: data[4],
                PercentageUsed: data[5],
                DataUnitsRead: ReadUInt128Low(data, 32),
                DataUnitsWritten: ReadUInt128Low(data, 48),
                HostReadCommands: ReadUInt128Low(data, 64),
                HostWriteCommands: ReadUInt128Low(data, 80),
                ControllerBusyMinutes: ReadUInt128Low(data, 96),
                PowerCycles: ReadUInt128Low(data, 112),
                PowerOnHours: ReadUInt128Low(data, 128),
                UnsafeShutdowns: ReadUInt128Low(data, 144),
                MediaErrors: ReadUInt128Low(data, 160 > data.Length - 8 ? data.Length - 8 : 160),
                PropertyIdUsed: propertyId);
            return true;
        }

        return false;
    }

    internal static bool TryReadIdentifyController(SafeFileHandle handle, out NvmeIdentify identify, out int win32Error)
    {
        identify = default;
        win32Error = 0;

        foreach (uint propertyId in new[] { StorageQuery.StorageAdapterProtocolSpecificProperty, StorageQuery.StorageDeviceProtocolSpecificProperty })
        {
            if (!StorageQuery.TryProtocolQuery(handle, propertyId, StorageQuery.ProtocolTypeNvme,
                    StorageQuery.NvmeDataTypeIdentify, CnsIdentifyController, 0, 4096, out byte[] data, out win32Error))
                continue;

            if (data.Length < 72)
            {
                win32Error = 0;
                return false;
            }

            identify = new NvmeIdentify(
                SerialNumber: ReadAscii(data, 4, 20),
                ModelNumber: ReadAscii(data, 24, 40),
                FirmwareRevision: ReadAscii(data, 64, 8));
            return true;
        }

        return false;
    }

    /// <summary>NVMe 的计数器是 128 位，实际取值远小于 2^64，取低 64 位即可。</summary>
    private static ulong ReadUInt128Low(byte[] data, int offset)
        => offset >= 0 && offset + 8 <= data.Length ? BitConverter.ToUInt64(data, offset) : 0UL;

    private static string ReadAscii(byte[] data, int offset, int count)
    {
        if (offset + count > data.Length)
            return string.Empty;

        return Encoding.ASCII.GetString(data, offset, count).Trim('\0', ' ');
    }
}

internal readonly record struct NvmeHealthLog(
    byte CriticalWarning,
    ushort TemperatureKelvin,
    byte AvailableSpare,
    byte AvailableSpareThreshold,
    byte PercentageUsed,
    ulong DataUnitsRead,
    ulong DataUnitsWritten,
    ulong HostReadCommands,
    ulong HostWriteCommands,
    ulong ControllerBusyMinutes,
    ulong PowerCycles,
    ulong PowerOnHours,
    ulong UnsafeShutdowns,
    ulong MediaErrors,
    uint PropertyIdUsed)
{
}

internal readonly record struct NvmeIdentify(string SerialNumber, string ModelNumber, string FirmwareRevision);
