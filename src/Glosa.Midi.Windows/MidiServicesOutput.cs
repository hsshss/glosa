using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Glosa.Midi.Windows;

/// <summary>MIDI output through Windows MIDI Services, on one group of an endpoint.</summary>
/// <remarks>
/// Each opening makes a session and connection of its own, so an output reopened for the
/// next song does not inherit a connection the service has stopped delivering on (see
/// IMPLEMENTATION.md, "Windows MIDI Services バックエンド").
/// </remarks>
public sealed class MidiServicesOutput : IMidiOutput
{
    /// <summary>How long a send waits for a full buffer to take it.</summary>
    private static readonly TimeSpan FullTimeout = TimeSpan.FromSeconds(1);

    private readonly string _endpoint;
    private readonly byte _group;
    private readonly Lock _gate = new();
    private readonly UmpEncoder _encoder;
    private readonly List<uint> _words = new(64);

    private nint _session;
    private nint _connection;
    private Guid _connectionId;
    private int _maxWords;
    private bool _disposed;

    internal MidiServicesOutput(MidiDeviceInfo device, string endpoint, byte group)
    {
        Device = device;
        _endpoint = endpoint;
        _group = group;
        _encoder = new UmpEncoder(group);
    }

    public MidiDeviceInfo Device { get; }

    public bool IsOpen => _connection != 0;

    /// <inheritdoc/>
    /// <remarks>Here: those the service still had no room for after a second.</remarks>
    public int DroppedLongMessages { get; private set; }

    /// <inheritdoc/>
    /// <remarks>
    /// Here: a send refused while the device was being put at rest, or what disconnecting
    /// said.
    /// </remarks>
    public string? CloseError { get; private set; }

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_connection != 0) return;

            nint session = MidiServices.CreateSession("Glosa");
            try
            {
                _connection = MidiServices.Connect(session, _endpoint, out _connectionId);
                _maxWords = MidiServices.MaxWordsPerSend(_connection);
            }
            catch (MidiDeviceException ex)
            {
                if (_connection != 0) MidiServices.Disconnect(session, _connection, _connectionId);
                _connection = 0;
                MidiServices.CloseSession(session);
                throw new MidiDeviceException($"{Device.Name}: {ex.Message}");
            }
            _session = session;
        }
    }

    public void SendShort(uint packedMessage)
    {
        // Under the same lock as Close, so nothing goes to a connection that has been released.
        lock (_gate)
        {
            if (_connection == 0) return;
            _words.Clear();
            UmpEncoder.Short(packedMessage, _words, _group);
            if (!Send("SendMultipleMessagesWordArray"))
                throw new MidiDeviceException($"{Device.Name}: the device is not taking messages");
        }
    }

    public void SendLong(ReadOnlySpan<byte> sysEx)
    {
        if (sysEx.IsEmpty) return;

        lock (_gate)
        {
            if (_connection == 0) return;
            _words.Clear();
            _encoder.Write(sysEx, _words);
            if (!Send("SendMultipleMessagesWordArray")) DroppedLongMessages++;
        }
    }

    /// <summary>Turns everything off, as <c>midiOutReset</c> does on WinMM.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_connection == 0) return;
            _words.Clear();
            SilenceInto(_words);
            Send("Reset");
        }
    }

    public void Close(bool reset = true)
    {
        lock (_gate)
        {
            if (_connection == 0) return;

            // Silenced on the way out unless told not to, as WinMM closes with midiOutReset.
            CloseError = null;
            if (reset)
            {
                _words.Clear();
                SilenceInto(_words);
                try
                {
                    if (!Send("Reset")) CloseError = $"{Device.Name}: the device is not taking messages";
                }
                catch (MidiDeviceException ex)
                {
                    // The connection has stopped delivering. Closing goes ahead all the same.
                    CloseError = ex.Message;
                }
            }

            string? disconnecting = MidiServices.Disconnect(_session, _connection, _connectionId);
            string? closing = MidiServices.CloseSession(_session);
            _connection = 0;
            _session = 0;
            if ((disconnecting ?? closing) is { } problem) CloseError ??= $"{Device.Name}: {problem}";
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
            UmpEncoder.Short(0xB0 | ch | 0x40 << 8, words, _group);   // Sustain off
            UmpEncoder.Short(0xB0 | ch | 0x7B << 8, words, _group);   // All Notes Off
        }
    }

    /// <summary>
    /// Sends <see cref="_words"/>, as many whole messages per send as the connection takes;
    /// false when the service had no room for all of <see cref="FullTimeout"/>.
    /// </summary>
    /// <exception cref="MidiDeviceException">
    /// Refused for another reason: the connection has stopped delivering, most likely.
    /// </exception>
    private bool Send(string what)
    {
        Span<uint> words = CollectionsMarshal.AsSpan(_words);
        int start = 0;
        while (start < words.Length)
        {
            int end = start;
            while (end < words.Length)
            {
                int next = end + UmpEncoder.WordsIn(words[end]);
                if (next - start > _maxWords && end > start) break;
                end = Math.Min(next, words.Length);
            }

            if (!SendPart(words[start..end], what)) return false;
            start = end;
        }
        return true;
    }

    private bool SendPart(ReadOnlySpan<uint> words, string what)
    {
        long deadline = 0;
        while (true)
        {
            uint result = MidiServices.Send(_connection, words);
            if ((result & MidiServices.Succeeded) != 0) return true;
            if ((result & MidiServices.BufferFull) == 0)
                throw new MidiDeviceException($"{Device.Name}: {what}: {MidiServices.Describe(result)}");

            long now = Stopwatch.GetTimestamp();
            if (deadline == 0) deadline = now + (long)(FullTimeout.TotalSeconds * Stopwatch.Frequency);
            else if (now >= deadline) return false;
            Thread.Sleep(1);
        }
    }
}
