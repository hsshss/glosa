using System.Runtime.InteropServices;

namespace Glosa.Midi.Linux;

/// <summary>The ALSA sequencer, from alsa-lib.</summary>
/// <remarks>
/// From alsa-lib's <c>seq.h</c>, <c>seq_event.h</c> and <c>seq_midi_event.h</c>. A handle
/// (<c>snd_seq_t *</c>) and the info records are opaque pointers; every call that can fail
/// answers a negative errno.
/// </remarks>
internal static unsafe partial class NativeMethods
{
    internal const string Asound = "libasound.so.2";

    // --- snd_seq_open ---

    internal const int OpenOutput = 1;
    internal const int OpenInput = 2;
    internal const int NonBlock = 1;

    // --- port capabilities and types ---

    internal const uint CapRead = 1 << 0;
    internal const uint CapWrite = 1 << 1;
    internal const uint CapSubsRead = 1 << 5;
    internal const uint CapSubsWrite = 1 << 6;
    internal const uint CapNoExport = 1 << 7;

    internal const uint TypeMidiGeneric = 1 << 1;
    internal const uint TypeSynth = 1 << 10;
    /// <summary>A port on a device driven by the kernel: a USB MIDI interface, a sound card's MIDI out.</summary>
    internal const uint TypeHardware = 1 << 16;
    internal const uint TypeApplication = 1 << 20;

    // --- events ---

    internal const byte EventSysEx = 130;
    internal const byte EventNone = 255;

    /// <summary><c>SND_SEQ_EVENT_LENGTH_VARIABLE</c>: the data is <c>ext</c>, bytes elsewhere.</summary>
    internal const byte LengthVariable = 1 << 2;

    /// <summary><c>SND_SEQ_QUEUE_DIRECT</c>: delivered as it is written, not scheduled.</summary>
    internal const byte QueueDirect = 253;

    internal const short PollIn = 1;

    internal const int EAGAIN = 11;
    internal const int ENOMEM = 12;
    internal const int ENOSPC = 28;

    /// <summary>
    /// <c>snd_seq_event_t</c>: 28 bytes, the same on every architecture. The time stamp is
    /// left zero (sent direct), and of the data only <c>ext</c> — a SysEx's length and
    /// where its bytes are — is read or written here; alsa-lib's MIDI encoder and decoder
    /// fill in and read the rest.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    internal struct SeqEvent
    {
        [FieldOffset(0)] public byte Type;
        [FieldOffset(1)] public byte Flags;
        [FieldOffset(2)] public byte Tag;
        [FieldOffset(3)] public byte Queue;
        [FieldOffset(12)] public byte SourceClient;
        [FieldOffset(13)] public byte SourcePort;
        [FieldOffset(14)] public byte DestClient;
        [FieldOffset(15)] public byte DestPort;
        // snd_seq_ev_ext_t is packed: the pointer follows the length straight on.
        [FieldOffset(16)] public uint ExtLength;
        [FieldOffset(20)] public nint ExtPointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    // --- the handle ---

    [LibraryImport(Asound, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int snd_seq_open(nint* handle, string name, int streams, int mode);

    [LibraryImport(Asound)]
    internal static partial int snd_seq_close(nint seq);

    [LibraryImport(Asound, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int snd_seq_set_client_name(nint seq, string name);

    [LibraryImport(Asound)]
    internal static partial int snd_seq_client_id(nint seq);

    [LibraryImport(Asound)]
    internal static partial int snd_seq_poll_descriptors_count(nint seq, short events);

    [LibraryImport(Asound)]
    internal static partial int snd_seq_poll_descriptors(nint seq, PollFd* fds, uint space, short events);

    // --- ports and connections ---

    [LibraryImport(Asound, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int snd_seq_create_simple_port(nint seq, string name, uint caps, uint type);

    [LibraryImport(Asound)]
    internal static partial int snd_seq_connect_from(nint seq, int myPort, int srcClient, int srcPort);

    // --- listing clients and ports ---

    [LibraryImport(Asound)]
    internal static partial int snd_seq_client_info_malloc(nint* info);

    [LibraryImport(Asound)]
    internal static partial void snd_seq_client_info_free(nint info);

    [LibraryImport(Asound)]
    internal static partial void snd_seq_client_info_set_client(nint info, int client);

    [LibraryImport(Asound)]
    internal static partial int snd_seq_client_info_get_client(nint info);

    [LibraryImport(Asound)]
    internal static partial nint snd_seq_client_info_get_name(nint info);

    [LibraryImport(Asound)]
    internal static partial int snd_seq_query_next_client(nint seq, nint info);

    [LibraryImport(Asound)]
    internal static partial int snd_seq_port_info_malloc(nint* info);

    [LibraryImport(Asound)]
    internal static partial void snd_seq_port_info_free(nint info);

    [LibraryImport(Asound)]
    internal static partial void snd_seq_port_info_set_client(nint info, int client);

    [LibraryImport(Asound)]
    internal static partial void snd_seq_port_info_set_port(nint info, int port);

    [LibraryImport(Asound)]
    internal static partial int snd_seq_port_info_get_port(nint info);

    [LibraryImport(Asound)]
    internal static partial nint snd_seq_port_info_get_name(nint info);

    [LibraryImport(Asound)]
    internal static partial uint snd_seq_port_info_get_capability(nint info);

    [LibraryImport(Asound)]
    internal static partial uint snd_seq_port_info_get_type(nint info);

    [LibraryImport(Asound)]
    internal static partial int snd_seq_query_next_port(nint seq, nint info);

    // --- events in and out ---

    [LibraryImport(Asound)]
    internal static partial int snd_seq_event_output_direct(nint seq, SeqEvent* ev);

    [LibraryImport(Asound)]
    internal static partial int snd_seq_event_input(nint seq, SeqEvent** ev);

    // --- MIDI bytes to and from events ---

    [LibraryImport(Asound)]
    internal static partial int snd_midi_event_new(nuint bufferSize, nint* dev);

    [LibraryImport(Asound)]
    internal static partial void snd_midi_event_free(nint dev);

    [LibraryImport(Asound)]
    internal static partial void snd_midi_event_reset_encode(nint dev);

    [LibraryImport(Asound)]
    internal static partial void snd_midi_event_no_status(nint dev, int on);

    [LibraryImport(Asound)]
    internal static partial nint snd_midi_event_encode(nint dev, byte* buffer, nint count, SeqEvent* ev);

    [LibraryImport(Asound)]
    internal static partial nint snd_midi_event_decode(nint dev, byte* buffer, nint count, SeqEvent* ev);

    [LibraryImport(Asound)]
    private static partial nint snd_strerror(int error);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int poll(PollFd* fds, nuint count, int timeout);

    /// <summary>Whether alsa-lib is there to be called.</summary>
    internal static bool IsAvailable { get; } = OperatingSystem.IsLinux() && NativeLibrary.TryLoad(Asound, out _);

    internal static string? ReadString(nint str) => str == 0 ? null : Marshal.PtrToStringUTF8(str);

    internal static void ThrowIfError(int result, string what)
    {
        if (result < 0)
            throw new MidiDeviceException($"{what} failed: {ReadString(snd_strerror(result))} (code {result})");
    }
}
