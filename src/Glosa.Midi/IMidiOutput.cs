namespace Glosa.Midi;

/// <summary>A MIDI output device that can be opened for sending.</summary>
public interface IMidiOutput : IDisposable
{
    /// <summary>The device this instance was created for.</summary>
    MidiDeviceInfo Device { get; }

    bool IsOpen { get; }

    void Open();

    /// <summary>
    /// Sends a channel/system-common message packed as
    /// <c>status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16</c>.
    /// </summary>
    void SendShort(uint packedMessage);

    /// <summary>Sends a complete SysEx message, including the leading F0 and trailing F7.</summary>
    void SendLong(ReadOnlySpan<byte> sysEx);

    /// <summary>
    /// Waits, for a bounded time, until the long messages handed over so far have gone out.
    /// A backend that sends them within <see cref="SendLong"/> has nothing to wait for.
    /// </summary>
    void WaitUntilSent() { }

    /// <summary>
    /// Whether what is sent goes down a MIDI cable with no more room in front of it than the
    /// cable empties, so it must not be handed things faster than a cable carries them.
    /// </summary>
    /// <remarks>
    /// Only a backend that throws away what does not fit, without a word, says so.
    /// </remarks>
    bool IsCable => false;

    /// <summary>Turns off all notes and resets controllers on the device.</summary>
    void Reset();

    /// <param name="reset">
    /// Turns everything off on the way out, as <see cref="Reset"/> does. What
    /// <see cref="IDisposable.Dispose"/> does.
    /// </param>
    void Close(bool reset = true);

    /// <summary>
    /// Raised, on the sending thread, after every note was turned off because the other end
    /// may have thrown away messages it had been sent — the note-offs among them.
    /// </summary>
    /// <remarks>
    /// Only a backend whose destination can lose what it has already taken raises it.
    /// </remarks>
    event Action? Silenced { add { } remove { } }

    /// <summary>Long messages dropped because the device was not taking them fast enough.</summary>
    int DroppedLongMessages { get; }

    /// <summary>What went wrong the last time the device was closed, or null.</summary>
    /// <remarks>
    /// Not thrown: closing happens on the way out, where nobody catches it, and a device that
    /// would not close is one the next run cannot open.
    /// </remarks>
    string? CloseError { get; }
}
