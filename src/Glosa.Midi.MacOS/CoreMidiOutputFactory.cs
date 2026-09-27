using static Glosa.Midi.MacOS.NativeMethods;

namespace Glosa.Midi.MacOS;

/// <summary>Enumerates the MIDI destinations CoreMIDI offers.</summary>
/// <remarks>
/// Devices plugged in, the IAC Driver's buses and the virtual ports other programs make.
/// </remarks>
public sealed class CoreMidiOutputFactory : IMidiOutputFactory
{
    public static bool IsSupported => OperatingSystem.IsMacOS();

    /// <remarks>Starts CoreMIDI on the calling thread (<see cref="CoreMidiClient.TryStart"/>).</remarks>
    public CoreMidiOutputFactory() => CoreMidiClient.TryStart();

    public string BackendName => "CoreMIDI";

    public IReadOnlyList<MidiDeviceInfo> Enumerate()
        => [.. Destinations().Select(entry => entry.Info)];

    /// <summary>Opens by index, or by the name the system shows.</summary>
    public IMidiOutput Create(string deviceId)
        => CoreMidiClient.Find(Destinations(), deviceId) is { } found
            ? new CoreMidiOutput(found.Info, found.Endpoint)
            : throw new MidiDeviceException($"CoreMIDI destination '{deviceId}' is not present.");

    private static IReadOnlyList<(MidiDeviceInfo Info, uint Endpoint)> Destinations()
        => CoreMidiClient.List(MIDIGetNumberOfDestinations, MIDIGetDestination);
}
