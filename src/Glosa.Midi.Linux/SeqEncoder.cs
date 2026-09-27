namespace Glosa.Midi.Linux;

/// <summary>One thing to send: a whole message, or a piece of SysEx.</summary>
/// <param name="IsSysEx">
/// True for SysEx bytes, sent as they are in one <c>SND_SEQ_EVENT_SYSEX</c>; false for a
/// channel or system message, which the sequencer has an event of its own for.
/// </param>
internal readonly record struct SeqChunk(bool IsSysEx, byte[] Bytes);

/// <summary>
/// Splits a long message's MIDI 1.0 bytes into what the sequencer sends: whole messages, and
/// SysEx in pieces of at most <see cref="ChunkSize"/> bytes.
/// </summary>
internal sealed class SeqEncoder
{
    /// <summary>The size ALSA's own MIDI ports cut SysEx into.</summary>
    public const int ChunkSize = 256;

    private readonly byte[] _chunk = new byte[ChunkSize];
    private int _chunkLength;
    private bool _inSysEx;

    private byte _status;
    private readonly byte[] _message = new byte[3];
    private int _messageLength;

    /// <summary>Bytes of a long message, read on from where the last one stopped.</summary>
    /// <remarks>A SysEx still open at the end is sent as far as it has got.</remarks>
    public void Write(ReadOnlySpan<byte> bytes, List<SeqChunk> output)
    {
        foreach (byte b in bytes)
        {
            // Realtime goes out at once, even from the middle of anything else.
            if (b >= 0xF8)
            {
                if (DataBytes(b) == 0) output.Add(new SeqChunk(false, [b]));
                continue;
            }

            if (_inSysEx)
            {
                if (b < 0x80 || b == 0xF7)
                {
                    if (_chunkLength == _chunk.Length) Flush(output);
                    _chunk[_chunkLength++] = b;
                    if (b == 0xF7)
                    {
                        Flush(output);
                        _inSysEx = false;
                    }
                    continue;
                }

                // Any other status cuts it short, as it would on a cable.
                Flush(output);
                _inSysEx = false;
            }

            if (b == 0xF0)
            {
                _inSysEx = true;
                _chunk[0] = b;
                _chunkLength = 1;
                _status = 0;
                _messageLength = 0;
                continue;
            }

            if (b >= 0x80)
            {
                _messageLength = 0;
                int needed = DataBytes(b);
                if (needed < 0) { _status = 0; continue; }       // F7 on its own, or undefined
                _status = b;
                _message[0] = b;
                _messageLength = 1;
                if (needed == 0) Complete(output);
                continue;
            }

            // A data byte: for the status in force, which a channel message keeps (running status).
            if (_status == 0) continue;
            if (_messageLength == 0) { _message[0] = _status; _messageLength = 1; }
            _message[_messageLength++] = b;
            if (_messageLength == 1 + DataBytes(_status)) Complete(output);
        }

        if (_inSysEx && _chunkLength > 0) Flush(output);
    }

    /// <summary>
    /// Closes a SysEx left open, so the device is not left waiting for its F7 and ignoring
    /// everything after; and forgets the running status.
    /// </summary>
    public void Reset(List<SeqChunk> output)
    {
        if (_inSysEx)
        {
            if (_chunkLength == _chunk.Length) Flush(output);
            _chunk[_chunkLength++] = 0xF7;
            Flush(output);
            _inSysEx = false;
        }
        _status = 0;
        _messageLength = 0;
    }

    private void Complete(List<SeqChunk> output)
    {
        output.Add(new SeqChunk(false, _message[.._messageLength]));
        _messageLength = 0;
        // System common messages do not run on.
        if (_status >= 0xF0) _status = 0;
    }

    /// <summary>Sends the SysEx bytes gathered so far, if there are any.</summary>
    private void Flush(List<SeqChunk> output)
    {
        if (_chunkLength == 0) return;
        output.Add(new SeqChunk(true, _chunk[.._chunkLength]));
        _chunkLength = 0;
    }

    /// <summary>
    /// How many data bytes follow <paramref name="status"/>; -1 for what is not a message on
    /// its own (a data byte, F0, F7) or is undefined.
    /// </summary>
    internal static int DataBytes(byte status) => status switch
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
