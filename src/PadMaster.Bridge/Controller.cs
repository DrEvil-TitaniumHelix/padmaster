using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace PadMaster;

public static class Controller
{
    const uint Hid = 2, DeviceInfo = 0x2000000b, DeviceName = 0x20000007;

    public static JsonObject Read()
    {
        var xinput = new JsonArray(); var hid = new JsonArray();
        for (uint slot = 0; slot < 4; slot++)
        {
            var status = XInputGetState(slot, out _);
            if (status is not (0 or 1167)) throw new Win32Exception((int)status, $"XInputGetState failed for slot {slot}");
            var row = new JsonObject { ["slot"] = slot, ["connected"] = status == 0 };
            if (status == 0 && XInputGetBatteryInformation(slot, 0, out var battery) == 0)
                row["battery"] = new JsonObject { ["type"] = battery.Type, ["level"] = battery.Type is 2 or 3 ? JsonValue.Create(battery.Level) : null };
            xinput.Add(row);
        }
        foreach (var device in Devices())
        {
            if (device.Type != Hid) continue;
            var size = (uint)Marshal.SizeOf<RawDeviceInfo>(); var info = new RawDeviceInfo { Size = size };
            if (GetRawInputDeviceInfoW(device.Handle, DeviceInfo, ref info, ref size) == uint.MaxValue || info.Type != Hid || info.UsagePage != 1 || info.Usage is not (4 or 5)) continue;
            var path = Path(device.Handle); if (path == null) continue;
            hid.Add(new JsonObject { ["name"] = Product(path), ["vid"] = ((ushort)info.Vendor).ToString("X4", CultureInfo.InvariantCulture), ["pid"] = ((ushort)info.Product).ToString("X4", CultureInfo.InvariantCulture), ["path"] = path });
        }
        return new JsonObject { ["xinput"] = xinput, ["hid"] = hid };
    }

    static RawDevice[] Devices()
    {
        var size = (uint)Marshal.SizeOf<RawDevice>();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint count = 0;
            if (GetRawInputDeviceList(null, ref count, size) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetRawInputDeviceList failed");
            if (count == 0) return [];
            var devices = new RawDevice[checked((int)count)];
            var copied = GetRawInputDeviceList(devices, ref count, size);
            if (copied != uint.MaxValue) return devices[..checked((int)copied)];
            var error = Marshal.GetLastWin32Error();
            if (error != 122) throw new Win32Exception(error, "GetRawInputDeviceList failed");
        }
        throw new InvalidOperationException("Raw input devices kept changing during enumeration; try again.");
    }

    static string? Path(nint device)
    {
        uint size = 0;
        if (GetRawInputDeviceInfoW(device, DeviceName, 0, ref size) == uint.MaxValue || size == 0) return null;
        var path = new StringBuilder(checked((int)size));
        return GetRawInputDeviceInfoW(device, DeviceName, path, ref size) == uint.MaxValue || path.Length == 0 ? null : path.ToString();
    }

    static string? Product(string path)
    {
        using var handle = CreateFileW(path, 0, 3, 0, 3, 0, 0);
        if (handle.IsInvalid) return null;
        var name = new StringBuilder(256);
        return !HidD_GetProductString(handle, name, (uint)(name.Capacity * sizeof(char))) || string.IsNullOrWhiteSpace(name.ToString()) ? null : name.ToString();
    }

    [StructLayout(LayoutKind.Sequential, Size = 16)]
    struct XInputState { public uint Packet; }
    [StructLayout(LayoutKind.Sequential)]
    struct Battery { public byte Type, Level; }
    [StructLayout(LayoutKind.Sequential)]
    struct RawDevice { public nint Handle; public uint Type; }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    struct RawDeviceInfo
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public uint Vendor;
        [FieldOffset(12)] public uint Product;
        [FieldOffset(20)] public ushort UsagePage;
        [FieldOffset(22)] public ushort Usage;
    }

    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    static extern uint XInputGetState(uint slot, out XInputState state);
    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    static extern uint XInputGetBatteryInformation(uint slot, byte deviceType, out Battery battery);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    static extern uint GetRawInputDeviceList([Out] RawDevice[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    static extern uint GetRawInputDeviceInfoW(nint device, uint command, ref RawDeviceInfo info, ref uint size);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    static extern uint GetRawInputDeviceInfoW(nint device, uint command, nint data, ref uint size);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetRawInputDeviceInfoW(nint device, uint command, StringBuilder data, ref uint size);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("hid.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetProductString(SafeFileHandle device, StringBuilder buffer, uint length);
}
