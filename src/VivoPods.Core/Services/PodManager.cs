using VivoPods.Core.Models;
using VivoPods.Core.Protocol;
using VivoPods.Core.Transport;

namespace VivoPods.Core.Services;

public enum ConnectionPhase { Disconnected, Connecting, Handshaking, Ready }

/// <summary>One session per selected address. Transport events from superseded sessions are ignored.</summary>
public sealed class PodManager(Func<PodDevice, IPodTransport> factory) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _command = new(1, 1);
    private IPodTransport? _transport;
    private CancellationTokenSource? _session;
    private Task? _pollTask;
    private CancellationTokenSource? _ringTimer;
    private Task? _ringStopTask;
    private event Action<GaiaFrame>? Received;
    public PodState State { get; private set; } = new();
    public PodDevice? Device { get; private set; }
    public DeviceProfile Profile { get; private set; } = DeviceProfile.Resolve("未知型号");
    public ConnectionPhase Phase { get; private set; }
    public bool Ready => Phase == ConnectionPhase.Ready;
    public string TransportLabel => _transport?.Label ?? "—";
    public event Action? Changed;
    public event Action<string>? Log;
    public bool Experimental { get; set; }

    public async Task ConnectAsync(PodDevice device, DeviceProfile? profile = null, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            await CloseCoreAsync();
            Device = device; Profile = profile ?? DeviceProfile.Resolve(device.Name); State = new();
            _session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            CancellationToken token = _session.Token;
            var transport = factory(device);
            _transport = transport;
            transport.FrameReceived += frame =>
            {
                if (!ReferenceEquals(_transport, transport)) return;
                Log?.Invoke($"RX {frame.Version} {frame.Vendor:X4}:{frame.Command:X4} {Convert.ToHexString(frame.Payload)}");
                State = State.Apply(frame);
                Received?.Invoke(frame);
                Changed?.Invoke();
                if (frame.Vendor == VivoProtocol.Vendor && frame.Command == 0x8509)
                    _ = ReplyTimeAsync(token);
            };
            transport.Disconnected += error =>
            {
                if (!ReferenceEquals(_transport, transport)) return;
                _session?.Cancel();
                Phase = ConnectionPhase.Disconnected;
                State = State.Disconnected();
                Log?.Invoke(error?.Message ?? "耳机已断开");
                Changed?.Invoke();
            };
            SetPhase(ConnectionPhase.Connecting);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                await transport.ConnectAsync(device, timeout.Token);
            }
            SetPhase(ConnectionPhase.Handshaking);
            var handshake = await ExchangeAsync(VivoProtocol.Handshake(),
                f => f.Vendor == VivoProtocol.GaiaVendor && f.Command == 0x8300, token);
            if (handshake.Payload.Length == 0 || handshake.Payload[0] != 0)
                throw new IOException("耳机拒绝了 GAIA 握手，请关闭手机端耳机管理应用后重试。");
            // Keep initialization ordered. Registration commands deliberately always use GAIA v4.
            foreach (var query in VivoProtocol.InitialQueries(Profile, Experimental))
            {
                await SendAsync(query, token);
                await Task.Delay(100, token);
            }
            token.ThrowIfCancellationRequested();
            SetPhase(ConnectionPhase.Ready);
            _pollTask = PollAsync(token);
        }
        catch
        {
            await CloseCoreAsync();
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!Ready) throw new InvalidOperationException("请先连接耳机");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _session!.Token);
        await _command.WaitAsync(linked.Token);
        try
        {
            await SendAsync(VivoProtocol.Battery(), linked.Token);
            await SendAsync(VivoProtocol.Command(Profile, 0x020D), linked.Token);
            if (Profile.Has(Features.Noise)) await SendAsync(VivoProtocol.Command(Profile, 0x0230, Profile.NoiseQuery), linked.Token);
            if (Profile.Has(Features.Dual) && !Profile.DualAlwaysOn) await SendAsync(VivoProtocol.Command(Profile, 0x0249), linked.Token);
        }
        finally { _command.Release(); }
    }

    public async Task ChangeAsync(GaiaFrame set, GaiaFrame? query, Func<PodState, bool> confirmed, CancellationToken ct = default)
    {
        if (!Ready) throw new InvalidOperationException("请先连接耳机");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _session!.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _command.WaitAsync(timeout.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(GaiaFrame frame)
        {
            if (frame.Vendor != VivoProtocol.Vendor) return;
            if (frame.Command == (set.Command | 0x8000) && frame.Payload.Length > 0 && frame.Payload[0] != 0)
                completion.TrySetException(new IOException($"耳机拒绝设置（状态码 {frame.Payload[0]}）"));
            bool relevant = frame.Command == (set.Command | 0x8000) || query != null && frame.Command == (query.Command | 0x8000);
            // A status-only ACK must not succeed by matching values left over from an older report.
            if (relevant && confirmed(new PodState().Apply(frame))) completion.TrySetResult();
        }
        Received += OnFrame;
        try
        {
            await SendAsync(set, timeout.Token);
            if (query != null)
            {
                await Task.Delay(160, timeout.Token);
                await SendAsync(query, timeout.Token);
            }
            await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && _session?.IsCancellationRequested == false)
        { throw new TimeoutException("耳机未确认设置，请重试或检查该型号是否支持此功能。"); }
        finally { Received -= OnFrame; _command.Release(); }
    }

    public Task SetNoiseAsync(NoiseMode mode, AncLevel? level = null, CancellationToken ct = default)
    {
        if (level == null && Profile.SupportsAncLevels && State.NoiseLevel is (byte)AncLevel.Mild or (byte)AncLevel.Balanced)
            level = (AncLevel)State.NoiseLevel.Value;
        var command = VivoProtocol.Noise(Profile, mode, level);
        return ChangeAsync(command, VivoProtocol.Command(Profile, 0x0230, Profile.NoiseQuery),
            s => s.Noise == mode && (!Profile.SupportsAncLevels || mode != NoiseMode.Anc || s.NoiseLevel == command.Payload[1]), ct);
    }

    public async Task FindAsync(bool start)
    {
        _ringTimer?.Cancel();
        if (_ringStopTask != null) await _ringStopTask;
        _ringTimer?.Dispose(); _ringTimer = null; _ringStopTask = null;
        await ChangeAsync(VivoProtocol.Command(Profile, 0x0120, start ? (byte)1 : (byte)0), null, s => s.Finding == start);
        if (start)
        {
            _ringTimer = CancellationTokenSource.CreateLinkedTokenSource(_session!.Token);
            _ringStopTask = AutoStopRingingAsync(_ringTimer.Token);
        }
    }
    private async Task AutoStopRingingAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            await ChangeAsync(VivoProtocol.Command(Profile, 0x0120, 0), null, s => s.Finding == false, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log?.Invoke($"自动停止响铃失败：{ex.Message}"); }
    }

    public async Task AcknowledgeAsync(GaiaFrame command, CancellationToken ct = default)
    {
        if (!Ready) throw new InvalidOperationException("请先连接耳机");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _session!.Token);
        await _command.WaitAsync(linked.Token);
        try
        {
            var reply = await ExchangeAsync(command, f => f.Vendor == command.Vendor && f.Command == (command.Command | 0x8000), linked.Token);
            if (reply.Payload.Length == 0 || reply.Payload[0] != 0) throw new IOException("耳机拒绝该命令");
        }
        finally { _command.Release(); }
    }

    public async Task DisconnectAsync(bool clearDevice = false)
    {
        _session?.Cancel();
        await _lifecycle.WaitAsync();
        try
        {
            await CloseCoreAsync();
            if (clearDevice)
            {
                Device = null; Profile = DeviceProfile.Resolve("未知型号"); State = new();
                Changed?.Invoke();
            }
        }
        finally { _lifecycle.Release(); }
    }
    private async Task CloseCoreAsync()
    {
        _ringTimer?.Cancel();
        if (_ringStopTask != null) await _ringStopTask;
        _ringTimer?.Dispose(); _ringTimer = null; _ringStopTask = null;
        if (_transport != null && State.Finding == true)
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await _transport.SendAsync(VivoProtocol.Command(Profile, 0x0120, 0), stopTimeout.Token); }
            catch (Exception ex) { Log?.Invoke($"断开前停止响铃失败：{ex.Message}"); }
        }
        _session?.Cancel();
        var old = _transport; _transport = null;
        if (old != null) await old.DisposeAsync();
        if (_pollTask != null) { try { await _pollTask; } catch (OperationCanceledException) { } }
        _pollTask = null;
        _session?.Dispose(); _session = null;
        State = State.Disconnected();
        SetPhase(ConnectionPhase.Disconnected);
    }
    private async Task<GaiaFrame> ExchangeAsync(GaiaFrame frame, Func<GaiaFrame, bool> match, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<GaiaFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(GaiaFrame value) { if (match(value)) completion.TrySetResult(value); }
        Received += OnFrame;
        try
        {
            await SendAsync(frame, ct);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(4), ct);
        }
        catch (TimeoutException) { throw new TimeoutException(frame.Command == 0x0300
            ? "GAIA 握手超时：蓝牙通道未返回有效协议响应。" : "耳机未确认命令，请检查型号兼容性。"); }
        finally { Received -= OnFrame; }
    }
    private async Task SendAsync(GaiaFrame frame, CancellationToken ct)
    {
        var transport = _transport ?? throw new IOException("耳机连接已断开");
        Log?.Invoke($"TX {frame.Version} {frame.Vendor:X4}:{frame.Command:X4} {Convert.ToHexString(frame.Payload)}");
        await transport.SendAsync(frame, ct);
    }
    private async Task ReplyTimeAsync(CancellationToken ct)
    {
        try { await SendAsync(VivoProtocol.HostTime(Profile, DateTime.Now), ct); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { Log?.Invoke(ex.Message); }
    }
    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(25));
            while (await timer.WaitForNextTickAsync(ct)) await RefreshAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log?.Invoke(ex.Message);
            _session?.Cancel();
            State = State.Disconnected(); SetPhase(ConnectionPhase.Disconnected);
        }
    }
    private void SetPhase(ConnectionPhase phase) { Phase = phase; Changed?.Invoke(); }
    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
