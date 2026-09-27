using System.Runtime.InteropServices;
using static Glosa.Midi.MacOS.NativeMethods;

namespace Glosa.Midi.MacOS;

/// <summary>MIDI output through CoreMIDI.</summary>
public sealed unsafe class CoreMidiOutput : IMidiOutput
{
    /// <summary>
    /// The event list's room. A SysEx longer than fits goes out in several lists, one after
    /// another, which is what CoreMIDI asks for past 64 KB anyway.
    /// </summary>
    private const int ListSize = 16 * 1024;

    private readonly uint _endpoint;
    private readonly Lock _gate = new();
    private readonly UmpEncoder _encoder = new();
    private readonly List<uint> _words = new(64);

    private byte* _list;
    private uint _port;
    private bool _disposed;

    internal CoreMidiOutput(MidiDeviceInfo device, uint endpoint)
    {
        Device = device;
        _endpoint = endpoint;
    }

    public MidiDeviceInfo Device { get; }

    public bool IsOpen => _list != null;

    /// <inheritdoc/>
    /// <remarks>Here always 0: CoreMIDI queues whatever it is given.</remarks>
    public int DroppedLongMessages => 0;

    /// <inheritdoc/>
    /// <remarks>Here: a send refused while the device was being put at rest on the way out.</remarks>
    public string? CloseError { get; private set; }

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_list != null) return;
            _port = CoreMidiClient.OutputPort;
            _list = (byte*)NativeMemory.Alloc(ListSize);
        }
    }

    public void SendShort(uint packedMessage)
    {
        // Under the same lock as Close, so nothing is written into a list that has been freed.
        lock (_gate)
        {
            if (_list == null) return;
            _words.Clear();
            UmpEncoder.Short(packedMessage, _words);
            Send("MIDISendEventList");
        }
    }

    public void SendLong(ReadOnlySpan<byte> sysEx)
    {
        if (sysEx.IsEmpty) return;

        lock (_gate)
        {
            if (_list == null) return;
            _words.Clear();
            _encoder.Write(sysEx, _words);
            Send("MIDISendEventList");
        }
    }

    /// <summary>Turns everything off, as <c>midiOutReset</c> does on Windows.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_list == null) return;
            _words.Clear();
            SilenceInto(_words);
            Send("Reset");
        }
    }

    public void Close(bool reset = true)
    {
        lock (_gate)
        {
            if (_list == null) return;

            // Silenced on the way out unless told not to, as WinMM closes with midiOutReset.
            CloseError = null;
            if (reset)
            {
                _words.Clear();
                SilenceInto(_words);
                try
                {
                    Send("Reset");
                }
                catch (MidiDeviceException ex)
                {
                    // Gone already — unplugged, most likely. Closing goes ahead all the same.
                    CloseError = ex.Message;
                }
            }

            NativeMemory.Free(_list);
            _list = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    /// <summary>What a reset sends: any open SysEx closed, then sustain off and All Notes Off.</summary>
    private void SilenceInto(List<uint> words)
    {
        _encoder.Reset(words);
        for (uint ch = 0; ch < 16; ch++)
        {
            UmpEncoder.Short(0xB0 | ch | 0x40 << 8, words);   // Sustain off
            UmpEncoder.Short(0xB0 | ch | 0x7B << 8, words);   // All Notes Off
        }
    }

    /// <summary>Sends <see cref="_words"/>, in as many event lists as it takes.</summary>
    private void Send(string what)
    {
        if (_words.Count == 0) return;

        byte* packet = MIDIEventListInit(_list, Protocol1_0);
        bool pending = false;
        Span<uint> words = CollectionsMarshal.AsSpan(_words);

        for (int i = 0; i < words.Length;)
        {
            int count = Math.Min(UmpEncoder.WordsIn(words[i]), words.Length - i);
            fixed (uint* message = &words[i])
            {
                // Time 0 is now.
                byte* next = MIDIEventListAdd(_list, ListSize, packet, 0, (nuint)count, message);
                if (next == null)
                {
                    // Full: send what there is, and start the next list with this message.
                    if (!pending) throw new MidiDeviceException($"{what}: message too long for an event list");
                    ThrowIfError(MIDISendEventList(_port, _endpoint, _list), what);
                    packet = MIDIEventListInit(_list, Protocol1_0);
                    pending = false;
                    continue;
                }
                packet = next;
            }
            pending = true;
            i += count;
        }

        if (pending) ThrowIfError(MIDISendEventList(_port, _endpoint, _list), what);
    }
}
