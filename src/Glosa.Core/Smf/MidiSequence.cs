namespace Glosa.Core.Smf;

/// <summary>
/// A parsed SMF, flattened into one time-ordered event array ready for playback.
/// Variable-length payloads (SysEx and meta bytes) live in a single block so that
/// nothing has to be allocated per event while playing.
/// </summary>
public sealed class MidiSequence
{
    internal MidiSequence(
        int format,
        int trackCount,
        int division,
        MidiEvent[] events,
        byte[] payload,
        long durationUs,
        string title,
        string copyright,
        string comment,
        IReadOnlyList<string> texts)
    {
        Format = format;
        TrackCount = trackCount;
        Division = division;
        Events = events;
        Payload = payload;
        DurationUs = durationUs;
        Title = title;
        Copyright = copyright;
        Comment = comment;
        Texts = texts;
    }

    public int Format { get; }

    public int TrackCount { get; }

    /// <summary>Raw division word from the header.</summary>
    public int Division { get; }

    /// <summary>Events ordered by <see cref="MidiEvent.TimeUs"/>, then by track.</summary>
    public MidiEvent[] Events { get; }

    /// <summary>Backing block for SysEx and meta payloads.</summary>
    public byte[] Payload { get; }

    public long DurationUs { get; }

    /// <summary>Track name of the first track carrying one; used for module auto-detection.</summary>
    public string Title { get; }

    /// <summary>
    /// The copyright notice (<c>FF 02</c>), or empty.
    /// </summary>
    /// <remarks>
    /// The only meta event whose defined meaning is "about this work", and where the habit
    /// is to write who made it. SMF has no field for a composer, so this and
    /// <see cref="Comment"/> are as close as the format gets.
    /// </remarks>
    public string Copyright { get; }

    /// <summary>The first text event (<c>FF 01</c>), or empty.</summary>
    /// <remarks>
    /// Free text, conventionally at the head of the first track, which is where credits and
    /// notes land when the writer did not use the copyright event. Only the first is kept:
    /// a block of them is a paragraph, and one line is what a display has room for.
    /// </remarks>
    public string Comment { get; }

    /// <summary>Text and marker events, in order. Also feeds module auto-detection.</summary>
    public IReadOnlyList<string> Texts { get; }

    /// <summary>Highest port index referenced by any event.</summary>
    public int MaxPort
    {
        get
        {
            int max = 0;
            foreach (MidiEvent e in Events)
                if (e.Port > max) max = e.Port;
            return max;
        }
    }

    public ReadOnlySpan<byte> GetData(in MidiEvent e)
        => Payload.AsSpan(e.DataOffset, e.DataLength);

    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationUs / 1000.0);
}
