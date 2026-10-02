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
    /// <summary>The MIDI outputs, or null where the platform has none the player can use.</summary>
    /// <remarks>
    /// Made the first time it is asked for, which is on the UI thread (for CoreMIDI's run loop).
    /// </remarks>
    public static IMidiOutputFactory? MidiOutputs { get; } = CreateMidiOutputs();

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
