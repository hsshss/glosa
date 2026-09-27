using System.Runtime.InteropServices;

namespace Glosa.Midi.MacOS;

/// <summary>CoreMIDI and the few CoreFoundation calls it needs.</summary>
/// <remarks>
/// From the macOS SDK's <c>MIDIServices.h</c>. Every CoreMIDI object — client, port,
/// endpoint — is a <c>UInt32</c> reference; every call answers an <c>OSStatus</c>.
/// </remarks>
internal static unsafe partial class NativeMethods
{
    private const string CoreMidi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    internal const int NoErr = 0;

    /// <summary><c>kMIDIProtocol_1_0</c>: event lists of MIDI 1.0 Universal MIDI Packets.</summary>
    internal const int Protocol1_0 = 1;

    // --- clients and ports ---

    [LibraryImport(CoreMidi)]
    internal static partial int MIDIClientCreate(nint name, nint notifyProc, nint notifyRefCon, uint* outClient);

    [LibraryImport(CoreMidi)]
    internal static partial int MIDIOutputPortCreate(uint client, nint portName, uint* outPort);

    [LibraryImport(CoreMidi)]
    internal static partial int MIDIInputPortCreate(
        uint client, nint portName, delegate* unmanaged<byte*, nint, nint, void> readProc,
        nint refCon, uint* outPort);

    [LibraryImport(CoreMidi)]
    internal static partial int MIDIPortConnectSource(uint port, uint source, nint connRefCon);

    [LibraryImport(CoreMidi)]
    internal static partial int MIDIPortDisconnectSource(uint port, uint source);

    [LibraryImport(CoreMidi)]
    internal static partial int MIDIPortDispose(uint port);

    // --- endpoints ---

    [LibraryImport(CoreMidi)]
    internal static partial nuint MIDIGetNumberOfDestinations();

    [LibraryImport(CoreMidi)]
    internal static partial uint MIDIGetDestination(nuint index);

    [LibraryImport(CoreMidi)]
    internal static partial nuint MIDIGetNumberOfSources();

    [LibraryImport(CoreMidi)]
    internal static partial uint MIDIGetSource(nuint index);

    [LibraryImport(CoreMidi)]
    internal static partial int MIDIObjectGetStringProperty(uint obj, nint propertyId, nint* str);

    // --- sending ---

    [LibraryImport(CoreMidi)]
    internal static partial byte* MIDIEventListInit(byte* list, int protocol);

    [LibraryImport(CoreMidi)]
    internal static partial byte* MIDIEventListAdd(
        byte* list, nuint listSize, byte* current, ulong time, nuint wordCount, uint* words);

    [LibraryImport(CoreMidi)]
    internal static partial int MIDISendEventList(uint port, uint destination, byte* list);

    // --- CoreFoundation strings ---

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringCreateWithCharacters(nint allocator, char* chars, nint length);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(nint str);

    [LibraryImport(CoreFoundation)]
    private static partial void CFStringGetCharacters(nint str, CFRange range, char* buffer);

    [LibraryImport(CoreFoundation)]
    internal static partial void CFRelease(nint obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct CFRange
    {
        public nint Location;
        public nint Length;
    }

    /// <summary><c>kMIDIPropertyDisplayName</c>: the name the system's own MIDI settings show.</summary>
    internal static nint DisplayNameProperty { get; } = Constant(CoreMidi, "kMIDIPropertyDisplayName");

    /// <summary>A <c>const CFStringRef</c> a framework exports.</summary>
    private static nint Constant(string library, string name)
    {
        if (!OperatingSystem.IsMacOS()) return 0;
        nint handle = NativeLibrary.Load(library);
        return *(nint*)NativeLibrary.GetExport(handle, name);
    }

    /// <summary>A new CFString, which the caller releases.</summary>
    internal static nint CreateString(string text)
    {
        fixed (char* chars = text) return CFStringCreateWithCharacters(0, chars, text.Length);
    }

    internal static string ReadString(nint str)
    {
        int length = (int)CFStringGetLength(str);
        var text = new string('\0', length);
        fixed (char* chars = text)
            CFStringGetCharacters(str, new CFRange { Location = 0, Length = length }, chars);
        return text;
    }

    /// <summary>The name an endpoint is shown under, or null when it has none.</summary>
    internal static string? NameOf(uint endpoint)
    {
        nint str;
        if (MIDIObjectGetStringProperty(endpoint, DisplayNameProperty, &str) != NoErr || str == 0)
            return null;
        try { return ReadString(str); }
        finally { CFRelease(str); }
    }

    /// <summary>The <c>MIDIServices.h</c> name of a CoreMIDI error, where it has one.</summary>
    internal static string DescribeError(int status) => status switch
    {
        -10830 => "kMIDIInvalidClient",
        -10831 => "kMIDIInvalidPort",
        -10832 => "kMIDIWrongEndpointType",
        -10833 => "kMIDINoConnection",
        -10834 => "kMIDIUnknownEndpoint",
        -10835 => "kMIDIUnknownProperty",
        -10836 => "kMIDIWrongPropertyType",
        -10837 => "kMIDINoCurrentSetup",
        -10838 => "kMIDIMessageSendErr",
        -10839 => "kMIDIServerStartErr",
        -10840 => "kMIDISetupFormatErr",
        -10841 => "kMIDIWrongThread",
        -10842 => "kMIDIObjectNotFound",
        -10843 => "kMIDIIDNotUnique",
        -10844 => "kMIDINotPermitted",
        -10845 => "kMIDIUnknownError",
        _ => $"CoreMIDI error {status}",
    };

    internal static void ThrowIfError(int status, string what)
    {
        if (status != NoErr)
            throw new MidiDeviceException($"{what} failed: {DescribeError(status)} (code {status})");
    }
}
