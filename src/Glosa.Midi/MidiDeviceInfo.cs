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
public readonly record struct MidiDeviceInfo(string Id, string Name, string? OtherName = null)
{
    public override string ToString() => Name;
}
