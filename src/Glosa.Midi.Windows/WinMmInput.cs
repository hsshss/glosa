using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static Glosa.Midi.Windows.NativeMethods;

namespace Glosa.Midi.Windows;

/// <summary>MIDI capture over the Win32 multimedia API.</summary>
/// <remarks>
/// The driver calls back on its own thread, and winmm allows almost nothing to be called
/// from there — <c>midiInAddBuffer</c> included. So the callback only copies bytes into a
/// preallocated ring and flags the SysEx buffer it emptied; a small worker thread hands
/// those buffers back to the driver.
/// </remarks>
public sealed unsafe class WinMmInput : IMidiInput
{
    private const int SysExBufferCount = 8;
    private const int SysExBufferSize = 64 * 1024;

    /// <summary>Room for the messages of one run; a capture that fills it is reported.</summary>
    private const int MaxMessages = 1 << 20;
    private const int BlobSize = 8 * 1024 * 1024;

    private readonly uint _deviceIndex;
    private readonly Lock _gate = new();

    private readonly byte[] _blob = new byte[BlobSize];
    private readonly Entry[] _entries = new Entry[MaxMessages];
    private readonly nint[] _headers = new nint[SysExBufferCount];
    private readonly bool[] _needsAdd = new bool[SysExBufferCount];

    private GCHandle _self;
    private nint _handle;
    private Thread? _recycler;
    private volatile bool _running;
    private int _count;
    private int _blobUsed;
    private int _overflows;
    private bool _disposed;

    internal WinMmInput(MidiDeviceInfo device, uint deviceIndex)
    {
        Device = device;
        _deviceIndex = deviceIndex;
    }

    public MidiDeviceInfo Device { get; }

    public bool IsOpen => _handle != 0;

    public int Overflows => Volatile.Read(ref _overflows);

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_handle != 0) return;

            _self = GCHandle.Alloc(this, GCHandleType.Normal);
            try
            {
                nint h;
                ThrowIfError(
                    midiInOpen(&h, _deviceIndex, &Callback, GCHandle.ToIntPtr(_self),
                               CALLBACK_FUNCTION),
                    "midiInOpen");
                _handle = h;

                for (int i = 0; i < SysExBufferCount; i++)
                {
                    nint hdrPtr = Marshal.AllocHGlobal(sizeof(MidiHdr));
                    NativeMemory.Clear((void*)hdrPtr, (nuint)sizeof(MidiHdr));
                    _headers[i] = hdrPtr;
                    var hdr = (MidiHdr*)hdrPtr;
                    hdr->lpData = Marshal.AllocHGlobal(SysExBufferSize);
                    hdr->dwBufferLength = SysExBufferSize;
                    hdr->dwUser = i;

                    ThrowIfError(midiInPrepareHeader(h, hdr, (uint)sizeof(MidiHdr)),
                                 "midiInPrepareHeader");
                    ThrowIfError(midiInAddBuffer(h, hdr, (uint)sizeof(MidiHdr)), "midiInAddBuffer");
                }
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
            if (_handle == 0 || _running) return;

            _count = 0;
            _blobUsed = 0;
            _overflows = 0;
            _running = true;

            _recycler = new Thread(Recycle) { IsBackground = true, Name = $"midiin-{_deviceIndex}" };
            _recycler.Start();

            ThrowIfError(midiInStart(_handle), "midiInStart");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_handle == 0 || !_running) return;

            midiInStop(_handle);
            _running = false;
            _recycler?.Join(500);
            _recycler = null;
        }
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

        lock (_gate) Release();
    }

    /// <summary>Closes the device and frees what opening it took, as far as it got.</summary>
    private void Release()
    {
        nint h = _handle;
        if (h != 0)
        {
            _running = false;
            _recycler?.Join(500);
            _recycler = null;

            midiInStop(h);
            midiInReset(h);            // returns every queued buffer

            for (int i = 0; i < SysExBufferCount; i++)
            {
                nint hdrPtr = _headers[i];
                if (hdrPtr == 0) continue;
                var hdr = (MidiHdr*)hdrPtr;
                midiInUnprepareHeader(h, hdr, (uint)sizeof(MidiHdr));
                if (hdr->lpData != 0) Marshal.FreeHGlobal(hdr->lpData);
                Marshal.FreeHGlobal(hdrPtr);
                _headers[i] = 0;
            }

            midiInClose(h);
            _handle = 0;
        }
        if (_self.IsAllocated) _self.Free();
    }

    /// <summary>Hands emptied SysEx buffers back, which the callback may not do itself.</summary>
    private void Recycle()
    {
        while (_running)
        {
            for (int i = 0; i < SysExBufferCount; i++)
            {
                if (!Volatile.Read(ref _needsAdd[i])) continue;
                Volatile.Write(ref _needsAdd[i], false);

                nint h = _handle;
                if (h == 0) return;
                var hdr = (MidiHdr*)_headers[i];
                hdr->dwBytesRecorded = 0;
                hdr->dwFlags &= ~MHDR_DONE;
                midiInAddBuffer(h, hdr, (uint)sizeof(MidiHdr));
            }
            Thread.Sleep(2);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static void Callback(nint hmi, uint msg, nint instance, nint p1, nint p2)
    {
        if (instance == 0) return;
        if (GCHandle.FromIntPtr(instance).Target is not WinMmInput self) return;

        switch (msg)
        {
            case MIM_DATA:
            {
                uint packed = (uint)p1;
                int length = ShortMessageLength((byte)packed);
                byte* bytes = stackalloc byte[3];
                bytes[0] = (byte)packed;
                bytes[1] = (byte)(packed >> 8);
                bytes[2] = (byte)(packed >> 16);
                self.Record((uint)p2, new ReadOnlySpan<byte>(bytes, length));
                break;
            }

            case MIM_LONGDATA:
            case MIM_LONGERROR:
            {
                var hdr = (MidiHdr*)p1;
                int recorded = (int)hdr->dwBytesRecorded;
                if (recorded > 0)
                    self.Record((uint)p2, new ReadOnlySpan<byte>((void*)hdr->lpData, recorded));

                // Re-queueing is not allowed from here; leave it to the recycler.
                int index = (int)hdr->dwUser;
                if ((uint)index < SysExBufferCount) Volatile.Write(ref self._needsAdd[index], true);
                break;
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

    /// <summary>How many of the three bytes of a packed message are real.</summary>
    private static int ShortMessageLength(byte status) => status switch
    {
        >= 0x80 and <= 0xBF => 3,
        >= 0xC0 and <= 0xDF => 2,
        >= 0xE0 and <= 0xEF => 3,
        0xF1 or 0xF3 => 2,
        0xF2 => 3,
        _ => 1,
    };

    private readonly record struct Entry(uint TimeMs, int Offset, int Length);
}

/// <summary>Opens <see cref="WinMmInput"/> instances by device id.</summary>
public sealed unsafe class WinMmInputFactory : IMidiInputFactory
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    public string BackendName => "WinMM";

    public IReadOnlyList<MidiDeviceInfo> Enumerate()
    {
        uint count = midiInGetNumDevs();
        var devices = new List<MidiDeviceInfo>((int)count);
        for (uint i = 0; i < count; i++)
        {
            MidiInCaps caps = default;
            if (midiInGetDevCaps(i, &caps, (uint)sizeof(MidiInCaps)) != MMSYSERR_NOERROR)
                continue;
            devices.Add(new MidiDeviceInfo(i.ToString(), NameOf(caps.szPname)));
        }
        return devices;
    }

    public IMidiInput Create(string deviceId)
    {
        IReadOnlyList<MidiDeviceInfo> devices = Enumerate();

        // Accept either the index or the name, so a harness can name the loopback port.
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i].Id == deviceId
                || string.Equals(devices[i].Name, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return new WinMmInput(devices[i], uint.Parse(devices[i].Id));
            }
        }
        throw new MidiDeviceException($"no MIDI input device '{deviceId}'");
    }
}
