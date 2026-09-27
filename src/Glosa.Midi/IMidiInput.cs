namespace Glosa.Midi;

/// <summary>One message as it arrived at an input port.</summary>
/// <param name="TimeMs">Milliseconds since the port was started.</param>
/// <param name="Data">
/// The message itself. Short messages are 1-3 bytes; a SysEx is the whole run including
/// <c>F0</c> and <c>F7</c>.
/// </param>
public readonly record struct CapturedMessage(uint TimeMs, byte[] Data)
{
    public override string ToString() => $"{TimeMs,8} {Convert.ToHexString(Data)}";
}

/// <summary>
/// Records what arrives on a MIDI input port.
/// </summary>
/// <remarks>
/// For checking what the outputs send, by recording from a port they are routed back to.
/// </remarks>
public interface IMidiInput : IDisposable
{
    MidiDeviceInfo Device { get; }

    bool IsOpen { get; }

    /// <summary>Messages dropped because the capture buffer filled up.</summary>
    int Overflows { get; }

    void Open();

    /// <summary>Begins recording. Timestamps are measured from this call.</summary>
    void Start();

    void Stop();

    /// <summary>Takes everything recorded so far, in arrival order.</summary>
    IReadOnlyList<CapturedMessage> Drain();
}

/// <summary>Enumerates and opens the input devices of one platform backend.</summary>
public interface IMidiInputFactory
{
    string BackendName { get; }

    IReadOnlyList<MidiDeviceInfo> Enumerate();

    /// <exception cref="MidiDeviceException">No device with that id exists.</exception>
    IMidiInput Create(string deviceId);
}
