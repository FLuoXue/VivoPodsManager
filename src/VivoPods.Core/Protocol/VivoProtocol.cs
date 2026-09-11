using VivoPods.Core.Models;

namespace VivoPods.Core.Protocol;

public static class VivoProtocol
{
    public const ushort Vendor = 0x001B, GaiaVendor = 0x000A;
    public static readonly Guid RfcommUuid = new("00000837-d102-11e1-9b23-00025b00a5a5");
    public static readonly Guid GattServiceUuid = new("00001100-d102-11e1-9b23-00025b00a5a5");
    public static readonly Guid GattWriteUuid = new("00001101-d102-11e1-9b23-00025b00a5a5");
    public static readonly Guid GattNotifyUuid = new("00001102-d102-11e1-9b23-00025b00a5a5");
    public static GaiaFrame Handshake() => new(4, GaiaVendor, 0x0300, []);
    public static GaiaFrame Battery() => new(4, Vendor, 0x0207, []);
    public static GaiaFrame Command(DeviceProfile p, ushort command, params byte[] payload) => new(p.Version, Vendor, command, payload);
    public static GaiaFrame Noise(DeviceProfile p, NoiseMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return Command(p, 0x0130, [(byte)mode, .. p.NoiseSuffix]);
    }
    public static GaiaFrame HostTime(DeviceProfile p, DateTime time) => Command(p, 0x0509,
        (byte)(time.Year / 100), (byte)(time.Year % 100), (byte)time.Month, (byte)time.Day,
        (byte)time.Hour, (byte)time.Minute, (byte)time.Second);
    public static IEnumerable<GaiaFrame> InitialQueries(DeviceProfile p, bool experimental)
    {
        if (p.RegisterNotifications)
            for (ushort c = 0x0202; c <= 0x0206; c++) yield return new(4, Vendor, c, []);
        yield return Battery();
        if (p.Has(Features.Noise)) yield return Command(p, 0x0230, p.NoiseQuery);
        yield return Command(p, 0x021C);
        yield return Command(p, 0x021B);
        yield return Command(p, 0x020D);
        yield return Command(p, 0x0218);
        yield return Command(p, 0x0202);
        yield return Command(p, 0x0231, 5);
        if (p.Has(Features.Wear)) yield return Command(p, 0x0203);
        if (p.Has(Features.Dual) && !p.DualAlwaysOn) yield return Command(p, 0x0249);
        if (experimental && p.Has(Features.Game)) yield return Command(p, 0x0251);
        if (experimental && p.Has(Features.Spatial)) yield return Command(p, 0x0239);
    }
    public static GaiaFrame MultiDeviceTable(DeviceProfile p, IEnumerable<PeerDevice> devices)
    {
        var payload = devices.SelectMany(d => Convert.FromHexString(d.Address) .Concat(new[] { d.Status })).ToArray();
        if (payload.Length > 254) throw new ArgumentException("设备列表过长");
        return Command(p, 0x014A, payload);
    }
}
