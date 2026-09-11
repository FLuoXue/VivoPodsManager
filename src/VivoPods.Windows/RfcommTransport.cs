using VivoPods.Core.Models;
using VivoPods.Core.Protocol;
using VivoPods.Core.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace VivoPods.Windows;

public sealed class RfcommTransport : IPodTransport
{
    private BluetoothDevice? _device;
    private RfcommDeviceService? _service;
    private StreamSocket? _socket;
    private DataReader? _reader;
    private DataWriter? _writer;
    private readonly CancellationTokenSource _life = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private Task? _readTask;
    public string Label => "Bluetooth · RFCOMM";
    public event Action<GaiaFrame>? FrameReceived;
    public event Action<Exception?>? Disconnected;

    public async Task ConnectAsync(PodDevice device, CancellationToken ct)
    {
        _device = await BluetoothDevice.FromBluetoothAddressAsync(device.Address).AsTask(ct)
            ?? throw new IOException("Windows 无法打开该蓝牙设备，请先在系统设置中配对。");
        var services = await _device.GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(VivoProtocol.RfcommUuid), BluetoothCacheMode.Uncached).AsTask(ct);
        if (services.Error != BluetoothError.Success || services.Services.Count == 0)
            throw new IOException($"未发现 vivo 私有 RFCOMM 服务（{services.Error}），请取出耳机并连接 Windows 蓝牙。");
        _service = services.Services[0];
        foreach (var extra in services.Services.Skip(1)) extra.Dispose();
        _socket = new StreamSocket();
        using var cancel = ct.Register(() => _socket?.Dispose());
        await _socket.ConnectAsync(_service.ConnectionHostName, _service.ConnectionServiceName).AsTask(ct);
        ct.ThrowIfCancellationRequested();
        _reader = new DataReader(_socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };
        _writer = new DataWriter(_socket.OutputStream);
        _readTask = ReadAsync(_life.Token);
    }
    private async Task ReadAsync(CancellationToken ct)
    {
        var decoder = new GaiaDecoder();
        Exception? failure = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                uint size = await _reader!.LoadAsync(2048).AsTask(ct);
                if (size == 0) break;
                byte[] chunk = new byte[size]; _reader.ReadBytes(chunk);
                foreach (var frame in decoder.Feed(chunk)) FrameReceived?.Invoke(frame);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally { if (!ct.IsCancellationRequested) Disconnected?.Invoke(failure); }
    }
    public async Task SendAsync(GaiaFrame frame, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _life.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        await _write.WaitAsync(timeout.Token);
        try
        {
            var writer = _writer ?? throw new IOException("蓝牙通道已关闭");
            writer.WriteBytes(frame.Encode());
            await writer.StoreAsync().AsTask(timeout.Token);
        }
        finally { _write.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        _life.Cancel(); _socket?.Dispose();
        if (_readTask != null) await _readTask;
        await _write.WaitAsync();
        try
        {
            _reader?.DetachStream(); _reader?.Dispose();
            _writer?.DetachStream(); _writer?.Dispose();
            _service?.Dispose(); _device?.Dispose();
            _reader = null; _writer = null; _socket = null;
        }
        finally { _write.Release(); }
    }
}
