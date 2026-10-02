namespace Glosa.Midi.Windows;

/// <summary>
/// Enumerates the MIDI 1.0 output ports Windows MIDI Services lists, with the names and in
/// the order WinMM gives them.
/// </summary>
/// <remarks>
/// A port the service does not carry (the GS Wavetable Synth) is opened through WinMM, so
/// the list is the same whichever backend is picked.
/// </remarks>
public sealed class MidiServicesOutputFactory : IMidiOutputFactory
{
    private MidiServicesOutputFactory() { }

    public string BackendName => "Windows MIDI Services";

    /// <summary>
    /// The factory, or null where Windows MIDI Services cannot be used, with why in
    /// <paramref name="why"/>.
    /// </summary>
    public static MidiServicesOutputFactory? TryCreate(out string? why)
    {
        why = OperatingSystem.IsWindows() ? MidiServices.Start() : "not Windows";
        return why is null ? new MidiServicesOutputFactory() : null;
    }

    /// <remarks>
    /// Empty when the service will not answer, as WinMM's list is when it has no devices:
    /// the screens that show the list do not expect it to throw.
    /// </remarks>
    public IReadOnlyList<MidiDeviceInfo> Enumerate()
    {
        try
        {
            return [.. MidiServices.DestinationPorts().Select(port => new MidiDeviceInfo(port.Number.ToString(), port.Name))];
        }
        catch (MidiDeviceException)
        {
            return [];
        }
    }

    /// <summary>Opens by device number, or by name, as <see cref="WinMmOutputFactory"/> does.</summary>
    public IMidiOutput Create(string deviceId)
    {
        foreach (MidiServices.Port port in MidiServices.DestinationPorts())
        {
            if (port.Number.ToString() != deviceId
                && !string.Equals(port.Name, deviceId, StringComparison.OrdinalIgnoreCase))
                continue;

            var device = new MidiDeviceInfo(port.Number.ToString(), port.Name);
            return port.Endpoint.Length == 0
                ? new WinMmOutput(device, port.Number)
                : new MidiServicesOutput(device, port.Endpoint, port.Group);
        }
        throw new MidiDeviceException($"Windows MIDI Services port '{deviceId}' is not present.");
    }
}
