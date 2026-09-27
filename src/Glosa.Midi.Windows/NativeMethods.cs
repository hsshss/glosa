using System.Runtime.InteropServices;

namespace Glosa.Midi.Windows;

internal static unsafe partial class NativeMethods
{
    private const string WinMM = "winmm.dll";

    internal const uint MMSYSERR_NOERROR = 0;
    internal const uint CALLBACK_NULL = 0;
    internal const uint CALLBACK_FUNCTION = 0x00030000;

    internal const uint MIM_DATA = 0x3C3;
    internal const uint MIM_LONGDATA = 0x3C4;
    internal const uint MIM_LONGERROR = 0x3C6;

    /// <summary>MIDIERR_BASE + 1: the device still has something queued.</summary>
    internal const uint MIDIERR_STILLPLAYING = 65;

    internal const uint MHDR_DONE = 0x00000001;
    internal const uint MHDR_PREPARED = 0x00000002;
    internal const uint MHDR_INQUEUE = 0x00000004;

    /// <summary>
    /// A device's name from its capabilities: up to the first NUL, since what follows it is
    /// whatever the driver left in the buffer.
    /// </summary>
    internal static string NameOf(char* szPname)
    {
        var name = new ReadOnlySpan<char>(szPname, 32);
        int end = name.IndexOf('\0');
        return new string(end < 0 ? name : name[..end]);
    }

    /// <summary>MIDIOUTCAPSW. Blittable so it can be used from a source-generated P/Invoke.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MidiOutCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        public fixed char szPname[32];
        public ushort wTechnology;
        public ushort wVoices;
        public ushort wNotes;
        public ushort wChannelMask;
        public uint dwSupport;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MidiHdr
    {
        public nint lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public nint dwUser;
        public uint dwFlags;
        public nint lpNext;
        public nint reserved;
        public uint dwOffset;
        public fixed long dwReserved[8];
    }

    [LibraryImport(WinMM)]
    internal static partial uint midiOutGetNumDevs();

    [LibraryImport(WinMM, EntryPoint = "midiOutGetDevCapsW")]
    internal static partial uint midiOutGetDevCaps(nuint uDeviceID, MidiOutCaps* pmoc, uint cbmoc);

    [LibraryImport(WinMM)]
    internal static partial uint midiOutOpen(
        nint* phmo, uint uDeviceID, nint dwCallback, nint dwInstance, uint fdwOpen);

    [LibraryImport(WinMM)]
    internal static partial uint midiOutClose(nint hmo);

    [LibraryImport(WinMM)]
    internal static partial uint midiOutShortMsg(nint hmo, uint dwMsg);

    [LibraryImport(WinMM)]
    internal static partial uint midiOutLongMsg(nint hmo, MidiHdr* pmh, uint cbmh);

    [LibraryImport(WinMM)]
    internal static partial uint midiOutPrepareHeader(nint hmo, MidiHdr* pmh, uint cbmh);

    [LibraryImport(WinMM)]
    internal static partial uint midiOutUnprepareHeader(nint hmo, MidiHdr* pmh, uint cbmh);

    [LibraryImport(WinMM)]
    internal static partial uint midiOutReset(nint hmo);

    [LibraryImport(WinMM, EntryPoint = "midiOutGetErrorTextW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint midiOutGetErrorText(uint mmrError, char* pszText, uint cchText);

    /// <summary>MIDIINCAPSW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MidiInCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        public fixed char szPname[32];
        public uint dwSupport;
    }

    [LibraryImport(WinMM)]
    internal static partial uint midiInGetNumDevs();

    [LibraryImport(WinMM, EntryPoint = "midiInGetDevCapsW")]
    internal static partial uint midiInGetDevCaps(nuint uDeviceID, MidiInCaps* pmic, uint cbmic);

    [LibraryImport(WinMM)]
    internal static partial uint midiInOpen(
        nint* phmi, uint uDeviceID,
        delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, void> dwCallback,
        nint dwInstance, uint fdwOpen);

    [LibraryImport(WinMM)]
    internal static partial uint midiInClose(nint hmi);

    [LibraryImport(WinMM)]
    internal static partial uint midiInStart(nint hmi);

    [LibraryImport(WinMM)]
    internal static partial uint midiInStop(nint hmi);

    [LibraryImport(WinMM)]
    internal static partial uint midiInReset(nint hmi);

    [LibraryImport(WinMM)]
    internal static partial uint midiInPrepareHeader(nint hmi, MidiHdr* pmh, uint cbmh);

    [LibraryImport(WinMM)]
    internal static partial uint midiInUnprepareHeader(nint hmi, MidiHdr* pmh, uint cbmh);

    [LibraryImport(WinMM)]
    internal static partial uint midiInAddBuffer(nint hmi, MidiHdr* pmh, uint cbmh);

    internal static string DescribeError(uint code)
    {
        const int Cap = 256;
        char* buf = stackalloc char[Cap];
        return midiOutGetErrorText(code, buf, Cap) == MMSYSERR_NOERROR
            ? new string(buf)
            : $"winmm error {code}";
    }

    internal static void ThrowIfError(uint code, string what)
    {
        if (code != MMSYSERR_NOERROR)
            throw new MidiDeviceException($"{what} failed: {DescribeError(code)} (code {code})");
    }
}
