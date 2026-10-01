using System.Diagnostics;
using Glosa.Core.Playback;
using Glosa.Core.Smf;

namespace Glosa.Tests;

public class PlaylistFileTests
{
    [Fact]
    public void RoundTripsThroughYaml()
    {
        var playlist = new Playlist
        {
            Name = "テスト",
            Items =
            [
                new PlaylistItem
                {
                    Path = @"D:\Music\曲.mid", Title = "曲名", DurationMs = 1234,
                    Module = "SC-88Pro",
                },
                new PlaylistItem { Path = @"D:\Music\b.mid" },
            ],
        };

        Playlist back = PlaylistFile.Parse(PlaylistFile.ToYaml(playlist));

        Assert.Equal("テスト", back.Name);
        Assert.Equal(2, back.Items.Count);
        Assert.Equal(@"D:\Music\曲.mid", back.Items[0].Path);
        Assert.Equal("曲名", back.Items[0].Title);
        Assert.Equal(1234, back.Items[0].DurationMs);
        Assert.Equal("SC-88Pro", back.Items[0].Module);
        Assert.Equal(string.Empty, back.Items[1].Title);

        // Empty is a song left to the detector, and must not come back as anything else.
        Assert.Equal(string.Empty, back.Items[1].Module);
    }

    [Fact]
    public void AFileSaysWhichVersionOfTheFormatItIs()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"glosa-list-{Guid.NewGuid():N}");
        string path = Path.Combine(folder, "list.yaml");
        try
        {
            PlaylistFile.Save(new Playlist(), path);

            Assert.Contains($"version: {PlaylistFile.FormatVersion}", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AFileInANewerFormatIsRefused()
        => Assert.Throws<InvalidDataException>(
            () => PlaylistFile.Parse($"version: {PlaylistFile.FormatVersion + 1}\nname: x"));

    [Fact]
    public void ANameLeftOutIsLeftToTheFile() => Assert.Equal("", PlaylistFile.Parse("items: []").Name);

    [Fact]
    public void ASongIsFoundFromTheFolderOfTheList()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"glosa-list-{Guid.NewGuid():N}");
        string path = Path.Combine(folder, "list.yaml");
        string elsewhere = Path.Combine(Path.GetTempPath(), "elsewhere.mid");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(path, $"items:\n  - path: song.mid\n  - path: sub/b.mid\n  - path: '{elsewhere}'\n");

            Assert.Equal(
                [Path.Combine(folder, "song.mid"), Path.Combine(folder, "sub", "b.mid"), elsewhere],
                PlaylistFile.Load(path).Items.Select(i => i.Path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AcceptsAHandWrittenFileWithKeysMissingAndKeysItDoesNotKnow()
    {
        Playlist list = PlaylistFile.Parse("""
            name: Mine
            items:
              - path: a.mid
                rating: 5
              - path: b.mid
                title: B
            """);

        Assert.Equal("Mine", list.Name);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(0, list.Items[0].DurationMs);
        Assert.Equal("B", list.Items[1].Title);
    }

    [Fact]
    public void FallsBackToTheFileNameWhenThereIsNoTitle()
    {
        var item = new PlaylistItem { Path = Path.Combine("Music", "song.mid") };
        Assert.Equal("song", item.Display);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("　　")]
    [InlineData(" 	　 ")]
    public void ATitleOfNothingButSpacingCountsAsNone(string title)
    {
        var item = new PlaylistItem { Path = Path.Combine("Music", "song.mid"), Title = title };
        Assert.Equal("song", item.Display);
    }

    [Fact]
    public void KeysLeftWithoutAValueReadAsTheirDefaults()
    {
        Playlist list = PlaylistFile.Parse("""
            name:
            items:
              - path: a.mid
                title:
                module:
            """);

        Assert.Equal("", list.Name);
        PlaylistItem item = Assert.Single(list.Items);
        Assert.Equal("", item.Title);
        Assert.Equal("", item.Module);
    }

    [Fact]
    public void NoItemsIsAnEmptyList() => Assert.Empty(PlaylistFile.Parse("items:").Items);

    [Fact]
    public void ItemsWithNothingToPlayAreDropped()
    {
        Playlist list = PlaylistFile.Parse("""
            items:
              -
              - path:
              - title: no path
              - path: b.mid
            """);

        Assert.Equal(["b.mid"], list.Items.Select(i => i.Path));
    }
}

public class PlaybackControllerTests
{
    private sealed class NullSink : IEventSink
    {
        public void SendShort(int port, uint packedMessage) { }
        public void SendLong(int port, ReadOnlySpan<byte> sysEx) { }
    }

    private sealed class ResetCounter : IPlaybackReset
    {
        public int Count { get; private set; }
        public void Reset() => Count++;
    }

    /// <summary>A song short enough that a test can watch several of them go by.</summary>
    private static MidiSequence Tiny()
        => SmfReader.Read(new SmfBuilder(480).Track(t => t.Short(0, 0x90, 60, 100).End(0)).Build());

    private static MidiSequence Long()
        => SmfReader.Read(new SmfBuilder(480)
            .Track(t => t.Short(0, 0x90, 60, 100).Short(1920, 0x80, 60, 0).End(0)).Build());

    private static Playlist ListOf(params string[] paths)
        => new() { Items = [.. paths.Select(p => new PlaylistItem { Path = p })] };

    private static PlaybackOptions Quiet() => new() { SendAllNotesOffOnStop = false };

    /// <summary>Waits for a request to have been dealt with.</summary>
    internal static void Done(Task request)
        => Assert.True(request.Wait(TimeSpan.FromSeconds(5)), "the request was never dealt with");

    [Fact]
    public void TheSongBeingLeftHasStoppedBeforeTheNextIsSetUp()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        player.SetPlaylist(ListOf("a.mid", "b.mid"));

        var states = new List<PlaybackState>();
        var second = new ManualResetEventSlim();
        player.Loading += (item, _) =>
        {
            lock (states) states.Add(sequencer.State);
            if (item.Path == "b.mid") second.Set();
        };

        player.Play();
        Assert.True(SpinWait.SpinUntil(() => sequencer.State == PlaybackState.Playing, TimeSpan.FromSeconds(5)),
                    "the first song did not start");
        player.Next();
        Assert.True(second.Wait(TimeSpan.FromSeconds(5)));
        Done(player.Stop());

        // A handler of Loading sends the next song's setup; nothing of the old song may be
        // going out while it does.
        lock (states) Assert.Equal([PlaybackState.Stopped, PlaybackState.Stopped], states);
    }

    [Fact]
    public void WalksTheListOnceAndStops()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny());
        player.SetPlaylist(ListOf("a.mid", "b.mid", "c.mid"));
        player.Repeat = RepeatMode.None;

        List<string> played = Started(player);
        var done = new ManualResetEventSlim();
        player.Stopped += _ => done.Set();

        player.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the list never ran out");

        lock (played) Assert.Equal(["a.mid", "b.mid", "c.mid"], played);
    }

    [Fact]
    public void AListThatRunsOutGoesBackToTheTop()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny());
        player.SetPlaylist(ListOf("a.mid", "b.mid", "c.mid"));
        player.Repeat = RepeatMode.None;
        List<string?> cursor = [];
        player.CurrentChanged += item => { lock (cursor) cursor.Add(item?.Path); };
        var done = new ManualResetEventSlim();
        player.Stopped += _ => done.Set();

        player.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the list never ran out");
        Done(player.WhenIdle());

        // Play again starts a new walk, not the last song over.
        Assert.Equal("a.mid", player.Current?.Path);
        lock (cursor) Assert.Equal("a.mid", cursor[^1]);
        List<string> played = Started(player);
        Done(player.Play());
        Done(player.WhenIdle());
        lock (played) Assert.Equal("a.mid", played[0]);
        Done(player.Stop());
    }

    [Fact]
    public void AStopByHandAtTheLastSongStaysThere()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        player.SetPlaylist(ListOf("a.mid", "b.mid"));
        player.Repeat = RepeatMode.None;

        Done(player.Play(1));
        Done(player.Stop());

        Assert.Equal("b.mid", player.Current?.Path);
    }

    [Fact]
    public void AnUnreadableLastSongStillGoesBackToTheTop()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(
            sequencer,
            path => path == "c.mid" ? throw new InvalidDataException("not an SMF") : Tiny());
        player.SetPlaylist(ListOf("a.mid", "b.mid", "c.mid"));
        player.Repeat = RepeatMode.None;
        var done = new ManualResetEventSlim();
        player.Stopped += _ => done.Set();

        player.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Done(player.WhenIdle());

        Assert.Equal("a.mid", player.Current?.Path);
    }

    [Fact]
    public void SingleStaysOnItsSong()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny());
        player.SetPlaylist(ListOf("a.mid", "b.mid", "c.mid"));
        player.Repeat = RepeatMode.Single;
        var done = new ManualResetEventSlim();
        player.Stopped += _ => done.Set();

        player.Play(2);
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Done(player.WhenIdle());

        Assert.Equal("c.mid", player.Current?.Path);
    }

    [Fact]
    public void AListIsHandedOverWithTheCursorWhereItWasLeft()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny());
        Playlist list = ListOf("a.mid", "b.mid", "c.mid");
        string? said = null;
        player.CurrentChanged += item => said = item?.Path;

        player.SetPlaylist(list, list.Items[1]);
        Assert.Equal("b.mid", player.Current?.Path);
        Assert.Equal("b.mid", said);

        // A song the list does not have is no place to start.
        player.SetPlaylist(list, new PlaylistItem { Path = "b.mid" });
        Assert.Equal("a.mid", player.Current?.Path);
    }

    [Fact]
    public void TakingOutTheSongTheCursorIsOnWhileStoppedSaysWhereItWent()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny());
        Playlist list = ListOf("a.mid", "b.mid", "c.mid");
        player.SetPlaylist(list, list.Items[1]);
        string? said = null;
        player.CurrentChanged += item => said = item?.Path;

        list.Items.RemoveAt(1);
        player.Rearranged();

        Assert.Equal("c.mid", player.Current?.Path);
        Assert.Equal("c.mid", said);
    }

    [Fact]
    public void RepeatAllStartsTheListOver()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny());
        player.SetPlaylist(ListOf("a.mid", "b.mid"));
        player.Repeat = RepeatMode.All;

        var fourth = new ManualResetEventSlim();
        List<string> played = [];
        player.Started += item =>
        {
            lock (played)
            {
                played.Add(item.Path);
                if (played.Count >= 4) fourth.Set();
            }
        };

        player.Play();
        Assert.True(fourth.Wait(TimeSpan.FromSeconds(10)), "the list did not come round");
        player.Stop();

        lock (played) Assert.Equal(["a.mid", "b.mid", "a.mid", "b.mid"], played.Take(4));
    }

    [Fact]
    public void SingleRepeatStaysOnTheSameSong()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny());
        player.SetPlaylist(ListOf("a.mid", "b.mid"));
        player.Repeat = RepeatMode.SingleRepeat;

        var third = new ManualResetEventSlim();
        List<string> played = [];
        player.CurrentChanged += item =>
        {
            if (item is null) return;
            lock (played)
            {
                played.Add(item.Path);
                if (played.Count >= 3) third.Set();
            }
        };

        player.Play();
        Assert.True(third.Wait(TimeSpan.FromSeconds(10)), "the song did not repeat");
        player.Stop();

        lock (played) Assert.All(played.Take(3), p => Assert.Equal("a.mid", p));
    }

    [Fact]
    public void SingleStopsAfterOneSong()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny());
        player.SetPlaylist(ListOf("a.mid", "b.mid"));
        player.Repeat = RepeatMode.Single;

        int changes = 0;
        player.CurrentChanged += _ => Interlocked.Increment(ref changes);
        var done = new ManualResetEventSlim();
        player.Stopped += _ => done.Set();

        player.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));

        // Only the one song; the change for the playlist being set came before we subscribed.
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ResetsTheCarriedStateBetweenSongs()
    {
        var reset = new ResetCounter();
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny(), reset);
        player.SetPlaylist(ListOf("a.mid", "b.mid"));

        var done = new ManualResetEventSlim();
        player.Stopped += _ => done.Set();
        player.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));

        // Once as each song starts, and once as the list comes to rest.
        Assert.Equal(3, reset.Count);
    }

    [Fact]
    public void ComingToAStopPutsTheCarriedStateBackToo()
    {
        var reset = new ResetCounter();
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long(), reset);
        player.SetPlaylist(ListOf("a.mid"));

        Done(player.Play());
        int started = reset.Count;

        // What the display is holding belongs to the song that was playing, and that song
        // is over.
        Done(player.Stop());
        Assert.Equal(started + 1, reset.Count);

        // Already stopped, so there is nothing to put back.
        Done(player.Stop());
        Assert.Equal(started + 1, reset.Count);
    }

    [Fact]
    public void TitleOrderDecidesWhatComesNext()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny());
        var list = ListOf("3.mid", "1.mid", "2.mid");
        list.Items[0].Title = "C";
        list.Items[1].Title = "A";
        list.Items[2].Title = "B";
        // The order goes on first: loading a playlist puts the cursor on its first song, and
        // changing the order later keeps the cursor where it is.
        player.Order = PlayOrder.Title;
        player.SetPlaylist(list);

        Assert.Equal("1.mid", player.Current?.Path);
        Done(player.Next());
        Assert.Equal("2.mid", player.Current?.Path);
        Done(player.Next());
        Assert.Equal("3.mid", player.Current?.Path);
        // Stepping by hand wraps whatever the repeat mode says.
        Done(player.Next());
        Assert.Equal("1.mid", player.Current?.Path);
    }

    [Fact]
    public void FileNameOrderListsNamesTheWayAFileManagerDoes()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny());
        player.Order = PlayOrder.FileName;
        player.SetPlaylist(ListOf("b.mid", "10.mid", "_intro.mid", "2.mid"));

        // Marks before digits and letters, and numbers by their value.
        var order = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            order.Add(player.Current!.Path);
            Done(player.Next());
        }
        Assert.Equal(["_intro.mid", "2.mid", "10.mid", "b.mid"], order);
    }

    [Fact]
    public void ChangingOrderKeepsTheCursorOnTheSameSong()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny());
        player.SetPlaylist(ListOf("a.mid", "b.mid", "c.mid"));
        Done(player.Next());
        Assert.Equal("b.mid", player.Current?.Path);

        player.Order = PlayOrder.FileName;
        Assert.Equal("b.mid", player.Current?.Path);
    }

    [Fact]
    public void SkipsASongItCannotRead()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(
            sequencer,
            path => path == "bad.mid" ? throw new InvalidDataException("not an SMF") : Tiny());
        player.SetPlaylist(ListOf("bad.mid", "good.mid"));

        var failures = new List<string>();
        player.LoadFailed += (item, _) => { lock (failures) failures.Add(item.Path); };
        var done = new ManualResetEventSlim();
        player.Stopped += _ => done.Set();

        player.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));

        lock (failures) Assert.Equal(["bad.mid"], failures);
    }

    [Fact]
    public void AnUnreadableLastSongEndsAListThatDoesNotRepeat()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(
            sequencer,
            path => path == "c.mid" ? throw new InvalidDataException("not an SMF") : Tiny());
        player.SetPlaylist(ListOf("a.mid", "b.mid", "c.mid"));
        player.Repeat = RepeatMode.None;
        List<string> started = Started(player);
        List<PlaybackStop> stops = Stops(player);
        var done = new ManualResetEventSlim();
        player.Stopped += _ => done.Set();

        player.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Done(player.WhenIdle());

        lock (started) Assert.Equal(["a.mid", "b.mid"], started);
        lock (stops) Assert.Equal([StopCause.EndOfList], stops.Select(s => s.Cause));
    }

    [Fact]
    public void AnUnreadableSongPlayedAloneIsNotSwappedForAnother()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(
            sequencer,
            path => path == "bad.mid" ? throw new InvalidDataException("not an SMF") : Tiny());
        player.SetPlaylist(ListOf("bad.mid", "good.mid"));
        player.Repeat = RepeatMode.Single;
        List<string> started = Started(player);
        List<PlaybackStop> stops = Stops(player);

        Done(player.Play(0));
        Done(player.WhenIdle());

        lock (started) Assert.Empty(started);
        lock (stops) Assert.Equal([StopCause.NothingPlayable], stops.Select(s => s.Cause));
    }

    [Fact]
    public void AStepBackPastAnUnreadableSongKeepsGoingBack()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(
            sequencer,
            path => path == "b.mid" ? throw new InvalidDataException("not an SMF") : Long());
        player.SetPlaylist(ListOf("a.mid", "b.mid", "c.mid"));

        Done(player.Play(2));
        Done(player.Previous());

        Assert.Equal("a.mid", player.Current?.Path);
        Done(player.Stop());
    }

    [Fact]
    public void AddingToTheListBeingPlayedLeavesTheCursorWhereItIs()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        Playlist list = ListOf("a.mid", "b.mid");
        player.SetPlaylist(list);

        Done(player.Play(1));
        Assert.Equal("b.mid", player.Current?.Path);

        // What a drop does to the list being played: the song running is still in it.
        list.Items.Add(new PlaylistItem { Path = "c.mid" });
        player.Rearranged();

        Assert.Equal("b.mid", player.Current?.Path);
        Done(player.Stop());
    }

    [Fact]
    public void StartedIsForTheSongsThatPlayNotForTheCursorMoving()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        player.SetPlaylist(ListOf("a.mid", "b.mid", "c.mid"));

        List<string> started = [];
        player.Started += item => { lock (started) started.Add(item.Path); };

        Done(player.Next());   // stopped: the cursor moves on to "b", and nothing plays
        Done(player.Play());   // "b"
        Done(player.Next());   // "c"
        Done(player.Stop());

        lock (started) Assert.Equal(["b.mid", "c.mid"], started);
    }

    /// <summary>Takes the song at <paramref name="from"/> out and puts it back at <paramref name="to"/>.</summary>
    private static void Move(Playlist list, int from, int to)
    {
        PlaylistItem item = list.Items[from];
        list.Items.RemoveAt(from);
        list.Items.Insert(to, item);
    }

    [Fact]
    public void MovingASongAcrossTheOnePlayingLeavesTheCursorOnIt()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        Playlist list = ListOf("a.mid", "b.mid", "c.mid", "d.mid");
        player.SetPlaylist(list);
        Done(player.Play(2));

        // What a drag does: the list changes first and the player is told after. In between,
        // "c" no longer sits where it did, and the cursor is still on it.
        Move(list, 0, 2);   // b c a d
        Assert.Equal("c.mid", player.Current?.Path);

        player.Rearranged();
        Assert.Equal("c.mid", player.Current?.Path);

        Done(player.Next());
        Assert.Equal("a.mid", player.Current?.Path);
        Done(player.Stop());
    }

    [Fact]
    public void MovingTheSongPlayingTakesTheCursorWithIt()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        Playlist list = ListOf("a.mid", "b.mid", "c.mid", "d.mid");
        player.SetPlaylist(list);
        Done(player.Play(1));

        Move(list, 1, 3);   // a c d b
        player.Rearranged();

        Assert.Equal("b.mid", player.Current?.Path);
        Done(player.Previous());
        Assert.Equal("d.mid", player.Current?.Path);
        Done(player.Stop());
    }

    [Fact]
    public void TakingASongOutOfTheListBeingPlayedLeavesTheCursorWhereItIs()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        Playlist list = ListOf("a.mid", "b.mid", "c.mid", "d.mid");
        player.SetPlaylist(list);
        Done(player.Play(2));

        list.Items.RemoveAt(0);
        player.Rearranged();

        Assert.Equal("c.mid", player.Current?.Path);
        Done(player.Next());
        Assert.Equal("d.mid", player.Current?.Path);
        Done(player.Stop());
    }

    [Fact]
    public void TheSongPlayingTakenOutOfTheListPlaysOnAndIsFollowedByTheOneAfterIt()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        Playlist list = ListOf("a.mid", "b.mid", "c.mid", "d.mid");
        player.SetPlaylist(list);
        Done(player.Play(1));

        list.Items.RemoveAt(1);
        player.Rearranged();

        Assert.Equal("b.mid", player.Current?.Path);
        Assert.Equal(TransportState.Playing, player.State);

        Done(player.Next());
        Assert.Equal("c.mid", player.Current?.Path);
        Done(player.Stop());
    }

    [Fact]
    public void BackFromTheSongPlayingTakenOutOfTheListIsTheOneBeforeIt()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        Playlist list = ListOf("a.mid", "b.mid", "c.mid", "d.mid");
        player.SetPlaylist(list);
        Done(player.Play(2));

        list.Items.RemoveAt(2);
        player.Rearranged();

        Done(player.Previous());
        Assert.Equal("b.mid", player.Current?.Path);
        Done(player.Stop());
    }

    [Fact]
    public void AtRestTheCursorGoesOnFromASongTakenOut()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        Playlist list = ListOf("a.mid", "b.mid", "c.mid", "d.mid");
        player.SetPlaylist(list);
        Done(player.Play(2));
        Done(player.Stop());

        list.Items.RemoveAt(2);
        player.Rearranged();

        Assert.Equal("d.mid", player.Current?.Path);
    }

    // ------------------------------------------------------------------ one request at a time

    /// <summary>A Loading handler that holds a song up until let go, the way a setup does.</summary>
    private sealed class HeldSetup
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly string _path;

        public HeldSetup(PlaybackController player, string path)
        {
            _path = path;
            player.Loading += (item, _) =>
            {
                if (item.Path != _path) return;
                _entered.Set();
                _release.Wait(TimeSpan.FromSeconds(10));
            };
        }

        public void WaitUntilEntered()
            => Assert.True(_entered.Wait(TimeSpan.FromSeconds(5)), _path + " was never set up");

        public void Release() => _release.Set();
    }

    private static List<string> Started(PlaybackController player)
    {
        List<string> started = [];
        player.Started += item => { lock (started) started.Add(item.Path); };
        return started;
    }

    private static List<PlaybackStop> Stops(PlaybackController player)
    {
        List<PlaybackStop> stops = [];
        player.Stopped += stop => { lock (stops) stops.Add(stop); };
        return stops;
    }

    [Fact]
    public void AStopWhileTheNextSongIsBeingSetUpKeepsItFromStarting()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny());
        player.SetPlaylist(ListOf("a.mid", "b.mid"));
        var held = new HeldSetup(player, "b.mid");
        List<string> started = Started(player);
        List<PlaybackStop> stops = Stops(player);

        // a runs out by itself, and b is being set up when the stop comes in.
        player.Play();
        held.WaitUntilEntered();
        player.Stop();
        held.Release();
        Done(player.WhenIdle());

        Assert.Equal(TransportState.Stopped, player.State);
        Assert.Equal(PlaybackState.Stopped, sequencer.State);
        lock (started) Assert.Equal(["a.mid"], started);
        lock (stops) Assert.Equal([StopCause.Requested], stops.Select(s => s.Cause));
    }

    [Fact]
    public void ASetupThatAsksForAStopKeepsTheSongFromStarting()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        player.SetPlaylist(ListOf("a.mid"));
        // What the player does when an output will not open.
        player.Loading += (_, _) => player.Stop();
        List<string> started = Started(player);
        List<PlaybackStop> stops = Stops(player);

        Done(player.Play());
        Done(player.WhenIdle());

        Assert.Equal(TransportState.Stopped, player.State);
        Assert.Equal(PlaybackState.Stopped, sequencer.State);
        lock (started) Assert.Empty(started);
        lock (stops) Assert.Equal([StopCause.Requested], stops.Select(s => s.Cause));
    }

    [Fact]
    public void AnotherSongAskedForDuringASetupIsTheOneThatStarts()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        player.SetPlaylist(ListOf("a.mid", "b.mid"));
        var held = new HeldSetup(player, "a.mid");
        List<string> started = Started(player);

        player.Play();
        held.WaitUntilEntered();
        player.Next();
        held.Release();
        Done(player.WhenIdle());

        Assert.Equal(TransportState.Playing, player.State);
        lock (started) Assert.Equal(["b.mid"], started);
        Done(player.Stop());
    }

    [Fact]
    public void APauseDuringASetupIsNotLost()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        player.SetPlaylist(ListOf("a.mid"));
        var held = new HeldSetup(player, "a.mid");

        player.Play();
        held.WaitUntilEntered();
        player.TogglePause();
        held.Release();
        Done(player.WhenIdle());

        Assert.Equal(TransportState.Paused, player.State);
        Assert.Equal(PlaybackState.Paused, sequencer.State);
        Done(player.Stop());
    }

    [Fact]
    public void NeverSetsUpTwoSongsAtOnce()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny());
        player.SetPlaylist(ListOf("a.mid", "b.mid", "c.mid"));
        player.Repeat = RepeatMode.All;

        int inside = 0, most = 0, setups = 0;
        player.Loading += (_, _) =>
        {
            int now = Interlocked.Increment(ref inside);
            if (now > Volatile.Read(ref most)) Volatile.Write(ref most, now);
            Interlocked.Increment(ref setups);
            Thread.Sleep(2);
            Interlocked.Decrement(ref inside);
        };

        // Songs end by themselves as fast as they can while the button is pressed too.
        player.Play();
        for (int i = 0; i < 50; i++)
        {
            player.Next();
            Thread.Sleep(1);
        }
        Done(player.Stop());

        Assert.True(setups > 1, "nothing was set up");
        Assert.Equal(1, most);
        Assert.Equal(TransportState.Stopped, player.State);
        Assert.Equal(PlaybackState.Stopped, sequencer.State);
    }

    // ------------------------------------------------------------------ when a song cannot go on

    /// <summary>An output that takes a few messages and then goes away, the way an unplugged one does.</summary>
    private sealed class UnpluggedSink(int accepts) : IEventSink
    {
        private int _left = accepts;

        public void SendShort(int port, uint packedMessage) => Take();
        public void SendLong(int port, ReadOnlySpan<byte> sysEx) => Take();

        private void Take()
        {
            if (Interlocked.Decrement(ref _left) < 0)
                throw new Glosa.Midi.MidiDeviceException("midiOutShortMsg failed: gone");
        }
    }

    /// <summary>A note and, a tenth of a second later, its release.</summary>
    private static MidiSequence Short()
        => SmfReader.Read(new SmfBuilder(480)
            .Track(t => t.Short(0, 0x90, 60, 100).Short(96, 0x80, 60, 0).End(0)).Build());

    /// <summary>Nine loops open at once: one more than the sequencer holds.</summary>
    private static MidiSequence Runaway()
        => new(1, 1, 480, [.. Enumerable.Range(0, 9).Select(_ => MidiEvent.Loop(0, 0, 0, MidiEventKind.LoopStart))],
               [], 0, "", "", "", []);

    [Fact]
    public void AnOutputLostWhilePlayingBringsTheListToRestAndSaysWhy()
    {
        using var sequencer = new Sequencer(new UnpluggedSink(accepts: 1), Quiet());
        using var player = new PlaybackController(sequencer, _ => Short());
        player.SetPlaylist(ListOf("a.mid", "b.mid"));
        player.Repeat = RepeatMode.All;
        List<string> started = Started(player);
        List<PlaybackStop> stops = Stops(player);
        var done = new ManualResetEventSlim();
        player.Stopped += _ => done.Set();

        player.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "the loss was never noticed");
        Done(player.WhenIdle());

        // Not on to the next song, which would go to the same absent output.
        lock (started) Assert.Equal(["a.mid"], started);
        PlaybackStop stop;
        lock (stops) stop = Assert.Single(stops);
        Assert.Equal(StopCause.DeviceLost, stop.Cause);
        Assert.Contains("gone", stop.Problem);
        Assert.Equal(TransportState.Stopped, player.State);
        Assert.Equal(PlaybackState.Stopped, sequencer.State);
    }

    [Fact]
    public void AProtectionStopEndsTheListRatherThanMovingOn()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(
            sequencer, path => path == "a.mid" ? Runaway() : Tiny());
        player.SetPlaylist(ListOf("a.mid", "b.mid"));
        player.Repeat = RepeatMode.All;
        List<string> started = Started(player);
        List<PlaybackStop> stops = Stops(player);
        var done = new ManualResetEventSlim();
        player.Stopped += _ => done.Set();

        player.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "the protection stop was never passed on");
        Done(player.WhenIdle());

        lock (started) Assert.Equal(["a.mid"], started);
        lock (stops) Assert.Equal([new PlaybackStop(StopCause.Faulted, SequencerFault.LoopNestOverflow)], stops);
        Assert.Equal(TransportState.Stopped, player.State);
        Assert.Equal(PlaybackState.Stopped, sequencer.State);
    }

    [Fact]
    public void ASetupThatThrowsBringsTheTransportToRestAndSaysWhy()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        player.SetPlaylist(ListOf("a.mid"));
        bool fail = true;
        player.Loading += (_, _) =>
        {
            if (Volatile.Read(ref fail)) throw new InvalidOperationException("the module did not answer");
        };
        List<PlaybackStop> stops = Stops(player);

        Done(player.Play());

        Assert.Equal(TransportState.Stopped, player.State);
        Assert.Equal(PlaybackState.Stopped, sequencer.State);
        lock (stops)
            Assert.Equal([new PlaybackStop(StopCause.Failed, Problem: "the module did not answer")], stops);

        // The next request finds the transport in a state it knows.
        Volatile.Write(ref fail, false);
        Done(player.Play());
        Assert.Equal(TransportState.Playing, player.State);
        Done(player.Stop());
    }

    [Fact]
    public void AListenerThatThrowsOnceASongHasStartedStopsIt()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        player.SetPlaylist(ListOf("a.mid", "b.mid"));
        player.Started += _ => throw new InvalidOperationException("the display has gone");
        List<PlaybackStop> stops = Stops(player);

        Done(player.Play());

        Assert.Equal(TransportState.Stopped, player.State);
        Assert.Equal(PlaybackState.Stopped, sequencer.State);
        lock (stops) Assert.Equal([StopCause.Failed], stops.Select(s => s.Cause));
    }

    [Fact]
    public void AListenerThatThrowsOnStoppingDoesNotTakeTheTransportWithIt()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long());
        player.SetPlaylist(ListOf("a.mid"));
        player.Stopped += _ => throw new InvalidOperationException("the display has gone");

        Done(player.Play());
        Done(player.Stop());
        Assert.Equal(TransportState.Stopped, player.State);
        Assert.Equal(PlaybackState.Stopped, sequencer.State);

        // Still taking requests.
        Done(player.Play());
        Assert.Equal(TransportState.Playing, player.State);
        Done(player.Stop());
    }

    // ------------------------------------------------------------------ random order

    private static Playlist Numbered(int count)
        => ListOf([.. Enumerable.Range(0, count).Select(i => $"{i}.mid")]);

    /// <summary>The songs the cursor is on, stepping through the list once by hand.</summary>
    private static List<string> StepThrough(PlaybackController player, int count)
    {
        var order = new List<string>();
        for (int i = 0; i < count; i++)
        {
            order.Add(player.Current!.Path);
            Done(player.Next());
        }
        return order;
    }

    [Fact]
    public void ARandomWalkPlaysEverySongOnceAndStops()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny(), randomSeed: 1);
        player.Order = PlayOrder.Random;
        Playlist list = Numbered(8);
        player.SetPlaylist(list);
        List<string> started = Started(player);
        List<PlaybackStop> stops = Stops(player);
        var done = new ManualResetEventSlim();
        player.Stopped += _ => done.Set();

        player.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the list never ran out");
        Done(player.WhenIdle());

        lock (started)
        {
            Assert.Equal(list.Items.Select(i => i.Path).Order(), started.Order());
            Assert.NotEqual(list.Items.Select(i => i.Path), started);
        }
        lock (stops) Assert.Equal([StopCause.EndOfList], stops.Select(s => s.Cause));
    }

    [Fact]
    public void ARandomWalkThatRunsOutIsShuffledAfreshForTheNext()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny(), randomSeed: 1);
        player.Order = PlayOrder.Random;
        player.SetPlaylist(Numbered(8));
        List<string> started = Started(player);
        var done = new ManualResetEventSlim();
        player.Stopped += _ => done.Set();

        List<string>[] passes = new List<string>[3];
        for (int pass = 0; pass < passes.Length; pass++)
        {
            done.Reset();
            lock (started) started.Clear();
            player.Play();
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the list never ran out");
            Done(player.WhenIdle());
            lock (started) passes[pass] = [.. started];

            // Ready for the next pass, and not on the song that ended this one.
            Assert.NotEqual(passes[pass][^1], player.Current?.Path);
        }

        Assert.All(passes, pass => Assert.Equal(passes[0].Order(), pass.Order()));
        Assert.False(passes.Skip(1).All(pass => pass.SequenceEqual(passes[0])), "every pass came round the same");
    }

    [Fact]
    public void EachPassOfARepeatingRandomWalkIsShuffledAfresh()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny(), randomSeed: 1);
        player.Order = PlayOrder.Random;
        player.Repeat = RepeatMode.All;
        Playlist list = Numbered(6);
        player.SetPlaylist(list);

        var enough = new ManualResetEventSlim();
        List<string> started = [];
        player.Started += item =>
        {
            lock (started)
            {
                started.Add(item.Path);
                if (started.Count >= 18) enough.Set();
            }
        };

        player.Play();
        Assert.True(enough.Wait(TimeSpan.FromSeconds(10)), "the list did not come round");
        Done(player.Stop());

        List<string>[] passes;
        lock (started) passes = [.. started.Take(18).Chunk(6).Select(p => p.ToList())];
        Assert.All(passes, pass => Assert.Equal(list.Items.Select(i => i.Path).Order(), pass.Order()));
        Assert.False(passes.Skip(1).All(pass => pass.SequenceEqual(passes[0])), "every pass came round the same");
    }

    [Fact]
    public void AReshuffledPassDoesNotStartOnTheSongJustPlayed()
    {
        // Two songs: without the check, each new pass would start on the last one half the time.
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny(), randomSeed: 3);
        player.Order = PlayOrder.Random;
        player.Repeat = RepeatMode.All;
        player.SetPlaylist(Numbered(2));

        var enough = new ManualResetEventSlim();
        List<string> started = [];
        player.Started += item =>
        {
            lock (started)
            {
                started.Add(item.Path);
                if (started.Count >= 20) enough.Set();
            }
        };

        player.Play();
        Assert.True(enough.Wait(TimeSpan.FromSeconds(10)), "the list did not come round");
        Done(player.Stop());

        lock (started)
            Assert.All(started.Take(20).Zip(started.Skip(1).Take(19)), pair => Assert.NotEqual(pair.First, pair.Second));
    }

    [Fact]
    public void TheSameSeedWalksTheSameOrder()
    {
        using var first = new Sequencer(new NullSink(), Quiet());
        using var second = new Sequencer(new NullSink(), Quiet());
        using var one = new PlaybackController(first, _ => Tiny(), randomSeed: 7);
        using var other = new PlaybackController(second, _ => Tiny(), randomSeed: 7);
        one.Order = other.Order = PlayOrder.Random;
        one.SetPlaylist(Numbered(8));
        other.SetPlaylist(Numbered(8));

        Assert.Equal(StepThrough(one, 8), StepThrough(other, 8));
    }

    [Fact]
    public void TurningRandomOnKeepsTheSongPlayingAndLeavesTheRestToFollow()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long(), randomSeed: 1);
        Playlist list = Numbered(6);
        player.SetPlaylist(list);
        Done(player.Play(2));

        player.Order = PlayOrder.Random;

        Assert.Equal("2.mid", player.Current?.Path);
        Assert.Equal(TransportState.Playing, player.State);
        // It is first in the new walk, with every other song after it.
        List<string> walk = StepThrough(player, 6);
        Assert.Equal("2.mid", walk[0]);
        Assert.Equal(list.Items.Select(i => i.Path).Order(), walk.Order());
        // Stepping past the end deals another pass, which does not start on the last song.
        Assert.NotEqual(walk[^1], player.Current?.Path);
        Done(player.Stop());
    }

    [Fact]
    public void ChangingTheListKeepsTheRandomWalkAndPutsNewSongsAhead()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny(), randomSeed: 1);
        player.Order = PlayOrder.Random;
        Playlist list = Numbered(6);
        player.SetPlaylist(list);
        List<string> before = StepThrough(player, 3);
        string on = player.Current!.Path;

        list.Items.Add(new PlaylistItem { Path = "new1.mid" });
        list.Items.Add(new PlaylistItem { Path = "new2.mid" });
        list.Items.Reverse();
        player.Rearranged();

        Assert.Equal(on, player.Current?.Path);
        Done(player.Previous());
        Assert.Equal(before[^1], player.Current?.Path);
        Done(player.Next());

        // The rest of the pass is every song not yet visited, new ones included, once each.
        List<string> rest = StepThrough(player, 8 - before.Count);
        Assert.Equal(on, rest[0]);
        Assert.Equal(list.Items.Select(i => i.Path).Except(before).Order(), rest.Order());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ARandomListHandedOverStartsItsWalkOnTheSongAskedFor(int start)
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Tiny(), randomSeed: 1);
        player.Order = PlayOrder.Random;
        Playlist list = Numbered(6);

        player.SetPlaylist(list, list.Items[start]);
        List<string> started = Started(player);
        var done = new ManualResetEventSlim();
        player.Stopped += _ => done.Set();

        // One pass, not the tail of one: every song plays before the list runs out.
        player.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "the list never ran out");
        Done(player.WhenIdle());

        lock (started)
        {
            Assert.Equal($"{start}.mid", started[0]);
            Assert.Equal(list.Items.Select(i => i.Path).Order(), started.Order());
        }
    }

    [Fact]
    public void ASongPickedOutOfTurnIsHeardNextAndBackReturnsToTheOneBefore()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long(), randomSeed: 1);
        player.Order = PlayOrder.Random;
        Playlist list = Numbered(6);
        player.SetPlaylist(list);
        Done(player.Play());
        string first = player.Current!.Path;
        int picked = list.Items.FindIndex(i => i.Path != first);

        Done(player.Play(picked));
        Assert.Equal(list.Items[picked].Path, player.Current?.Path);

        Done(player.Previous());
        Assert.Equal(first, player.Current?.Path);
        Done(player.Stop());

        // The pass from the top is still every song once: the picked one has not been left
        // to come round again.
        Done(player.Previous());
        Assert.Equal(first, player.Current?.Path);
        Assert.Equal(list.Items.Select(i => i.Path).Order(), StepThrough(player, 6).Order());
    }

    [Fact]
    public void BackFromTheTopOfARandomPassStaysOnTheSong()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long(), randomSeed: 1);
        player.Order = PlayOrder.Random;
        player.SetPlaylist(Numbered(6));
        string first = player.Current!.Path;

        Done(player.Previous());
        Assert.Equal(first, player.Current?.Path);

        Done(player.Play());
        Done(player.Previous());
        Assert.Equal(first, player.Current?.Path);
        Assert.Equal(TransportState.Playing, player.State);
        Done(player.Stop());
    }

    [Fact]
    public void TakingOutTheFirstSongOfARandomPassWhileItPlaysDoesNotDealAgain()
    {
        using var sequencer = new Sequencer(new NullSink(), Quiet());
        using var player = new PlaybackController(sequencer, _ => Long(), randomSeed: 1);
        player.Order = PlayOrder.Random;
        Playlist list = Numbered(6);
        player.SetPlaylist(list);
        List<string> walk = StepThrough(player, 2);
        Done(player.Previous());
        Done(player.Previous());
        Assert.Equal(walk[0], player.Current?.Path);

        List<string> started = Started(player);
        var second = new ManualResetEventSlim();
        player.Started += item => { if (item.Path != walk[0]) second.Set(); };

        Done(player.Play());
        list.Items.RemoveAll(i => i.Path == walk[0]);
        player.Rearranged();

        // The song after it is now first in the walk, which is not the walk coming round.
        Assert.True(second.Wait(TimeSpan.FromSeconds(10)), "the song never ended");
        lock (started) Assert.Equal(walk[1], started[1]);
        Done(player.Stop());
    }
}

public class PlaylistScanTests
{
    private static SongSummary Song(int ticks)
        => SmfReader.ReadSummary(new SmfBuilder(480)
            .Track(t => t
                .Meta(0, Glosa.Core.Smf.MetaType.TrackName, Glosa.Core.Text.Cp932.Encoding.GetBytes("題名"))
                .Short(0, 0x90, 60, 100).Short(ticks, 0x80, 60, 0).End(0))
            .Build(), openingMessages: 0);

    [Fact]
    public void FillsLengthAndTitle()
    {
        var list = new Playlist { Items = [new PlaylistItem { Path = "a.mid" }] };

        int read = PlaylistScan.Fill(list.Items, _ => Song(960));

        Assert.Equal(1, read);
        Assert.Equal(1000, list.Items[0].DurationMs);     // 960 ticks at 480ppqn, 120 BPM
        Assert.Equal("題名", list.Items[0].Title);
    }

    [Fact]
    public void AsksWhatTheDataSaysWhileTheSongIsOpen()
    {
        var list = new Playlist { Items = [new PlaylistItem { Path = "a.mid" }] };
        var asked = new List<SongSummary>();

        PlaylistScan.Fill(list.Items, _ => Song(960),
                          moduleFromData: song => { asked.Add(song); return "GS"; });

        Assert.Single(asked);
        Assert.Equal("GS", list.Items[0].ModuleFromData);
    }

    [Fact]
    public void SkipsEntriesThatAlreadyKnowTheirLength()
    {
        var list = new Playlist
        {
            Items =
            [
                new PlaylistItem { Path = "known.mid", DurationMs = 5000 },
                new PlaylistItem { Path = "new.mid" },
            ],
        };

        var opened = new List<string>();
        int read = PlaylistScan.Fill(list.Items, path => { opened.Add(path); return Song(480); });

        Assert.Equal(1, read);
        Assert.Equal(["new.mid"], opened);
        Assert.Equal(5000, list.Items[0].DurationMs);
    }

    [Fact]
    public void AnUnreadableFileDoesNotStopTheScan()
    {
        var list = new Playlist
        {
            Items = [new PlaylistItem { Path = "bad.mid" }, new PlaylistItem { Path = "good.mid" }],
        };

        int read = PlaylistScan.Fill(
            list.Items,
            path => path == "bad.mid" ? throw new InvalidDataException("no") : Song(480));

        Assert.Equal(1, read);
        Assert.Equal(0, list.Items[0].DurationMs);
        Assert.Equal(500, list.Items[1].DurationMs);
    }

    [Fact]
    public void StopsWhenCancelled()
    {
        var list = new Playlist
        {
            Items = [.. Enumerable.Range(0, 10).Select(i => new PlaylistItem { Path = $"{i}.mid" })],
        };

        using var cancel = new CancellationTokenSource();
        int read = PlaylistScan.Fill(
            list.Items,
            _ => Song(480),
            scanned: _ => cancel.Cancel(),
            cancellation: cancel.Token);

        Assert.Equal(1, read);
    }
}

public class YamlFileTests
{
    public sealed class Sample
    {
        public string Name { get; set; } = "既定";
        public int Count { get; set; } = 7;
        public RepeatMode Repeat { get; set; }
    }

    private static string TempPath() => Path.Combine(
        Path.GetTempPath(), $"glosa-test-{Guid.NewGuid():N}", "settings.yaml");

    [Fact]
    public void RoundTripsThroughAFile()
    {
        string path = TempPath();
        try
        {
            YamlFile.Save(new Sample { Name = "変えた", Count = 3 }, path);
            Sample back = YamlFile.Load<Sample>(path);

            Assert.Equal("変えた", back.Name);
            Assert.Equal(3, back.Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void LeavesNoTemporaryFileBehind()
    {
        string path = TempPath();
        try
        {
            YamlFile.Save(new Sample(), path);
            YamlFile.Save(new Sample(), path);

            Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void AFileThatIsNotUtf8IsNotRead()
    {
        string path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // "あ" in CP932, which read as UTF-8 would be replaced, and written back so.
            File.WriteAllBytes(path, [.. "name: "u8, 0x82, 0xA0, (byte)'\n']);

            Assert.Throws<InvalidDataException>(() => YamlFile.Load<Sample>(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void AFailedSaveTakesItsTemporaryFileWithIt()
    {
        string path = TempPath();
        try
        {
            // A folder where the file would go, so the file cannot be moved into place.
            Directory.CreateDirectory(path);

            Exception thrown = Assert.ThrowsAny<Exception>(() => YamlFile.Save(new Sample(), path));
            Assert.True(thrown is IOException or UnauthorizedAccessException, thrown.ToString());
            Assert.Equal([path], Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void AReadOnlyFileIsNotWrittenOver()
    {
        string path = TempPath();
        try
        {
            YamlFile.Save(new Sample { Name = "kept" }, path);
            File.SetAttributes(path, FileAttributes.ReadOnly);

            Assert.Throws<UnauthorizedAccessException>(() => YamlFile.Save(new Sample { Name = "new" }, path));
            Assert.Equal("kept", YamlFile.Load<Sample>(path).Name);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ALinkIsFollowedAndStaysALink()
    {
        string path = TempPath();
        string folder = Path.GetDirectoryName(path)!;
        string target = Path.Combine(folder, "real.yaml");
        try
        {
            YamlFile.Save(new Sample(), target);
            try
            {
                File.CreateSymbolicLink(path, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;     // Windows without the right to make links.
            }

            YamlFile.Save(new Sample { Name = "through" }, path);

            Assert.NotNull(new FileInfo(path).LinkTarget);
            Assert.Equal("through", YamlFile.Load<Sample>(target).Name);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TextNestedTooDeepIsRefused()
    {
        string yaml = "unknown: " + new string('[', YamlFile.MaxDepth + 1) + new string(']', YamlFile.MaxDepth + 1);

        Assert.Throws<InvalidDataException>(() => YamlFile.Parse<Sample>(yaml));
    }

    [Fact]
    public void TextNestedAsDeepAsAllowedIsRead()
    {
        // The top-level mapping is one level.
        string yaml = "unknown: " + new string('[', YamlFile.MaxDepth - 1) + new string(']', YamlFile.MaxDepth - 1);

        Assert.Equal(7, YamlFile.Parse<Sample>(yaml).Count);
    }

    [Fact]
    public void WrittenWholeTheDefaultsAreWrittenToo()
    {
        Assert.DoesNotContain("count", YamlFile.ToYaml(new Sample()));
        Assert.Contains("count: 7", YamlFile.ToYaml(new Sample(), whole: true));
    }

    [Theory]
    [InlineData("repeat: All", RepeatMode.All)]
    [InlineData("repeat: singleRepeat", RepeatMode.SingleRepeat)]
    public void AnEnumIsReadByItsName(string yaml, RepeatMode repeat)
        => Assert.Equal(repeat, YamlFile.Parse<Sample>(yaml).Repeat);

    [Theory]
    [InlineData("repeat: 3")]
    [InlineData("repeat: 7")]
    [InlineData("repeat: Sometimes")]
    public void AnEnumIsReadByNothingButItsName(string yaml)
        => Assert.Throws<YamlDotNet.Core.YamlException>(() => YamlFile.Parse<Sample>(yaml));

    [Fact]
    public void KeysItDoesNotKnowAreIgnored()
    {
        Sample value = YamlFile.Parse<Sample>("name: X\nunknown: 1\n");

        Assert.Equal("X", value.Name);
        Assert.Equal(7, value.Count);
    }
}

public class SongFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"glosa-drop-{Guid.NewGuid():N}");

    public SongFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "album", "disc2"));
        foreach (string name in new[]
                 {
                     "b.mid", "a.MIDI", "readme.txt",
                     Path.Combine("album", "two.mid"),
                     Path.Combine("album", "one.mid"),
                     Path.Combine("album", "cover.png"),
                     Path.Combine("album", "disc2", "deep.mid"),
                 })
        {
            File.WriteAllText(Path.Combine(_root, name), "x");
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string[] Expand(params string[] paths)
        => [.. SongFiles.Expand(paths.Select(p => Path.Combine(_root, p)))
                        .Select(p => Path.GetRelativePath(_root, p))];

    [Fact]
    public void ASongFileHereCanBeReadUnasked()
        => Assert.True(SongFiles.IsLocalFile(Path.Combine(_root, "b.mid")));

    [Theory]
    [InlineData(@"\\server\share\a.mid")]
    [InlineData("//server/share/a.mid")]
    public void ASongOnAShareIsReadOnlyWhenPlayed(string path)
        => Assert.False(SongFiles.IsLocalFile(path));

    [Fact]
    public void WhatIsNoSongOrHoldsNothingIsNotReadUnasked()
    {
        File.WriteAllBytes(Path.Combine(_root, "empty.mid"), []);

        Assert.False(SongFiles.IsLocalFile(Path.Combine(_root, "readme.txt")));
        Assert.False(SongFiles.IsLocalFile(Path.Combine(_root, "empty.mid")));
        Assert.False(SongFiles.IsLocalFile(Path.Combine(_root, "missing.mid")));
    }

    [Fact]
    public void ASongInsideAnArchiveHereCanBeReadUnasked()
    {
        string archive = Path.Combine(_root, "album.zip");
        using (var zip = new System.IO.Compression.ZipArchive(File.Create(archive),
                   System.IO.Compression.ZipArchiveMode.Create))
            zip.CreateEntry("SONG.MID").Open().Dispose();

        Assert.True(SongFiles.IsLocalFile(Path.Combine(archive, "SONG.MID")));
    }

    [Fact]
    public void KeepsOnlyPlayableFiles()
        => Assert.Equal(["b.mid", "a.MIDI"], Expand("b.mid", "a.MIDI", "readme.txt"));

    [Fact]
    public void AFolderContributesEverythingInIt()
        => Assert.Equal(
            [Path.Combine("album", "disc2", "deep.mid"),
             Path.Combine("album", "one.mid"),
             Path.Combine("album", "two.mid")],
            Expand("album"));

    [Fact]
    public void AFolderItMayNotEnterIsPassedOver()
    {
        string locked = Path.Combine(_root, "album", "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "hidden.mid"), "x");
        Lock(locked, true);
        try
        {
            // The rest are found. Whether the locked one is depends on who runs this: root
            // is let in anyway.
            string[] found = Expand("album");
            Assert.Contains(Path.Combine("album", "disc2", "deep.mid"), found);
            Assert.Contains(Path.Combine("album", "one.mid"), found);
            Assert.Contains(Path.Combine("album", "two.mid"), found);
        }
        finally
        {
            Lock(locked, false);
        }
    }

    /// <summary>Keeps this process out of a folder, the way a drive's system folders do.</summary>
    private static void Lock(string folder, bool locked)
    {
        if (OperatingSystem.IsWindows())
        {
            var folderInfo = new DirectoryInfo(folder);
            System.Security.AccessControl.DirectorySecurity security = folderInfo.GetAccessControl();
            var deny = new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny);
            if (locked) security.AddAccessRule(deny);
            else security.RemoveAccessRule(deny);
            folderInfo.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(folder, locked
                ? UnixFileMode.None
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void TheOrderGivenIsKept()
        => Assert.Equal(["b.mid", Path.Combine("album", "disc2", "deep.mid"),
                         Path.Combine("album", "one.mid"), Path.Combine("album", "two.mid")],
                        Expand("b.mid", "album"));

    [Fact]
    public void SomethingThatIsNotThereIsSkipped()
        => Assert.Empty(Expand("missing.mid"));

    [Theory]
    [InlineData("song.mid", true)]
    [InlineData("song.MID", true)]
    [InlineData("song.midi", true)]
    [InlineData("song.rmi", true)]
    [InlineData("song.RCP", true)]
    [InlineData("song.g36", true)]
    [InlineData("song.eup", false)]
    [InlineData("song", false)]
    public void RecognisesWhatTheReaderCanOpen(string name, bool expected)
        => Assert.Equal(expected, SongFiles.IsSong(name));
}

public class PlaybackControllerSwitchTests
{
    private sealed class NullSink : IEventSink
    {
        public void SendShort(int port, uint packedMessage) { }
        public void SendLong(int port, ReadOnlySpan<byte> sysEx) { }
    }

    private static MidiSequence Long()
        => SmfReader.Read(new SmfBuilder(480)
            .Track(t => t.Short(0, 0x90, 60, 100).Short(3840, 0x80, 60, 0).End(0)).Build());

    /// <summary>Twenty seconds at 120 BPM: room to be well into it.</summary>
    private static MidiSequence VeryLong()
        => SmfReader.Read(new SmfBuilder(480)
            .Track(t => t.Short(0, 0x90, 60, 100).Short(19200, 0x80, 60, 0).End(0)).Build());

    private static (Sequencer, PlaybackController) PlayingSecondOfThree()
    {
        var sequencer = new Sequencer(new NullSink(), new PlaybackOptions { SendAllNotesOffOnStop = false });
        var player = new PlaybackController(sequencer, _ => VeryLong());
        player.SetPlaylist(new Playlist
        {
            Items = [new PlaylistItem { Path = "a.mid" }, new PlaylistItem { Path = "b.mid" },
                     new PlaylistItem { Path = "c.mid" }],
        });
        PlaybackControllerTests.Done(player.Play(1));
        return (sequencer, player);
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "it never got there");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void PreviousWellIntoASongGoesBackToItsStart()
    {
        (Sequencer sequencer, PlaybackController player) = PlayingSecondOfThree();
        using (sequencer)
        using (player)
        {
            PlaybackControllerTests.Done(player.Seek(TimeSpan.FromSeconds(10)));
            WaitUntil(() => sequencer.Position > TimeSpan.FromSeconds(9));

            PlaybackControllerTests.Done(player.Previous());
            WaitUntil(() => sequencer.Position < TimeSpan.FromSeconds(2));

            Assert.Equal("b.mid", player.Current?.Path);
            Assert.Equal(TransportState.Playing, player.State);
            PlaybackControllerTests.Done(player.Stop());
        }
    }

    [Fact]
    public void PreviousNearTheStartOfASongGoesToTheOneBefore()
    {
        (Sequencer sequencer, PlaybackController player) = PlayingSecondOfThree();
        using (sequencer)
        using (player)
        {
            PlaybackControllerTests.Done(player.Previous());

            Assert.Equal("a.mid", player.Current?.Path);
            Assert.Equal(TransportState.Playing, player.State);
            PlaybackControllerTests.Done(player.Stop());
        }
    }

    [Fact]
    public void PreviousWellIntoAPausedSongGoesBackToItsStartAndStaysPaused()
    {
        (Sequencer sequencer, PlaybackController player) = PlayingSecondOfThree();
        using (sequencer)
        using (player)
        {
            PlaybackControllerTests.Done(player.Seek(TimeSpan.FromSeconds(10)));
            WaitUntil(() => sequencer.Position > TimeSpan.FromSeconds(9));
            PlaybackControllerTests.Done(player.Pause());

            PlaybackControllerTests.Done(player.Previous());
            WaitUntil(() => sequencer.Position < TimeSpan.FromSeconds(1));

            Assert.Equal("b.mid", player.Current?.Path);
            Assert.Equal(TransportState.Paused, player.State);
            PlaybackControllerTests.Done(player.Stop());
        }
    }

    [Fact]
    public void StartingAnotherSongWhileOneIsPlayingSwitchesToIt()
    {
        using var sequencer = new Sequencer(new NullSink(),
                                            new PlaybackOptions { SendAllNotesOffOnStop = false });
        using var player = new PlaybackController(sequencer, _ => Long());
        player.SetPlaylist(new Playlist
        {
            Items = [new PlaylistItem { Path = "a.mid" }, new PlaylistItem { Path = "b.mid" }],
        });

        PlaybackControllerTests.Done(player.Play());
        Assert.Equal("a.mid", player.Current?.Path);

        PlaybackControllerTests.Done(player.Play(1));
        Assert.Equal("b.mid", player.Current?.Path);
        Assert.Equal(PlaybackState.Playing, sequencer.State);

        PlaybackControllerTests.Done(player.Stop());
    }
}

public class PlaybackControllerPauseTests
{
    private sealed class NullSink : IEventSink
    {
        public void SendShort(int port, uint packedMessage) { }
        public void SendLong(int port, ReadOnlySpan<byte> sysEx) { }
    }

    private static MidiSequence Long()
        => SmfReader.Read(new SmfBuilder(480)
            .Track(t => t.Short(0, 0x90, 60, 100).Short(3840, 0x80, 60, 0).End(0)).Build());

    private static PlaybackController Playing(Sequencer sequencer)
    {
        var player = new PlaybackController(sequencer, _ => Long());
        player.SetPlaylist(new Playlist { Items = [new PlaylistItem { Path = "a.mid" }] });
        PlaybackControllerTests.Done(player.Play());
        return player;
    }

    [Fact]
    public void TheOneButtonPausesAndThenLetsPlaybackCarryOn()
    {
        using var sequencer = new Sequencer(new NullSink(),
                                            new PlaybackOptions { SendAllNotesOffOnStop = false });
        using PlaybackController player = Playing(sequencer);
        Assert.Equal(PlaybackState.Playing, sequencer.State);

        PlaybackControllerTests.Done(player.TogglePause());
        Assert.Equal(PlaybackState.Paused, sequencer.State);

        PlaybackControllerTests.Done(player.TogglePause());
        Assert.Equal(PlaybackState.Playing, sequencer.State);

        PlaybackControllerTests.Done(player.Stop());
    }

    [Fact]
    public void PausingWhileStoppedStartsNothing()
    {
        using var sequencer = new Sequencer(new NullSink(),
                                            new PlaybackOptions { SendAllNotesOffOnStop = false });
        using var player = new PlaybackController(sequencer, _ => Long());
        player.SetPlaylist(new Playlist { Items = [new PlaylistItem { Path = "a.mid" }] });

        PlaybackControllerTests.Done(player.TogglePause());

        Assert.Equal(PlaybackState.Stopped, sequencer.State);
    }
}
