using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace VivoPods.Windows;

/// <summary>Signals changes only; discovery supplies the authoritative device snapshot.</summary>
public sealed class BluetoothDeviceWatcher : IDisposable
{
    private readonly List<DeviceWatcher> _watchers = [];
    private volatile bool _disposed;
    public event Action? Changed;
    public event Action<string>? Warning;

    public void Start()
    {
        if (_disposed || _watchers.Count != 0) return;
        foreach (string selector in new[] { BluetoothDevice.GetDeviceSelectorFromPairingState(true), BluetoothLEDevice.GetDeviceSelectorFromPairingState(true) })
        {
            try
            {
                var watcher = DeviceInformation.CreateWatcher(selector, ["System.Devices.Aep.IsConnected"], DeviceInformationKind.AssociationEndpoint);
                watcher.Added += OnAdded;
                watcher.Removed += OnRemoved;
                watcher.Updated += OnUpdated;
                watcher.EnumerationCompleted += OnEnumerationCompleted;
                _watchers.Add(watcher);
                watcher.Start();
            }
            catch (Exception ex) { Warning?.Invoke($"蓝牙事件监听不可用，将定时检查设备：{ex.Message}"); }
        }
    }

    private void Signal() { if (!_disposed) Changed?.Invoke(); }
    private void OnAdded(DeviceWatcher sender, DeviceInformation args) => Signal();
    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate args) => Signal();
    private void OnEnumerationCompleted(DeviceWatcher sender, object args) => Signal();
    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        if (args.Properties.ContainsKey("System.Devices.Aep.IsConnected") || args.Properties.ContainsKey("System.ItemNameDisplay")) Signal();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var watcher in _watchers)
        {
            watcher.Added -= OnAdded; watcher.Removed -= OnRemoved; watcher.Updated -= OnUpdated;
            watcher.EnumerationCompleted -= OnEnumerationCompleted;
            try { watcher.Stop(); } catch (Exception ex) { Warning?.Invoke(ex.Message); }
        }
        _watchers.Clear();
    }
}
