using System.Diagnostics;
using System.Runtime.InteropServices;
using static Glosa.Midi.Windows.NativeMethods;

namespace Glosa.Midi.Windows;

/// <summary>MIDI output over the Win32 multimedia API.</summary>
/// <remarks>
/// A long message's buffer stays the driver's until it is marked done, so closing follows
/// three rules, all to leave the device on the other end of the cable usable:
///
/// <list type="number">
/// <item>Let the queue drain before resetting. <c>midiOutReset</c> marks queued buffers done
/// wherever they are, and a device left waiting for the F7 of a SysEx cut in half ignores
/// everything after it until it is restarted.</item>
/// <item>Never free a block the driver may still hold. If unpreparing fails, the memory is
/// leaked on purpose.</item>
/// <item>Every wait is bounded. An unbounded one hangs the window's close handler, leaving
/// the process alive with the port still open.</item>
/// </list>
/// </remarks>
public sealed unsafe class WinMmOutput : IMidiOutput
{
    private const int SysExBufferCount = 16;
    private const int InitialSysExCapacity = 1024;

    /// <summary>How long to wait for the driver before giving up on a tidy shutdown.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly uint _deviceIndex;
    private readonly SysExBuffer[] _sysExBuffers = new SysExBuffer[SysExBufferCount];
    private readonly Lock _gate = new();

    private nint _handle;
    private bool _disposed;

    internal WinMmOutput(MidiDeviceInfo device, uint deviceIndex)
    {
        Device = device;
        _deviceIndex = deviceIndex;
    }

    public MidiDeviceInfo Device { get; }

    public bool IsOpen => _handle != 0;

    /// <inheritdoc/>
    /// <remarks>Here: no buffer came free in time.</remarks>
    public int DroppedLongMessages { get; private set; }

    /// <inheritdoc/>
    /// <remarks>Here: what <c>midiOutClose</c> said, when it had something to say.</remarks>
    public string? CloseError { get; private set; }

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_handle != 0) return;

            nint h;
            ThrowIfError(midiOutOpen(&h, _deviceIndex, 0, 0, CALLBACK_NULL), "midiOutOpen");
            _handle = h;

            for (int i = 0; i < _sysExBuffers.Length; i++)
                _sysExBuffers[i] = new SysExBuffer(InitialSysExCapacity);
        }
    }

    public void SendShort(uint packedMessage)
    {
        // Under the same lock as Close, so nothing goes to a handle closed on another thread.
        lock (_gate)
        {
            nint h = _handle;
            if (h == 0) return;
            ThrowIfError(midiOutShortMsg(h, packedMessage), "midiOutShortMsg");
        }
    }

    public void SendLong(ReadOnlySpan<byte> sysEx)
    {
        if (sysEx.IsEmpty) return;

        lock (_gate)
        {
            nint h = _handle;
            if (h == 0) return;

            SysExBuffer? buffer = RentBuffer(sysEx.Length);
            if (buffer is null)
            {
                // Every buffer is still with the driver. Dropping one message is better than
                // blocking the playback thread until the device catches up.
                DroppedLongMessages++;
                return;
            }

            buffer.Fill(sysEx);
            MidiHdr* hdr = buffer.Header;
            ThrowIfError(midiOutPrepareHeader(h, hdr, (uint)sizeof(MidiHdr)), "midiOutPrepareHeader");
            buffer.IsPrepared = true;

            uint rc = midiOutLongMsg(h, hdr, (uint)sizeof(MidiHdr));
            if (rc != MMSYSERR_NOERROR)
            {
                midiOutUnprepareHeader(h, hdr, (uint)sizeof(MidiHdr));
                buffer.IsPrepared = false;
                ThrowIfError(rc, "midiOutLongMsg");
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Here the driver sends a long message after <see cref="SendLong"/> returns, and whether a
    /// short message sent meanwhile waits behind it is the driver's to say.
    /// </remarks>
    public void WaitUntilSent()
    {
        lock (_gate)
        {
            if (_handle == 0) return;
            Drain();
        }
    }

    /// <summary>
    /// Finds a buffer the driver is done with, or null if none comes free in time. The driver
    /// keeps the pointer until it sets MHDR_DONE, so a buffer may only be reused after that.
    /// </summary>
    private SysExBuffer? RentBuffer(int length)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(DrainTimeout.TotalSeconds * Stopwatch.Frequency);
        var spin = new SpinWait();

        while (true)
        {
            foreach (SysExBuffer candidate in _sysExBuffers)
            {
                if (candidate is null || !candidate.IsAvailable) continue;
                if (candidate.IsPrepared)
                {
                    if (midiOutUnprepareHeader(_handle, candidate.Header, (uint)sizeof(MidiHdr))
                        != MMSYSERR_NOERROR) continue;
                    candidate.IsPrepared = false;
                }
                candidate.EnsureCapacity(length);
                return candidate;
            }

            if (Stopwatch.GetTimestamp() > deadline) return null;
            spin.SpinOnce();
        }
    }

    /// <summary>
    /// Turns everything off, once whatever is already queued has gone out (rule 1).
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            nint h = _handle;
            if (h == 0) return;

            Drain();
            ThrowIfError(midiOutReset(h), "midiOutReset");
        }
    }

    public void Close(bool reset = true)
    {
        lock (_gate)
        {
            nint h = _handle;
            if (h == 0) return;

            Drain();
            if (reset) midiOutReset(h);

            foreach (SysExBuffer buffer in _sysExBuffers)
            {
                if (buffer is null) continue;

                if (buffer.IsPrepared &&
                    midiOutUnprepareHeader(h, buffer.Header, (uint)sizeof(MidiHdr))
                        != MMSYSERR_NOERROR)
                {
                    // Still the driver's: left unfreed (rule 2).
                    continue;
                }

                buffer.Dispose();
            }
            Array.Clear(_sysExBuffers);

            uint rc = midiOutClose(h);
            if (rc == MIDIERR_STILLPLAYING)
            {
                // Something was queued after all. Leaving the device open would keep the
                // port from being reopened, so the message is sacrificed to the reset.
                midiOutReset(h);
                rc = midiOutClose(h);
            }

            _handle = 0;
            CloseError = rc == MMSYSERR_NOERROR ? null : DescribeError(rc);
        }
    }

    /// <summary>Waits, up to <see cref="DrainTimeout"/>, for the driver to finish sending.</summary>
    private void Drain()
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(DrainTimeout.TotalSeconds * Stopwatch.Frequency);
        var spin = new SpinWait();

        while (Stopwatch.GetTimestamp() <= deadline)
        {
            bool busy = false;
            foreach (SysExBuffer buffer in _sysExBuffers)
                if (buffer is not null && !buffer.IsAvailable) { busy = true; break; }

            if (!busy) return;
            spin.SpinOnce();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    /// <summary>A MIDIHDR plus its data block, both in unmanaged memory the driver can hold.</summary>
    private sealed class SysExBuffer : IDisposable
    {
        private nint _headerPtr;
        private nint _dataPtr;
        private int _capacity;

        internal SysExBuffer(int capacity)
        {
            _headerPtr = Marshal.AllocHGlobal(sizeof(MidiHdr));
            NativeMemory.Clear((void*)_headerPtr, (nuint)sizeof(MidiHdr));
            _dataPtr = Marshal.AllocHGlobal(capacity);
            _capacity = capacity;
            Header->lpData = _dataPtr;
        }

        internal MidiHdr* Header => (MidiHdr*)_headerPtr;

        internal bool IsPrepared { get; set; }

        /// <summary>True when the driver is not holding this buffer.</summary>
        internal bool IsAvailable
        {
            get
            {
                uint flags = Header->dwFlags;
                if ((flags & MHDR_INQUEUE) != 0) return false;
                // Never sent, or the driver has signalled completion.
                return (flags & MHDR_PREPARED) == 0 || (flags & MHDR_DONE) != 0;
            }
        }

        internal void EnsureCapacity(int length)
        {
            if (length <= _capacity) return;
            Marshal.FreeHGlobal(_dataPtr);
            _dataPtr = Marshal.AllocHGlobal(length);
            _capacity = length;
            Header->lpData = _dataPtr;
        }

        internal void Fill(ReadOnlySpan<byte> data)
        {
            data.CopyTo(new Span<byte>((void*)_dataPtr, _capacity));
            MidiHdr* h = Header;
            h->dwBufferLength = (uint)data.Length;
            h->dwBytesRecorded = (uint)data.Length;
            h->dwFlags = 0;
            h->dwUser = 0;
            h->lpNext = 0;
            h->reserved = 0;
            h->dwOffset = 0;
        }

        public void Dispose()
        {
            if (_dataPtr != 0) { Marshal.FreeHGlobal(_dataPtr); _dataPtr = 0; }
            if (_headerPtr != 0) { Marshal.FreeHGlobal(_headerPtr); _headerPtr = 0; }
        }
    }
}
