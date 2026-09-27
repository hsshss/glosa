namespace Glosa.Midi;

/// <summary>Enumerates and opens the output devices of one platform backend.</summary>
public interface IMidiOutputFactory
{
    /// <summary>Backend name, for diagnostics ("WinMM", "ALSA", ...).</summary>
    string BackendName { get; }

    IReadOnlyList<MidiDeviceInfo> Enumerate();

    /// <summary>Creates an unopened output for <paramref name="deviceId"/>.</summary>
    /// <exception cref="MidiDeviceException">No device with that id exists.</exception>
    IMidiOutput Create(string deviceId);
}
