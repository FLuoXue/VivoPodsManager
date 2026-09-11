using System.Text;
using VivoPods.Core.Protocol;

namespace VivoPods.Core.Models;

public sealed record BatteryReading(int? Level = null, bool Charging = false, bool Stale = true)
{
    public string Text => Level is null ? "—" : $"{Level}%";
    public BatteryReading Update(byte value, bool charging) => value <= 100 ? new(value, charging, false) : this with { Stale = true };
}
public sealed record PeerDevice(string Address, string Name, byte Status)
{
    public string StateLabel => Status switch { 0 => "未连接", 1 => "已连接", 2 => "正在使用", _ => "未知" };
}
public sealed record PodState
{
    public BatteryReading Left { get; init; } = new();
    public BatteryReading Right { get; init; } = new();
    public BatteryReading Case { get; init; } = new();
    public NoiseMode? Noise { get; init; }
    public string LeftWear { get; init; } = "状态未知";
    public string RightWear { get; init; } = "状态未知";
    public bool? WearDetection { get; init; }
    public byte? Eq { get; init; }
    public byte? LeftTap { get; init; }
    public byte? RightTap { get; init; }
    public byte? LeftCycle { get; init; }
    public byte? RightCycle { get; init; }
    public bool? Gaming { get; init; }
    public bool? Spatial { get; init; }
    public bool? Finding { get; init; }
    public string Firmware { get; init; } = "待获取";
    public string ModelId { get; init; } = "待获取";
    public byte? MultiStatus { get; init; }
    public IReadOnlyList<PeerDevice> Peers { get; init; } = [];
    public DateTimeOffset? UpdatedAt { get; init; }

    public PodState Disconnected() => this with
    {
        Left = Left with { Stale = true }, Right = Right with { Stale = true }, Case = Case with { Stale = true },
        Noise = null, LeftWear = "状态未知", RightWear = "状态未知", WearDetection = null, Eq = null,
        LeftTap = null, RightTap = null, LeftCycle = null, RightCycle = null, Gaming = null, Spatial = null,
        Finding = null, MultiStatus = null, Peers = []
    };

    public PodState Apply(GaiaFrame frame)
    {
        if (frame.Vendor != VivoProtocol.Vendor) return this;
        byte[] p = frame.Payload;
        if (frame.Command == 0x8249) return ParsePeers(p);
        if (p.Length < 2 || p[0] != 0) return this;
        PodState result = frame.Command switch
        {
            0x8207 when p.Length >= 5 => this with
            {
                Left = Left.Update(p[1], (p[4] & 1) != 0), Right = Right.Update(p[2], (p[4] & 2) != 0),
                Case = Case.Update(p[3], (p[4] & 4) != 0)
            },
            0x8230 or 0x8130 when p[1] <= 2 => this with { Noise = (NoiseMode)p[1] },
            0x820D => this with { LeftWear = Wear(p[1], 1, 4), RightWear = Wear(p[1], 2, 8) },
            0x8203 or 0x8103 when p[1] <= 1 => this with { WearDetection = p[1] == 1 },
            0x8218 or 0x8118 => this with { Eq = p[1] },
            0x8202 when p.Length >= 3 => this with { LeftTap = p[1], RightTap = p[2] },
            0x8231 or 0x8131 when p.Length >= 4 && p[1] == 5 => this with { LeftCycle = p[2], RightCycle = p[3] },
            0x8251 or 0x8151 when p[1] <= 1 => this with { Gaming = p[1] == 1 },
            0x8239 or 0x8139 when p[1] <= 1 => this with { Spatial = p[1] == 1 },
            0x8120 when p[1] <= 1 => this with { Finding = p[1] == 1 },
            0x821C => this with { Firmware = p.Length >= 7 && p[2] <= 99 && p[5] <= 99 && p[6] <= 99
                ? $"{p[2]}.{p[5]}.{p[6]}" : Convert.ToHexString(p.AsSpan(1)) },
            0x821B => this with { ModelId = Convert.ToHexString(p.AsSpan(1)) },
            _ => this
        };
        return ReferenceEquals(result, this) ? this : result with { UpdatedAt = DateTimeOffset.Now };
    }
    private static string Wear(byte bits, int inCase, int worn) => (bits & inCase) != 0 ? "已入盒" : (bits & worn) != 0 ? "佩戴中" : "已摘下";
    private PodState ParsePeers(byte[] p)
    {
        if (p.Length < 2) return this;
        List<PeerDevice> peers = [];
        int offset = 2;
        for (int i = 0; i < p[1]; i++)
        {
            if (offset + 11 > p.Length) return this;
            int nameLength = p[offset + 10];
            if (offset + 11 + nameLength > p.Length) return this;
            peers.Add(new(Convert.ToHexString(p.AsSpan(offset, 6)), Encoding.UTF8.GetString(p, offset + 11, nameLength), p[offset + 9]));
            offset += 11 + nameLength;
        }
        return this with { MultiStatus = p[0], Peers = peers, UpdatedAt = DateTimeOffset.Now };
    }
}
