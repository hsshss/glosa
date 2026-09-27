namespace Glosa.Midi;

/// <summary>
/// An output as a port map names it: by name, and which of the devices with that name it is,
/// counted from 0 in the order the backend lists them. Names compare without regard to case.
/// </summary>
/// <remarks>
/// The device is looked up again by name and number each time (<see cref="Find"/>), as the
/// list's order moves when devices come and go.
///
/// A name no device has is looked for among the devices' other names
/// (<see cref="MidiDeviceInfo.OtherName"/>), counted the same way.
/// </remarks>
public readonly record struct DeviceName(string Name, int Nth)
{
    /// <summary>
    /// The device each port leads to, from the names the ports are set to; null for an unused
    /// port (an empty name).
    /// </summary>
    /// <remarks>
    /// The first port naming a machine takes the first device by that name, the second port
    /// the second, and so on. A port past the devices there are, or whose machine is not
    /// there, takes the first.
    /// </remarks>
    public static DeviceName?[] ForPorts(IReadOnlyList<string> ports, IReadOnlyList<MidiDeviceInfo> devices)
    {
        var names = new DeviceName?[ports.Count];
        for (int port = 0; port < ports.Count; port++)
        {
            string name = ports[port];
            if (name.Length == 0) continue;

            int nth = ports.Take(port).Count(other => Same(other, name));
            int there = devices.Count(Named(name, devices));
            names[port] = new DeviceName(name, nth < there ? nth : 0);
        }

        return names;
    }

    /// <summary>The device this names among <paramref name="devices"/>, or null when it is not there.</summary>
    public MidiDeviceInfo? Find(IReadOnlyList<MidiDeviceInfo> devices)
    {
        Func<MidiDeviceInfo, bool> named = Named(Name, devices);
        int nth = Nth;
        foreach (MidiDeviceInfo device in devices)
            if (named(device) && nth-- == 0)
                return device;
        return null;
    }

    /// <summary>
    /// What picks out the devices <paramref name="name"/> means: those it is the name of, or,
    /// when there are none, those it is the other name of.
    /// </summary>
    private static Func<MidiDeviceInfo, bool> Named(string name, IReadOnlyList<MidiDeviceInfo> devices)
        => devices.Any(device => Same(device.Name, name))
            ? device => Same(device.Name, name)
            : device => device.OtherName is { } other && Same(other, name);

    public bool Equals(DeviceName other) => Nth == other.Nth && Same(Name, other.Name);

    public override int GetHashCode()
        => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Name), Nth);

    /// <summary>The name, with "#2" and on for the second device by that name and after.</summary>
    public override string ToString() => Nth == 0 ? Name : $"{Name} #{Nth + 1}";

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
