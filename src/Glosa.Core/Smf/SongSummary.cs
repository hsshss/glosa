namespace Glosa.Core.Smf;

/// <summary>
/// A song's length, title and opening messages (<see cref="SmfReader.ReadSummary(string, int, int)"/>).
/// </summary>
public sealed class SongSummary
{
    internal SongSummary(long durationUs, string title, MidiEvent[] opening, byte[] payload)
    {
        DurationUs = durationUs;
        Title = title;
        Opening = opening;
        Payload = payload;
    }

    /// <inheritdoc cref="MidiSequence.DurationUs"/>
    public long DurationUs { get; }

    /// <inheritdoc cref="MidiSequence.Title"/>
    public string Title { get; }

    /// <summary>The first channel and exclusive messages, in play order; times are not filled in.</summary>
    public MidiEvent[] Opening { get; }

    /// <summary>Backing block for the exclusive messages in <see cref="Opening"/>.</summary>
    public byte[] Payload { get; }

    public ReadOnlySpan<byte> GetData(in MidiEvent e)
        => Payload.AsSpan(e.DataOffset, e.DataLength);
}
