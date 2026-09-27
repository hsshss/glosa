using System.Buffers.Binary;
using System.Text;
using Glosa.Core.Text;

namespace Glosa.Core.Archives;

/// <summary>One file stored in an LZH archive.</summary>
/// <param name="Name">The path inside the archive, with <c>/</c> between folders.</param>
/// <param name="Method">The five-character method id, such as <c>-lh5-</c>.</param>
/// <param name="Offset">Where the packed data starts in the archive.</param>
public sealed record LzhEntry(string Name, string Method, long Offset,
                              long PackedSize, long Size, ushort Crc);

/// <summary>
/// Reads LZH (LHA) archives.
/// </summary>
/// <remarks>
/// Header levels 0 to 3 are read. Of the methods, <c>-lh0-</c> and <c>-lz4-</c> store the
/// file as it is and <c>-lh4-</c> to <c>-lh7-</c> are the one LZSS-and-Huffman scheme with
/// dictionaries of different sizes. That is every method in a collection of 2,600 MIDI
/// archives; <c>-lh1-</c> to <c>-lh3-</c> and LArc's <c>-lzs-</c> / <c>-lz5-</c> belong to the
/// 1980s and are refused rather than guessed at.
///
/// Names are Shift-JIS, as MS-DOS and Windows wrote them. An archive made on Unix may
/// carry UTF-8 instead; that is taken when the header says Unix and the bytes are valid
/// UTF-8, since nothing else in the header says which.
/// </remarks>
public static class LzhArchive
{
    private const int BaseLength = 22;

    public static IReadOnlyList<LzhEntry> List(Stream stream)
    {
        var entries = new List<LzhEntry>();
        long at = FindFirstHeader(stream);
        while (at >= 0 && ReadHeader(stream, at) is { } header)
        {
            if (header.Entry is { } entry) entries.Add(entry);
            at = header.Next;
        }
        return entries;
    }

    public static byte[] Extract(Stream stream, LzhEntry entry)
    {
        if (entry.PackedSize > SongStore.MaxEntrySize || entry.Size > SongStore.MaxEntrySize)
            throw new InvalidDataException(string.Format(Strings.ArchiveEntryTooLarge, entry.Name));

        var packed = new byte[entry.PackedSize];
        stream.Position = entry.Offset;
        stream.ReadExactly(packed);

        byte[] data = entry.Method switch
        {
            "-lh0-" or "-lz4-" => packed,
            "-lh4-" or "-lh5-" => LzhDecoder.Decode(packed, (int)entry.Size, LzhDecoder.Lh5),
            "-lh6-" => LzhDecoder.Decode(packed, (int)entry.Size, LzhDecoder.Lh6),
            "-lh7-" => LzhDecoder.Decode(packed, (int)entry.Size, LzhDecoder.Lh7),
            _ => throw new InvalidDataException(
                string.Format(Strings.LzhUnknownMethod, entry.Name, entry.Method)),
        };

        if (data.Length != entry.Size || Crc16.Compute(data) != entry.Crc)
            throw new InvalidDataException(string.Format(Strings.LzhCrcMismatch, entry.Name));
        return data;
    }

    private readonly record struct Header(LzhEntry? Entry, long Next);

    /// <summary>
    /// Where the first header is. Normally at the very start; a self-extracting archive has
    /// its program in front, so the search looks a little way in for a method id.
    /// </summary>
    private static long FindFirstHeader(Stream stream)
    {
        const int Window = 64 * 1024;
        stream.Position = 0;
        var buffer = new byte[(int)Math.Min(Window, stream.Length)];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

        for (int at = 0; at + BaseLength <= read; at++)
            if (LooksLikeHeader(buffer.AsSpan(at, BaseLength))) return at;
        return -1;
    }

    private static bool LooksLikeHeader(ReadOnlySpan<byte> b)
        => b[2] == '-' && b[3] == 'l' && (b[4] == 'h' || b[4] == 'z') && b[6] == '-'
           && b[20] <= 3;

    private static Header? ReadHeader(Stream stream, long at)
    {
        stream.Position = at;
        Span<byte> head = stackalloc byte[BaseLength];
        if (stream.ReadAtLeast(head, BaseLength, throwOnEndOfStream: false) < BaseLength)
            return null;
        // A zero where the next header would start is the end mark.
        if (head[0] == 0) return null;
        if (!LooksLikeHeader(head)) throw new InvalidDataException(Strings.LzhHeaderBroken);

        return head[20] switch
        {
            0 or 1 => ReadLevel01(stream, at, head[0] + 2, head[20]),
            2 => ReadLevel2(stream, at, BinaryPrimitives.ReadUInt16LittleEndian(head)),
            _ => ReadLevel3(stream, at),
        };
    }

    private static Header ReadLevel01(Stream stream, long at, int length, int level)
    {
        if (length < BaseLength) throw new InvalidDataException(Strings.LzhHeaderBroken);

        byte[] b = ReadAt(stream, at, length);
        string method = Encoding.ASCII.GetString(b, 2, 5);
        long packed = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(7));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(11));
        int nameLength = b[21];
        // The name and CRC; level 1 adds the OS byte and the first extension's size.
        if (BaseLength + nameLength + (level == 1 ? 5 : 2) > length)
            throw new InvalidDataException(Strings.LzhHeaderBroken);
        byte[] name = b.AsSpan(22, nameLength).ToArray();
        ushort crc = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(22 + nameLength));

        var names = new Names(name, os: level == 1 ? b[24 + nameLength] : (byte)'M');
        long dataAt = at + length;

        if (level == 1)
        {
            // The packed size counts the extended headers that follow the base one.
            int next = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(length - 2));
            while (next > 0)
            {
                if (next < 3) throw new InvalidDataException(Strings.LzhHeaderBroken);
                byte[] ext = ReadAt(stream, dataAt, next);
                names.Take(ext[0], ext.AsSpan(1, next - 3));
                dataAt += next;
                packed -= next;
                next = BinaryPrimitives.ReadUInt16LittleEndian(ext.AsSpan(next - 2));
            }
        }

        return Finish(method, names, dataAt, packed, size, crc);
    }

    private static Header ReadLevel2(Stream stream, long at, int length)
    {
        if (length < 26) throw new InvalidDataException(Strings.LzhHeaderBroken);

        byte[] b = ReadAt(stream, at, length);
        string method = Encoding.ASCII.GetString(b, 2, 5);
        long packed = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(7));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(11));
        ushort crc = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(21));
        var names = new Names([], os: b[23]);

        for (int o = 24, next = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(24));
             next > 0;)
        {
            o += 2;
            if (next < 3 || o + next > length) throw new InvalidDataException(Strings.LzhHeaderBroken);
            names.Take(b[o], b.AsSpan(o + 1, next - 3));
            o += next - 2;
            next = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
        }

        return Finish(method, names, at + length, packed, size, crc);
    }

    /// <summary>Level 3 is level 2 with every size widened to four bytes.</summary>
    private static Header ReadLevel3(Stream stream, long at)
    {
        byte[] fixedPart = ReadAt(stream, at, 32);
        long length = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(24));
        if (length is < 32 or > 1 << 20) throw new InvalidDataException(Strings.LzhHeaderBroken);

        byte[] b = ReadAt(stream, at, (int)length);
        string method = Encoding.ASCII.GetString(b, 2, 5);
        long packed = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(7));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(11));
        ushort crc = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(21));
        var names = new Names([], os: b[23]);

        for (int o = 28, next = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(28));
             next > 0;)
        {
            o += 4;
            if (next < 5 || (long)o + next > length) throw new InvalidDataException(Strings.LzhHeaderBroken);
            names.Take(b[o], b.AsSpan(o + 1, next - 5));
            o += next - 4;
            next = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
        }

        return Finish(method, names, at + length, packed, size, crc);
    }

    private static Header Finish(string method, Names names, long dataAt,
                                 long packed, long size, ushort crc)
    {
        if (packed < 0) throw new InvalidDataException(Strings.LzhHeaderBroken);
        long next = dataAt + packed;
        // Folders are listed as entries of their own; only files are worth handing out.
        LzhEntry? entry = method == "-lhd-"
            ? null
            : new LzhEntry(names.Path, method, dataAt, packed, size, crc);
        return new Header(entry, next);
    }

    private static byte[] ReadAt(Stream stream, long at, int length)
    {
        var b = new byte[length];
        stream.Position = at;
        if (stream.ReadAtLeast(b, length, throwOnEndOfStream: false) < length)
            throw new EndOfStreamException(Strings.LzhHeaderCut);
        return b;
    }

    /// <summary>The name and folder, which may come from the base header or from extensions.</summary>
    private sealed class Names(byte[] file, byte os)
    {
        private byte[] _file = file;
        private byte[] _folder = [];

        public void Take(byte type, ReadOnlySpan<byte> data)
        {
            switch (type)
            {
                case 0x01: _file = data.ToArray(); break;
                case 0x02: _folder = data.ToArray(); break;
            }
        }

        public string Path
        {
            get
            {
                string folder = Decode(_folder);
                string file = Decode(_file);
                string joined = folder.Length == 0 ? file : folder.TrimEnd('/') + "/" + file;
                return joined.TrimStart('/');
            }
        }

        /// <summary>
        /// 0xFF is how the extensions separate folders. It cannot occur inside a Shift-JIS
        /// character, so it is swapped before decoding; a backslash can (as the second byte
        /// of a double-byte character), so that one waits until the text is characters.
        /// </summary>
        private string Decode(byte[] bytes)
        {
            byte[] copy = [.. bytes.Select(b => b == 0xFF ? (byte)'/' : b)];
            string text = os == (byte)'U' && Utf8(copy) is { } utf8
                ? utf8
                : Cp932.Encoding.GetString(copy);
            return text.Replace('\\', '/');
        }

        private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

        private static string? Utf8(byte[] bytes)
        {
            try { return StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { return null; }
        }
    }
}

/// <summary>CRC-16 as LHA uses it (polynomial 0xA001, reflected, starting from zero).</summary>
internal static class Crc16
{
    private static readonly ushort[] Table = Build();

    private static ushort[] Build()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xA001 : c >> 1;
            table[i] = (ushort)c;
        }
        return table;
    }

    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return (ushort)crc;
    }
}
