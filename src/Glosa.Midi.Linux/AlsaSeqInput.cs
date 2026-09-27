using System.Diagnostics;
using System.Runtime.CompilerServices;
using static Glosa.Midi.Linux.NativeMethods;

namespace Glosa.Midi.Linux;

/// <summary>MIDI capture through the ALSA sequencer.</summary>
/// <remarks>
/// Each whole message is copied into a preallocated ring, so recording allocates nothing.
/// </remarks>
public sealed unsafe class AlsaSeqInput : IMidiInput
{
    /// <summary>Room for the messages of one run; a capture that fills it is reported.</summary>
    private const int MaxMessages = 1 << 20;
    private const int BlobSize = 8 * 1024 * 1024;

    /// <summary>How often the reading thread looks up to see whether it should stop.</summary>
    private const int PollMilliseconds = 100;

    private readonly SeqAddress _source;
    private readonly Lock _gate = new();

    private readonly byte[] _blob = new byte[BlobSize];
    private readonly Entry[] _entries = new Entry[MaxMessages];

    private nint _seq;
    private nint _decoder;
    private Thread? _reader;
    private volatile bool _stopping;
    private volatile bool _running;
    private long _startTicks;
    private int _count;
    private int _blobUsed;
    private int _overflows;
    private bool _disposed;

    // The SysEx being put back together, on the reading thread only.
    private byte[] _sysEx = new byte[64 * 1024];
    private int _sysExLength;
    private bool _inSysEx;
    private uint _sysExTime;

    internal AlsaSeqInput(MidiDeviceInfo device, SeqAddress source)
    {
        Device = device;
        _source = source;
    }

    public MidiDeviceInfo Device { get; }

    public bool IsOpen => _seq != 0;

    public int Overflows => Volatile.Read(ref _overflows);

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_seq != 0) return;

            nint seq = AlsaSeqClient.Open(OpenInput, NonBlock);
            try
            {
                int port = snd_seq_create_simple_port(seq, "Glosa in", CapWrite | CapSubsWrite,
                                                      TypeMidiGeneric | TypeApplication);
                ThrowIfError(port, "snd_seq_create_simple_port");
                ThrowIfError(snd_seq_connect_from(seq, port, _source.Client, _source.Port),
                             "snd_seq_connect_from");

                nint decoder;
                ThrowIfError(snd_midi_event_new(16, &decoder), "snd_midi_event_new");
                // Every message with its status byte, as it is recorded elsewhere.
                snd_midi_event_no_status(decoder, 1);
                _decoder = decoder;
            }
            catch
            {
                snd_seq_close(seq);
                throw;
            }

            _seq = seq;
            _stopping = false;
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ALSA MIDI input" };
            _reader.Start();
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_seq == 0 || _running) return;

            _count = 0;
            _blobUsed = 0;
            _overflows = 0;
            _startTicks = Stopwatch.GetTimestamp();
            _running = true;
        }
    }

    public void Stop()
    {
        lock (_gate) _running = false;
    }

    /// <summary>Takes what has been recorded. Call after <see cref="Stop"/>.</summary>
    public IReadOnlyList<CapturedMessage> Drain()
    {
        int count = Math.Min(Volatile.Read(ref _count), _entries.Length);
        var result = new List<CapturedMessage>(count);
        for (int i = 0; i < count; i++)
        {
            Entry e = _entries[i];
            result.Add(new CapturedMessage(e.TimeMs, _blob[e.Offset..(e.Offset + e.Length)]));
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            _running = false;
            if (_seq == 0) return;

            _stopping = true;
            _reader?.Join();
            _reader = null;

            // Closing the client takes its port and the connection with it.
            snd_seq_close(_seq);
            _seq = 0;
            snd_midi_event_free(_decoder);
            _decoder = 0;
        }
    }

    private void ReadLoop()
    {
        int count = snd_seq_poll_descriptors_count(_seq, PollIn);
        var fds = new PollFd[Math.Max(count, 1)];
        fixed (PollFd* pfds = fds)
        {
            count = snd_seq_poll_descriptors(_seq, pfds, (uint)fds.Length, PollIn);
            while (!_stopping)
            {
                poll(pfds, (nuint)count, PollMilliseconds);
                while (true)
                {
                    SeqEvent* ev;
                    int result = snd_seq_event_input(_seq, &ev);
                    if (result == -ENOSPC)
                    {
                        // The sequencer's queue for this port overflowed; events were lost.
                        Interlocked.Increment(ref _overflows);
                        continue;
                    }
                    if (result < 0) break;   // nothing more for now
                    if (_running) Take(ev);
                }
            }
        }
    }

    /// <summary>Milliseconds since <see cref="Start"/>.</summary>
    private uint Now() => (uint)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;

    private void Take(SeqEvent* ev)
    {
        uint time = Now();

        if (ev->Type == EventSysEx)
        {
            TakeSysEx(time, new ReadOnlySpan<byte>((void*)ev->ExtPointer, (int)ev->ExtLength));
            return;
        }

        Span<byte> message = stackalloc byte[16];
        long length;
        fixed (byte* bytes = message)
            length = (long)snd_midi_event_decode(_decoder, bytes, message.Length, ev);
        // Negative for what is not MIDI: the notices of connections being made, say.
        if (length > 0) Record(time, message[..(int)length]);
    }

    /// <summary>A piece of SysEx: whole once its F7 arrives.</summary>
    private void TakeSysEx(uint time, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b == 0xF0)
            {
                // One not finished is recorded as far as it got, as the other backends do.
                if (_inSysEx) Record(_sysExTime, _sysEx.AsSpan(0, _sysExLength));
                _inSysEx = true;
                _sysExTime = time;
                _sysExLength = 0;
            }
            if (!_inSysEx) continue;

            if (_sysExLength == _sysEx.Length) Array.Resize(ref _sysEx, _sysEx.Length * 2);
            _sysEx[_sysExLength++] = b;
            if (b == 0xF7)
            {
                Record(_sysExTime, _sysEx.AsSpan(0, _sysExLength));
                _inSysEx = false;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Record(uint timeMs, ReadOnlySpan<byte> data)
    {
        int slot = Interlocked.Increment(ref _count) - 1;
        int offset = Interlocked.Add(ref _blobUsed, data.Length) - data.Length;

        if (slot >= _entries.Length || offset + data.Length > _blob.Length)
        {
            Interlocked.Increment(ref _overflows);
            return;
        }

        data.CopyTo(_blob.AsSpan(offset));
        _entries[slot] = new Entry(timeMs, offset, data.Length);
    }

    private readonly record struct Entry(uint TimeMs, int Offset, int Length);
}

/// <summary>Opens <see cref="AlsaSeqInput"/> instances by <c>client:port</c> or name.</summary>
public sealed class AlsaSeqInputFactory : IMidiInputFactory
{
    public static bool IsSupported => IsAvailable;

    public string BackendName => "ALSA";

    public IReadOnlyList<MidiDeviceInfo> Enumerate()
        => [.. Sources().Select(entry => entry.Info)];

    public IMidiInput Create(string deviceId)
        => AlsaSeqClient.Find(Sources(), deviceId) is { } found
            ? new AlsaSeqInput(found.Info, found.Address)
            : throw new MidiDeviceException($"no MIDI input device '{deviceId}'");

    private static IReadOnlyList<(MidiDeviceInfo Info, SeqAddress Address, bool Hardware)> Sources()
        => AlsaSeqClient.List(NativeMethods.CapRead | NativeMethods.CapSubsRead);
}
