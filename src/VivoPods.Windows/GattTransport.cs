using VivoPods.Core.Models;
using VivoPods.Core.Protocol;
using VivoPods.Core.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace VivoPods.Windows;

public sealed class GattTransport : IPodTransport
{
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _write, _notify;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _closing;
    public string Label => "Bluetooth LE · GATT";
    public event Action<GaiaFrame>? FrameReceived;
    public event Action<Exception?>? Disconnected;
    public async Task ConnectAsync(PodDevice device, CancellationToken ct)
    {
        _device = await BluetoothLEDevice.FromIdAsync(device.Id).AsTask(ct)
            ?? throw new IOException("无法打开 BLE 设备，请先完成系统配对。");
        var services = await _device.GetGattServicesForUuidAsync(VivoProtocol.GattServiceUuid, BluetoothCacheMode.Uncached).AsTask(ct);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            throw new IOException("该设备未提供 vivo BLE 管理服务，请选择同名的经典蓝牙设备。");
        _service = services.Services[0];
        foreach (var extra in services.Services.Skip(1)) extra.Dispose();
        var chars = await _service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct);
        if (chars.Status != GattCommunicationStatus.Success) throw new IOException("读取 BLE 特征失败");
        _write = chars.Characteristics.FirstOrDefault(x => x.Uuid == VivoProtocol.GattWriteUuid);
        _notify = chars.Characteristics.FirstOrDefault(x => x.Uuid == VivoProtocol.GattNotifyUuid);
        if (_write == null || _notify == null || !_write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
            throw new IOException("BLE 管理特征不完整或不支持无响应写入");
        _notify.ValueChanged += OnValue;
        var result = await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
        if (result != GattCommunicationStatus.Success) throw new IOException("订阅 BLE 状态通知失败");
        _device.ConnectionStatusChanged += OnConnection;
    }
    private void OnConnection(BluetoothLEDevice sender, object args)
    {
        if (!_closing && sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) Disconnected?.Invoke(null);
    }
    private void OnValue(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (_closing) return;
        using var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var bytes = new byte[reader.UnconsumedBufferLength]; reader.ReadBytes(bytes);
        if (bytes.Length >= 4) FrameReceived?.Invoke(GaiaFrame.DecodeGatt(bytes));
    }
    public async Task SendAsync(GaiaFrame frame, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        await _writeGate.WaitAsync(timeout.Token);
        try
        {
            using var writer = new DataWriter(); writer.WriteBytes(frame.EncodeGatt());
            var result = await (_write ?? throw new IOException("BLE 连接已关闭"))
                .WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse).AsTask(timeout.Token);
            if (result != GattCommunicationStatus.Success) throw new IOException($"BLE 写入失败：{result}");
        }
        finally { _writeGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        _closing = true;
        await _writeGate.WaitAsync();
        try
        {
            if (_device != null) _device.ConnectionStatusChanged -= OnConnection;
            if (_notify != null) _notify.ValueChanged -= OnValue;
            _service?.Dispose(); _device?.Dispose(); _write = null;
        }
        finally { _writeGate.Release(); }
    }
}
