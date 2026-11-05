using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskWriteTracker.Core.Interop;

/// <summary>打开物理驱动器时实际取得的权限级别。disks 命令会展示它。</summary>
internal enum DeviceAccess
{
    None = 0,
    Read = 1,
    ReadWrite = 2
}

/// <summary>
/// 对 kernel32 的最小封装。全部为标准 Win32，无第三方依赖。
/// </summary>
internal static class NativeMethods
{
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;

    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_ACCESS_DENIED = 5;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileNative(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        byte[]? lpInBuffer,
        int nInBufferSize,
        byte[]? lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>
    /// 打开物理驱动器。按 读写 → 只读 → 零访问 三级降级。
    /// 零访问足以支撑 IOCTL_STORAGE_QUERY_PROPERTY（非提权读 NVMe 的路径）；
    /// 而 SMART_RCV_DRIVE_DATA 要求带写权限，非提权通常只能拿到零访问句柄。
    /// </summary>
    internal static SafeFileHandle? OpenPhysicalDrive(int driveNumber, out DeviceAccess access, out int lastError)
    {
        string path = @"\\.\PhysicalDrive" + driveNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

        (uint Requested, DeviceAccess Level)[] attempts =
        [
            (GENERIC_READ | GENERIC_WRITE, DeviceAccess.ReadWrite),
            (GENERIC_READ, DeviceAccess.Read),
            (0, DeviceAccess.None)
        ];

        lastError = 0;

        foreach (var (requested, level) in attempts)
        {
            var handle = CreateFileNative(
                path,
                requested,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                access = level;
                lastError = 0;
                return handle;
            }

            lastError = Marshal.GetLastWin32Error();
            handle.Dispose();
        }

        access = DeviceAccess.None;
        return null;
    }

    /// <summary>设备类型。0 表示该盘号不存在。</summary>
    internal static bool TryGetDeviceType(SafeFileHandle handle, out uint deviceType)
    {
        const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
        var buffer = new byte[12]; // STORAGE_DEVICE_NUMBER: DeviceType, DeviceNumber, PartitionNumber

        deviceType = 0;
        if (!DeviceIoControl(handle, IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0, buffer, buffer.Length, out _, IntPtr.Zero))
            return false;

        deviceType = BitConverter.ToUInt32(buffer, 0);
        return true;
    }

    /// <summary>磁盘容量（字节）。无读权限时可能失败，失败返回 null。</summary>
    internal static long? TryGetLength(SafeFileHandle handle)
    {
        const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
        var buffer = new byte[8]; // GET_LENGTH_INFORMATION: LARGE_INTEGER Length

        if (!DeviceIoControl(handle, IOCTL_DISK_GET_LENGTH_INFO, null, 0, buffer, buffer.Length, out _, IntPtr.Zero))
            return null;

        long length = BitConverter.ToInt64(buffer, 0);
        return length > 0 ? length : null;
    }

    /// <summary>逻辑扇区大小（字节），用于 SATA 累计写入的单位换算。</summary>
    internal static int? TryGetBytesPerSector(SafeFileHandle handle)
    {
        const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
        var buffer = new byte[32]; // DISK_GEOMETRY_EX：BytesPerSector 位于偏移 20

        if (!DeviceIoControl(handle, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 0, buffer, buffer.Length, out _, IntPtr.Zero))
            return null;

        int bytesPerSector = BitConverter.ToInt32(buffer, 20);
        return bytesPerSector is 512 or 4096 ? bytesPerSector : null;
    }
}
