using DiskWriteTracker.Core.Interop;

namespace DiskWriteTracker.Core.Discovery;

/// <summary>
/// 通过逐个打开 \\.\PhysicalDriveN 枚举磁盘。不依赖 WMI，零 NuGet。
/// </summary>
public static class DiskEnumerator
{
    /// <summary>PhysicalDrive 编号上限，够覆盖绝大多数工作站。</summary>
    private const int MaxDriveNumber = 32;

    public static IReadOnlyList<PhysicalDiskInfo> Enumerate()
    {
        var result = new List<PhysicalDiskInfo>();

        for (int driveNumber = 0; driveNumber < MaxDriveNumber; driveNumber++)
        {
            var handle = NativeMethods.OpenPhysicalDrive(driveNumber, out DeviceAccess access, out int openError);
            if (handle is null)
            {
                // 盘号连续，遇到第一个"不存在"即可停止；ACCESS_DENIED 则继续试探
                if (openError == NativeMethods.ERROR_FILE_NOT_FOUND)
                    break;

                continue;
            }

            using (handle)
            {
                if (!NativeMethods.TryGetDeviceType(handle, out uint deviceType))
                    continue;

                // FILE_DEVICE_DISK = 7
                if (deviceType != 7)
                    continue;

                if (!StorageQuery.TryGetDescriptor(handle, out DeviceDescriptor descriptor, out _))
                    continue;

                if (string.IsNullOrWhiteSpace(descriptor.ProductId) && string.IsNullOrWhiteSpace(descriptor.SerialNumber))
                    continue;

                long? size = NativeMethods.TryGetLength(handle);

                result.Add(new PhysicalDiskInfo(
                    DriveNumber: driveNumber,
                    Model: descriptor.ProductId ?? descriptor.VendorId ?? "Unknown",
                    SerialNumber: descriptor.SerialNumber ?? string.Empty,
                    Firmware: descriptor.ProductRevision ?? string.Empty,
                    BusType: StorageQuery.BusTypeName(descriptor.BusType),
                    SizeBytes: size,
                    AccessLevel: access.ToString().ToLowerInvariant()));
            }
        }

        return result;
    }
}
