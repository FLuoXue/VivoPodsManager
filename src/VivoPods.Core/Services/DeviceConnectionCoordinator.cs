using VivoPods.Core.Models;
using VivoPods.Core.Transport;

namespace VivoPods.Core.Services;

/// <summary>Serializes discovery, automatic attachment, user selection, and intentional disconnection.</summary>
public sealed class DeviceConnectionCoordinator(IDeviceDiscovery discovery, PodManager manager,
    Func<PodDevice, DeviceProfile?>? profileResolver = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = [];
    private CancellationTokenSource? _attempt;
    private int _pauseGeneration;
    private bool _disposed;
    public IReadOnlyList<DiscoveredDevice> Devices { get; private set; } = [];
    public bool Automatic { get; set; } = true;
    public bool Paused { get; private set; }
    public bool IsScanning { get; private set; }
    public bool IsConnecting { get; private set; }
    public string? PreferredKey { get; set; }
    public string? PreferredEndpointId { get; set; }
    public string? LastError { get; private set; }
    public event Action? Changed;
    public event Action<PodDevice>? Connected;
    public event Action<string>? Log;

    public Task RefreshAsync(CancellationToken ct = default) => RunAsync(async token =>
    {
        if (!await DiscoverAsync(token)) return;
        if (manager.Device?.Transport == TransportKind.Demo) return;
        // A successful fresh Windows snapshot is authoritative; discovery failures never tear down a session.
        if (manager.Ready && manager.Device is { } current &&
            !Devices.Any(device => device.Key == DiscoveredDevice.KeyFor(current) && device.IsConnected))
            await manager.DisconnectAsync();
        if (Automatic && !Paused && !manager.Ready) await AttachAsync(null, false, token);
    }, ct);

    public Task RetryAsync(CancellationToken ct = default)
    {
        Paused = false;
        return RunAsync(async token =>
        {
            _retryAfter.Clear();
            if (manager.Device?.Transport == TransportKind.Demo) await manager.DisconnectAsync(clearDevice: true);
            if (await DiscoverAsync(token) && !manager.Ready) await AttachAsync(null, true, token);
        }, ct);
    }

    public Task SelectAsync(string key, CancellationToken ct = default)
    {
        Paused = false;
        return RunAsync(async token =>
        {
            if (!await DiscoverAsync(token)) return;
            if (manager.Ready && manager.Device is { } current && DiscoveredDevice.KeyFor(current) == key) return;
            await AttachAsync(key, true, token);
        }, ct);
    }

    public void Pause()
    {
        Interlocked.Increment(ref _pauseGeneration);
        Paused = true;
        try { _attempt?.Cancel(); } catch (ObjectDisposedException) { }
        Changed?.Invoke();
    }
    public void Resume()
    {
        Paused = false; LastError = null;
        Changed?.Invoke();
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        Pause();
        return RunAsync(async _ => { await manager.DisconnectAsync(); LastError = null; }, ct);
    }

    public Task DemoAsync(string model = "vivo TWS 5", CancellationToken ct = default)
    {
        Pause();
        return RunAsync(async token =>
        {
            await manager.ConnectAsync(new("demo", model, 0, true, TransportKind.Demo), DeviceProfile.Resolve(model), token);
            LastError = null;
        }, ct);
    }

    private async Task<bool> DiscoverAsync(CancellationToken ct)
    {
        IsScanning = true; Changed?.Invoke();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var found = DiscoveredDevice.Group(await discovery.FindAsync(timeout.Token));
            var previousOnline = Devices.SelectMany(device => device.Endpoints).Where(device => device.IsConnected)
                .Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var endpoint in found.SelectMany(device => device.Endpoints).Where(device => device.IsConnected))
                if (!previousOnline.Contains(endpoint.Id)) _retryAfter.Remove(endpoint.Id);
            Devices = found;
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastError = "暂时无法读取蓝牙设备，请检查 Windows 蓝牙是否开启。";
            Log?.Invoke($"Discovery: {ex}");
            return false;
        }
        finally { IsScanning = false; Changed?.Invoke(); }
    }

    private async Task AttachAsync(string? selectedKey, bool force, CancellationToken ct)
    {
        var candidates = Devices.Where(device => device.IsConnected && (selectedKey == null || device.Key == selectedKey))
            .OrderByDescending(device => device.Key == PreferredKey || device.Endpoints.Any(endpoint => endpoint.Id == PreferredEndpointId))
            .ToArray();
        if (candidates.Length == 0)
        {
            if (force) LastError = "请先在 Windows 蓝牙设置中连接耳机，程序会自动识别。";
            return;
        }
        foreach (var device in candidates)
        {
            foreach (var endpoint in device.Endpoints.Where(endpoint => endpoint.IsConnected).OrderBy(endpoint => endpoint.Transport))
            {
                ct.ThrowIfCancellationRequested();
                if (Paused) return;
                if (!force && _retryAfter.TryGetValue(endpoint.Id, out var retryAt) && retryAt > DateTimeOffset.UtcNow) continue;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                int pauseGeneration = Volatile.Read(ref _pauseGeneration);
                attempt.CancelAfter(TimeSpan.FromSeconds(8));
                _attempt = attempt;
                IsConnecting = true; Changed?.Invoke();
                try
                {
                    await manager.ConnectAsync(endpoint, profileResolver?.Invoke(endpoint), attempt.Token);
                    if (pauseGeneration != Volatile.Read(ref _pauseGeneration)) { await manager.DisconnectAsync(); return; }
                    PreferredKey = device.Key; PreferredEndpointId = endpoint.Id;
                    LastError = null; _retryAfter.Remove(endpoint.Id);
                    Connected?.Invoke(endpoint);
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (Paused || pauseGeneration != Volatile.Read(ref _pauseGeneration)) return;
                    _retryAfter[endpoint.Id] = DateTimeOffset.UtcNow.AddSeconds(30);
                    LastError = $"暂时无法连接 {device.Name}，稍后会重试；也可以点击重新连接。";
                    Log?.Invoke($"Connect {endpoint.Id}: {ex.Message}");
                }
                finally { _attempt = null; IsConnecting = false; Changed?.Invoke(); }
            }
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        if (_disposed) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _gate.WaitAsync(linked.Token);
        try { if (!_disposed) await operation(linked.Token); }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; Pause(); _lifetime.Cancel();
        await _gate.WaitAsync();
        _gate.Release();
        _lifetime.Dispose();
    }
}
