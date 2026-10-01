using Glosa.Core.Smf;

namespace Glosa.Core.Playback;

/// <summary>Order the playlist is walked in.</summary>
public enum PlayOrder
{
    /// <summary>The order songs were added in.</summary>
    Registered,
    Random,
    FileName,
    Title,
}

/// <summary>What happens when a song ends.</summary>
public enum RepeatMode
{
    /// <summary>Walk the list once and stop at the end.</summary>
    None,
    /// <summary>Play this song and stop.</summary>
    Single,
    /// <summary>Play this song over and over.</summary>
    SingleRepeat,
    /// <summary>Walk the list, then start it again.</summary>
    All,
}

/// <summary>State that carries across a song and has to be put back between songs.</summary>
public interface IPlaybackReset
{
    void Reset();
}

/// <summary>Where the transport is. Only <see cref="PlaybackController"/> moves it.</summary>
public enum TransportState
{
    /// <summary>At rest: nothing playing, and nothing on its way.</summary>
    Stopped,
    /// <summary>A song is being read and set up. Nothing is going out of the sequencer.</summary>
    Preparing,
    Playing,
    Paused,
}

/// <summary>Why the transport came to rest.</summary>
public enum StopCause
{
    /// <summary>Asked to: the stop button, or something that could not let the song go on.</summary>
    Requested,
    /// <summary>The songs ran out — the end of the list, or the one song of single mode.</summary>
    EndOfList,
    /// <summary>Not one song on the list could be read.</summary>
    NothingPlayable,
    /// <summary>A protection limit stopped the song (<see cref="PlaybackStop.Fault"/>).</summary>
    Faulted,
    /// <summary>An output refused what was sent (<see cref="PlaybackStop.Problem"/>).</summary>
    DeviceLost,
    /// <summary>Something threw while a song was being changed (<see cref="PlaybackStop.Problem"/>).</summary>
    Failed,
}

/// <summary>How the transport came to rest.</summary>
public sealed record PlaybackStop(StopCause Cause, SequencerFault Fault = SequencerFault.None,
                                  string? Problem = null);

/// <summary>
/// Drives a <see cref="Sequencer"/> through a <see cref="Playlist"/>: which song is next,
/// in what order, and what happens when one ends.
/// </summary>
/// <remarks>
/// This lives in the core rather than in a view model so that the console harness walks a
/// playlist through the same song-change path the UI takes, emulation reset included.
///
/// <para>
/// The transport is a state machine with one thread to move it. Every request — the
/// buttons, and the sequencer saying a song has ended — goes into a queue, and the
/// controller's own thread takes them one at a time. A change of song is several steps that
/// have to happen in order (stop the old song, read the new one, set the module up for it,
/// start it); since one request finishes before the next is looked at, two changes never
/// interleave and a stop is never undone by the rest of one. The steps are written out
/// once, in order, in <see cref="Start"/>.
/// </para>
/// <para>
/// A song being set up can be overtaken. A stop, or a request for another song, that comes
/// in while one is being prepared is not left to wait behind it: between steps the
/// preparation looks for one, and gives up before its next step if there is. That is also
/// how whoever sets the module up (<see cref="Loading"/>) refuses a song — by asking for a
/// stop.
/// </para>
/// <para>
/// The requests return at once, with a task that completes once the request has been dealt
/// with. The UI does not wait on it: the handlers run on the controller's thread and may
/// themselves need the UI thread.
/// </para>
/// </remarks>
public sealed class PlaybackController : IDisposable
{
    private readonly Sequencer _sequencer;
    private readonly Func<string, MidiSequence> _load;
    private readonly Lock _gate = new();
    private readonly Random _random;

    private Playlist _playlist = new();

    /// <summary>The songs in the order they are walked: the list as it stood when last told.</summary>
    /// <remarks>
    /// The songs themselves, not where they sit in the list. The list is the UI's, and it
    /// changes first and says so after (<see cref="Rearranged"/>); positions read in between
    /// would name whatever song had moved into the place, and the cursor would be taken to
    /// be on a song it is not on. A copy also keeps this thread from reading a list the UI
    /// may be writing.
    /// </remarks>
    private PlaylistItem[] _order = [];

    /// <summary>
    /// Where in <see cref="_order"/> the cursor is — or, while <see cref="_detached"/>, the
    /// song the cursor goes on to next (<c>_order.Length</c> when there is none).
    /// </summary>
    private int _position = -1;

    /// <summary>
    /// The song under the cursor once it has been taken out of the list while it plays, or
    /// null. It plays on; the cursor sits where it was, between the songs either side.
    /// </summary>
    private PlaylistItem? _detached;

    /// <summary>The song <see cref="CurrentChanged"/> last named.</summary>
    private PlaylistItem? _announced;

    private PlayOrder _playOrder = PlayOrder.Registered;

    /// <summary>The requests not taken yet. Also what the thread waits on.</summary>
    private readonly Queue<Request> _queue = new();

    /// <summary>Numbers the requests as they come in.</summary>
    private int _lastSeq;

    /// <summary>The number of the latest request that overtakes a song being set up.</summary>
    private int _lastOvertaking;

    /// <summary>The sequencer run the controller last started, so a late end is known for one.</summary>
    private int _run;

    private volatile TransportState _state = TransportState.Stopped;

    /// <summary>
    /// Held around starting the sequencer, so that once <see cref="Dispose"/> has returned
    /// nothing is started.
    /// </summary>
    private readonly Lock _life = new();
    private volatile bool _disposed;

    private readonly Thread _thread;

    public PlaybackController(Sequencer sequencer,
                              Func<string, MidiSequence>? load = null,
                              IPlaybackReset? reset = null,
                              int? randomSeed = null)
    {
        _sequencer = sequencer;
        _load = load ?? (path => SmfReader.Read(path));
        Reset = reset;
        _random = randomSeed is null ? new Random() : new Random(randomSeed.Value);
        _sequencer.Ended += OnSequencerEnded;
        _sequencer.Rewinding += OnRewinding;

        _thread = new Thread(Work) { Name = "glosa-transport", IsBackground = true };
        _thread.Start();
    }

    public Playlist Playlist => _playlist;

    /// <summary>Where the transport is.</summary>
    public TransportState State => _state;

    /// <summary>
    /// What gets put back between songs. Settable because rebuilding the emulation layer
    /// replaces the object that holds the state.
    /// </summary>
    public IPlaybackReset? Reset { get; set; }

    /// <summary>What happens at the end of a song.</summary>
    public RepeatMode Repeat { get; set; } = RepeatMode.None;

    public PlayOrder Order
    {
        get => _playOrder;
        set
        {
            lock (_gate)
            {
                if (_playOrder == value) return;
                _playOrder = value;
                // The song playing now keeps playing; only what comes after it changes.
                BuildOrder(Current);
            }
            Announce();
        }
    }

    /// <summary>The song the cursor is on, or null when the list is empty.</summary>
    public PlaylistItem? Current
    {
        get
        {
            lock (_gate)
            {
                if (_detached is not null) return _detached;
                return _position >= 0 && _position < _order.Length ? _order[_position] : null;
            }
        }
    }

    /// <summary>
    /// Raised once a song has been read and before it starts, so whoever is listening can
    /// set things up for it.
    /// </summary>
    /// <remarks>
    /// On the controller's thread, which waits for the handlers: the module a song was
    /// written for has to be settled before a note of it goes out. The song before it has
    /// already been stopped. A handler that finds the song cannot go on asks for a
    /// <see cref="Stop"/>, and the song is not started.
    /// </remarks>
    public event Action<PlaylistItem, MidiSequence>? Loading;

    /// <summary>Raised when the cursor moves to another song.</summary>
    /// <remarks>
    /// Whether or not anything plays: stepping while stopped, or a list running out, moves
    /// it too. For the songs actually played, see <see cref="Started"/>. Raised on the thread
    /// that moved it, so two may cross; <see cref="Current"/> is where it is now.
    /// </remarks>
    public event Action<PlaylistItem?>? CurrentChanged;

    /// <summary>Raised on the controller's thread once a song has started playing.</summary>
    public event Action<PlaylistItem>? Started;

    /// <summary>
    /// Raised when the transport comes to rest, and why. Not between songs: only when
    /// nothing is going to follow.
    /// </summary>
    /// <remarks>
    /// On the controller's thread, which waits for the handlers, so whatever they let go
    /// of — the outputs — is let go before the next request is looked at.
    /// </remarks>
    public event Action<PlaybackStop>? Stopped;

    /// <summary>Raised when a song could not be read; playback skips it.</summary>
    public event Action<PlaylistItem, Exception>? LoadFailed;

    /// <summary>
    /// Raised on the sequencer thread as a seek goes back towards the start of the song,
    /// after the carried state has been put back and before the song is replayed.
    /// </summary>
    public event Action? Rewinding;

    /// <summary>Hands over another list, with the cursor on <paramref name="start"/> or at the top.</summary>
    public void SetPlaylist(Playlist playlist, PlaylistItem? start = null)
    {
        PlaylistItem? now;
        lock (_gate)
        {
            _playlist = playlist;
            BuildOrder(null);
            if (start is not null && IndexOf(_order, start) is >= 0 and int at) _position = at;
            now = _announced = Current;
        }
        CurrentChanged?.Invoke(now);
    }

    /// <summary>
    /// Works the walk order out again, for a list that has been changed in place: songs
    /// moved, added or taken out.
    /// </summary>
    /// <remarks>
    /// Not <see cref="SetPlaylist"/>: that is for a different list and starts at the top of
    /// it. Here the list is the same one, so the cursor stays on its song and only what
    /// comes after changes. A song taken out while it plays goes on playing, and is followed
    /// by the song that followed it.
    /// </remarks>
    public void Rearranged()
    {
        lock (_gate) BuildOrder(Current);
        Announce();
    }

    /// <summary>Starts the song at <paramref name="itemIndex"/> in the playlist's own order.</summary>
    /// <remarks>
    /// The song is taken now, not when the request is dealt with: the list may have been
    /// changed in between, and it is this song that was asked for.
    /// </remarks>
    public Task Play(int itemIndex)
    {
        PlaylistItem? item;
        lock (_gate)
        {
            item = itemIndex >= 0 && itemIndex < _playlist.Items.Count
                ? _playlist.Items[itemIndex]
                : null;
        }
        return Ask(new PlayItem(item));
    }

    /// <summary>Starts whatever the cursor is on, or lifts a pause.</summary>
    public Task Play() => Ask(new PlayCursor());

    public Task Pause() => Ask(new PauseRequest());

    /// <summary>Moves the song on the transport to <paramref name="position"/>.</summary>
    public Task Seek(TimeSpan position) => Ask(new SeekRequest(position));

    /// <summary>
    /// Pauses, or lifts a pause. What the one button on a transport does.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Pause"/> so that method keeps meaning what it says; a
    /// caller that wants a pause and nothing else still has one.
    /// </remarks>
    public Task TogglePause() => Ask(new TogglePauseRequest());

    public Task Stop() => Ask(new StopRequest());

    public Task Next() => Ask(new StepRequest(+1));

    /// <summary>
    /// Goes to the song before — or, more than <see cref="RestartAfter"/> into a song that is
    /// playing or paused, back to the start of this one.
    /// </summary>
    public Task Previous() => Ask(new StepRequest(-1));

    /// <summary>Completes once every request made before it has been dealt with.</summary>
    public Task WhenIdle() => Ask(new Nothing());

    // ------------------------------------------------------------------ requests

    private abstract class Request
    {
        public int Seq;

        public readonly TaskCompletionSource Done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Whether this request makes a song still being set up pointless: it either stops
        /// the transport or starts another song.
        /// </summary>
        public virtual bool Overtakes => false;
    }

    private sealed class PlayItem(PlaylistItem? item) : Request
    {
        public PlaylistItem? Item { get; } = item;
        public override bool Overtakes => true;
    }

    private sealed class PlayCursor : Request;

    private sealed class PauseRequest : Request;

    private sealed class TogglePauseRequest : Request;

    private sealed class SeekRequest(TimeSpan position) : Request
    {
        public TimeSpan Position { get; } = position;
    }

    private sealed class StopRequest : Request
    {
        public override bool Overtakes => true;
    }

    private sealed class StepRequest(int by) : Request
    {
        public int By { get; } = by;
        public override bool Overtakes => true;
    }

    private sealed class SongEnded(SequencerEnd end) : Request
    {
        public SequencerEnd End { get; } = end;
    }

    private sealed class Nothing : Request;

    private Task Ask(Request request)
    {
        lock (_queue)
        {
            if (_disposed)
            {
                request.Done.TrySetResult();
                return request.Done.Task;
            }

            request.Seq = ++_lastSeq;
            if (request.Overtakes) Volatile.Write(ref _lastOvertaking, request.Seq);
            _queue.Enqueue(request);
            Monitor.Pulse(_queue);
        }
        return request.Done.Task;
    }

    /// <summary>Whether a stop or another song has been asked for since <paramref name="request"/>.</summary>
    private bool Overtaken(Request request) => Volatile.Read(ref _lastOvertaking) > request.Seq;

    private Request? Take()
    {
        lock (_queue)
        {
            while (_queue.Count == 0 && !_disposed) Monitor.Wait(_queue);
            return _disposed ? null : _queue.Dequeue();
        }
    }

    private void Work()
    {
        while (Take() is { } request)
        {
            try
            {
                Handle(request);
                Announce();
            }
            catch (Exception ex)
            {
                // A handler threw — setting the module up, most likely. The song cannot be
                // trusted to be where it should be, so the transport comes to rest and says
                // why, and the next request finds it in a state it knows.
                ComeToRest(new PlaybackStop(StopCause.Failed, Problem: ex.Message), force: true);
            }
            finally
            {
                request.Done.TrySetResult();
            }
        }
    }

    // ------------------------------------------------------------------ transitions

    private void Handle(Request request)
    {
        switch (request)
        {
            case PlayItem play:
            {
                int at;
                lock (_gate) at = play.Item is null ? -1 : Array.FindIndex(
                    _order, item => ReferenceEquals(item, play.Item));
                if (at < 0)
                {
                    // The song has left the list since. Whatever this overtook — a song
                    // being set up, or the one that had just ended — is not going on either,
                    // so the transport comes to rest rather than be left saying it plays.
                    if (_state == TransportState.Preparing || _sequencer.State == PlaybackState.Stopped)
                        ComeToRest(new PlaybackStop(StopCause.Requested));
                    return;
                }
                lock (_gate)
                {
                    _position = at;
                    _detached = null;
                }
                Start(request);
                return;
            }

            case PlayCursor:
                switch (_state)
                {
                    case TransportState.Paused:
                        Resume();
                        return;
                    case TransportState.Stopped:
                        lock (_gate)
                        {
                            if (_position < 0 && _order.Length > 0) _position = 0;
                        }
                        Start(request);
                        return;
                    default:
                        return;
                }

            case PauseRequest:
                if (_state == TransportState.Playing) PauseNow();
                return;

            case TogglePauseRequest:
                if (_state == TransportState.Playing) PauseNow();
                else if (_state == TransportState.Paused) Resume();
                return;

            case SeekRequest seek:
                if (_state is TransportState.Playing or TransportState.Paused)
                    _sequencer.Seek(seek.Position);
                return;

            case StopRequest:
                ComeToRest(new PlaybackStop(StopCause.Requested));
                return;

            case StepRequest step:
                Step(step);
                return;

            case SongEnded ended:
                OnSongEnded(ended);
                return;
        }
    }

    private void PauseNow()
    {
        _sequencer.Pause();
        // The song may have ended a moment ago, with the news of it still in the queue;
        // then there is nothing to pause, and the end is dealt with next.
        if (_sequencer.State == PlaybackState.Paused) _state = TransportState.Paused;
    }

    private void Resume()
    {
        _sequencer.Resume();
        if (_sequencer.State == PlaybackState.Playing) _state = TransportState.Playing;
    }

    /// <summary>
    /// How far into a song "previous" goes back to its start rather than to the song before.
    /// </summary>
    public static readonly TimeSpan RestartAfter = TimeSpan.FromSeconds(3);

    private void Step(StepRequest step)
    {
        // Back, well into a song, is back to its start: what a listener pressing it a few
        // seconds in almost always wants. Only near the top does it go to the song before —
        // there the start is where they already are.
        if (step.By < 0 && _state is TransportState.Playing or TransportState.Paused
            && _sequencer.Position > RestartAfter)
        {
            // The way a seek to the top does it, module setup included, and a pause stays a
            // pause. A song that has just run out, with the news of it overtaken by this,
            // has nothing left to seek in, so it is started again.
            if (_sequencer.State != PlaybackState.Stopped) _sequencer.Seek(TimeSpan.Zero);
            else Start(step);
            return;
        }

        // Anything but rest counts as playing: a song being set up is a song the listener
        // is waiting to hear, and stepping past it means hearing the next one instead.
        bool playing = _state != TransportState.Stopped;
        bool empty;

        lock (_gate)
        {
            // Stepping by hand wraps, whatever the repeat mode says: the button is the user
            // asking for another song, not the list reaching its end.
            empty = !MoveCursor(step.By, wrap: true);
        }

        // Outside the lock: the handlers may need the UI thread, which may be waiting on it.
        if (empty)
        {
            if (playing) ComeToRest(new PlaybackStop(StopCause.Requested));
            return;
        }

        if (playing) Start(step);
    }

    private void OnSongEnded(SongEnded ended)
    {
        // The end of a song this controller is no longer playing — one that ended just as
        // another was asked for — says nothing about the song playing now.
        if (ended.End.Run != _run
            || _state is not (TransportState.Playing or TransportState.Paused))
            return;

        // A stop or another song has been asked for since; that request says what happens
        // next, not the repeat mode.
        if (Overtaken(ended)) return;

        if (ended.End.Fault != SequencerFault.None)
        {
            ComeToRest(new PlaybackStop(StopCause.Faulted, Fault: ended.End.Fault));
            return;
        }
        if (ended.End.DeviceProblem is { } problem)
        {
            ComeToRest(new PlaybackStop(StopCause.DeviceLost, Problem: problem));
            return;
        }

        switch (Repeat)
        {
            case RepeatMode.Single:
                ComeToRest(new PlaybackStop(StopCause.EndOfList));
                return;

            case RepeatMode.SingleRepeat:
                Start(ended);
                return;

            case RepeatMode.None:
            case RepeatMode.All:
                lock (_gate)
                {
                    if (!MoveOn(wrap: Repeat == RepeatMode.All)) goto done;
                }
                Start(ended);
                return;
        }

    done:
        ComeToRest(new PlaybackStop(StopCause.EndOfList));
    }

    /// <summary>
    /// Changes to the song under the cursor: the one transition with more than one step,
    /// written out in the order the steps have to happen.
    /// </summary>
    /// <remarks>
    /// Overtaken — a stop or another song asked for — it gives up before its next step and
    /// leaves the transport <see cref="TransportState.Preparing"/>; the request that
    /// overtook it is still in the queue and says where the transport goes from there.
    /// </remarks>
    private void Start(Request cause)
    {
        _state = TransportState.Preparing;

        // Bounded so a list of unreadable files stops instead of spinning.
        int attempts;
        lock (_gate) attempts = _order.Length;
        StopCause stop = StopCause.NothingPlayable;

        for (int i = 0; i < attempts; i++)
        {
            if (Overtaken(cause)) return;

            PlaylistItem? item = Current;
            if (item is null) break;

            MidiSequence sequence;
            try
            {
                sequence = _load(item.Path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                                          or UnauthorizedAccessException)
            {
                LoadFailed?.Invoke(item, ex);
                if (PastUnreadable(cause) is not { } next) break;
                lock (_gate)
                {
                    bool moved = cause is StepRequest
                        ? MoveCursor(next.By, next.Wrap)
                        : MoveOn(next.Wrap);
                    if (!moved) { stop = StopCause.EndOfList; break; }
                }
                continue;
            }

            if (Overtaken(cause)) return;

            item.DurationMs = sequence.DurationUs / 1000;
            if (Text.TitleText.IsBlank(item.Title)) item.Title = sequence.Title;

            // 1. The song being left stops, before anything is sent for the next one. The
            //    next song's setup is a module reset with a wait in it, which the old song
            //    would otherwise play on through. Only once the file has been read: a song
            //    that cannot be read leaves the old one playing while the next is tried.
            _sequencer.Stop();

            // 2. The module is set up for the song. Before the reset, because a handler may
            //    well replace what gets reset.
            Loading?.Invoke(item, sequence);

            // A handler that could not set the module up asks for a stop; so may the
            // listener, while the setup was going out.
            if (Overtaken(cause)) return;

            // 3. Nothing of the last song may leak into this one.
            Reset?.Reset();

            // 4. The song starts.
            _sequencer.Load(sequence);
            lock (_gate) _announced = item;
            CurrentChanged?.Invoke(item);
            lock (_life)
            {
                if (_disposed) return;
                _sequencer.Play();
                _run = _sequencer.CurrentRun;
                _state = TransportState.Playing;
            }
            Started?.Invoke(item);
            return;
        }

        ComeToRest(new PlaybackStop(stop));
    }

    /// <summary>
    /// Which way to go on past a song that cannot be read, or null to go no further.
    /// </summary>
    /// <remarks>
    /// The way the request was going. A step by hand keeps its direction and wraps, as the
    /// step itself does. Otherwise the repeat mode says: round to the top only when the list
    /// repeats, and on to no other song when one song was asked for.
    /// </remarks>
    private (int By, bool Wrap)? PastUnreadable(Request cause) => cause switch
    {
        StepRequest step => (step.By < 0 ? -1 : 1, true),
        _ => Repeat switch
        {
            RepeatMode.All => (1, true),
            RepeatMode.None => (1, false),
            _ => null,
        },
    };

    /// <summary>
    /// Brings the transport to rest and says so, unless it is at rest already.
    /// </summary>
    /// <remarks>
    /// <paramref name="force"/> is for when a handler threw and the state cannot be trusted
    /// to say whether anything is still going: it stops and tells regardless.
    /// </remarks>
    private void ComeToRest(PlaybackStop stop, bool force = false)
    {
        if (_state == TransportState.Stopped && !force) return;

        _sequencer.Stop();
        _state = TransportState.Stopped;

        // A song taken out of the list while it played is over now, and the cursor moves on
        // to the song that took its place rather than keep a song the list no longer has.
        lock (_gate)
        {
            if (_detached is not null)
            {
                _detached = null;
                if (_position >= _order.Length) _position = _order.Length > 0 ? 0 : -1;
            }
        }

        // What the display is holding — a line of text, a picture, the parts' levels —
        // belongs to the song that was playing, and that song is over; leaving it there
        // says something is still going on.
        Reset?.Reset();

        if (!force)
        {
            Stopped?.Invoke(stop);
            return;
        }

        // Already on the way out of a failure; one more must not take the thread with it.
        try
        {
            Stopped?.Invoke(stop);
        }
        catch (Exception)
        {
        }
    }

    // ------------------------------------------------------------------ from the sequencer

    /// <summary>A run ended by itself. Taken in turn with everything else, on this thread.</summary>
    private void OnSequencerEnded(SequencerEnd end) => Ask(new SongEnded(end));

    /// <summary>
    /// A seek is going back towards the start of the song. On the sequencer thread, which
    /// replays the song as soon as this returns, so it is done here and not queued.
    /// </summary>
    private void OnRewinding()
    {
        // Seeking backwards replays the song from its start, which needs the same clean
        // slate a song change does — and then whatever the listeners put back after it.
        Reset?.Reset();
        Rewinding?.Invoke();
    }

    /// <summary>
    /// Makes the walk order again from the list, with the cursor on
    /// <paramref name="keepOn"/>, or at the top when that is null.
    /// </summary>
    /// <remarks>
    /// When <paramref name="keepOn"/> has been taken out of the list, the cursor stays where
    /// it was: in front of the first song after it, in the order as it was, that is still
    /// there. While the song plays it is kept as <see cref="_detached"/>, so it is still
    /// the one under the cursor; at rest there is nothing to keep, and the cursor is simply
    /// on the song after it.
    /// </remarks>
    private void BuildOrder(PlaylistItem? keepOn)
    {
        PlaylistItem[] was = _order;
        int wasAt = _detached is null ? _position + 1 : _position;

        PlaylistItem[] order = [.. _playlist.Items];
        switch (_playOrder)
        {
            case PlayOrder.Registered:
                break;
            case PlayOrder.FileName:
            {
                StringComparer names = NameOrder.Comparer;
                Array.Sort(order, (a, b) => names.Compare(
                    System.IO.Path.GetFileName(a.Path), System.IO.Path.GetFileName(b.Path)));
                break;
            }
            case PlayOrder.Title:
                // Culture-aware: the titles are mostly Japanese, where ordinal order is noise.
                Array.Sort(order, (a, b) => string.Compare(
                    a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase));
                break;
            case PlayOrder.Random:
                Shuffle(order);
                break;
        }

        _order = order;
        _detached = null;

        if (keepOn is null)
        {
            _position = order.Length > 0 ? 0 : -1;
            return;
        }

        _position = IndexOf(order, keepOn);
        if (_position >= 0) return;

        _position = order.Length;
        for (int i = Math.Max(wasAt, 0); i < was.Length; i++)
        {
            int after = IndexOf(order, was[i]);
            if (after < 0) continue;
            _position = after;
            break;
        }

        if (_state != TransportState.Stopped) _detached = keepOn;
        else if (_position >= order.Length) _position = order.Length > 0 ? 0 : -1;
    }

    /// <summary>Raises <see cref="CurrentChanged"/> if the cursor has moved. Outside the lock.</summary>
    private void Announce()
    {
        PlaylistItem? now;
        lock (_gate)
        {
            now = Current;
            if (ReferenceEquals(now, _announced)) return;
            _announced = now;
        }
        CurrentChanged?.Invoke(now);
    }

    private static int IndexOf(PlaylistItem[] order, PlaylistItem item)
        => Array.FindIndex(order, each => ReferenceEquals(each, item));

    private void Shuffle(PlaylistItem[] order)
    {
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
    }

    /// <summary>
    /// Goes on to the next song as the list does by itself. At the end the cursor goes back
    /// to the top; false when it does not <paramref name="wrap"/>.
    /// </summary>
    private bool MoveOn(bool wrap)
    {
        PlaylistItem? leaving = Current;
        bool moved = MoveCursor(+1, wrap);
        if (!moved || _position == 0) Rewind(leaving);
        return moved;
    }

    /// <summary>
    /// Puts the cursor at the top for another pass. A random walk is reshuffled, and does
    /// not start on <paramref name="leaving"/>.
    /// </summary>
    private void Rewind(PlaylistItem? leaving)
    {
        _detached = null;
        _position = _order.Length > 0 ? 0 : -1;
        if (_playOrder != PlayOrder.Random) return;

        Shuffle(_order);
        if (_order.Length > 1 && ReferenceEquals(_order[0], leaving))
        {
            int other = 1 + _random.Next(_order.Length - 1);
            (_order[0], _order[other]) = (_order[other], _order[0]);
        }
    }

    /// <summary>
    /// Moves the cursor <paramref name="by"/> songs. False when there is nowhere to go: the
    /// list is empty, or — not wrapping — the walk has run off the end, where the cursor is
    /// left on the last song.
    /// </summary>
    /// <remarks>
    /// Off a song that has been taken out of the list, the cursor is already in front of
    /// the song after it, so going on is one step fewer.
    /// </remarks>
    private bool MoveCursor(int by, bool wrap)
    {
        int count = _order.Length;
        int at = _detached is not null && by > 0 ? _position + by - 1 : _position + by;
        _detached = null;

        if (count == 0)
        {
            _position = -1;
            return false;
        }

        if (wrap)
        {
            _position = (at % count + count) % count;
            return true;
        }

        if (at < 0 || at >= count)
        {
            _position = count - 1;
            return false;
        }

        _position = at;
        return true;
    }

    /// <summary>
    /// Stops taking requests. What is still queued is dropped, and nothing is started from
    /// here on.
    /// </summary>
    /// <remarks>
    /// Does not wait for the thread. It may be inside a handler that is waiting for the
    /// thread calling this — the UI, on its way out — and it is a background thread.
    /// </remarks>
    public void Dispose()
    {
        lock (_life) _disposed = true;
        lock (_queue)
        {
            foreach (Request left in _queue) left.Done.TrySetResult();
            _queue.Clear();
            Monitor.PulseAll(_queue);
        }
        _sequencer.Ended -= OnSequencerEnded;
        _sequencer.Rewinding -= OnRewinding;
    }
}
