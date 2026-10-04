using Glosa.Core.Playback;
using Glosa.Midi;
#if WINDOWS
using Glosa.Midi.Windows;
#endif
#if MACOS
using Glosa.Midi.MacOS;
#endif
#if LINUX
using Glosa.Midi.Linux;
#endif
#if BRACK
using Glosa.Midi.Brack;
#endif

namespace Glosa.App.Services;

/// <summary>
/// The platform's MIDI outputs and timer: the one place the player picks them.
/// </summary>
/// <remarks>
/// Each backend is also checked at run time: a build made for no runtime in particular
/// carries the backend of the machine it was built on, and can be run elsewhere.
/// </remarks>
internal static class Backends
{
#if BRACK
    /// <summary>
    /// The audio plugins; null before <see cref="StartAudioPlugins"/>, or where Brack cannot
    /// load (<see cref="BrackUnavailable"/>).
    /// </summary>
    public static BrackRack? AudioPlugins { get; private set; }

    /// <summary>Why Brack cannot be used.</summary>
    public static string? BrackUnavailable { get; private set; }

    /// <summary>The plugin scan's cache.</summary>
    public static string PluginScanCachePath => Path.Combine(AppSettings.ConfigDirectory, "brack-plugin-cache.json");

    /// <summary>
    /// Loads the rack from the settings folder, listing its devices after the system's. For
    /// the player only, not the tests.
    /// </summary>
    public static void StartAudioPlugins()
    {
        AudioPlugins = BrackRack.TryCreate(Path.Combine(AppSettings.ConfigDirectory, "brack-session.json"), out string? why);
        BrackUnavailable = why;
        if (AudioPlugins is { } rack)
            MidiOutputs = MidiOutputs is { } system ? new CombinedMidiOutputFactory(system, rack.Outputs) : rack.Outputs;
    }
#endif

    /// <summary>The MIDI outputs, or null where the platform has none the player can use.</summary>
    /// <remarks>
    /// Made the first time it is asked for, which is on the UI thread (for CoreMIDI's run loop).
    /// </remarks>
    public static IMidiOutputFactory? MidiOutputs { get; private set; } = CreateMidiOutputs();

    /// <summary>Why Windows MIDI Services is not the backend, where WinMM is.</summary>
    public static string? MidiServicesUnavailable { get; private set; }

    /// <summary>What raises the system timer's resolution while playing, where there is one.</summary>
    public static IPlatformTimer Timer { get; } = CreateTimer();

    private static IMidiOutputFactory? CreateMidiOutputs()
    {
#if WINDOWS
        if (MidiServicesOutputFactory.TryCreate(out string? why) is { } services) return services;
        MidiServicesUnavailable = why;
        if (WinMmOutputFactory.IsSupported) return new WinMmOutputFactory();
#endif
#if MACOS
        if (CoreMidiOutputFactory.IsSupported) return new CoreMidiOutputFactory();
#endif
#if LINUX
        if (AlsaSeqOutputFactory.IsSupported) return new AlsaSeqOutputFactory();
#endif
        return null;
    }

    private static IPlatformTimer CreateTimer()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()) return new WinMmPlatformTimer();
#endif
        return NullPlatformTimer.Instance;
    }
}
