using System.Diagnostics;
using System.Runtime;
using Glosa.Core.Smf;
using Glosa.Midi;

namespace Glosa.Core.Playback;

public enum PlaybackState { Stopped, Playing, Paused }

/// <summary>Why playback stopped by itself: one of the two protection stops.</summary>
public enum SequencerFault
{
    None,
    /// <summary>Loops nested deeper than the stack holds ("loop nesting error").</summary>
    LoopNestOverflow,
    /// <summary>Too many events at one instant, so the data is looping on the spot
    /// ("loop limit exceeded").</summary>
    LoopLimitExceeded,
}

/// <summary>How a run of the sequencer came to an end by itself.</summary>
/// <param name="Run">Which run it was (<see cref="Sequencer.CurrentRun"/>).</param>
/// <param name="Fault">The protection stop that ended it, if one did.</param>
/// <param name="DeviceProblem">What the output said when it refused, if that ended it.</param>
public readonly record struct SequencerEnd(int Run, SequencerFault Fault, string? DeviceProblem);

/// <summary>
/// Drives a <see cref="MidiSequence"/> against an <see cref="IEventSink"/> from a dedicated
/// thread.
/// </summary>
/// <remarks>
/// Timing is the part that decides whether playback sounds right, so the loop is built to
/// avoid the things that cost accuracy: no timer callbacks, no task scheduling, and no
/// allocation once playing. Long waits sleep, short ones spin.
/// </remarks>
public sealed class Sequencer : IDisposable
{
    private const long SleepThresholdUs = 5_000;
    private const long SleepMarginUs = 2_000;
    /// <summary>Longest single sleep, so Stop stays responsive across long silent gaps.</summary>
    private const int MaxSleepMs = 10;

    /// <summary>Loops the stack holds; a ninth open loop stops playback.</summary>
    private const int LoopStackDepth = 8;

    /// <summary>
    /// Events accepted at one instant before the data counts as runaway and playback stops
    /// with a protection error. This is what catches a loop whose body is empty.
    /// </summary>
    private const int SameTimeEventLimit = 0x2000;

    private IEventSink _sink;
    private readonly PlaybackOptions _options;
    private readonly IPlatformTimer _platformTimer;
    private readonly Lock _gate = new();

    private MidiSequence? _sequence;
    private Thread? _thread;

    /// <summary>
    /// A playback thread that was asked to stop and had not finished by the time
    /// <see cref="Stop"/> gave up waiting for it.
    /// </summary>
    /// <remarks>
    /// Stuck in the output, most likely, and still holding the cursor. Nothing new starts
    /// until it is gone (<see cref="AwaitLingering"/>): two loops on one cursor would play
    /// both songs at once.
    /// </remarks>
    private Thread? _lingering;

    private volatile bool _stopRequested;
    private volatile PlaybackState _state = PlaybackState.Stopped;

    private long _originTimestamp;
    private long _pausedAtUs;
    private int _index;

    /// <summary>A seek waiting for the playback loop to take it, or -1.</summary>
    private long _seekRequestUs = -1;

    /// <summary>Where a catch-up is heading, or -1 when playing in real time.</summary>
    private long _catchUpToUs = -1;

    /// <summary>
    /// Wall clock for the transfer-rate cap.
    /// </summary>
    /// <remarks>
    /// Separate from the position, because during a catch-up the position stands still while
    /// the bytes going down the cable still take the time they take.
    /// </remarks>
    private readonly long _clockOrigin = Stopwatch.GetTimestamp();

    /// <summary>What a MIDI cable carries: 31250 baud, ten bits a byte.</summary>
    private const int CableBytesPerSecond = 3125;

    /// <summary>
    /// When each port may next be handed something under the rate cap, on the wall clock
    /// (<see cref="ElapsedUs"/>). Each port is its own cable, so each keeps its own account.
    /// </summary>
    private readonly long[] _portReadyUs = new long[IEventSink.PortCount];

    /// <summary>When the send the rate cap last held back may go, for the loop to wait for.</summary>
    private long _readyAtUs;

    /// <summary>
    /// Whether a SysEx is still waiting for its F7 on each port: one the file splits into
    /// parts, between them. Under the gate, as every send is.
    /// </summary>
    private readonly bool[] _sysExOpen = new bool[IEventSink.PortCount];

    private static readonly byte[] EndOfExclusive = [0xF7];

    // What a catch-up holds back (Hold): per port and channel the 128 controllers, then pitch
    // bend and channel pressure, linked in the order they were last set. Sized once so the
    // playback loop still allocates nothing.
    private const int HeldKinds = 130;
    private const int HeldSlots = IEventSink.PortCount * 16 * HeldKinds;
    private readonly uint[] _heldMessage = new uint[HeldSlots];
    private readonly int[] _heldPrev = new int[HeldSlots];
    private readonly int[] _heldNext = new int[HeldSlots];
    private readonly bool[] _isHeld = new bool[HeldSlots];
    private int _heldFirst = -1;
    private int _heldLast = -1;

    // Loop bookkeeping. Sized once so the playback loop still allocates nothing. Index 0 is
    // left unused: depth 0 means "not inside a loop".
    private readonly int[] _loopReturn = new int[LoopStackDepth + 1];
    private readonly long[] _loopReturnTimeUs = new long[LoopStackDepth + 1];
    private readonly int[] _loopCounter = new int[LoopStackDepth + 1];
    private readonly bool[] _loopInfinite = new bool[LoopStackDepth + 1];
    private int _loopDepth;

    /// <summary>
    /// How far event times have been pushed back by loops taken so far. Events carry absolute
    /// times, so replaying a span means adding the span's length rather than rewinding a clock.
    /// </summary>
    private long _timeShiftUs;

    public Sequencer(IEventSink sink, PlaybackOptions? options = null,
                     IPlatformTimer? platformTimer = null)
    {
        _sink = sink;
        _options = options ?? new PlaybackOptions();
        _platformTimer = platformTimer ?? NullPlatformTimer.Instance;
    }

    public PlaybackState State => _state;

    /// <summary>
    /// Where events go. Settable so the emulation layer can be rebuilt for another module
    /// pair without throwing the sequencer away; stop playback before changing it, since the
    /// playback thread reads it without a lock.
    /// </summary>
    public IEventSink Sink
    {
        get => _sink;
        set => _sink = value;
    }

    public MidiSequence? Sequence => _sequence;

    /// <summary>Why the last run stopped by itself, if it did.</summary>
    public SequencerFault Fault { get; private set; }

    /// <summary>Raised on the sequencer thread when the end of the sequence is reached.</summary>
    public event Action? Finished;

    /// <summary>Raised on the sequencer thread when a protection limit stopped playback.</summary>
    public event Action<SequencerFault>? Faulted;

    /// <summary>
    /// Raised when an output refused what was sent and playback had to stop.
    /// </summary>
    /// <remarks>
    /// A device can leave in the middle of a song — unplugged, switched off, or a virtual
    /// port taken away by whatever made it. Every write after that throws, and nothing above
    /// the playback thread would catch it.
    /// </remarks>
    public event Action<string>? DeviceFailed;

    /// <summary>
    /// Raised when playback comes to rest, however it got there.
    /// </summary>
    /// <remarks>
    /// Stopped by hand, the song running out, a protection limit, a lost output: the other
    /// events tell them apart. Raised on whichever thread stopped it, so all but the first
    /// arrive on the sequencer thread.
    /// </remarks>
    public event Action? Stopped;

    /// <summary>
    /// Raised on the sequencer thread before a seek replays a song from its beginning.
    /// </summary>
    /// <remarks>
    /// Going backwards means undoing everything the song has set up so far, and nothing in
    /// the data says how. Whoever holds that state puts it back, exactly as between songs.
    /// </remarks>
    public event Action? Rewinding;

    /// <summary>
    /// Raised on the sequencer thread when a run ends by itself — the song running out, a
    /// protection stop, an output refusing — after the event that says which.
    /// </summary>
    /// <remarks>
    /// Carries the run it ended, because it arrives from another thread and may arrive late:
    /// by the time whoever drives the sequencer gets to it, another song may be playing, and
    /// the end of the last one must not be taken for the end of this one.
    /// </remarks>
    public event Action<SequencerEnd>? Ended;

    /// <summary>
    /// The run the last <see cref="Play"/> from a standstill started, counting from 1. A
    /// resume carries on the same run.
    /// </summary>
    public int CurrentRun => Volatile.Read(ref _run);

    private int _run;

    public long PositionUs
    {
        get
        {
            // A catch-up is work, not time: the listener is already at the target, and the
            // clock must not creep while the data is being brought up to it.
            long catchUp = Volatile.Read(ref _catchUpToUs);
            if (catchUp >= 0) return catchUp;

            if (_state != PlaybackState.Playing) return _pausedAtUs;
            long elapsed = Stopwatch.GetTimestamp() - _originTimestamp;
            return elapsed * 1_000_000L / Stopwatch.Frequency;
        }
    }

    public TimeSpan Position => TimeSpan.FromMilliseconds(PositionUs / 1000.0);

    public void Load(MidiSequence sequence)
    {
        Stop();
        AwaitLingering();
        lock (_gate)
        {
            _sequence = sequence;
            _index = 0;
            _pausedAtUs = 0;
            _seekRequestUs = -1;
            Array.Clear(_sysExOpen);
            DropHeld();
            ResetLoopState();
        }
    }

    public void Play()
    {
        AwaitLingering();
        lock (_gate)
        {
            if (_sequence is null) throw new InvalidOperationException("No sequence loaded.");
            if (_state == PlaybackState.Playing) return;

            if (_state == PlaybackState.Paused)
            {
                ResumeFrom(_pausedAtUs);
                return;
            }

            _stopRequested = false;
            Volatile.Write(ref _catchUpToUs, -1);
            Interlocked.Increment(ref _run);
            _index = 0;
            _pausedAtUs = 0;
            Fault = SequencerFault.None;
            DropHeld();
            ResetLoopState();
            ResumeFrom(0);

            _thread = new Thread(Run)
            {
                Name = "glosa-sequencer",
                IsBackground = true,
                Priority = MapPriority(_options.Priority),
            };
            TrySetPriority(_thread, _options.Priority);
            _thread.Start();
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_state != PlaybackState.Playing) return;
            _pausedAtUs = PositionUs;
            _state = PlaybackState.Paused;
            // The controllers stay as the song set them: nothing sends them again on resume.
            SilenceAllChannels(resetControllers: false);
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_state != PlaybackState.Paused) return;
            ResumeFrom(_pausedAtUs);
        }
    }

    public void Stop()
    {
        Thread? thread;
        bool wasRunning;
        lock (_gate)
        {
            wasRunning = _state != PlaybackState.Stopped;
            if (!wasRunning) { thread = null; }
            else
            {
                _stopRequested = true;
                _state = PlaybackState.Stopped;
                thread = _thread;
                _thread = null;
            }
        }

        // Not for ever: an output that stopped answering must not hang whoever asked.
        if (thread is not null && !thread.Join(2000)) _lingering = thread;

        lock (_gate)
        {
            _index = 0;
            _pausedAtUs = 0;
            _seekRequestUs = -1;
            Volatile.Write(ref _catchUpToUs, -1);
            DropHeld();
            ResetLoopState();
            // Nothing was sounding if we were already stopped, so stay quiet. A SysEx left
            // open is closed whatever the setting: the song it belonged to is over.
            if (wasRunning) { CloseOpenSysEx(); SilenceAllChannels(); }
        }

        // Outside the lock: a handler is free to take as long as it likes, and none of
        // what it does belongs to the state this guards.
        if (wasRunning) Stopped?.Invoke();
    }

    /// <summary>
    /// Waits for a playback thread <see cref="Stop"/> gave up on (<see cref="_lingering"/>).
    /// </summary>
    /// <remarks>
    /// Outside the gate, which the thread may need on its way out. It has been asked to stop
    /// and sends nothing more (<see cref="TrySendShort"/>), so it only has to come back from
    /// the output.
    /// </remarks>
    private void AwaitLingering() => Interlocked.Exchange(ref _lingering, null)?.Join();

    /// <summary>
    /// Asks for playback to move to <paramref name="position"/>.
    /// </summary>
    /// <remarks>
    /// Seeking is not a second path through the data. The loop plays on from where it is
    /// with the timestamps ignored, as fast as the device will take it, until it reaches the
    /// target — so everything the song sets up on the way is set up for real, by the same
    /// code that would have sent it had the listener sat through it. Only notes are left
    /// out; see <see cref="Loop"/>.
    ///
    /// The move itself belongs to the playback loop, which is the only thing allowed to
    /// touch the cursor or to send, so this records the request and returns.
    /// </remarks>
    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_sequence is null) return;
            _seekRequestUs = Math.Max(0, (long)(position.TotalMilliseconds * 1000));

            // Nothing is running to take it, but the position still has to read as asked.
            // Play catches the data up from the start of the song.
            if (_state == PlaybackState.Stopped) _pausedAtUs = _seekRequestUs;
        }
    }

    /// <summary>
    /// Starts a requested seek, and reports whether one was waiting.
    /// </summary>
    /// <remarks>
    /// Forwards, the module already holds everything the song sent up to here, so the
    /// catch-up carries on from the cursor. Backwards there is no way to undo what has gone
    /// out, so the state is put back the way it is between songs and the song is replayed
    /// from its beginning — which is also why the loop bookkeeping starts over.
    ///
    /// Either way the notes sounding now are cut (<see cref="CutSound"/>).
    ///
    /// While a split SysEx is open the request waits and playback goes on, so the cut does not
    /// land inside it; the next event on that port that is not more of it closes it. Paused,
    /// the SysEx is cut short instead.
    /// </remarks>
    private bool BeginSeek()
    {
        // Read without the lock first: this is on the playback loop's hot path, and a request
        // that lands a moment later is taken on the next turn.
        if (Volatile.Read(ref _seekRequestUs) < 0) return false;

        long target;
        lock (_gate)
        {
            if (_seekRequestUs < 0) return false;
            if (Array.IndexOf(_sysExOpen, true) >= 0)
            {
                if (_state != PlaybackState.Paused) return false;
                CloseOpenSysEx(catchingUp: true);
            }
            target = _seekRequestUs;
            _seekRequestUs = -1;
        }

        if (target < PositionUs)
        {
            Rewinding?.Invoke();
            _index = 0;
            ResetLoopState();
            // Held on the way to a place now given up; the replay sets them again.
            DropHeld();
        }

        CutSound();
        Volatile.Write(ref _catchUpToUs, target);
        return true;
    }

    /// <summary>Hands the loop back to the clock once the data has caught up.</summary>
    /// <remarks>
    /// Under the lock, so a pause cannot land between reading the state and restarting the
    /// clock and then be undone by it.
    /// </remarks>
    private void EndSeek()
    {
        lock (_gate)
        {
            long target = _catchUpToUs;
            Volatile.Write(ref _catchUpToUs, -1);

            if (_state == PlaybackState.Playing) ResumeFrom(target);
            else _pausedAtUs = target;
        }
    }

    /// <summary>
    /// Sends a channel message unless a pause has got in first, and reports whether it went.
    /// </summary>
    /// <remarks>
    /// The loop's own look at the state is only a shortcut: between that look and the send,
    /// <see cref="Pause"/> can take the lock, silence everything and return, and a note sent
    /// after that sounds for the whole pause. Looking again under the lock that Pause holds
    /// means that once Pause has returned, nothing more goes out.
    /// </remarks>
    private bool TrySendShort(int port, uint packed, bool catchingUp)
    {
        lock (_gate)
        {
            if (_state == PlaybackState.Stopped || HeldByPause(catchingUp)) return false;
            _sink.SendShort(port, packed);
            Spend(port, ShortLength(packed), RateFor(catchingUp));
            // A status byte ends a SysEx on the cable, so the device takes it as closed.
            if ((uint)port < (uint)_sysExOpen.Length) _sysExOpen[port] = false;
            return true;
        }
    }

    /// <summary>The system-exclusive counterpart of <see cref="TrySendShort"/>.</summary>
    /// <remarks>
    /// With no SysEx open on the port, data bytes at the front are the rest of one cut short
    /// (<see cref="CloseOpenSysEx"/>) and are dropped: with nothing to belong to, the device
    /// would read them under the running status of the last channel message.
    /// </remarks>
    private bool TrySendLong(int port, ReadOnlySpan<byte> data, bool catchingUp)
    {
        lock (_gate)
        {
            if (_state == PlaybackState.Stopped || HeldByPause(catchingUp)) return false;

            bool tracked = (uint)port < (uint)_sysExOpen.Length;
            if (tracked && !_sysExOpen[port])
            {
                int start = 0;
                while (start < data.Length && data[start] < 0x80) start++;
                data = data[start..];
            }
            if (data.IsEmpty) return true;

            _sink.SendLong(port, data);
            Spend(port, data.Length, RateFor(catchingUp));
            if (tracked) _sysExOpen[port] = LeavesSysExOpen(_sysExOpen[port], data);
            return true;
        }
    }

    /// <summary>
    /// Whether a SysEx is open once <paramref name="bytes"/> have gone out, given whether one
    /// was before. F0 opens one and any other status byte closes it; real-time bytes may sit
    /// inside one and change nothing.
    /// </summary>
    internal static bool LeavesSysExOpen(bool open, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            if (b is >= 0x80 and < 0xF8) open = b == 0xF0;
        return open;
    }

    /// <summary>
    /// Ends every SysEx left open with an F7, so what goes out next is not taken as more of it.
    /// </summary>
    /// <remarks>
    /// The device gets the message cut short and drops it. The rest, should it come later, is
    /// dropped here (<see cref="TrySendLong"/>). Under the gate, or on the playback thread
    /// once nothing else can send. A device that has gone is left to the caller's silence.
    /// </remarks>
    private void CloseOpenSysEx(bool catchingUp = false)
    {
        int rate = RateFor(catchingUp);
        try
        {
            for (int port = 0; port < _sysExOpen.Length; port++)
            {
                if (!_sysExOpen[port]) continue;
                _sysExOpen[port] = false;
                WaitForPort(port, rate);
                _sink.SendLong(port, EndOfExclusive);
                Spend(port, EndOfExclusive.Length, rate);
            }
        }
        catch (MidiDeviceException)
        {
            // The output is the thing that is gone; there is nothing to say to it.
        }
    }

    /// <summary>A pause holds the cursor, except for a seek being carried out under it.</summary>
    private bool HeldByPause(bool catchingUp) => _state == PlaybackState.Paused && !catchingUp;

    private void ResumeFrom(long positionUs)
    {
        _originTimestamp = Stopwatch.GetTimestamp() - positionUs * Stopwatch.Frequency / 1_000_000L;
        _state = PlaybackState.Playing;
    }

    private void Run()
    {
        GCLatencyMode previous = GCSettings.LatencyMode;
        try
        {
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        }
        catch (InvalidOperationException)
        {
            // Not permitted in every hosting configuration; timing simply stays as-is.
        }

        using IDisposable timerScope = _platformTimer.BeginHighResolution();
        int run = _run;
        try
        {
            Loop(run);
        }
        catch (MidiDeviceException ex)
        {
            LostDevice(ex, run);
        }
        finally
        {
            try { GCSettings.LatencyMode = previous; } catch (InvalidOperationException) { }
        }
    }

    private void Loop(int run)
    {
        MidiSequence sequence = _sequence!;
        MidiEvent[] events = sequence.Events;
        byte[] payload = sequence.Payload;
        long sameTimeUs = long.MinValue;
        int sameTimeCount = 0;

        while (!_stopRequested)
        {
            if (BeginSeek()) sameTimeUs = long.MinValue;

            // A pause holds the cursor where it is, but a seek asked for while paused is
            // still carried out: the module is brought to the new place and waits there.
            bool catchingUp = _catchUpToUs >= 0;
            if (HeldByPause(catchingUp))
            {
                Thread.Sleep(5);
                continue;
            }

            // Ignoring the timestamps is all a seek is: with the target standing in for the
            // clock, everything up to it is due at once and goes out as fast as a cable
            // carries it (RateFor).
            long now = catchingUp ? _catchUpToUs : PositionUs;
            int rate = RateFor(catchingUp);
            bool rateHeld = false;

            // A catch-up stops short of the target: what falls on it, notes included, is due
            // once the clock takes over there, after what the catch-up held.
            while (_index < events.Length
                   && (catchingUp ? events[_index].TimeUs + _timeShiftUs < now
                                  : events[_index].TimeUs + _timeShiftUs <= now))
            {
                ref readonly MidiEvent e = ref events[_index];

                long eventTimeUs = e.TimeUs + _timeShiftUs;
                if (eventTimeUs != sameTimeUs) { sameTimeUs = eventTimeUs; sameTimeCount = 0; }
                if (++sameTimeCount > SameTimeEventLimit)
                {
                    Fail(SequencerFault.LoopLimitExceeded, run);
                    return;
                }

                switch (e.Kind)
                {
                    case MidiEventKind.Channel:
                        if (catchingUp)
                        {
                            // Notes, and the pressure on them, belong to moments being
                            // skipped, and their note-offs are skipped with them.
                            if (IsNote(e.Packed) || IsKeyPressure(e.Packed)) break;
                            // Only the last value of a controller counts once the target is
                            // reached; anything else goes out after what is held, in order.
                            if (Hold(e.Port, e.Packed)) break;
                        }
                        // Not taken while held back by the rate cap, so not counted either:
                        // the cap brings us back to this same event.
                        if ((catchingUp && !SendHeld(rate)) || !Ready(e.Port, rate))
                        {
                            sameTimeCount--;
                            rateHeld = true;
                            goto pacing;
                        }
                        if (!TrySendShort(e.Port, e.Packed, catchingUp))
                        {
                            // A pause got in first. Not taken, so not counted either.
                            sameTimeCount--;
                            goto pacing;
                        }
                        break;

                    case MidiEventKind.SysEx:
                        // A SysEx may set or reset what is held, so that goes out first.
                        if ((catchingUp && !SendHeld(rate)) || !Ready(e.Port, rate))
                        {
                            sameTimeCount--;
                            rateHeld = true;
                            goto pacing;
                        }
                        if (!TrySendLong(e.Port, payload.AsSpan(e.DataOffset, e.DataLength),
                                         catchingUp))
                        {
                            sameTimeCount--;
                            goto pacing;
                        }
                        break;

                    case MidiEventKind.Meta:
                        // Tempo is already folded into TimeUs; nothing goes to the device.
                        break;

                    case MidiEventKind.LoopStart:
                        if (_loopDepth == LoopStackDepth)
                        {
                            Fail(SequencerFault.LoopNestOverflow, run);
                            return;
                        }
                        _loopDepth++;
                        _loopReturn[_loopDepth] = _index + 1;
                        _loopReturnTimeUs[_loopDepth] = e.TimeUs;
                        _loopCounter[_loopDepth] = 0;
                        _loopInfinite[_loopDepth] = false;
                        break;

                    case MidiEventKind.LoopEnd:
                        // A jump has already moved the cursor to the body's first event.
                        if (TakeLoopBack(in e)) continue;
                        break;
                }
                _index++;
            }

            // The target has been reached, or the song ran out before it. Either way the
            // data is where it should be, so the clock takes over again.
            if (catchingUp && !rateHeld)
            {
                if (!SendHeld(rate))
                {
                    rateHeld = true;
                    goto pacing;
                }
                EndSeek();
                continue;
            }

            if (_index >= events.Length) break;

        pacing:
            // A catch-up waits for the cable and nothing else; ordinary playback waits for
            // the next event, and for the cable when that is further off.
            long waitUs;
            long cableWaitUs = rateHeld ? _readyAtUs - ElapsedUs() : 0;
            if (catchingUp)
            {
                waitUs = cableWaitUs;
            }
            else
            {
                waitUs = events[_index].TimeUs + _timeShiftUs - PositionUs;
                if (cableWaitUs > waitUs) waitUs = cableWaitUs;
            }

            if (waitUs <= 0) continue;

            if (waitUs > SleepThresholdUs)
            {
                int ms = (int)Math.Min(MaxSleepMs, (waitUs - SleepMarginUs) / 1000);
                if (ms > 0) Thread.Sleep(ms);
            }
            else
            {
                Thread.SpinWait(80);
            }
        }

        if (!_stopRequested)
        {
            _pausedAtUs = events.Length > 0 ? events[^1].TimeUs + _timeShiftUs : 0;
            if (!Settle()) return;
            Stopped?.Invoke();
            Finished?.Invoke();
            Ended?.Invoke(new SequencerEnd(run, SequencerFault.None, null));
        }
    }

    /// <summary>
    /// Applies the loop-end rule and reports whether playback jumped back to the loop's start.
    /// </summary>
    /// <remarks>
    /// The written repeat count is counted down, and for a loop the data says runs forever
    /// the endless loop repeat count (<see cref="PlaybackOptions.InfiniteLoopRepeatCount"/>)
    /// stands in: it is loaded plus one and the loop is left when one is left, so the body
    /// plays exactly that many times. A setting of zero degenerates to a single pass. A loop end
    /// with no loop start open is ignored rather than treated as an error.
    /// </remarks>
    private bool TakeLoopBack(in MidiEvent e)
    {
        if (_loopDepth == 0) return false;
        int depth = _loopDepth;

        if (_loopCounter[depth] == 0)
        {
            _loopCounter[depth] = e.RepeatCount;
            if (_loopCounter[depth] == 0)
            {
                int repeat = Math.Max(0, _options.InfiniteLoopRepeatCount);
                _loopInfinite[depth] = repeat != 0;
                _loopCounter[depth] = repeat + 1;
            }
        }

        _loopCounter[depth]--;

        // An endless loop stops one short, a counted one when the count runs out.
        if (_loopInfinite[depth] ? _loopCounter[depth] == 1 : _loopCounter[depth] == 0)
        {
            _loopDepth--;
            return false;
        }

        _timeShiftUs += e.TimeUs - _loopReturnTimeUs[depth];
        _index = _loopReturn[depth];
        return true;
    }

    private void ResetLoopState()
    {
        _loopDepth = 0;
        _timeShiftUs = 0;
    }

    /// <summary>
    /// Comes to rest because the output would not take any more.
    /// </summary>
    /// <remarks>
    /// Nothing is silenced on the way out. The thing that would have to be told is the
    /// thing that just refused, and asking it again would only throw again — this time
    /// with no one left to catch it.
    /// </remarks>
    private void LostDevice(MidiDeviceException ex, int run)
    {
        _pausedAtUs = PositionUs;
        _state = PlaybackState.Stopped;
        Stopped?.Invoke();
        DeviceFailed?.Invoke(ex.Message);
        Ended?.Invoke(new SequencerEnd(run, SequencerFault.None, ex.Message));
    }

    /// <summary>Stops playback with a protection error, and says why.</summary>
    private void Fail(SequencerFault fault, int run)
    {
        Fault = fault;
        _pausedAtUs = PositionUs;
        if (!Settle()) return;
        Stopped?.Invoke();
        Faulted?.Invoke(fault);
        Ended?.Invoke(new SequencerEnd(run, fault, null));
    }

    /// <summary>
    /// Silences what the song left sounding, then comes to rest. False when a
    /// <see cref="Stop"/> came in the meantime and took over, telling of the stop itself.
    /// </summary>
    /// <remarks>
    /// Stopped only once the silence has gone out. Until then a stop, or the next song
    /// starting, waits for this thread (<see cref="Stop"/> joins it), instead of the silence
    /// landing on the next song's first messages.
    /// </remarks>
    private bool Settle()
    {
        CloseOpenSysEx();
        SilenceAllChannels();
        lock (_gate)
        {
            if (_stopRequested) return false;
            _state = PlaybackState.Stopped;
            return true;
        }
    }

    /// <summary>True for note on and note off, which a catch-up leaves out.</summary>
    private static bool IsNote(uint packed) => (packed & 0xF0) is 0x80 or 0x90;

    /// <summary>True for polyphonic key pressure, which a catch-up leaves out with the notes.</summary>
    private static bool IsKeyPressure(uint packed) => (packed & 0xF0) == 0xA0;

    /// <summary>
    /// Holds a message back from a catch-up in place of sending it, when only its last value
    /// matters: a controller that sets a value, pitch bend or channel pressure. False for one
    /// that must go out where it is.
    /// </summary>
    /// <remarks>
    /// Each port, channel and kind keeps its last message, and a newer one moves to the end of
    /// the line, so the values go out in the order they were last set. Only the playback
    /// thread touches what is held.
    /// </remarks>
    private bool Hold(int port, uint packed)
    {
        int kind = HeldKind(packed);
        if (kind < 0 || (uint)port >= IEventSink.PortCount) return false;

        int slot = (port * 16 + (int)(packed & 0x0F)) * HeldKinds + kind;
        if (_isHeld[slot]) Unlink(slot);
        else _isHeld[slot] = true;

        _heldPrev[slot] = _heldLast;
        _heldNext[slot] = -1;
        if (_heldLast >= 0) _heldNext[_heldLast] = slot;
        else _heldFirst = slot;
        _heldLast = slot;
        _heldMessage[slot] = packed;
        return true;
    }

    /// <summary>
    /// Where a message is held (a controller by its number, then pitch bend and channel
    /// pressure), or -1 for one that must go out where it is.
    /// </summary>
    /// <remarks>
    /// Out where they are, as their order carries their meaning: bank select (0, 32), data
    /// entry and increment (6, 38, 96, 97), RPN and NRPN (98 to 101), and the channel mode
    /// messages (120 to 127).
    /// </remarks>
    private static int HeldKind(uint packed)
    {
        int number = (int)(packed >> 8) & 0x7F;
        return (packed & 0xF0) switch
        {
            0xB0 when number is 0 or 32 or 6 or 38 or (>= 96 and <= 101) or >= 120 => -1,
            0xB0 => number,
            0xE0 => 128,
            0xD0 => 129,
            _ => -1,
        };
    }

    private void Unlink(int slot)
    {
        int prev = _heldPrev[slot], next = _heldNext[slot];
        if (prev >= 0) _heldNext[prev] = next;
        else _heldFirst = next;
        if (next >= 0) _heldPrev[next] = prev;
        else _heldLast = prev;
    }

    /// <summary>
    /// Sends what is held, in the order it was last set, as far as the rate cap lets it: before
    /// a message that must go out where it is, and once the catch-up is done. False when the
    /// cap stopped it, and the rest waits for the loop to come back.
    /// </summary>
    /// <remarks>
    /// Every port's, not only the one the next message is for: ports routed to the same
    /// machine would otherwise see a reset or a program change overtake a value set before it.
    /// </remarks>
    private bool SendHeld(int rate)
    {
        while (_heldFirst >= 0)
        {
            int slot = _heldFirst;
            int port = slot / (16 * HeldKinds);
            if (!Ready(port, rate)) return false;

            _heldFirst = _heldNext[slot];
            if (_heldFirst >= 0) _heldPrev[_heldFirst] = -1;
            _isHeld[slot] = false;
            TrySendShort(port, _heldMessage[slot], catchingUp: true);
        }
        _heldLast = -1;
        return true;
    }

    /// <summary>Forgets everything held, for a catch-up that is abandoned.</summary>
    private void DropHeld()
    {
        for (int slot = _heldFirst; slot >= 0; slot = _heldNext[slot]) _isHeld[slot] = false;
        _heldFirst = _heldLast = -1;
    }

    /// <summary>Microseconds of wall clock, for pacing rather than for position.</summary>
    private long ElapsedUs() => (long)Stopwatch.GetElapsedTime(_clockOrigin).TotalMicroseconds;

    /// <summary>
    /// Silences every part at once with All Sound Off and All Notes Off, whatever the stop
    /// setting says.
    /// </summary>
    /// <remarks>
    /// Not <see cref="SilenceAllChannels"/>, which follows the setting: the note-offs of what
    /// is sounding lie in the stretch being skipped. Each port first waits for the SysEx
    /// already handed to it to go out (<see cref="IEventSink.WaitUntilSent"/>).
    /// </remarks>
    private void CutSound()
    {
        int ports = PortsUsed();
        for (int port = 0; port < ports; port++) _sink.WaitUntilSent(port);
        SweepChannels(ports, RateFor(catchingUp: true), 0x78, 0x7B);  // All Sound Off, All Notes Off
    }

    /// <summary>
    /// Sends the controllers <paramref name="numbers"/> (value 0) on every channel of the
    /// first <paramref name="ports"/> ports, waiting here for each port's share of the cable.
    /// </summary>
    /// <remarks>
    /// Each message goes to every port before the next one does, so the ports' cables are
    /// kept busy side by side rather than one after another.
    /// </remarks>
    private void SweepChannels(int ports, int rate, params ReadOnlySpan<byte> numbers)
    {
        for (int ch = 0; ch < 16; ch++)
            foreach (byte number in numbers)
                for (int port = 0; port < ports; port++)
                    SendPaced(port, (uint)(0xB0 | ch) | (uint)number << 8, rate);
    }

    /// <summary>
    /// Bytes a second each port may be handed, or 0 for no cap: the setting, and during a
    /// catch-up never more than a cable carries. A port that goes down a cable is held to it
    /// as well (<see cref="RateOn"/>).
    /// </summary>
    private int RateFor(bool catchingUp)
    {
        int rate = Math.Max(0, _options.TransferRateBytesPerSecond);
        return catchingUp && (rate == 0 || rate > CableBytesPerSecond) ? CableBytesPerSecond : rate;
    }

    /// <summary>
    /// <paramref name="rate"/> for <paramref name="port"/>, and never more than a cable carries
    /// when the port goes down one (<see cref="IEventSink.IsCable"/>).
    /// </summary>
    /// <remarks>
    /// Such an output has a cable's worth of room in front of it and throws away the rest.
    /// Asked of the port each time: the outputs are routed afresh from song to song.
    /// </remarks>
    private int RateOn(int port, int rate)
        => (rate <= 0 || rate > CableBytesPerSecond) && _sink.IsCable(port) ? CableBytesPerSecond : rate;

    /// <summary>
    /// Whether <paramref name="port"/> may be handed something now; when not, notes when it
    /// may (<see cref="_readyAtUs"/>).
    /// </summary>
    private bool Ready(int port, int rate)
    {
        if ((uint)port >= (uint)_portReadyUs.Length || (rate = RateOn(port, rate)) <= 0) return true;

        long ready = Volatile.Read(ref _portReadyUs[port]);
        if (ElapsedUs() >= ready) return true;
        _readyAtUs = ready;
        return false;
    }

    /// <summary>Counts <paramref name="bytes"/> handed to <paramref name="port"/> against its cable.</summary>
    private void Spend(int port, int bytes, int rate)
    {
        if ((uint)port >= (uint)_portReadyUs.Length || (rate = RateOn(port, rate)) <= 0) return;

        long now = ElapsedUs();
        long from = Math.Max(now, _portReadyUs[port]);
        Volatile.Write(ref _portReadyUs[port], from + bytes * 1_000_000L / rate);
    }

    /// <summary>Waits here until <paramref name="port"/> may be handed something.</summary>
    private void WaitForPort(int port, int rate)
    {
        if ((uint)port >= (uint)_portReadyUs.Length || (rate = RateOn(port, rate)) <= 0) return;

        long until = Volatile.Read(ref _portReadyUs[port]);
        for (long left; (left = until - ElapsedUs()) > 0;)
        {
            if (left > SleepThresholdUs) Thread.Sleep((int)Math.Min(MaxSleepMs, (left - SleepMarginUs) / 1000));
            else Thread.SpinWait(80);
        }
    }

    /// <summary>Sends a channel message once the port's cable allows, waiting for it here.</summary>
    private void SendPaced(int port, uint packed, int rate)
    {
        WaitForPort(port, rate);
        _sink.SendShort(port, packed);
        Spend(port, ShortLength(packed), rate);
    }

    /// <summary>Bytes a channel message takes on the cable, without running status.</summary>
    private static int ShortLength(uint packed) => (packed & 0xF0) is 0xC0 or 0xD0 ? 2 : 3;

    /// <summary>
    /// How many ports to sweep: the ones the song reaches, and never more than there are.
    /// </summary>
    private int PortsUsed()
        => Math.Clamp((_sequence?.MaxPort ?? 0) + 1, 1, IEventSink.PortCount);

    /// <summary>
    /// Quietens every part, if the settings ask for it: All Sound Off, All Notes Off and, unless
    /// <paramref name="resetControllers"/> is false, Reset All Controllers, so nothing is left
    /// hanging.
    /// </summary>
    /// <remarks>
    /// All Sound Off is what makes it quiet at once. All Notes Off only lets go of the keys, and
    /// every note then fades out on its own release, which on a pad can go on for seconds.
    /// What the effects already hold — the reverb or delay tail — is past the parts, and
    /// neither message reaches it.
    ///
    /// A SysEx the song left open is closed first, so these are not taken as part of it.
    ///
    /// A device that has gone is caught here rather than thrown on: this runs on the way out
    /// of playback, on whichever thread asked for the stop, and there is nothing left to
    /// quieten anyway.
    /// </remarks>
    private void SilenceAllChannels(bool resetControllers = true)
    {
        if (!_options.SendAllNotesOffOnStop) return;

        CloseOpenSysEx();
        try
        {
            int ports = PortsUsed(), rate = RateFor(catchingUp: false);
            // All Sound Off, All Notes Off, Reset All Controllers
            if (resetControllers) SweepChannels(ports, rate, 0x78, 0x7B, 0x79);
            else SweepChannels(ports, rate, 0x78, 0x7B);
        }
        catch (MidiDeviceException)
        {
            // The output is the thing that is gone; there is nothing to say to it.
        }
    }

    private static ThreadPriority MapPriority(PlaybackPriority priority) => priority switch
    {
        PlaybackPriority.Low => ThreadPriority.BelowNormal,
        PlaybackPriority.High => ThreadPriority.AboveNormal,
        _ => ThreadPriority.Normal,
    };

    /// <summary>
    /// Raising thread priority needs privileges on some systems, so a refusal is not fatal.
    /// </summary>
    private static void TrySetPriority(Thread thread, PlaybackPriority priority)
    {
        try { thread.Priority = MapPriority(priority); }
        catch (PlatformNotSupportedException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => Stop();
}
