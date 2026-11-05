using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DiskWriteTracker.Core.Interop;

/// <summary>
/// IOCTL_STORAGE_QUERY_PROPERTY 的两种用法：
///  1) StorageDeviceProperty(0)              —— 拿型号/序列号/固件/总线类型，替代 WMI。
///  2) ProtocolSpecific(50 设备 / 49 适配器) —— 拿 NVMe SMART 日志与 Identify。
/// 常量取自 smartmontools 7.5 os_win32.cpp（旧 SDK 头没有 49/50）。
/// </summary>
internal static class StorageQuery
{
    internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    internal const uint StorageDeviceProperty = 0;
    internal const uint StorageAdapterProtocolSpecificProperty = 49;
    internal const uint StorageDeviceProtocolSpecificProperty = 50;

    internal const uint ProtocolTypeNvme = 3;

    internal const uint NvmeDataTypeIdentify = 1;
    internal const uint NvmeDataTypeLogPage = 2;

    /// <summary>STORAGE_PROTOCOL_SPECIFIC_DATA 恒为 40 字节。</summary>
    internal const int ProtocolDataHeaderSize = 40;

    /// <summary>query 结构体头 8 字节：{ PropertyId, QueryType }（不含 AdditionalParameters）。</summary>
    private const int QueryHeaderSize = 8;

    internal static bool TryGetDescriptor(SafeFileHandle handle, out DeviceDescriptor descriptor, out int win32Error)
    {
        descriptor = DeviceDescriptor.Empty;
        var input = new byte[12]; // STORAGE_PROPERTY_QUERY
        BitConverter.GetBytes(StorageDeviceProperty).CopyTo(input, 0);
        BitConverter.GetBytes(0u).CopyTo(input, 4); // PropertyStandardQuery

        var output = new byte[1024];
        if (!NativeMethods.DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, input, input.Length,
                output, output.Length, out int returned, IntPtr.Zero))
        {
            win32Error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }

        win32Error = 0;
        if (returned < 36)
            return false;

        descriptor = DeviceDescriptor.Parse(output, returned);
        return true;
    }

    /// <summary>
    /// 协议特定查询。输入输出共用同一块缓冲：8 字节 query 头 + 40 字节协议头 + 数据区，
    /// 返回的数据从 8 + ProtocolDataOffset 开始。
    /// </summary>
    internal static bool TryProtocolQuery(
        SafeFileHandle handle,
        uint propertyId,
        uint protocolType,
        uint dataType,
        uint requestValue,
        uint requestSubValue,
        int dataLength,
        out byte[] data,
        out int win32Error)
    {
        data = [];
        int total = QueryHeaderSize + ProtocolDataHeaderSize + dataLength;
        var buffer = new byte[total];

        BitConverter.GetBytes(propertyId).CopyTo(buffer, 0);
        BitConverter.GetBytes(0u).CopyTo(buffer, 4); // PropertyStandardQuery

        int h = QueryHeaderSize;
        BitConverter.GetBytes(protocolType).CopyTo(buffer, h + 0);
        BitConverter.GetBytes(dataType).CopyTo(buffer, h + 4);
        BitConverter.GetBytes(requestValue).CopyTo(buffer, h + 8);
        BitConverter.GetBytes(requestSubValue).CopyTo(buffer, h + 12);
        BitConverter.GetBytes((uint)ProtocolDataHeaderSize).CopyTo(buffer, h + 16);
        BitConverter.GetBytes((uint)dataLength).CopyTo(buffer, h + 20);
        // h+24 FixedProtocolReturnData、h+28..39 Reserved 保持 0

        if (!NativeMethods.DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, buffer, buffer.Length,
                buffer, buffer.Length, out _, IntPtr.Zero))
        {
            win32Error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }

        win32Error = 0;

        // 驱动回填的偏移优先，缺失则按规范用 40
        int reportedOffset = BitConverter.ToInt32(buffer, h + 16);
        int reportedLength = BitConverter.ToInt32(buffer, h + 20);
        if (reportedOffset <= 0 || reportedOffset > 4096)
            reportedOffset = ProtocolDataHeaderSize;

        int start = QueryHeaderSize + reportedOffset;
        int available = buffer.Length - start;
        if (available <= 0)
            return false;

        int take = reportedLength > 0 ? Math.Min(reportedLength, available) : Math.Min(dataLength, available);
        if (take <= 0)
            return false;

        data = new byte[take];
        Array.Copy(buffer, start, data, 0, take);
        return true;
    }

    internal static string BusTypeName(uint busType) => busType switch
    {
        0 => "Unknown",
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE1394",
        5 => "SSA",
        6 => "Fibre",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "FileBackedVirtual",
        16 => "Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "Bus(0x" + busType.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + ")"
    };
}

/// <summary>STORAGE_DEVICE_DESCRIPTOR 的托管投影。</summary>
internal sealed record DeviceDescriptor(
    string? VendorId,
    string? ProductId,
    string? ProductRevision,
    string? SerialNumber,
    uint BusType,
    byte DeviceType,
    bool RemovableMedia)
{
    internal static readonly DeviceDescriptor Empty = new(null, null, null, null, 0, 0, false);

    internal static DeviceDescriptor Parse(byte[] buffer, int length)
    {
        uint vendorOffset = BitConverter.ToUInt32(buffer, 12);
        uint productOffset = BitConverter.ToUInt32(buffer, 16);
        uint revisionOffset = BitConverter.ToUInt32(buffer, 20);
        uint serialOffset = BitConverter.ToUInt32(buffer, 24);
        uint busType = BitConverter.ToUInt32(buffer, 28);

        return new DeviceDescriptor(
            ReadAnsi(buffer, length, vendorOffset),
            ReadAnsi(buffer, length, productOffset),
            ReadAnsi(buffer, length, revisionOffset),
            ReadAnsi(buffer, length, serialOffset),
            busType,
            buffer.Length > 8 ? buffer[8] : (byte)0,
            buffer.Length > 10 && buffer[10] != 0);
    }

    private static string? ReadAnsi(byte[] buffer, int length, uint offset)
    {
        if (offset == 0 || offset >= (uint)length)
            return null;

        int start = (int)offset;
        int end = start;
        while (end < length && buffer[end] != 0)
            end++;

        if (end == start)
            return null;

        string value = Encoding.ASCII.GetString(buffer, start, end - start).Trim();
        return value.Length == 0 ? null : value;
    }
}
