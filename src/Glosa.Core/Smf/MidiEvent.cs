namespace Glosa.Core.Smf;

public enum MidiEventKind : byte
{
    /// <summary>Channel voice / system common message, held in <see cref="MidiEvent.Packed"/>.</summary>
    Channel,
    /// <summary>F0/F7 message; bytes live in the owning sequence's payload block.</summary>
    SysEx,
    /// <summary>Meta event; bytes live in the owning sequence's payload block.</summary>
    Meta,
    /// <summary>Loop start. Marks the point a matching <see cref="LoopEnd"/> returns to.</summary>
    LoopStart,
    /// <summary>
    /// Loop end. <see cref="MidiEvent.RepeatCount"/> carries how many times the body plays;
    /// zero means the data loops forever.
    /// </summary>
    LoopEnd,
}

public static class MetaType
{
    public const byte SequenceNumber = 0x00;
    public const byte Text = 0x01;
    public const byte Copyright = 0x02;
    public const byte TrackName = 0x03;
    public const byte Instrument = 0x04;
    public const byte Lyric = 0x05;
    public const byte Marker = 0x06;
    public const byte CuePoint = 0x07;
    /// <summary>MIDI port: which port the track's events go to.</summary>
    public const byte MidiPort = 0x21;
    public const byte EndOfTrack = 0x2F;
    public const byte Tempo = 0x51;
    public const byte SmpteOffset = 0x54;
    public const byte TimeSignature = 0x58;
    public const byte KeySignature = 0x59;
    public const byte SequencerSpecific = 0x7F;
}

/// <summary>
/// One event in a <see cref="MidiSequence"/>. A struct so the playback loop never allocates;
/// variable-length payloads are referenced by offset into a shared block.
/// </summary>
public readonly struct MidiEvent
{
    public MidiEvent(long ticks, int track, int port, uint packed)
    {
        Ticks = ticks;
        Track = track;
        Port = port;
        Kind = MidiEventKind.Channel;
        Packed = packed;
    }

    public MidiEvent(long ticks, int track, int port, MidiEventKind kind,
                     byte metaType, int dataOffset, int dataLength)
    {
        Ticks = ticks;
        Track = track;
        Port = port;
        Kind = kind;
        MetaType = metaType;
        DataOffset = dataOffset;
        DataLength = dataLength;
    }

    /// <summary>A loop boundary. No SMF construct produces one.</summary>
    public static MidiEvent Loop(long ticks, int track, int port, MidiEventKind kind,
                                 int repeatCount = 0)
        => new(ticks, track, port, (uint)repeatCount) { Kind = kind };

    public long Ticks { get; }

    /// <summary>Absolute playback position, filled in when the sequence is built.</summary>
    public long TimeUs { get; init; }

    public int Track { get; }

    /// <summary>Destination port, from the track's <c>FF 21</c> meta event (0 when absent).</summary>
    public int Port { get; }

    public MidiEventKind Kind { get; init; }

    /// <summary><c>status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16</c>. Channel events only.</summary>
    public uint Packed { get; }

    public byte MetaType { get; }

    public int DataOffset { get; }

    public int DataLength { get; }

    public byte Status => (byte)(Packed & 0xFF);

    public int Channel => (int)(Packed & 0x0F);

    public byte Data1 => (byte)((Packed >> 8) & 0xFF);

    public byte Data2 => (byte)((Packed >> 16) & 0xFF);

    /// <summary>Times the loop body plays, for <see cref="MidiEventKind.LoopEnd"/>. 0 = forever.</summary>
    public int RepeatCount => (int)Packed;

    public MidiEvent WithTime(long timeUs) => this with { TimeUs = timeUs };
}
