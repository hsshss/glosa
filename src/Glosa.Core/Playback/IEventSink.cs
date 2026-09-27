namespace Glosa.Core.Playback;

/// <summary>
/// Where the sequencer hands finished events. Sinks are chained: the emulation layer,
/// Capital Tone Fallback, and last the ports (<see cref="PortSink"/>).
/// </summary>
public interface IEventSink
{
    /// <summary>
    /// How many ports there are: A to F. A message for a port past the last one is dropped.
    /// </summary>
    public const int PortCount = 6;

    void SendShort(int port, uint packedMessage);
    void SendLong(int port, ReadOnlySpan<byte> sysEx);

    /// <summary>
    /// Waits until what has been handed on for <paramref name="port"/> has gone out, for a
    /// message that must not go out in the middle of it. A sink that hands everything on at
    /// once has nothing to wait for.
    /// </summary>
    void WaitUntilSent(int port) { }

    /// <summary>
    /// Whether <paramref name="port"/> goes to an output that must not be handed things
    /// faster than a MIDI cable carries them (<see cref="Glosa.Midi.IMidiOutput.IsCable"/>).
    /// </summary>
    bool IsCable(int port) => false;
}
