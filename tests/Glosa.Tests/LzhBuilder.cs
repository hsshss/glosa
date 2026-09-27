using System.Buffers.Binary;
using Glosa.Core.Text;

namespace Glosa.Tests;

/// <summary>
/// Builds small LZH archives, since no tool on hand writes them and real archives are
/// someone else's work.
/// </summary>
/// <remarks>
/// <c>-lh5-</c> is written with fixed code tables rather than ones fitted to the data: every
/// length is 8 or 9 bits and every distance 3 or 4, which is a complete code and so a legal
/// one. The point is to put every part of the format in front of the reader — the table
/// that codes the tables, the zero-run skip after its third entry, matches reaching back —
/// not to compress well.
/// </remarks>
internal sealed class LzhBuilder
{
    private readonly List<byte> _output = [];

    /// <param name="folder">Where in the archive, with <c>/</c> between folders.</param>
    /// <param name="os">The header's OS id; <c>U</c> lets the name be UTF-8.</param>
    public LzhBuilder Add(string name, byte[] data, int level = 2, string method = "-lh5-",
                          string folder = "", char os = 'M', byte[]? rawName = null)
    {
        byte[] packed = method == "-lh5-" ? Lh5(data) : data;
        byte[] nameBytes = rawName ?? Cp932.Encoding.GetBytes(name);
        byte[] folderBytes = [.. Cp932.Encoding.GetBytes(folder).Select(b => b == '/' ? (byte)0xFF : b)];
        ushort crc = Crc16(data);

        switch (level)
        {
            case 0:
            {
                byte[] full = folder.Length == 0
                    ? nameBytes
                    : [.. Cp932.Encoding.GetBytes(folder.Replace('/', '\\') + "\\"), .. nameBytes];
                var h = new List<byte>();
                Common(h, method, packed.Length, data.Length, level: 0);
                h.Add((byte)full.Length);
                h.AddRange(full);
                h.AddRange(Le16(crc));
                Finish01(h);
                _output.AddRange(h);
                break;
            }
            case 1:
            {
                var ext = new List<byte>();
                if (folderBytes.Length > 0)
                    ext.AddRange([0x02, .. folderBytes]);
                var h = new List<byte>();
                int extSize = folderBytes.Length > 0 ? 3 + folderBytes.Length : 0;
                Common(h, method, packed.Length + extSize, data.Length, level: 1);
                h.Add((byte)nameBytes.Length);
                h.AddRange(nameBytes);
                h.AddRange(Le16(crc));
                h.Add((byte)os);
                h.AddRange(Le16((ushort)extSize));
                Finish01(h);
                _output.AddRange(h);
                if (extSize > 0)
                {
                    _output.AddRange(ext);
                    _output.AddRange(Le16(0));
                }
                break;
            }
            case 3:
            {
                // Level 2 with every size four bytes wide, and the header's own size after
                // the OS byte; the first two bytes are the width of a word.
                List<byte[]> exts = [[0x01, .. nameBytes]];
                if (folderBytes.Length > 0) exts.Add([0x02, .. folderBytes]);

                var h = new List<byte>();
                Common(h, method, packed.Length, data.Length, level: 3);
                h[0] = 4;
                h.AddRange(Le16(crc));
                h.Add((byte)os);
                h.AddRange(Le32(0));
                foreach (byte[] ext in exts)
                {
                    h.AddRange(Le32(ext.Length + 4));
                    h.AddRange(ext);
                }
                h.AddRange(Le32(0));
                BinaryPrimitives.WriteInt32LittleEndian(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(h)[24..], h.Count);
                _output.AddRange(h);
                break;
            }
            default:
            {
                List<byte[]> exts = [[0x01, .. nameBytes]];
                if (folderBytes.Length > 0) exts.Add([0x02, .. folderBytes]);

                var h = new List<byte>();
                Common(h, method, packed.Length, data.Length, level: 2);
                h.AddRange(Le16(crc));
                h.Add((byte)os);
                foreach (byte[] ext in exts)
                {
                    h.AddRange(Le16((ushort)(ext.Length + 2)));
                    h.AddRange(ext);
                }
                h.AddRange(Le16(0));
                BinaryPrimitives.WriteUInt16LittleEndian(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(h), (ushort)h.Count);
                _output.AddRange(h);
                break;
            }
        }

        _output.AddRange(packed);
        return this;
    }

    public byte[] Build() => [.. _output, 0];

    /// <summary>Everything from the method id to the level byte, which all levels share.</summary>
    private static void Common(List<byte> h, string method, int packed, int size, int level)
    {
        h.AddRange([0, 0]);
        h.AddRange(System.Text.Encoding.ASCII.GetBytes(method));
        h.AddRange(Le32(packed));
        h.AddRange(Le32(size));
        h.AddRange(Le32(0));
        h.Add(0x20);
        h.Add((byte)level);
    }

    /// <summary>Levels 0 and 1 count the header less its first two bytes, and checksum it.</summary>
    private static void Finish01(List<byte> h)
    {
        h[0] = (byte)(h.Count - 2);
        h[1] = (byte)h.Skip(2).Sum(b => b);
    }

    private static byte[] Le16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }
    private static byte[] Le32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); return b; }

    internal static ushort Crc16(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return (ushort)crc;
    }

    /// <summary>Greedy LZSS in one block, coded with the fixed tables described above.</summary>
    internal static byte[] Lh5(byte[] data)
    {
        const int Window = 8192, MaxMatch = 256, MinMatch = 3;
        var tokens = new List<(int Symbol, int Distance)>();
        for (int pos = 0; pos < data.Length;)
        {
            int bestLength = 0, bestFrom = 0;
            for (int from = Math.Max(0, pos - Window); from < pos; from++)
            {
                int length = 0;
                while (length < MaxMatch && pos + length < data.Length
                       && data[from + length] == data[pos + length]) length++;
                if (length > bestLength) (bestLength, bestFrom) = (length, from);
            }

            if (bestLength >= MinMatch)
            {
                tokens.Add((256 + bestLength - MinMatch, pos - bestFrom - 1));
                pos += bestLength;
            }
            else
            {
                tokens.Add((data[pos++], -1));
            }
        }
        if (tokens.Count > 0xFFFF) throw new ArgumentException("one block is all this writes");

        var w = new BitWriter();
        w.Write(tokens.Count, 16);

        // The table for table lengths: symbols 10 and 11, one bit each, standing for
        // lengths 8 and 9. Twelve entries, with the skip after the third used for three.
        w.Write(12, 5);
        for (int i = 0; i < 3; i++) w.Write(0, 3);
        w.Write(3, 2);
        for (int i = 6; i < 10; i++) w.Write(0, 3);
        w.Write(1, 3);
        w.Write(1, 3);

        // Literals and lengths: 0 and 1 get 8 bits, the other 508 get 9.
        w.Write(510, 9);
        for (int i = 0; i < 510; i++) w.Write(i < 2 ? 0 : 1, 1);

        // Distances: 0 and 1 get 3 bits, the other twelve get 4.
        w.Write(14, 4);
        for (int i = 0; i < 14; i++) w.Write(i < 2 ? 3 : 4, 3);

        foreach ((int symbol, int distance) in tokens)
        {
            if (symbol < 2) w.Write(symbol, 8);
            else w.Write(4 + symbol - 2, 9);
            if (distance < 0) continue;

            int bits = distance == 0 ? 0 : 32 - int.LeadingZeroCount(distance);
            if (bits < 2) w.Write(bits, 3);
            else w.Write(4 + bits - 2, 4);
            if (bits > 1) w.Write(distance - (1 << (bits - 1)), bits - 1);
        }

        return w.ToArray();
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _current, _used;

        public void Write(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                _current = (_current << 1) | ((value >> i) & 1);
                if (++_used == 8) { _bytes.Add((byte)_current); _current = _used = 0; }
            }
        }

        public byte[] ToArray()
        {
            if (_used > 0) _bytes.Add((byte)(_current << (8 - _used)));
            return [.. _bytes];
        }
    }
}
