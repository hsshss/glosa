using System.Diagnostics;
using static Glosa.Midi.Linux.NativeMethods;

namespace Glosa.Midi.Linux;

/// <summary>MIDI output through the ALSA sequencer.</summary>
public sealed unsafe class AlsaSeqOutput : IMidiOutput
{
    /// <summary>How long a send waits for a full destination to take it.</summary>
    private static readonly TimeSpan FullTimeout = TimeSpan.FromSeconds(1);

    private readonly SeqAddress _destination;
    private readonly Lock _gate = new();
    private readonly SeqEncoder _encoder = new();
    private readonly List<SeqChunk> _chunks = new(16);

    private nint _midiEncoder;
    private int _port = -1;
    private bool _disposed;

    internal AlsaSeqOutput(MidiDeviceInfo device, SeqAddress destination, bool hardware)
    {
        Device = device;
        _destination = destination;
        IsCable = hardware;
    }

    public MidiDeviceInfo Device { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// Here: a hardware port. The kernel holds 4 KB for its cable and throws away what does
    /// not fit, telling nobody; a program's port refuses instead, which is waited out.
    /// </remarks>
    public bool IsCable { get; }

    public bool IsOpen => _midiEncoder != 0;

    /// <inheritdoc/>
    /// <remarks>Here: those the destination still had no room for after a second.</remarks>
    public int DroppedLongMessages { get; private set; }

    /// <inheritdoc/>
    /// <remarks>Here: a send refused while the device was being put at rest on the way out.</remarks>
    public string? CloseError { get; private set; }

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_midiEncoder != 0) return;
            _port = AlsaSeqClient.OutputPort;

            nint encoder;
            ThrowIfError(snd_midi_event_new(16, &encoder), "snd_midi_event_new");
            _midiEncoder = encoder;
        }
    }

    public void SendShort(uint packedMessage)
    {
        byte status = (byte)packedMessage;
        int needed = SeqEncoder.DataBytes(status);
        if (needed < 0) return;

        Span<byte> message = [status, (byte)(packedMessage >> 8 & 0x7F), (byte)(packedMessage >> 16 & 0x7F)];

        // Under the same lock as Close, so nothing goes through an encoder that has been freed.
        lock (_gate)
        {
            if (_midiEncoder == 0) return;
            if (!SendMessage(message[..(1 + needed)], "snd_seq_event_output_direct"))
                throw new MidiDeviceException($"{Device.Name}: the device is not taking messages");
            SilenceAfterOverflow();
        }
    }

    /// <inheritdoc/>
    public event Action? Silenced;

    /// <summary>
    /// Set when the destination refused an event for want of room, until it has been put at
    /// rest (<see cref="SilenceAfterOverflow"/>).
    /// </summary>
    /// <remarks>
    /// A client whose queue has overflowed has everything waiting in it thrown away when it
    /// next reads, so what it had been sent before, note-offs included, may never arrive.
    /// Sending again after a wait does not bring those back.
    /// </remarks>
    private bool _overflowed;

    /// <summary>
    /// Once the destination is taking events again after overflowing, turns every note off,
    /// so none is left sounding for want of the note-off it lost.
    /// </summary>
    private void SilenceAfterOverflow()
    {
        if (!_overflowed) return;
        _overflowed = false;

        _chunks.Clear();
        SilenceInto(_chunks);
        if (SendChunks("Reset")) Silenced?.Invoke();
    }

    public void SendLong(ReadOnlySpan<byte> sysEx)
    {
        if (sysEx.IsEmpty) return;

        lock (_gate)
        {
            if (_midiEncoder == 0) return;
            _chunks.Clear();
            _encoder.Write(sysEx, _chunks);
            if (SendChunks("snd_seq_event_output_direct")) SilenceAfterOverflow();
            else DroppedLongMessages++;
        }
    }

    /// <summary>Turns everything off, as <c>midiOutReset</c> does on Windows.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_midiEncoder == 0) return;
            _chunks.Clear();
            SilenceInto(_chunks);
            SendChunks("Reset");
        }
    }

    public void Close(bool reset = true)
    {
        lock (_gate)
        {
            if (_midiEncoder == 0) return;

            // Silenced on the way out unless told not to, as WinMM closes with midiOutReset.
            CloseError = null;
            if (reset)
            {
                _chunks.Clear();
                SilenceInto(_chunks);
                try
                {
                    if (!SendChunks("Reset")) CloseError = $"{Device.Name}: the device is not taking messages";
                }
                catch (MidiDeviceException ex)
                {
                    // Gone already — unplugged, or the program closed. Closing goes ahead all the same.
                    CloseError = ex.Message;
                }
            }

            snd_midi_event_free(_midiEncoder);
            _midiEncoder = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    /// <summary>What a reset sends: any open SysEx closed, then sustain off and All Notes Off.</summary>
    private void SilenceInto(List<SeqChunk> chunks)
    {
        _encoder.Reset(chunks);
        for (byte ch = 0; ch < 16; ch++)
        {
            chunks.Add(new SeqChunk(false, [(byte)(0xB0 | ch), 0x40, 0]));   // Sustain off
            chunks.Add(new SeqChunk(false, [(byte)(0xB0 | ch), 0x7B, 0]));   // All Notes Off
        }
    }

    /// <summary>Sends <see cref="_chunks"/>; false when the destination had no room.</summary>
    private bool SendChunks(string what)
    {
        foreach (SeqChunk chunk in _chunks)
        {
            bool sent = chunk.IsSysEx ? SendSysEx(chunk.Bytes, what) : SendMessage(chunk.Bytes, what);
            if (!sent) return false;
        }
        return true;
    }

    /// <summary>A whole channel or system message, as the event the encoder makes of it.</summary>
    private bool SendMessage(ReadOnlySpan<byte> message, string what)
    {
        SeqEvent ev = default;
        snd_midi_event_reset_encode(_midiEncoder);
        fixed (byte* bytes = message)
            if (snd_midi_event_encode(_midiEncoder, bytes, message.Length, &ev) < 0 || ev.Type == EventNone)
                return true;   // not a message the sequencer has an event for; nothing to send
        return Deliver(&ev, what);
    }

    private bool SendSysEx(byte[] bytes, string what)
    {
        fixed (byte* data = bytes)
        {
            SeqEvent ev = default;
            ev.Type = EventSysEx;
            ev.Flags = LengthVariable;
            ev.ExtLength = (uint)bytes.Length;
            ev.ExtPointer = (nint)data;
            return Deliver(&ev, what);
        }
    }

    /// <summary>
    /// Writes <paramref name="ev"/> straight to the destination; false when it stayed full
    /// for all of <see cref="FullTimeout"/>.
    /// </summary>
    /// <exception cref="MidiDeviceException">Refused for another reason: gone, most likely.</exception>
    private bool Deliver(SeqEvent* ev, string what)
    {
        ev->Queue = QueueDirect;
        ev->SourcePort = (byte)_port;
        ev->DestClient = (byte)_destination.Client;
        ev->DestPort = (byte)_destination.Port;

        long deadline = 0;
        while (true)
        {
            int result;
            lock (AlsaSeqClient.Gate) result = snd_seq_event_output_direct(AlsaSeqClient.Handle, ev);
            if (result >= 0) return true;
            if (result != -EAGAIN && result != -ENOMEM) ThrowIfError(result, what);
            _overflowed = true;

            long now = Stopwatch.GetTimestamp();
            if (deadline == 0) deadline = now + (long)(FullTimeout.TotalSeconds * Stopwatch.Frequency);
            else if (now >= deadline) return false;
            Thread.Sleep(1);
        }
    }
}

/// <summary>Enumerates the sequencer ports that can be written to.</summary>
public sealed class AlsaSeqOutputFactory : IMidiOutputFactory
{
    public static bool IsSupported => IsAvailable;

    public string BackendName => "ALSA";

    public IReadOnlyList<MidiDeviceInfo> Enumerate()
        => [.. Destinations().Select(entry => entry.Info)];

    /// <summary>Opens by <c>client:port</c>, or by the name shown.</summary>
    public IMidiOutput Create(string deviceId)
        => AlsaSeqClient.Find(Destinations(), deviceId) is { } found
            ? new AlsaSeqOutput(found.Info, found.Address, found.Hardware)
            : throw new MidiDeviceException($"ALSA sequencer port '{deviceId}' is not present.");

    private static IReadOnlyList<(MidiDeviceInfo Info, SeqAddress Address, bool Hardware)> Destinations()
        => AlsaSeqClient.List(NativeMethods.CapWrite | NativeMethods.CapSubsWrite);
}
