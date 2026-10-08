namespace Glosa.Midi;

/// <summary>
/// An output as a port map names it: name, kind, and which of the devices of that name and
/// kind it is, from 0 in list order. Names ignore case.
/// </summary>
/// <remarks>
/// The device is looked up again by name and number each time (<see cref="Find"/>), as the
/// list's order moves when devices come and go.
///
/// Stored as one string (<see cref="Key"/>): a port's name, or a plugin's after
/// <see cref="PluginPrefix"/>, so the two never mix.
///
/// A name no device has is looked for among the devices' other names
/// (<see cref="MidiDeviceInfo.OtherName"/>), counted the same way.
/// </remarks>
public readonly record struct DeviceName(string Name, int Nth, MidiDeviceKind Kind = MidiDeviceKind.Port)
{
    /// <summary>What a stored audio plugin's name starts with.</summary>
    public const string PluginPrefix = "plugin:";

    /// <summary>How a port map stores this device.</summary>
    public string Key => KeyOf(Name, Kind);

    /// <summary>How a port map stores <paramref name="device"/>.</summary>
    public static string KeyOf(MidiDeviceInfo device) => KeyOf(device.Name, device.Kind);

    private static string KeyOf(string name, MidiDeviceKind kind)
        => kind == MidiDeviceKind.AudioPlugin ? PluginPrefix + name : name;

    /// <summary>The name and kind of the device a port map stores as <paramref name="key"/>.</summary>
    public static (string Name, MidiDeviceKind Kind) Parse(string key)
        => key.StartsWith(PluginPrefix, StringComparison.Ordinal)
            ? (key[PluginPrefix.Length..], MidiDeviceKind.AudioPlugin)
            : (key, MidiDeviceKind.Port);

    /// <summary>
    /// The device each port leads to, from what the ports are set to (<see cref="Key"/>); null
    /// for an unused port (an empty key).
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
            string key = ports[port];
            if (key.Length == 0) continue;

            (string name, MidiDeviceKind kind) = Parse(key);
            int nth = ports.Take(port).Count(other => Same(other, key));
            int there = devices.Count(Named(name, kind, devices));
            names[port] = new DeviceName(name, nth < there ? nth : 0, kind);
        }

        return names;
    }

    /// <summary>The device this names among <paramref name="devices"/>, or null when it is not there.</summary>
    public MidiDeviceInfo? Find(IReadOnlyList<MidiDeviceInfo> devices)
    {
        Func<MidiDeviceInfo, bool> named = Named(Name, Kind, devices);
        int nth = Nth;
        foreach (MidiDeviceInfo device in devices)
            if (named(device) && nth-- == 0)
                return device;
        return null;
    }

    /// <summary>
    /// Picks the devices of <paramref name="kind"/> named <paramref name="name"/>, or failing
    /// that, with it as their other name.
    /// </summary>
    private static Func<MidiDeviceInfo, bool> Named(string name, MidiDeviceKind kind, IReadOnlyList<MidiDeviceInfo> devices)
        => devices.Any(device => device.Kind == kind && Same(device.Name, name))
            ? device => device.Kind == kind && Same(device.Name, name)
            : device => device.Kind == kind && device.OtherName is { } other && Same(other, name);

    public bool Equals(DeviceName other) => Nth == other.Nth && Kind == other.Kind && Same(Name, other.Name);

    public override int GetHashCode()
        => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Name), Nth, Kind);

    /// <summary>The name, with "#2" and on for the second device by that name and after.</summary>
    public override string ToString() => Nth == 0 ? Name : $"{Name} #{Nth + 1}";

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
