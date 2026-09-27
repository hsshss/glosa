namespace Glosa.Midi.MacOS;

/// <summary>
/// Turns MIDI 1.0 bytes into the Universal MIDI Packets CoreMIDI sends, group 0, MIDI 1.0
/// protocol.
/// </summary>
/// <remarks>
/// What comes out is words: one for a channel or system message (types 1 and 2), two for
/// each six bytes of SysEx (type 3). A message's length is in its first word's top four
/// bits (<see cref="WordsIn"/>).
/// </remarks>
internal sealed class UmpEncoder
{
    private const int SysExComplete = 0;
    private const int SysExStart = 1;
    private const int SysExContinue = 2;
    private const int SysExEnd = 3;

    private readonly byte[] _chunk = new byte[6];
    private int _chunkLength;
    private bool _inSysEx;
    private bool _sysExStarted;

    private byte _status;
    private readonly byte[] _data = new byte[2];
    private int _dataLength;

    /// <summary>How many words the message starting with <paramref name="first"/> takes.</summary>
    public static int WordsIn(uint first) => (first >> 28) switch
    {
        0 or 1 or 2 => 1,
        3 or 4 => 2,
        _ => 4,
    };

    /// <summary>A packed short message (<c>status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16</c>).</summary>
    /// <remarks>
    /// On its own, apart from the stream <see cref="Write"/> reads: it is always a whole
    /// message, and a SysEx left open by a long message stays open around it, as UMP lets it.
    /// </remarks>
    public static void Short(uint packed, List<uint> words)
    {
        byte status = (byte)packed;
        byte data1 = (byte)(packed >> 8 & 0x7F);
        byte data2 = (byte)(packed >> 16 & 0x7F);
        int needed = DataBytes(status);
        if (needed < 0) return;
        words.Add(Message(status, needed > 0 ? data1 : (byte)0, needed > 1 ? data2 : (byte)0));
    }

    /// <summary>Bytes of a long message, read on from where the last one stopped.</summary>
    /// <remarks>A SysEx still open at the end is sent as far as it has got.</remarks>
    public void Write(ReadOnlySpan<byte> bytes, List<uint> words)
    {
        foreach (byte b in bytes)
        {
            // Realtime goes out at once, even from the middle of anything else.
            if (b >= 0xF8)
            {
                if (DataBytes(b) == 0) words.Add(Message(b, 0, 0));
                continue;
            }

            if (_inSysEx)
            {
                if (b < 0x80)
                {
                    // Held back while full: the last six bytes go out as the end, not before it.
                    if (_chunkLength == _chunk.Length) EmitSysEx(words, last: false);
                    _chunk[_chunkLength++] = b;
                    continue;
                }

                // F7 closes it; any other status cuts it short, as it would on a cable.
                EmitSysEx(words, last: true);
                if (b == 0xF7) continue;
            }

            if (b == 0xF0)
            {
                _inSysEx = true;
                _sysExStarted = false;
                _chunkLength = 0;
                _status = 0;
                continue;
            }

            if (b >= 0x80)
            {
                _dataLength = 0;
                int needed = DataBytes(b);
                if (needed < 0) { _status = 0; continue; }       // F7 on its own, or undefined
                _status = b;
                if (needed == 0) Complete(words);
                continue;
            }

            // A data byte: for the status in force, which a channel message keeps (running status).
            if (_status == 0) continue;
            _data[_dataLength++] = b;
            if (_dataLength == DataBytes(_status)) Complete(words);
        }

        if (_inSysEx && _chunkLength > 0) EmitSysEx(words, last: false);
    }

    /// <summary>
    /// Closes a SysEx left open, so the device is not left waiting for its F7 and ignoring
    /// everything after; and forgets the running status.
    /// </summary>
    public void Reset(List<uint> words)
    {
        if (_inSysEx) EmitSysEx(words, last: true);
        _status = 0;
        _dataLength = 0;
    }

    private void Complete(List<uint> words)
    {
        words.Add(Message(_status, _dataLength > 0 ? _data[0] : (byte)0, _dataLength > 1 ? _data[1] : (byte)0));
        _dataLength = 0;
        // System common messages do not run on.
        if (_status >= 0xF0) _status = 0;
    }

    private void EmitSysEx(List<uint> words, bool last)
    {
        int status = last
            ? _sysExStarted ? SysExEnd : SysExComplete
            : _sysExStarted ? SysExContinue : SysExStart;

        Span<byte> b = stackalloc byte[6];
        _chunk.AsSpan(0, _chunkLength).CopyTo(b);
        words.Add(0x3000_0000u | (uint)status << 20 | (uint)_chunkLength << 16 | (uint)b[0] << 8 | b[1]);
        words.Add((uint)b[2] << 24 | (uint)b[3] << 16 | (uint)b[4] << 8 | b[5]);

        _chunkLength = 0;
        _sysExStarted = !last;
        if (last) _inSysEx = false;
    }

    /// <summary>One word: a channel voice message (type 2) or a system message (type 1).</summary>
    private static uint Message(byte status, byte data1, byte data2)
    {
        uint type = status < 0xF0 ? 0x2000_0000u : 0x1000_0000u;
        return type | (uint)status << 16 | (uint)data1 << 8 | data2;
    }

    /// <summary>
    /// How many data bytes follow <paramref name="status"/>; -1 for what is not a message on
    /// its own (a data byte, F0, F7) or is undefined.
    /// </summary>
    private static int DataBytes(byte status) => status switch
    {
        < 0x80 => -1,
        < 0xC0 => 2,
        < 0xE0 => 1,
        < 0xF0 => 2,
        0xF1 or 0xF3 => 1,
        0xF2 => 2,
        0xF6 or 0xF8 or 0xFA or 0xFB or 0xFC or 0xFE or 0xFF => 0,
        _ => -1,
    };
}
