namespace Glosa.Midi;

/// <summary>Several backends' outputs as one list, in the order given. Their ids must not overlap.</summary>
public sealed class CombinedMidiOutputFactory(params IReadOnlyList<IMidiOutputFactory> factories) : IMidiOutputFactory
{
    public string BackendName => string.Join(" + ", factories.Select(factory => factory.BackendName));

    public IReadOnlyList<MidiDeviceInfo> Enumerate() => [.. factories.SelectMany(factory => factory.Enumerate())];

    public void WaitUntilListed()
    {
        foreach (IMidiOutputFactory factory in factories) factory.WaitUntilListed();
    }

    public IMidiOutput Create(string deviceId)
    {
        foreach (IMidiOutputFactory factory in factories)
            if (factory.Enumerate().Any(device => device.Id == deviceId))
                return factory.Create(deviceId);
        throw new MidiDeviceException($"no output device '{deviceId}'");
    }
}
