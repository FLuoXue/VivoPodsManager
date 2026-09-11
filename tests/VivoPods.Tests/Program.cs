using VivoPods.Core.Models;
using VivoPods.Core.Protocol;
using VivoPods.Core.Services;
using VivoPods.Core.Transport;
using VivoPods.Windows;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name); passed++;
}
void Hex(GaiaFrame frame, string expected, string name) => Check(Convert.ToHexString(frame.Encode()) == expected, name);
if (args.Contains("--probe"))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var devices = await new DeviceDiscovery().FindAsync(timeout.Token);
    foreach (var device in devices) Console.WriteLine($"{device.Name} | {device.AddressText} | {device.Transport} | connected={device.IsConnected}");
    if (args.Contains("--connect"))
    {
        var device = devices.FirstOrDefault(d => d.IsConnected && d.Transport == TransportKind.Rfcomm);
        if (device == null) { Console.WriteLine("NO_CONNECTED_DEVICE: 请在 Windows 蓝牙设置中连接耳机后再验证。"); return 2; }
        await using var manager = new PodManager(d => d.Transport == TransportKind.Gatt ? new GattTransport() : new RfcommTransport());
        manager.Log += Console.WriteLine;
        await manager.ConnectAsync(device, ct: timeout.Token);
        await Task.Delay(1500, timeout.Token);
        Console.WriteLine($"REAL STATE: {device.Name}: L={manager.State.Left.Text} R={manager.State.Right.Text} C={manager.State.Case.Text}; noise={manager.State.Noise}; fw={manager.State.Firmware}");
        Check(!manager.State.Left.Stale || !manager.State.Right.Stale, "real battery response");
    }
    return 0;
}

var air = DeviceProfile.Resolve("vivo TWS Air3 Pro");
var three = DeviceProfile.Resolve("vivo TWS 3e");
var five = DeviceProfile.Resolve("vivo TWS 5");
Hex(VivoProtocol.Handshake(), "FF040000000A0300", "captured GAIA handshake");
Hex(VivoProtocol.Battery(), "FF040000001B0207", "battery always v4");
Hex(VivoProtocol.Noise(air, NoiseMode.Anc), "FF030003001B0130000400", "Air3 Pro ANC captured command");
Hex(VivoProtocol.Noise(air, NoiseMode.Off), "FF030003001B0130010400", "Air3 Pro OFF byte mapping");
Hex(VivoProtocol.Noise(three, NoiseMode.Transparency), "FF030002001B01300203", "TWS 3e transparency command");
Hex(VivoProtocol.Noise(five, NoiseMode.Anc), "FF040003001B0130000301", "TWS 5 profile command");
Check(!VivoProtocol.InitialQueries(three, true).Any(f => f.Command == 0x0249), "forced dual does not issue unsupported query");
Check(VivoProtocol.InitialQueries(air, false).Take(5).All(f => f.Version == 4), "notification chain uses v4");
Check(VivoProtocol.InitialQueries(five, false).First().Command == 0x0207, "TWS 5 skips registration chain");
Check(!DeviceProfile.Resolve("iQOO TWS Air").Has(Features.Noise), "non ANC model capability");
Check(!DeviceProfile.Resolve("unknown").Known, "unknown model identified explicitly");
Check(DeviceProfile.Resolve("vivo TWS Air200").Name == "vivo TWS Air2", "model alias");

byte[] captured = Convert.FromHexString("FF040005001B82070056644005");
for (int split = 0; split <= captured.Length; split++)
{
    var decoder = new GaiaDecoder();
    var frames = decoder.Feed(captured.AsSpan(0, split)).Concat(decoder.Feed(captured.AsSpan(split))).ToArray();
    Check(frames.Length == 1 && frames[0].Payload.SequenceEqual(new byte[] { 0, 86, 100, 64, 5 }), "fragment split " + split);
}
var glued = new GaiaDecoder().Feed([1, 2, 3, .. captured, .. captured]);
Check(glued.Count == 2, "noise and concatenation");
var checkedFrame = new GaiaFrame(3, 27, 0x820D, [0, 12], 1).Encode();
checkedFrame[^1] ^= 1;
Check(new GaiaDecoder().Feed([.. checkedFrame, .. captured]).Count == 1, "bad checksum resynchronization");
var extended = new GaiaFrame(4, 27, 0x8224, Enumerable.Repeat((byte)42, 512).ToArray(), 3);
Check(new GaiaDecoder().Feed(extended.Encode()).Single().Payload.Length == 512, "extended length and checksum");
Check(GaiaFrame.DecodeGatt(Convert.FromHexString("001B82070056644005")).Payload.Length == 5, "GATT naked frame");
Check(Convert.ToHexString(VivoProtocol.Battery().EncodeGatt()) == "001B0207", "GATT has no GAIA header");
var state = new PodState().Apply(new GaiaDecoder().Feed(captured).Single());
Check(state.Left.Level == 86 && state.Right.Level == 100 && state.Case.Level == 64 && state.Left.Charging && state.Case.Charging, "battery and charge flags");
state = state.Apply(new(4, 27, 0x8207, [0, 255, 90, 255, 0]));
Check(state.Left.Level == 86 && state.Left.Stale && state.Right.Level == 90 && !state.Right.Stale, "invalid battery preserves stale reading");
Check(state.Disconnected().Right.Stale, "disconnect marks battery stale");
state = state.Apply(new(3, 27, 0x820D, [0, 12]));
Check(state.LeftWear == "佩戴中" && state.RightWear == "佩戴中", "wear bits 2 and 3");
state = state.Apply(new(3, 27, 0x820D, [0, 3]));
Check(state.LeftWear == "已入盒" && state.RightWear == "已入盒", "in case bits 0 and 1");
Check(ReferenceEquals(state, state.Apply(new(3, 10, 0x8207, [0, 1, 2, 3, 0]))), "reject foreign vendor");
Check(ReferenceEquals(state, state.Apply(new(3, 27, 0x8230, [1, 0]))), "reject failed status");
Check(state.Apply(new(3, 27, 0x8203, [0])).WearDetection == null, "registration ACK not wear switch");
Check(state.Apply(new(3, 27, 0x821C, [0, 1, 2, 3, 4, 5, 9, 12])).Firmware == "2.5.9", "binary firmware capture");
Check(ReferenceEquals(state, state.Apply(new(4, 27, 0x8249, [1, 1, 0, 0]))), "truncated peer table is rejected atomically");
Hex(VivoProtocol.HostTime(air, new(2026, 8, 6, 11, 12, 29)), "FF030007001B0509141A08060B0C1D", "time sync binary not BCD");
var table = VivoProtocol.MultiDeviceTable(five, [new("AABBCC112233", "PC", 2), new("AABBCC445566", "Phone", 0)]);
Check(Convert.ToHexString(table.Payload) == "AABBCC11223302AABBCC44556600", "peer table preserves active state");

DemoTransport? demo = null;
await using (var manager = new PodManager(_ => demo = new DemoTransport()))
{
    await manager.ConnectAsync(new("demo", "vivo TWS 5", 0, true, TransportKind.Demo));
    Check(manager.Ready && manager.State.Left.Level == 86 && manager.State.Peers.Count == 2, "session handshake, init, battery, peer list");
    await manager.SetNoiseAsync(NoiseMode.Transparency);
    Check(manager.State.Noise == NoiseMode.Transparency, "set noise and confirmed readback");
    await manager.ChangeAsync(VivoProtocol.Command(five, 0x0102, 0x12), VivoProtocol.Command(five, 0x0202), s => s.RightTap == 0x12);
    Check(manager.State.RightTap == 0x12 && manager.State.LeftTap == 1, "right gesture set preserves left gesture");
    await manager.ChangeAsync(VivoProtocol.Command(five, 0x0118, 2), VivoProtocol.Command(five, 0x0218), s => s.Eq == 2);
    Check(manager.State.Eq == 2, "EQ setting confirmed");
    await manager.AcknowledgeAsync(VivoProtocol.Command(five, 0x014C, [0xAA, 0xBB, 0xCC, 0x11, 0x22, 0x33, 0]));
    await manager.RefreshAsync();
    Check(manager.State.MultiStatus == 0, "dual connection acknowledged and re-read");
    await manager.FindAsync(true);
    Check(manager.State.Finding == true, "find starts only on confirmed ACK");
    await manager.FindAsync(false);
    Check(manager.State.Finding == false, "find stop cancels automatic stop timer");
    demo!.SuppressReplies = true;
    bool timeout = false;
    try { await manager.SetNoiseAsync(NoiseMode.Off); } catch (TimeoutException) { timeout = true; }
    Check(timeout && manager.State.Noise == NoiseMode.Transparency, "timeout does not claim success or optimistically mutate state");
    demo.Drop();
    Check(!manager.Ready && manager.State.Left.Stale && manager.State.Noise == null, "transport disconnect invalidates state");
    await manager.ConnectAsync(new("demo2", "vivo TWS 3e", 1, true, TransportKind.Demo));
    Check(manager.Ready && manager.Profile.DualAlwaysOn, "session can reconnect to another model");
}
await using (var manager = new PodManager(_ => new SilentTransport()))
{
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    bool cancelled = false;
    try { await manager.ConnectAsync(new("silent", "vivo TWS 5", 0, true), ct: cancel.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && !manager.Ready, "cancelling a silent handshake cleans up session");
}
await using (var manager = new PodManager(_ => new SilentTransport(reject: true)))
{
    bool rejected = false;
    try { await manager.ConnectAsync(new("reject", "vivo TWS 5", 0, true)); }
    catch (IOException) { rejected = true; }
    Check(rejected && !manager.Ready, "rejected handshake never claims connected");
}
Console.WriteLine($"\n{passed} checks passed.");
return 0;

sealed class SilentTransport(bool reject = false) : IPodTransport
{
    public event Action<GaiaFrame>? FrameReceived;
    public event Action<Exception?>? Disconnected { add { } remove { } }
    public string Label => "test";
    public Task ConnectAsync(PodDevice device, CancellationToken ct) => Task.CompletedTask;
    public Task SendAsync(GaiaFrame f, CancellationToken ct)
    {
        if (reject) FrameReceived?.Invoke(new(4, 10, 0x8300, [1]));
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
