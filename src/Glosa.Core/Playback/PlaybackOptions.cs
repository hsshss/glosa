namespace Glosa.Core.Playback;

/// <summary>Sequencer thread priority.</summary>
public enum PlaybackPriority { Low, Normal, High }

public sealed class PlaybackOptions
{
    /// <summary>Calls <c>midiOutReset</c> on stop in addition to sending All Notes Off.</summary>
    public bool UseMidiOutReset { get; set; } = true;

    public PlaybackPriority Priority { get; set; } = PlaybackPriority.High;

    /// <summary>
    /// Caps the bytes a second handed to each port, every message counted, for hardware that
    /// cannot keep up. Zero means unlimited; 3125 matches the MIDI cable rate. A seek never
    /// goes faster than a cable, whatever this says, nor does a port whose output goes down
    /// one (<see cref="IEventSink.IsCable"/>).
    /// </summary>
    public int TransferRateBytesPerSecond { get; set; }

    /// <summary>
    /// Sends All Sound Off, All Notes Off and Reset All Controllers on every channel when
    /// playback comes to rest: stopped, at the end of the song, or on a protection error. A
    /// pause sends only the first two, since the controllers are wanted again on resume.
    /// </summary>
    /// <remarks>A seek silences the notes whatever this says.</remarks>
    public bool SendAllNotesOffOnStop { get; set; } = true;

    /// <summary>
    /// How many times a loop that the data says repeats forever actually plays before the
    /// sequencer moves on. Zero plays such a loop once.
    /// </summary>
    public int InfiniteLoopRepeatCount { get; set; } = 2;
}
