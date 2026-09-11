namespace VivoPods.Core.Models;

[Flags]
public enum Features { None = 0, Noise = 1, Find = 2, Wear = 4, Dual = 8, Game = 16, Spatial = 32 }
public enum NoiseMode : byte { Anc = 0, Off = 1, Transparency = 2 }
public enum TransportKind { Rfcomm, Gatt, Demo }

public sealed record PodDevice(string Id, string Name, ulong Address, bool IsConnected, TransportKind Transport = TransportKind.Rfcomm)
{
    public string AddressText => string.Join(":", Enumerable.Range(0, 6).Select(i => ((Address >> ((5 - i) * 8)) & 255).ToString("X2")));
    public string Display => $"{Name}  ·  {(IsConnected ? "已连接" : "已配对")} {(Transport == TransportKind.Gatt ? "· BLE" : "")}";
    public override string ToString() => Display;
}

public sealed record DeviceProfile(string Name, byte Version, byte[] NoiseSuffix, byte[] NoiseQuery, Features Capabilities,
    bool RegisterNotifications, bool DualAlwaysOn = false, bool Known = true)
{
    public bool Has(Features feature) => Capabilities.HasFlag(feature);
    public static string Normalize(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    public static bool IsFamily(string value)
    {
        string n = Normalize(value);
        return n.StartsWith("vivotws") || n.StartsWith("iqootws") || n is "vivoheadphones" or "iqooheadphones";
    }
    private const Features N = Features.Noise, F = Features.Find, D = Features.Dual, G = Features.Game, S = Features.Spatial;
    // Capability declarations from TWS-Pods-PC/vivo/vivo_features.py (official app configuration).
    public static IReadOnlyList<DeviceProfile> All { get; } = new (string Name, Features Caps)[]
    {
        ("vivo TWS 1", 0), ("vivo TWS 2", N|F), ("vivo TWS 2e", F),
        ("vivo TWS 3", N|F), ("vivo TWS 3 Pro", N|F), ("vivo TWS 3e", N|F|Features.Wear|D), ("vivo TWS 3i", F),
        ("vivo TWS 4", N|F|G), ("vivo TWS 4 HiFi", N|F|G), ("vivo TWS 5", N|F|S|D|G),
        ("vivo TWS 5 HiFi", N|F|S|D|G), ("vivo TWS 5 Pro", N|F|S|D|G), ("vivo TWS 5e", N|F|D|G), ("vivo TWS 5i", F|D|G),
        ("vivo TWS Air", F), ("vivo TWS Air Pro", N|F), ("vivo TWS Air2", F),
        ("vivo TWS Air3", F|G), ("vivo TWS Air3 Pro", N|F|G), ("vivo TWS Neo", F), ("vivo TWS X1", N|F),
        ("vivo TWS A1", F), ("vivo TWS A1 Pro", N|F), ("vivo TWS A2", F), ("vivo TWS A3", F), ("vivo TWS A4", F|G), ("vivo TWS A5", F|D|G),
        ("iQOO TWS 1", N|F), ("iQOO TWS 1e", N|F), ("iQOO TWS 1i", F), ("iQOO TWS 2", N|F|G),
        ("iQOO TWS 5", N|F|S|D|G), ("iQOO TWS 5e", N|F|D|G), ("iQOO TWS 5i", F|D|G),
        ("iQOO TWS Air", F), ("iQOO TWS Air Pro", N|F), ("iQOO TWS Air2", F), ("iQOO TWS Air3", F|G), ("iQOO TWS Air3 Pro", N|F|G),
        ("vivo Headphones", N|F|S|D|G), ("iQOO Headphones", N|F|S|D|G)
    }.Select(entry =>
    {
        string n = Normalize(entry.Name);
        bool air3 = n is "vivotwsair3pro" or "iqootwsair3pro";
        bool threeE = n == "vivotws3e";
        bool four = n is "vivotws4" or "vivotws4hifi";
        return new DeviceProfile(entry.Name, (byte)(air3 || threeE ? 3 : 4),
            air3 ? [4, 0] : threeE ? [3] : [3, 1], air3 || threeE ? [] : [0],
            entry.Caps, air3 || threeE || four, threeE);
    }).ToArray();

    public static DeviceProfile Resolve(string name)
    {
        string n = Normalize(name);
        if (n == "vivotwsair200") n = "vivotwsair2";
        return All.FirstOrDefault(x => Normalize(x.Name) == n)
            ?? new(name, 4, [3, 1], [0], Features.None, false, Known: false);
    }
}
