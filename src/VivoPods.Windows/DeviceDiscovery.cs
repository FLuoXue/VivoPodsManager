using System.Runtime.InteropServices;
using VivoPods.Core.Models;
using VivoPods.Core.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace VivoPods.Windows;

public sealed class DeviceDiscovery : IDeviceDiscovery
{
    public string? LastWarning { get; private set; }
    public async Task<IReadOnlyList<PodDevice>> FindAsync(CancellationToken ct)
    {
        LastWarning = null;
        var devices = await Task.Run(FindClassic, ct);
        var paired = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
            null, DeviceInformationKind.AssociationEndpoint).AsTask(ct);
        foreach (var info in paired)
        {
            if (!DeviceProfile.IsFamily(info.Name)) continue;
            try
            {
                using var ble = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(ct);
                if (ble != null)
                    devices.Add(new(info.Id, info.Name, ble.BluetoothAddress,
                        ble.ConnectionStatus == BluetoothConnectionStatus.Connected, TransportKind.Gatt));
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
            { LastWarning = $"已跳过 Windows 中无效的 BLE 设备记录：{info.Name}"; }
        }
        return devices.OrderByDescending(d => d.IsConnected).ThenBy(d => d.Transport).ThenBy(d => d.Name).ToArray();
    }

    public static List<PodDevice> FindClassic()
    {
        var result = new List<PodDevice>();
        var search = new SearchParams { Size = (uint)Marshal.SizeOf<SearchParams>(), Authenticated = 1, Remembered = 1, Connected = 1 };
        var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>(), Name = "" };
        IntPtr handle = BluetoothFindFirstDevice(ref search, ref info);
        if (handle == IntPtr.Zero) return result;
        try
        {
            do
            {
                if (DeviceProfile.IsFamily(info.Name))
                    result.Add(new($"classic:{info.Address:X12}", info.Name, info.Address, info.Connected != 0));
                info = new() { Size = (uint)Marshal.SizeOf<DeviceInfo>(), Name = "" };
            } while (BluetoothFindNextDevice(handle, ref info));
        }
        finally { BluetoothFindDeviceClose(handle); }
        return result;
    }

    public static async Task<byte[]> LocalAddressAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter == null) throw new IOException("没有可用蓝牙适配器");
        return Convert.FromHexString(adapter.BluetoothAddress.ToString("X12"));
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceInfo
    {
        public uint Size;
        public ulong Address;
        public uint Class;
        public int Connected, Remembered, Authenticated;
        public SystemTime Seen, Used;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SearchParams
    {
        public uint Size;
        public int Authenticated, Remembered, Unknown, Connected, Inquiry;
        public byte Timeout;
        public IntPtr Radio;
    }
    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref SearchParams search, ref DeviceInfo info);
    [DllImport("bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindNextDevice(IntPtr handle, ref DeviceInfo info);
    [DllImport("bthprops.cpl")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BluetoothFindDeviceClose(IntPtr handle);
}
