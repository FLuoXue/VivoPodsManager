using System.Text;
using VivoPods.Core.Models;
using VivoPods.Core.Protocol;

namespace VivoPods.Core.Transport;

/// <summary>Explicit demo only. Replies go through the same frame decoder and state reducer as real hardware.</summary>
public sealed class DemoTransport : IPodTransport
{
    public string Label => "演示设备 · 不连接蓝牙";
    public event Action<GaiaFrame>? FrameReceived;
    public event Action<Exception?>? Disconnected;
    private byte _noise, _eq, _leftTap = 1, _rightTap = 0x13, _leftCycle = 11, _rightCycle = 11;
    private byte _wear = 1, _game, _spatial;
    private bool _connected;
    public bool SuppressReplies { get; set; }
    public Task ConnectAsync(PodDevice device, CancellationToken ct) { ct.ThrowIfCancellationRequested(); _connected = true; return Task.CompletedTask; }
    public async Task SendAsync(GaiaFrame f, CancellationToken ct)
    {
        if (!_connected) throw new IOException("演示会话未连接");
        await Task.Delay(15, ct);
        if (SuppressReplies) return;
        byte value = f.Payload.FirstOrDefault();
        byte[]? reply = f.Command switch
        {
            0x0300 => [0], 0x0207 => [0, 86, 92, 64, 4], 0x020D => [0, 12],
            0x021C => [0, 1, 2, 3, 4, 11, 1, 12], 0x021B => [0, 192],
            0x0230 => [0, _noise, 3, 1], 0x0130 => [0, _noise = value, 3, 1],
            0x0218 => [0, _eq], 0x0118 => [0, _eq = value],
            0x0202 => [0, _leftTap, _rightTap], 0x0102 => SetTap(value),
            0x0231 => [0, 5, _leftCycle, _rightCycle], 0x0131 => SetCycle(f.Payload),
            0x0203 => [0, _wear], 0x0103 => [0, _wear = value],
            0x0251 => [0, _game], 0x0151 => [0, _game = value],
            0x0239 => [0, _spatial], 0x0139 => [0, _spatial = value],
            0x0120 => [0, value], 0x0249 => PeerList(),
            0x014A => SetPeers(f.Payload), 0x014C => SetDual(f.Payload),
            _ => null
        };
        if (reply != null)
        {
            var decoder = new GaiaDecoder();
            foreach (var frame in decoder.Feed(new GaiaFrame(f.Version, f.Vendor, (ushort)(f.Command | 0x8000), reply).Encode()))
                FrameReceived?.Invoke(frame);
        }
    }
    private byte _pcState = 2, _phoneState = 1;
    private byte _dualState = 1;
    private byte[] SetDual(byte[] values) { _dualState = values[^1]; return [0]; }
    private byte[] SetTap(byte value) { if (value < 16) _leftTap = value; else _rightTap = value; return [0]; }
    private byte[] SetCycle(byte[] values) { _leftCycle = values[1]; _rightCycle = values[2]; return [0, .. values]; }
    private byte[] SetPeers(byte[] values) { _pcState = values[6]; _phoneState = values[13]; return [0]; }
    private byte[] PeerList()
    {
        byte[] pc = Encoding.UTF8.GetBytes("这台电脑"), phone = Encoding.UTF8.GetBytes("vivo X200");
        return [_dualState, 2, 0xAA, 0xBB, 0xCC, 0x11, 0x22, 0x33, 0, 0, 0, _pcState, (byte)pc.Length, .. pc,
            0xAA, 0xBB, 0xCC, 0x44, 0x55, 0x66, 0, 0, 0, _phoneState, (byte)phone.Length, .. phone];
    }
    public void Drop() { _connected = false; Disconnected?.Invoke(null); }
    public ValueTask DisposeAsync() { _connected = false; return ValueTask.CompletedTask; }
}
