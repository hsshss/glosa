namespace Glosa.Core.Archives;

/// <summary>
/// Unpacks <c>-lh4-</c> to <c>-lh7-</c>: LZSS whose literals, lengths and distances are
/// coded with Huffman tables sent at the head of each block.
/// </summary>
/// <remarks>
/// The four methods differ only in how far back a match may reach, which shows up here as
/// the number of distance codes and the width of the field that counts them. The format is
/// Haruhiko Okumura's <c>ar002</c>, which LHA 2 adopted.
///
/// Codes are canonical — shorter codes first, equal lengths in symbol order — so a table
/// is fully described by its lengths and decoded by walking those, a bit at a time. MIDI
/// files are small enough that a lookup table would buy nothing.
/// </remarks>
internal static class LzhDecoder
{
    internal readonly record struct Kind(int PositionCodes, int PositionBits);

    /// <summary>
    /// <c>-lh4-</c> as well. Its smaller dictionary never matters here, because the whole
    /// file is kept and any distance the data names can be reached.
    /// </summary>
    internal static readonly Kind Lh5 = new(14, 4);
    internal static readonly Kind Lh6 = new(16, 5);
    internal static readonly Kind Lh7 = new(17, 5);

    /// <summary>Literals 0-255, then match lengths 3-256.</summary>
    private const int CharCodes = 256 + 256 - 3 + 1;
    private const int CharCountBits = 9;
    private const int LengthCodes = 19;
    private const int LengthCountBits = 5;
    private const int MinMatch = 3;

    public static byte[] Decode(byte[] packed, int size, Kind kind)
    {
        var output = new byte[size];
        var bits = new BitReader(packed);
        int pos = 0;
        int blockLeft = 0;
        Huffman chars = null!, positions = null!;

        while (pos < size)
        {
            if (blockLeft == 0)
            {
                blockLeft = bits.Read(16);
                Huffman lengths = ReadLengths(bits, LengthCodes, LengthCountBits, special: 3);
                chars = ReadCharLengths(bits, lengths);
                positions = ReadLengths(bits, kind.PositionCodes, kind.PositionBits, special: -1);
                if (blockLeft == 0) throw new InvalidDataException(Strings.LzhBlockEmpty);
            }
            blockLeft--;

            int c = chars.Decode(bits);
            if (c < 256)
            {
                output[pos++] = (byte)c;
                continue;
            }

            int length = c - 256 + MinMatch;
            int distance = positions.Decode(bits);
            if (distance > 1) distance = (1 << (distance - 1)) + bits.Read(distance - 1);

            // Before the start is the dictionary's initial fill, which LHA sets to spaces.
            for (int from = pos - distance - 1; length > 0 && pos < size; length--, from++)
                output[pos++] = from >= 0 ? output[from] : (byte)' ';
        }

        return output;
    }

    /// <summary>
    /// The lengths of the table that codes the other tables' lengths, or of the distances.
    /// </summary>
    /// <param name="special">
    /// After this many lengths, two bits give a run of zeros to skip. Only the first table
    /// has it, at three: the lengths of codes 0-2, which are rare.
    /// </param>
    private static Huffman ReadLengths(BitReader bits, int count, int countBits, int special)
    {
        int n = bits.Read(countBits);
        if (n == 0) return Huffman.Only(bits.Read(countBits), count);
        if (n > count) throw new InvalidDataException(Strings.LzhTableBroken);

        var lengths = new int[count];
        for (int i = 0; i < n;)
        {
            // Three bits, and past 6 a run of ones adding one each, closed by a zero.
            int length = bits.Read(3);
            if (length == 7)
                while (bits.Read(1) == 1)
                    if (++length > 16) throw new InvalidDataException(Strings.LzhTableBroken);
            lengths[i++] = length;

            if (i == special)
                for (int skip = bits.Read(2); skip > 0 && i < count; skip--) lengths[i++] = 0;
        }

        return Huffman.FromLengths(lengths);
    }

    /// <summary>
    /// The literal-and-length table, whose own lengths are coded with
    /// <paramref name="lengths"/>. Codes 0-2 of that stand for runs of zero.
    /// </summary>
    private static Huffman ReadCharLengths(BitReader bits, Huffman lengths)
    {
        int n = bits.Read(CharCountBits);
        if (n == 0) return Huffman.Only(bits.Read(CharCountBits), CharCodes);
        if (n > CharCodes) throw new InvalidDataException(Strings.LzhTableBroken);

        var table = new int[CharCodes];
        for (int i = 0; i < n;)
        {
            int c = lengths.Decode(bits);
            if (c > 2)
            {
                table[i++] = c - 2;
                continue;
            }

            int zeros = c switch
            {
                0 => 1,
                1 => bits.Read(4) + 3,
                _ => bits.Read(CharCountBits) + 20,
            };
            for (; zeros > 0 && i < CharCodes; zeros--) table[i++] = 0;
        }

        return Huffman.FromLengths(table);
    }

    /// <summary>A canonical Huffman code, known by the lengths of its codes.</summary>
    private sealed class Huffman
    {
        private const int MaxLength = 16;

        private readonly int[] _counts = new int[MaxLength + 1];
        private readonly int[] _symbols = [];
        private readonly int _only = -1;

        private Huffman(int only) => _only = only;

        private Huffman(int[] lengths)
        {
            foreach (int length in lengths) _counts[length]++;
            _counts[0] = 0;

            var offsets = new int[MaxLength + 2];
            for (int length = 1; length <= MaxLength; length++)
                offsets[length + 1] = offsets[length] + _counts[length];

            _symbols = new int[offsets[MaxLength + 1]];
            for (int symbol = 0; symbol < lengths.Length; symbol++)
                if (lengths[symbol] != 0) _symbols[offsets[lengths[symbol]]++] = symbol;
        }

        /// <summary>A table with one symbol in it, which takes no bits to say.</summary>
        /// <remarks>
        /// The symbol comes from the data, so one past the table's end is refused.
        /// </remarks>
        public static Huffman Only(int symbol, int count)
            => symbol < count ? new(symbol) : throw new InvalidDataException(Strings.LzhTableBroken);

        public static Huffman FromLengths(int[] lengths) => new(lengths);

        public int Decode(BitReader bits)
        {
            if (_only >= 0) return _only;

            int code = 0, first = 0, index = 0;
            for (int length = 1; length <= MaxLength; length++)
            {
                code |= bits.Read(1);
                int count = _counts[length];
                if (code - first < count) return _symbols[index + code - first];
                index += count;
                first = (first + count) << 1;
                code <<= 1;
            }
            throw new InvalidDataException(Strings.LzhDataBroken);
        }
    }

    /// <summary>Most significant bit first. Past the end it reads zeros, as LHA does.</summary>
    private sealed class BitReader(byte[] data)
    {
        private int _at;
        private uint _buffer;
        private int _held;

        public int Read(int count)
        {
            while (_held < count)
            {
                _buffer = (_buffer << 8) | (_at < data.Length ? data[_at++] : 0u);
                _held += 8;
            }
            _held -= count;
            return (int)((_buffer >> _held) & ((1u << count) - 1));
        }
    }
}
