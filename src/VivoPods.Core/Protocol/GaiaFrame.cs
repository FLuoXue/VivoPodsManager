using System.Buffers.Binary;

namespace VivoPods.Core.Protocol;

public sealed record GaiaFrame(byte Version, ushort Vendor, ushort Command, byte[] Payload, byte Flags = 0)
{
    public byte[] Encode()
    {
        if ((Flags & ~3) != 0) throw new ArgumentOutOfRangeException(nameof(Flags));
        bool extended = (Flags & 2) != 0;
        if (Payload.Length > (extended ? ushort.MaxValue : byte.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(Payload));
        int header = extended ? 5 : 4;
        var bytes = new byte[header + 4 + Payload.Length + (Flags & 1)];
        bytes[0] = 0xFF; bytes[1] = Version; bytes[2] = Flags;
        if (extended) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(3), (ushort)Payload.Length);
        else bytes[3] = (byte)Payload.Length;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(header), Vendor);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(header + 2), Command);
        Payload.CopyTo(bytes, header + 4);
        if ((Flags & 1) != 0)
            foreach (byte b in bytes.AsSpan(0, bytes.Length - 1)) bytes[^1] ^= b;
        return bytes;
    }

    public byte[] EncodeGatt()
    {
        var bytes = new byte[4 + Payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, Vendor);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), Command);
        Payload.CopyTo(bytes, 4);
        return bytes;
    }

    public static GaiaFrame DecodeGatt(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) throw new FormatException("GATT 帧不足 4 字节");
        return new(0, BinaryPrimitives.ReadUInt16BigEndian(bytes),
            BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]), bytes[4..].ToArray());
    }
}

/// <summary>Single-reader incremental decoder. Handles fragmentation, concatenation and XOR validation.</summary>
public sealed class GaiaDecoder
{
    private readonly List<byte> _buffer = [];
    public int BufferedBytes => _buffer.Count;
    public void Reset() => _buffer.Clear();
    public IReadOnlyList<GaiaFrame> Feed(ReadOnlySpan<byte> chunk)
    {
        List<GaiaFrame> frames = [];
        // Process incrementally so even an arbitrarily large chunk cannot grow the retained buffer unboundedly.
        foreach (byte value in chunk)
        {
            _buffer.Add(value);
            while (_buffer.Count > 0)
            {
                if (_buffer[0] != 0xFF) { _buffer.RemoveAt(0); continue; }
                if (_buffer.Count < 4) break;
                byte version = _buffer[1], flags = _buffer[2];
                if (version is not (3 or 4) || (flags & ~3) != 0) { _buffer.RemoveAt(0); continue; }
                int header = (flags & 2) != 0 ? 5 : 4;
                if (_buffer.Count < header) break;
                int payloadLength = header == 5 ? (_buffer[3] << 8) | _buffer[4] : _buffer[3];
                int length = header + 4 + payloadLength + (flags & 1);
                if (_buffer.Count < length) break;
                byte[] raw = _buffer.GetRange(0, length).ToArray();
                if ((flags & 1) != 0 && raw.Aggregate(0, (a, b) => a ^ b) != 0)
                { _buffer.RemoveAt(0); continue; }
                frames.Add(new(version, BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(header)),
                    BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(header + 2)),
                    raw.AsSpan(header + 4, payloadLength).ToArray(), flags));
                _buffer.RemoveRange(0, length);
            }
        }
        return frames;
    }
}
