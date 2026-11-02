using Microsoft.Win32.SafeHandles;

namespace DiskWriteTracker.Core.Interop;

internal enum AtaTransport
{
    /// <summary>IOCTL_STORAGE_PREDICT_FAILURE —— 非提权可用，SATA 首选。</summary>
    PredictFailure,

    /// <summary>SMART_RCV_DRIVE_DATA —— 需要句柄带写权限（通常等同管理员）。</summary>
    SmartRcvDriveData
}

/// <summary>
/// 通道 B：SATA SMART 属性表。
/// 优先 IOCTL_STORAGE_PREDICT_FAILURE(0x002D1100)：返回 512 字节厂商区，恰好就是属性页，且**无需管理员**。
/// 失败再退 SMART_RCV_DRIVE_DATA(0x07C088)，该通道需要 GENERIC_WRITE。
/// </summary>
internal static class AtaSmart
{
    private const uint IOCTL_STORAGE_PREDICT_FAILURE = 0x002D1100;
    private const uint IOCTL_SMART_RCV_DRIVE_DATA = 0x07C088;

    private const byte SMART_CMD = 0xB0;
    private const byte SMART_READ_DATA = 0xD0;

    private const int PredictFailureBufferSize = 516; // ULONG PredictFailure + UCHAR VendorSpecific[512]
    private const int PredictFailureDataOffset = 4;

    private const int SendCmdInParamsSize = 36;
    private const int SendCmdOutParamsSize = 528;
    private const int SmartRcvDataOffset = 16;

    internal static bool TryReadAttributes(SafeFileHandle handle, out AtaAttributeTable table, out AtaTransport transport, out int win32Error)
    {
        if (TryReadViaPredictFailure(handle, out table, out win32Error))
        {
            transport = AtaTransport.PredictFailure;
            return true;
        }

        if (TryReadViaSmartRcv(handle, out table, out win32Error))
        {
            transport = AtaTransport.SmartRcvDriveData;
            return true;
        }

        transport = AtaTransport.PredictFailure;
        return false;
    }

    private static bool TryReadViaPredictFailure(SafeFileHandle handle, out AtaAttributeTable table, out int win32Error)
    {
        table = AtaAttributeTable.Empty;
        var output = new byte[PredictFailureBufferSize];

        if (!NativeMethods.DeviceIoControl(handle, IOCTL_STORAGE_PREDICT_FAILURE,
                null, 0, output, output.Length, out int returned, IntPtr.Zero))
        {
            win32Error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }

        win32Error = 0;
        if (returned < PredictFailureDataOffset + 362)
            return false;

        table = AtaAttributeTable.Parse(output, PredictFailureDataOffset);
        return table.Count > 0;
    }

    private static bool TryReadViaSmartRcv(SafeFileHandle handle, out AtaAttributeTable table, out int win32Error)
    {
        table = AtaAttributeTable.Empty;

        var input = new byte[SendCmdInParamsSize];
        BitConverter.GetBytes(512u).CopyTo(input, 0); // cBufferSize

        input[4] = SMART_READ_DATA; // bFeaturesReg
        input[5] = 0x01;            // bSectorCountReg
        input[6] = 0x01;            // bSectorNumberReg
        input[7] = 0x4F;            // bCylLowReg
        input[8] = 0xC2;            // bCylHighReg
        input[9] = 0xA0;            // bDriveHeadReg
        input[10] = SMART_CMD;      // bCommandReg
        input[12] = 0;              // bDriveNumber

        var output = new byte[SendCmdOutParamsSize];

        if (!NativeMethods.DeviceIoControl(handle, IOCTL_SMART_RCV_DRIVE_DATA,
                input, input.Length, output, output.Length, out int returned, IntPtr.Zero))
        {
            win32Error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }

        win32Error = 0;
        if (returned < SmartRcvDataOffset + 362)
            return false;

        if (output[4] != 0) // bDriverError
            return false;

        table = AtaAttributeTable.Parse(output, SmartRcvDataOffset);
        return table.Count > 0;
    }
}

/// <summary>ATA SMART 属性表中的一条记录。</summary>
internal readonly record struct AtaAttribute(byte Id, ushort Flags, byte Value, byte Worst, ulong Raw)
{
    /// <summary>原始值低 8 位，用于以字节表示的属性（温度、寿命百分比等）。</summary>
    internal byte RawLowByte => (byte)(Raw & 0xFF);
}

/// <summary>SMART 属性页解析结果。</summary>
internal sealed class AtaAttributeTable
{
    internal static readonly AtaAttributeTable Empty = new(new Dictionary<byte, AtaAttribute>());

    private readonly Dictionary<byte, AtaAttribute> _attributes;

    private AtaAttributeTable(Dictionary<byte, AtaAttribute> attributes) => _attributes = attributes;

    internal int Count => _attributes.Count;

    internal bool TryGet(byte id, out AtaAttribute attribute) => _attributes.TryGetValue(id, out attribute);

    /// <summary>各厂商口径不同：Micron/Crucial 用 246，部分用 241，少数用 233。</summary>
    internal static readonly byte[] WrittenLbaAttributeIds = [246, 241, 233];

    internal bool TryGetWrittenLba(out byte attributeId, out ulong rawValue)
    {
        foreach (byte id in WrittenLbaAttributeIds)
        {
            if (_attributes.TryGetValue(id, out AtaAttribute attr))
            {
                attributeId = id;
                rawValue = attr.Raw;
                return true;
            }
        }

        attributeId = 0;
        rawValue = 0;
        return false;
    }

    internal static AtaAttributeTable Parse(byte[] buffer, int start)
    {
        var map = new Dictionary<byte, AtaAttribute>();

        // 前 2 字节为结构修订号，随后是 30 条 × 12 字节
        for (int i = 0; i < 30; i++)
        {
            int p = start + 2 + (i * 12);
            if (p + 12 > buffer.Length)
                break;

            byte id = buffer[p];
            if (id == 0)
                continue;

            ushort flags = BitConverter.ToUInt16(buffer, p + 1);
            byte value = buffer[p + 3];
            byte worst = buffer[p + 4];

            ulong raw = 0;
            for (int b = 5; b >= 0; b--)
                raw = (raw << 8) | buffer[p + 5 + b];

            map[id] = new AtaAttribute(id, flags, value, worst, raw);
        }

        return new AtaAttributeTable(map);
    }
}
