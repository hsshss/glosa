using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Glosa.Midi.MacOS.NativeMethods;

namespace Glosa.Midi.MacOS;

/// <summary>MIDI capture through CoreMIDI.</summary>
/// <remarks>
/// CoreMIDI calls back on a high-priority thread of its own. Each whole message is copied
/// into a preallocated ring, so recording allocates nothing.
/// </remarks>
public sealed unsafe class CoreMidiInput : IMidiInput
{
    /// <summary>Room for the messages of one run; a capture that fills it is reported.</summary>
    private const int MaxMessages = 1 << 20;
    private const int BlobSize = 8 * 1024 * 1024;

    private readonly uint _endpoint;
    private readonly Lock _gate = new();

    private readonly byte[] _blob = new byte[BlobSize];
    private readonly Entry[] _entries = new Entry[MaxMessages];

    private GCHandle _self;
    private uint _port;
    private volatile bool _running;
    private ulong _startTicks;
    private int _count;
    private int _blobUsed;
    private int _overflows;
    private bool _disposed;

    // The stream being read, on CoreMIDI's thread only.
    private byte[] _sysEx = new byte[64 * 1024];
    private int _sysExLength;
    private bool _inSysEx;
    private uint _sysExTime;
    private byte _status;
    private readonly byte[] _message = new byte[3];
    private int _messageLength;

    internal CoreMidiInput(MidiDeviceInfo device, uint endpoint)
    {
        Device = device;
        _endpoint = endpoint;
    }

    public MidiDeviceInfo Device { get; }

    public bool IsOpen => _port != 0;

    public int Overflows => Volatile.Read(ref _overflows);

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_port != 0) return;

            uint client = CoreMidiClient.Client;
            _self = GCHandle.Alloc(this, GCHandleType.Normal);
            try
            {
                nint name = CreateString("Glosa in");
                try
                {
                    uint port;
                    ThrowIfError(MIDIInputPortCreate(client, name, &Read, GCHandle.ToIntPtr(_self), &port),
                                 "MIDIInputPortCreate");
                    _port = port;
                }
                finally { CFRelease(name); }

                ThrowIfError(MIDIPortConnectSource(_port, _endpoint, 0), "MIDIPortConnectSource");
            }
            catch
            {
                // The handle keeps this object, and its buffers, alive until it is freed.
                Release();
                throw;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_port == 0 || _running) return;

            _count = 0;
            _blobUsed = 0;
            _overflows = 0;
            _startTicks = mach_absolute_time();
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
            Release();
        }
    }

    /// <summary>Lets go of the port and the handle, as far as opening got.</summary>
    private void Release()
    {
        if (_port != 0)
        {
            MIDIPortDisconnectSource(_port, _endpoint);
            // Once the port is gone, CoreMIDI calls nothing more with our handle.
            MIDIPortDispose(_port);
            _port = 0;
        }
        if (_self.IsAllocated) _self.Free();
    }

    /// <summary><c>MIDIReadProc</c>. Nothing may be thrown back into CoreMIDI, so nothing is.</summary>
    [UnmanagedCallersOnly]
    private static void Read(byte* list, nint readRefCon, nint sourceRefCon)
    {
        try
        {
            if (readRefCon == 0) return;
            if (GCHandle.FromIntPtr(readRefCon).Target is not CoreMidiInput self || !self._running) return;

            // MIDIPacketList: UInt32 numPackets, then the packets, packed to 4 bytes. A packet:
            // UInt64 timeStamp, UInt16 length, the bytes; the next one on a 4-byte boundary on
            // ARM, straight after on Intel (MIDIPacketNext).
            uint packets = Unsafe.ReadUnaligned<uint>(list);
            byte* packet = list + 4;
            for (uint i = 0; i < packets; i++)
            {
                ulong time = Unsafe.ReadUnaligned<ulong>(packet);
                ushort length = Unsafe.ReadUnaligned<ushort>(packet + 8);
                self.Take(self.Milliseconds(time), new ReadOnlySpan<byte>(packet + 10, length));

                byte* end = packet + 10 + length;
                packet = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm
                    ? (byte*)(((nuint)end + 3) & ~(nuint)3)
                    : end;
            }
        }
        catch
        {
            // A message lost.
        }
    }

    /// <summary>Milliseconds since <see cref="Start"/> of a packet's host time; now when it has none.</summary>
    private uint Milliseconds(ulong ticks)
    {
        if (ticks < _startTicks) ticks = mach_absolute_time();
        return (uint)((ticks - _startTicks) * TickNanoseconds / 1_000_000);
    }

    /// <summary>Reads one packet's bytes on from where the last packet left off.</summary>
    private void Take(uint time, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b >= 0xF8)
            {
                Record(time, [b]);
                continue;
            }

            if (_inSysEx)
            {
                if (b < 0x80 || b == 0xF7)
                {
                    if (_sysExLength == _sysEx.Length) Array.Resize(ref _sysEx, _sysEx.Length * 2);
                    _sysEx[_sysExLength++] = b;
                    if (b != 0xF7) continue;
                }
                // F7 ends it; any other status cuts it short, and is read on as itself.
                Record(_sysExTime, _sysEx.AsSpan(0, _sysExLength));
                _inSysEx = false;
                if (b == 0xF7) continue;
            }

            if (b == 0xF0)
            {
                _inSysEx = true;
                _sysExTime = time;
                _sysEx[0] = b;
                _sysExLength = 1;
                _status = 0;
                continue;
            }

            if (b >= 0x80)
            {
                _status = b;
                _message[0] = b;
                _messageLength = 1;
                if (DataBytes(b) == 0) Complete(time);
                continue;
            }

            if (_status == 0) continue;
            if (_messageLength == 0) { _message[0] = _status; _messageLength = 1; }   // running status
            _message[_messageLength++] = b;
            if (_messageLength == 1 + DataBytes(_status)) Complete(time);
        }
    }

    private void Complete(uint time)
    {
        Record(time, _message.AsSpan(0, _messageLength));
        _messageLength = 0;
        if (_status >= 0xF0) _status = 0;
    }

    private static int DataBytes(byte status) => status switch
    {
        < 0xC0 => 2,
        < 0xE0 => 1,
        < 0xF0 => 2,
        0xF1 or 0xF3 => 1,
        0xF2 => 2,
        _ => 0,
    };

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

    /// <summary>Nanoseconds in one tick of <c>mach_absolute_time</c>.</summary>
    private static readonly double TickNanoseconds = TimeBase();

    private static double TimeBase()
    {
        if (!OperatingSystem.IsMacOS()) return 1;
        uint* info = stackalloc uint[2];
        return mach_timebase_info(info) == 0 && info[1] != 0 ? (double)info[0] / info[1] : 1;
    }

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern ulong mach_absolute_time();

    [DllImport("/usr/lib/libSystem.B.dylib")]
    private static extern int mach_timebase_info(uint* info);
}

/// <summary>Opens <see cref="CoreMidiInput"/> instances by source index or name.</summary>
public sealed class CoreMidiInputFactory : IMidiInputFactory
{
    public static bool IsSupported => OperatingSystem.IsMacOS();

    public CoreMidiInputFactory() => CoreMidiClient.TryStart();

    public string BackendName => "CoreMIDI";

    public IReadOnlyList<MidiDeviceInfo> Enumerate()
        => [.. Sources().Select(entry => entry.Info)];

    public IMidiInput Create(string deviceId)
        => CoreMidiClient.Find(Sources(), deviceId) is { } found
            ? new CoreMidiInput(found.Info, found.Endpoint)
            : throw new MidiDeviceException($"no MIDI input device '{deviceId}'");

    private static IReadOnlyList<(MidiDeviceInfo Info, uint Endpoint)> Sources()
        => CoreMidiClient.List(MIDIGetNumberOfSources, MIDIGetSource);
}
