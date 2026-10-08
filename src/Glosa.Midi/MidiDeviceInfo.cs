namespace Glosa.Midi;

/// <summary>Identifies an output device offered by a backend.</summary>
/// <param name="Id">
/// What the backend calls it this run (on WinMM, its position in the enumeration); not for
/// storing, which uses <paramref name="Name"/>.
/// </param>
/// <param name="Name">Display name, and what the player stores.</param>
/// <param name="OtherName">
/// The name the device goes by when the list around it is different, or null: a name stored
/// then still finds it (<see cref="DeviceName.Find"/>). Only ALSA has one, since whether it
/// adds the client's name depends on the other ports there are.
/// </param>
/// <param name="Kind">What it leads to, so lists can tell them apart.</param>
public readonly record struct MidiDeviceInfo(string Id, string Name, string? OtherName = null,
                                             MidiDeviceKind Kind = MidiDeviceKind.Port)
{
    public override string ToString() => Name;
}

/// <summary>What an output device leads to.</summary>
public enum MidiDeviceKind
{
    /// <summary>A MIDI port the system offers: a machine on a cable, or another program.</summary>
    Port,

    /// <summary>An audio plugin the player hosts itself.</summary>
    AudioPlugin,
}
