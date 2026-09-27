using System.Diagnostics;
using Glosa.Core.Playback;
using Glosa.Core.Smf;

namespace Glosa.Tests;

public class SequencerTests
{
    private sealed class RecordingSink : IEventSink
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public List<(uint Packed, int Port, double Ms)> Shorts { get; } = [];
        public List<(byte[] Data, int Port)> Longs { get; } = [];

        /// <summary>Everything in the order it came: "cc 78", "long F7", "wait 0" and so on.</summary>
        public List<string> Log { get; } = [];

        public void SendShort(int port, uint packedMessage)
        {
            lock (Shorts) Shorts.Add((packedMessage, port, _clock.Elapsed.TotalMilliseconds));
            lock (Log) Log.Add((packedMessage & 0xF0) == 0xB0
                ? $"cc {(packedMessage >> 8) & 0x7F:X2}"
                : $"msg {packedMessage & 0xFF:X2}");
        }

        public void SendLong(int port, ReadOnlySpan<byte> sysEx)
        {
            lock (Longs) Longs.Add((sysEx.ToArray(), port));
            lock (Log) Log.Add($"long {Convert.ToHexString(sysEx)}");
        }

        public void WaitUntilSent(int port)
        {
            lock (Log) Log.Add($"wait {port}");
        }

        public int IndexOf(string entry)
        {
            lock (Log) return Log.IndexOf(entry);
        }
    }

    /// <summary>An output that has gone away, the way an unplugged one has.</summary>
    private sealed class GoneSink : IEventSink
    {
        public void SendShort(int port, uint packedMessage)
            => throw new Glosa.Midi.MidiDeviceException("midiOutShortMsg failed: gone");

        public void SendLong(int port, ReadOnlySpan<byte> sysEx)
            => throw new Glosa.Midi.MidiDeviceException("midiOutLongMsg failed: gone");
    }

    private static MidiSequence Notes(params int[] deltaTicks)
    {
        var track = new TrackBuilder();
        foreach (int d in deltaTicks) track.Short(d, 0x90, 60, 100);
        track.End(0);
        return SmfReader.Read(new SmfBuilder(division: 480).Track(track).Build());
    }

    [Fact]
    public void AnOutputThatGoesAwayStopsPlaybackInsteadOfTheProcess()
    {
        // The playback thread has nobody above it to catch a throw from the output.
        using var seq = new Sequencer(new GoneSink(),
                                      new PlaybackOptions { SendAllNotesOffOnStop = true });
        seq.Load(Notes(0, 10, 10));

        string? said = null;
        var done = new ManualResetEventSlim();
        seq.DeviceFailed += message => { said = message; done.Set(); };

        seq.Play();

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Assert.Contains("gone", said);
        Assert.Equal(PlaybackState.Stopped, seq.State);

        // And stopping afterwards does not throw either, though the silencing it would
        // normally do goes to the same absent output.
        seq.Stop();
    }

    [Fact]
    public void PlaysEveryEventThenReportsFinished()
    {
        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(Notes(0, 48, 48));       // 0 / 50 / 100 ms at 120 BPM

        var done = new ManualResetEventSlim();
        seq.Finished += () => done.Set();
        seq.Play();

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "playback did not finish");
        Assert.Equal(3, sink.Shorts.Count);
        Assert.Equal(PlaybackState.Stopped, seq.State);
    }

    [Fact]
    public void KeepsEventsRoughlyOnSchedule()
    {
        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(Notes(0, 192, 192));     // 0 / 200 / 400 ms

        var done = new ManualResetEventSlim();
        seq.Finished += () => done.Set();
        seq.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));

        double first = sink.Shorts[0].Ms;
        double second = sink.Shorts[1].Ms - first;
        double third = sink.Shorts[2].Ms - first;

        // Generous bounds: CI machines are noisy, but a broken clock would be far off.
        Assert.InRange(second, 150, 320);
        Assert.InRange(third, 350, 560);
    }

    [Fact]
    public void SilencesEveryChannelWhenStopping()
    {
        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = true });
        seq.Load(Notes(0));
        seq.Play();
        seq.Stop();

        // 16 channels x 3 controllers, at least once.
        Assert.Contains(sink.Shorts, s => (s.Packed & 0xFF) == 0xB0
                                       && ((s.Packed >> 8) & 0xFF) == 0x78);
        Assert.Contains(sink.Shorts, s => (s.Packed & 0xFF) == 0xB0
                                       && ((s.Packed >> 8) & 0xFF) == 0x7B);
        Assert.Contains(sink.Shorts, s => (s.Packed & 0xFF) == 0xB0
                                       && ((s.Packed >> 8) & 0xFF) == 0x79);
    }

    [Fact]
    public void APauseSilencesButLeavesTheControllersAsTheSongSetThem()
    {
        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = true });
        seq.Load(Notes(0, 4800));
        seq.Play();
        seq.Pause();

        // Nothing sends them again on resume, so they are not reset.
        Assert.Contains(sink.Shorts, s => Cc(s.Packed) is { Number: 0x78 });
        Assert.Contains(sink.Shorts, s => Cc(s.Packed) is { Number: 0x7B });
        Assert.DoesNotContain(sink.Shorts, s => Cc(s.Packed) is { Number: 0x79 });
        seq.Stop();
    }

    /// <summary>
    /// An output that holds the first wait for what it was handed until let go, the way a
    /// driver that has stopped answering does.
    /// </summary>
    private sealed class StuckSink : IEventSink
    {
        private int _waits;
        public ManualResetEventSlim Waiting { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public List<int> Keys { get; } = [];

        public void SendShort(int port, uint packedMessage)
        {
            if ((packedMessage & 0xF0) == 0x90) lock (Keys) Keys.Add((int)(packedMessage >> 8) & 0x7F);
        }

        public void SendLong(int port, ReadOnlySpan<byte> sysEx) { }

        public void WaitUntilSent(int port)
        {
            if (Interlocked.Increment(ref _waits) != 1) return;
            Waiting.Set();
            Release.Wait();
        }
    }

    private static MidiSequence Keys(int key, int count)
    {
        var track = new TrackBuilder();
        for (int i = 0; i < count; i++) track.Short(i == 0 ? 0 : 48, 0x90, (byte)key, 100);
        track.End(0);
        return SmfReader.Read(new SmfBuilder(division: 480).Track(track).Build());
    }

    [Fact]
    public async Task ALoopStuckInTheOutputPlaysNothingMoreOnceStopped()
    {
        var sink = new StuckSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(Keys(60, 200));
        seq.Play();
        Assert.True(SpinWait.SpinUntil(() => { lock (sink.Keys) return sink.Keys.Count > 0; },
                                       TimeSpan.FromSeconds(5)), "the first song never started");

        // The seek's cut waits on the output, which does not come back before the stop gives up.
        seq.Seek(TimeSpan.FromSeconds(5));
        Assert.True(sink.Waiting.Wait(TimeSpan.FromSeconds(5)), "the seek never reached the output");
        seq.Stop();
        int played;
        lock (sink.Keys) played = sink.Keys.Count;

        var next = Task.Run(() => { seq.Load(Keys(72, 3)); seq.Play(); });
        // Held until the stuck loop is let go, so no machine is too slow for this.
        Assert.NotSame(next, await Task.WhenAny(next, Task.Delay(200)));
        var done = new ManualResetEventSlim();
        seq.Finished += () => done.Set();
        sink.Release.Set();
        Assert.Same(next, await Task.WhenAny(next, Task.Delay(TimeSpan.FromSeconds(5))));
        await next;
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "the next song never finished");

        lock (sink.Keys) Assert.Equal([72, 72, 72], sink.Keys.Skip(played));
    }

    [Fact]
    public void ASeekToTheStartPlaysWhatIsAtTheStart()
    {
        RecordingSink sink = PlayFrom(Notes(0, 480, 480), 0);

        Assert.Equal(3, sink.Shorts.Count(s => (s.Packed & 0xF0) == 0x90));
    }

    [Fact]
    public void ASeekOntoANotePlaysIt()
    {
        // Notes at 0, 500 and 1000 ms.
        RecordingSink sink = PlayFrom(Notes(0, 480, 480), 500);

        Assert.Equal(2, sink.Shorts.Count(s => (s.Packed & 0xF0) == 0x90));
    }

    [Fact]
    public void PassesSysExThroughUnchanged()
    {
        byte[] body = [0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7];
        byte[] smf = new SmfBuilder(480).Track(t => t.SysEx(0, body).End(0)).Build();

        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(SmfReader.Read(smf));

        var done = new ManualResetEventSlim();
        seq.Finished += () => done.Set();
        seq.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));

        byte[] sent = Assert.Single(sink.Longs).Data;
        Assert.Equal(0xF0, sent[0]);
        Assert.Equal(0xF7, sent[^1]);
        Assert.Equal(11, sent.Length);
    }

    /// <summary>Controller number and value of a control change, or null for anything else.</summary>
    private static (uint Number, uint Value)? Cc(uint packed)
        => (packed & 0xF0) == 0xB0 ? ((packed >> 8) & 0x7F, (packed >> 16) & 0x7F) : null;

    [Fact]
    public void SeekPlaysTheSongForwardWithoutItsNotes()
    {
        var track = new TrackBuilder();
        track.Short(0, 0xB0, 7, 100);     //    0 ms  volume
        track.Short(0, 0x90, 60, 100);    //    0 ms  note
        track.Short(480, 0xB0, 10, 64);   //  500 ms  pan
        track.Short(480, 0x90, 62, 100);  // 1000 ms  note
        track.Short(480, 0x90, 64, 100);  // 1500 ms  note
        track.End(0);

        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(SmfReader.Read(new SmfBuilder(division: 480).Track(track).Build()));

        bool rewound = false;
        seq.Rewinding += () => rewound = true;
        var done = new ManualResetEventSlim();
        seq.Finished += () => done.Set();

        seq.Seek(TimeSpan.FromMilliseconds(1200));
        seq.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "playback did not finish");

        // Whatever the song set up on the way is set up for real.
        Assert.Contains(sink.Shorts, s => Cc(s.Packed) == ((uint)7, (uint)100));
        Assert.Contains(sink.Shorts, s => Cc(s.Packed) == ((uint)10, (uint)64));

        // Notes are the one thing left out, so only the one past the target sounds.
        (uint Packed, int Port, double Ms) note =
            Assert.Single(sink.Shorts, s => (s.Packed & 0xF0) == 0x90);
        Assert.Equal(64u, (note.Packed >> 8) & 0x7F);

        // Whatever was sounding at the moment being left is cut, at once rather than on its
        // release, and the controllers the song set up are left as they are.
        Assert.Contains(sink.Shorts, s => Cc(s.Packed) is { Number: 0x78 });
        Assert.Contains(sink.Shorts, s => Cc(s.Packed) is { Number: 0x7B });
        Assert.DoesNotContain(sink.Shorts, s => Cc(s.Packed) is { Number: 0x79 });

        // Forwards there is nothing to undo, so nobody is asked to put anything back.
        Assert.False(rewound, "a forward seek asked for a reset");
    }

    /// <summary>Seeks to <paramref name="ms"/> from a standstill and plays the rest out.</summary>
    private static RecordingSink PlayFrom(MidiSequence song, int ms)
    {
        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(song);
        var done = new ManualResetEventSlim();
        seq.Finished += () => done.Set();

        seq.Seek(TimeSpan.FromMilliseconds(ms));
        seq.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "playback did not finish");
        return sink;
    }

    private static MidiSequence Song(params Action<TrackBuilder>[] tracks)
    {
        var smf = new SmfBuilder(division: 480);
        foreach (Action<TrackBuilder> track in tracks) smf.Track(t => { track(t); t.End(0); });
        return SmfReader.Read(smf.Build());
    }

    [Fact]
    public void ACatchUpSendsOnlyTheLastValueOfEachController()
    {
        RecordingSink sink = PlayFrom(Song(t => t
            .Short(0, 0xB0, 7, 10).Short(96, 0xB0, 7, 20).Short(96, 0xB0, 7, 30)      // volume
            .Short(0, 0xE0, 0, 32).Short(96, 0xE0, 0, 96)                             // bend
            .Short(0, 0xD0, 40).Short(96, 0xD0, 80)                                   // pressure
            .Short(0, 0xB1, 7, 90)                                                    // channel 2
            .Short(480, 0x90, 60, 100)), 700);

        Assert.Equal(["B0 07 1E", "E0 00 60", "D0 50", "B1 07 5A"],
                     sink.Shorts.Where(s => (s.Packed & 0xF0) != 0x90 && Cc(s.Packed) is not { Number: 0x78 or 0x7B })
                                .Select(s => Hex(s.Packed)));
    }

    [Fact]
    public void KeyPressureIsLeftOutOfACatchUp()
    {
        RecordingSink sink = PlayFrom(Song(t => t
            .Short(0, 0xA0, 60, 50).Short(480, 0xA0, 60, 60)), 200);

        Assert.Equal(["A0 3C 3C"], sink.Shorts.Where(s => (s.Packed & 0xF0) == 0xA0).Select(s => Hex(s.Packed)));
    }

    [Fact]
    public void WhatIsHeldGoesOutBeforeAMessageThatMustKeepItsPlace()
    {
        RecordingSink sink = PlayFrom(Song(t => t
            .Short(0, 0xB0, 7, 100)
            .Short(0, 0xB0, 0, 8).Short(0, 0xC0, 5)                   // bank select, program
            .Short(0, 0xB0, 11, 90)
            .SysEx(0, [0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7])
            .Short(0, 0xB0, 10, 64)
            .Short(0, 0xB0, 121, 0)                                   // reset all controllers
            .Short(480, 0x90, 60, 100)), 200);

        Assert.Equal(["cc 07", "cc 00", "msg C0", "cc 0B", "long F04110421240007F0041F7",
                      "cc 0A", "cc 79"],
                     sink.Log.Where(entry => entry.StartsWith("cc") || entry.StartsWith("msg C")
                                             || entry.StartsWith("long"))
                             .Where(entry => entry is not ("cc 78" or "cc 7B")));
    }

    [Fact]
    public void ParameterNumbersAndTheirDataGoOutAsTheyAre()
    {
        RecordingSink sink = PlayFrom(Song(t => t
            .Short(0, 0xB0, 101, 0).Short(0, 0xB0, 100, 0).Short(0, 0xB0, 6, 12)     // bend range
            .Short(0, 0xB0, 101, 0).Short(0, 0xB0, 100, 1).Short(0, 0xB0, 6, 70)     // fine tune
            .Short(480, 0x90, 60, 100)), 200);

        Assert.Equal(["B0 65 00", "B0 64 00", "B0 06 0C", "B0 65 00", "B0 64 01", "B0 06 46"],
                     sink.Shorts.Where(s => Cc(s.Packed) is { Number: not (0x78 or 0x7B) })
                                .Select(s => Hex(s.Packed)));
    }

    [Fact]
    public void HeldValuesGoOutInTheOrderTheyWereLastSet()
    {
        // Ports A and B may be routed to one machine, which keeps whichever value came last.
        RecordingSink sink = PlayFrom(Song(
            a => a.Meta(0, MetaType.MidiPort, [0]).Short(0, 0xB0, 7, 100).Short(192, 0xB0, 7, 80),
            b => b.Meta(0, MetaType.MidiPort, [1]).Short(96, 0xB0, 7, 50)
                  .Short(480, 0x90, 60, 100)), 400);

        Assert.Equal([(1, "B0 07 32"), (0, "B0 07 50")],
                     sink.Shorts.Where(s => Cc(s.Packed) is { Number: 7 })
                                .Select(s => (s.Port, Hex(s.Packed))));
    }

    [Fact]
    public void ASeekBackDropsWhatTheCatchUpHeld()
    {
        // Forty bytes a message against two hundred a second is a fifth of a second each, so
        // once the second SysEx is out the catch-up holds the pan and waits to send the third,
        // and the seek back comes then.
        byte[] body = [.. Enumerable.Repeat((byte)0x7F, 38), (byte)0xF7];
        RecordingSink sink = new();
        using var seq = new Sequencer(sink, new PlaybackOptions
        {
            SendAllNotesOffOnStop = false,
            TransferRateBytesPerSecond = 200,
        });
        seq.Load(Song(t => t
            .SysEx(0, body).SysEx(96, body)
            .Short(96, 0xB0, 10, 64).SysEx(0, body)                   // 200 ms
            .SysEx(96, body)
            .Short(480, 0x90, 60, 100)));
        var done = new ManualResetEventSlim();
        seq.Finished += () => done.Set();

        seq.Seek(TimeSpan.FromMilliseconds(950));
        seq.Play();
        Assert.True(SpinWait.SpinUntil(() => { lock (sink.Longs) return sink.Longs.Count >= 2; },
                                       TimeSpan.FromSeconds(5)), "the catch-up did not start");
        seq.Seek(TimeSpan.Zero);
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "playback did not finish");

        // The pan goes out once, when the replay reaches it, not also as a leftover.
        Assert.Single(sink.Log, entry => entry == "cc 0A");
    }

    /// <summary>How long the controllers numbered <paramref name="number"/> took to go out, first to last.</summary>
    private static double Span(RecordingSink sink, uint number, int? port = null)
    {
        double[] times = [.. sink.Shorts.Where(s => Cc(s.Packed) is { } cc && cc.Number == number
                                                    && (port is null || s.Port == port))
                                        .Select(s => s.Ms)];
        return times.Max() - times.Min();
    }

    [Fact]
    public void TheRateCapCoversChannelMessagesAndGivesEachPortItsOwnCable()
    {
        // Sixty controllers at the same instant: 180 bytes, 58 ms at 3125 a second on one
        // port. Split between A and B, taking turns, each port's cable carries half of them.
        // Every wait overshoots a little on a busy machine, so the two are compared with each
        // other rather than with the clock.
        double onOne = Span(PlayCapped(ports: 1), 7);
        double onTwo = Span(PlayCapped(ports: 2), 7);

        Assert.True(onOne >= 45, $"sixty controllers on one port took {onOne:F0} ms");
        Assert.True(onTwo < onOne * 0.75, $"two ports took {onTwo:F0} ms against {onOne:F0} on one");

        static RecordingSink PlayCapped(int ports)
        {
            var sink = new RecordingSink();
            using var seq = new Sequencer(sink, new PlaybackOptions
            {
                SendAllNotesOffOnStop = false,
                TransferRateBytesPerSecond = 3125,
            });
            seq.Load(Song(t =>
            {
                for (int i = 0; i < 60; i++)
                    t.Meta(0, MetaType.MidiPort, [(byte)(i % ports)]).Short(0, 0xB0, 7, (byte)i);
            }));
            var done = new ManualResetEventSlim();
            seq.Finished += () => done.Set();

            seq.Play();
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "playback did not finish");
            return sink;
        }
    }

    [Fact]
    public void ASeekGoesAtTheCableRateWhateverTheSetting()
    {
        // 128 values to hold, each a controller of its own: 384 bytes, 123 ms on a cable.
        RecordingSink sink = PlayFrom(Song(t =>
        {
            for (int ch = 0; ch < 16; ch++)
                for (byte number = 70; number < 78; number++)
                    t.Short(0, (byte)(0xB0 | ch), number, 64);
            t.Short(480, 0x90, 60, 100);
        }), 200);

        double[] held = [.. sink.Shorts.Where(s => Cc(s.Packed) is { Number: >= 70 and < 78 })
                                       .Select(s => s.Ms)];
        Assert.Equal(128, held.Length);
        Assert.True(held.Max() - held.Min() >= 100, $"the catch-up took {held.Max() - held.Min():F0} ms");
    }

    [Fact]
    public void WithNoCapPlaybackGoesAsFastAsTheDeviceTakesIt()
    {
        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(Song(t => { for (int i = 0; i < 128; i++) t.Short(0, 0xB0, 7, (byte)i); }));
        var done = new ManualResetEventSlim();
        seq.Finished += () => done.Set();

        seq.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "playback did not finish");

        Assert.InRange(Span(sink, 7), 0, 40);
    }

    [Fact]
    public void TheSilenceOnStoppingKeepsToTheCap()
    {
        // 48 messages of 3 bytes: 46 ms at 3125 a second.
        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions
        {
            SendAllNotesOffOnStop = true,
            TransferRateBytesPerSecond = 3125,
        });
        seq.Load(Notes(0, 480));
        seq.Play();
        Thread.Sleep(50);
        seq.Stop();

        Assert.InRange(Span(sink, 0x79), 35, 200);
    }

    private static string Hex(uint packed)
        => (packed & 0xF0) is 0xC0 or 0xD0
            ? $"{packed & 0xFF:X2} {(packed >> 8) & 0x7F:X2}"
            : $"{packed & 0xFF:X2} {(packed >> 8) & 0x7F:X2} {(packed >> 16) & 0x7F:X2}";

    /// <summary>
    /// A SysEx split in two, its halves 300 ms apart, and a note at 1000 ms. Or, with
    /// <paramref name="control"/>, a controller at 200 ms in place of the second half.
    /// </summary>
    private static MidiSequence SplitSysEx(bool control = false)
    {
        var track = new TrackBuilder();
        track.SysEx(0, [0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F]);   // no F7 yet
        if (control) track.Short(192, 0xB0, 7, 100).Short(768, 0x90, 60, 100);
        else track.Escape(288, [0x00, 0x41, 0xF7]).Short(672, 0x90, 60, 100);
        track.End(0);
        return SmfReader.Read(new SmfBuilder(division: 480).Track(track).Build());
    }

    /// <summary>
    /// Waits for the first half of <see cref="SplitSysEx"/> to go out, which leaves playback
    /// between the halves.
    /// </summary>
    private static void AwaitFirstHalf(RecordingSink sink)
        => Assert.True(SpinWait.SpinUntil(() => sink.IndexOf("long F04110421240007F") >= 0,
                                          TimeSpan.FromSeconds(5)), "the first half was not sent");

    [Fact]
    public void ASeekBetweenTheHalvesOfASplitSysExWaitsForTheSecond()
    {
        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(SplitSysEx());
        var done = new ManualResetEventSlim();
        seq.Finished += () => done.Set();

        seq.Play();
        AwaitFirstHalf(sink);
        seq.Seek(TimeSpan.FromMilliseconds(800));
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "playback did not finish");

        // The cut goes out once the SysEx is whole, and only after what the port was already
        // handed has gone.
        int second = sink.IndexOf("long 0041F7");
        Assert.True(second >= 0, "the second half was not sent");
        Assert.True(sink.IndexOf("wait 0") > second, "the seek did not wait for the SysEx");
        Assert.True(sink.IndexOf("cc 78") > sink.IndexOf("wait 0"), "the cut did not wait");
    }

    [Fact]
    public void AMessageOnThePortCountsAsClosingASplitSysEx()
    {
        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(SplitSysEx(control: true));
        var done = new ManualResetEventSlim();
        seq.Finished += () => done.Set();

        seq.Play();
        AwaitFirstHalf(sink);
        seq.Seek(TimeSpan.FromMilliseconds(800));
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "playback did not finish");

        // The SysEx never ends, and the seek waits only until the controller has ended it.
        Assert.True(sink.IndexOf("cc 78") > sink.IndexOf("cc 07"), "the seek did not wait");
    }

    [Fact]
    public void ASeekWhilePausedCutsASplitSysExShort()
    {
        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(SplitSysEx());

        seq.Play();
        AwaitFirstHalf(sink);
        seq.Pause();
        seq.Seek(TimeSpan.FromMilliseconds(800));
        Assert.True(SpinWait.SpinUntil(() => sink.IndexOf("cc 78") >= 0, TimeSpan.FromSeconds(5)),
                    "a paused seek waited for a SysEx nothing would close");
        seq.Stop();

        // Closed before the cut, and the second half, reached by the catch-up with nothing to
        // belong to, goes without its data bytes.
        Assert.InRange(sink.IndexOf("long F7"), 0, sink.IndexOf("cc 78"));
        Assert.Equal(-1, sink.IndexOf("long 0041F7"));
        lock (sink.Longs) Assert.All(sink.Longs, l => Assert.True(l.Data[0] >= 0x80));
    }

    [Fact]
    public void StoppingInsideASplitSysExClosesItBeforeTheSilence()
    {
        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = true });
        seq.Load(SplitSysEx());

        seq.Play();
        AwaitFirstHalf(sink);
        seq.Stop();

        Assert.InRange(sink.IndexOf("long F7"), 0, sink.IndexOf("cc 78"));
    }

    [Fact]
    public void StoppingInsideASplitSysExClosesItWithTheSilenceOff()
    {
        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(SplitSysEx());

        seq.Play();
        AwaitFirstHalf(sink);
        seq.Stop();

        Assert.True(sink.IndexOf("long F7") >= 0, "the SysEx was left open");
        Assert.Equal(-1, sink.IndexOf("cc 78"));
    }

    [Theory]
    [InlineData(false, "F0 41 10", true)]
    [InlineData(false, "F0 41 F7", false)]
    [InlineData(true, "00 41", true)]
    [InlineData(true, "00 F7", false)]
    [InlineData(true, "F8 00", true)]      // real time sits inside a SysEx
    [InlineData(true, "B0 07 64", false)]  // any other status ends it
    [InlineData(false, "00 41", false)]
    public void TracksWhetherASysExIsLeftOpen(bool before, string bytes, bool after)
        => Assert.Equal(after, Sequencer.LeavesSysExOpen(before, Convert.FromHexString(bytes.Replace(" ", ""))));

    [Fact]
    public void ASeekAskedForDuringOneIsTakenAsWell()
    {
        // Forty bytes a message against four hundred a second is a tenth of a second each,
        // so the catch-up is still paying for the cable when the second seek arrives.
        byte[] body = [.. Enumerable.Repeat((byte)0x7F, 38), (byte)0xF7];
        var track = new TrackBuilder();
        for (int i = 0; i < 10; i++) track.SysEx(i == 0 ? 0 : 96, body);   // every 100 ms
        track.Short(480, 0x90, 60, 100);                                   // 1400 ms
        track.End(0);

        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions
        {
            SendAllNotesOffOnStop = false,
            TransferRateBytesPerSecond = 400,
        });
        seq.Load(SmfReader.Read(new SmfBuilder(division: 480).Track(track).Build()));

        int sentWhenRewound = -1;
        var rewound = new ManualResetEventSlim();
        seq.Rewinding += () =>
        {
            lock (sink.Longs) sentWhenRewound = sink.Longs.Count;
            rewound.Set();
        };
        var done = new ManualResetEventSlim();
        seq.Finished += () => done.Set();

        seq.Seek(TimeSpan.FromMilliseconds(950));    // catch up over every SysEx
        seq.Play();
        // Once the first is out, the rate cap holds the catch-up back from the second.
        Assert.True(SpinWait.SpinUntil(() => { lock (sink.Longs) return sink.Longs.Count >= 1; },
                                       TimeSpan.FromSeconds(5)), "the catch-up did not start");

        seq.Seek(TimeSpan.FromMilliseconds(250));    // change our mind, mid catch-up
        Assert.True(rewound.Wait(TimeSpan.FromSeconds(5)), "the second seek was never taken");

        // Taken at the rate cap's next pause rather than after the first one had finished.
        Assert.InRange(sentWhenRewound, 1, 9);

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "playback did not finish");
        Assert.Equal(SequencerFault.None, seq.Fault);
        Assert.Single(sink.Shorts, s => (s.Packed & 0xF0) == 0x90);
    }

    [Fact]
    public void SeekingBackAsksForTheStateToBePutBack()
    {
        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(Notes(0, 480, 480, 480, 480));   // 0 .. 2000 ms

        var rewound = new ManualResetEventSlim();
        seq.Rewinding += () => rewound.Set();

        seq.Play();
        Assert.True(SpinWait.SpinUntil(() => seq.Position > TimeSpan.Zero, TimeSpan.FromSeconds(5)),
                    "playback did not start");
        seq.Pause();
        seq.Seek(TimeSpan.Zero);

        Assert.True(rewound.Wait(TimeSpan.FromSeconds(5)), "no reset was asked for");

        // Rewinding is raised before the seek has taken the position anywhere: the loop goes
        // on to set it once the handlers have returned, on its own thread. Read at once, the
        // position may still be where the pause left it.
        Assert.True(SpinWait.SpinUntil(() => seq.Position == TimeSpan.Zero, TimeSpan.FromSeconds(5)),
                    $"the position stayed at {seq.Position}");
        Assert.Equal(PlaybackState.Paused, seq.State);
    }

    [Fact]
    public void PauseStopsDispatchingUntilResumed()
    {
        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(Notes(0, 480, 480));

        seq.Play();
        seq.Pause();
        int afterPause = sink.Shorts.Count;
        Thread.Sleep(200);

        Assert.Equal(afterPause, sink.Shorts.Count);
        Assert.Equal(PlaybackState.Paused, seq.State);

        seq.Resume();
        Assert.Equal(PlaybackState.Playing, seq.State);
        seq.Stop();
    }

    /// <summary>An output that holds on to its first message until told to let go.</summary>
    private sealed class HoldingSink : IEventSink
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void SendShort(int port, uint packedMessage)
        {
            if (Interlocked.Increment(ref _count) == 1)
            {
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(5));
            }
        }

        public void SendLong(int port, ReadOnlySpan<byte> sysEx) { }
    }

    [Fact]
    public async Task PauseHoldsBackTheRestOfAnInstantAlreadyGoingOut()
    {
        // Anything let through after Pause returns goes out past its All Notes Off and
        // sounds for the whole pause.
        var sink = new HoldingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(Notes(0, 0, 0));

        seq.Play();
        Assert.True(sink.Entered.Wait(TimeSpan.FromSeconds(5)), "nothing was sent");

        var pausing = Task.Run(() => { seq.Pause(); return sink.Count; });
        // Pause waits for the message on its way out, so this times out; it only lets Pause
        // go first if Pause did not wait.
        await Task.WhenAny(pausing, Task.Delay(100));
        sink.Release.Set();
        int afterPause = await pausing;

        // Stop joins the playback thread, so anything it was going to send has been sent.
        seq.Stop();
        Assert.Equal(afterPause, sink.Count);
    }

    /// <summary>Notes when each SysEx arrives, on a port that says it goes down a cable.</summary>
    private sealed class CableSink : IEventSink
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public List<double> LongsAt { get; } = [];

        public void SendShort(int port, uint packedMessage) { }

        public void SendLong(int port, ReadOnlySpan<byte> sysEx)
        {
            lock (LongsAt) LongsAt.Add(_clock.Elapsed.TotalMilliseconds);
        }

        public bool IsCable(int port) => port == 0;
    }

    [Fact]
    public void APortThatGoesDownACableIsHeldToItWithNoCapSet()
    {
        // Two SysEx of 313 bytes at once: a tenth of a second each down a cable.
        byte[] body = [.. Enumerable.Repeat((byte)0x7F, 311), 0xF7];
        var sink = new CableSink();
        using var seq = new Sequencer(sink, new PlaybackOptions
        {
            SendAllNotesOffOnStop = false,
            TransferRateBytesPerSecond = 0,
        });
        seq.Load(SmfReader.Read(new SmfBuilder(division: 480)
            .Track(t => t.SysEx(0, body).SysEx(0, body).End(0)).Build()));
        var done = new ManualResetEventSlim();
        seq.Finished += () => done.Set();

        seq.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "playback did not finish");

        lock (sink.LongsAt)
        {
            Assert.Equal(2, sink.LongsAt.Count);
            Assert.True(sink.LongsAt[1] - sink.LongsAt[0] >= 90,
                        $"the second went {sink.LongsAt[1] - sink.LongsAt[0]:F0} ms after the first");
        }
    }

    /// <summary>An output that holds on to the first All Sound Off until told to let go.</summary>
    private sealed class SilenceHoldingSink : IEventSink
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();

        public void SendShort(int port, uint packedMessage)
        {
            if ((packedMessage & 0xFFF0) != 0x78B0 || Entered.IsSet) return;
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(5));
        }

        public void SendLong(int port, ReadOnlySpan<byte> sysEx) { }
    }

    [Fact]
    public void ASongThatEndsIsSilencedBeforeItComesToRest()
    {
        // Until it has, a stop or the next song waits for it, rather than having the silence
        // land on the next song's first messages.
        var sink = new SilenceHoldingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = true });
        seq.Load(Notes(0));
        var finished = new ManualResetEventSlim();
        seq.Finished += () => finished.Set();

        seq.Play();
        Assert.True(sink.Entered.Wait(TimeSpan.FromSeconds(5)), "nothing was silenced");
        Assert.Equal(PlaybackState.Playing, seq.State);
        Assert.False(finished.IsSet);

        sink.Release.Set();
        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)), "the song did not finish");
        Assert.Equal(PlaybackState.Stopped, seq.State);
    }

    [Fact]
    public async Task AStopDuringTheSilenceAtTheEndWaitsForIt()
    {
        var sink = new SilenceHoldingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = true });
        seq.Load(Notes(0));
        int finished = 0;
        seq.Finished += () => Interlocked.Increment(ref finished);

        seq.Play();
        Assert.True(sink.Entered.Wait(TimeSpan.FromSeconds(5)), "nothing was silenced");
        var stopping = Task.Run(seq.Stop);
        Assert.NotSame(stopping, await Task.WhenAny(stopping, Task.Delay(100)));

        sink.Release.Set();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        // The stop took over: the end of the song is not also told.
        Assert.Equal(0, Volatile.Read(ref finished));
    }

    /// <summary>Builds a sequence with loop boundaries directly.</summary>
    private sealed class LoopBuilder
    {
        private readonly List<MidiEvent> _events = [];

        public LoopBuilder Note(long ms)
            => Add(new MidiEvent(0, 0, 0, 0x90u | 60u << 8 | 100u << 16), ms);

        public LoopBuilder Start(long ms)
            => Add(MidiEvent.Loop(0, 0, 0, MidiEventKind.LoopStart), ms);

        public LoopBuilder End(long ms, int repeatCount)
            => Add(MidiEvent.Loop(0, 0, 0, MidiEventKind.LoopEnd, repeatCount), ms);

        public MidiSequence Build()
            => new(1, 1, 480, [.. _events], [],
                   _events.Count > 0 ? _events[^1].TimeUs : 0, "", "", "", []);

        private LoopBuilder Add(MidiEvent e, long ms)
        {
            _events.Add(e.WithTime(ms * 1000));
            return this;
        }
    }

    private static int PlayCountingNotes(MidiSequence sequence, PlaybackOptions options,
                                         RecordingSink sink)
    {
        using var seq = new Sequencer(sink, options);
        seq.Load(sequence);

        var done = new ManualResetEventSlim();
        seq.Finished += () => done.Set();
        seq.Faulted += _ => done.Set();
        seq.Play();
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "playback did not finish");

        return sink.Shorts.Count(s => (s.Packed & 0xFF) == 0x90);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(1, 1)]
    // Zero means the loop is not repeated at all, so its body still plays through once.
    [InlineData(0, 1)]
    public void EndlessLoopPlaysAsManyTimesAsTheSettingSays(int setting, int expected)
    {
        MidiSequence sequence = new LoopBuilder()
            .Start(0).Note(0).End(50, 0)
            .Note(60)
            .Build();

        var sink = new RecordingSink();
        var options = new PlaybackOptions
        {
            SendAllNotesOffOnStop = false,
            InfiniteLoopRepeatCount = setting,
        };

        // The note after the loop plays once whatever happens inside it.
        Assert.Equal(expected + 1, PlayCountingNotes(sequence, options, sink));
    }

    [Fact]
    public void WrittenRepeatCountIsUsedInsteadOfTheSetting()
    {
        MidiSequence sequence = new LoopBuilder()
            .Start(0).Note(0).End(20, 2)
            .Build();

        var sink = new RecordingSink();
        var options = new PlaybackOptions
        {
            SendAllNotesOffOnStop = false,
            InfiniteLoopRepeatCount = 7,
        };

        Assert.Equal(2, PlayCountingNotes(sequence, options, sink));
    }

    [Fact]
    public void EventsAfterALoopWaitForEveryPassThroughIt()
    {
        MidiSequence sequence = new LoopBuilder()
            .Start(0).Note(0).End(100, 0)
            .Note(100)
            .Build();

        var sink = new RecordingSink();
        var options = new PlaybackOptions
        {
            SendAllNotesOffOnStop = false,
            InfiniteLoopRepeatCount = 3,
        };

        Assert.Equal(4, PlayCountingNotes(sequence, options, sink));

        // Three 100 ms passes, so the trailing note lands near 300 ms rather than at 100 ms.
        double first = sink.Shorts[0].Ms;
        Assert.InRange(sink.Shorts[^1].Ms - first, 220, 450);
    }

    [Fact]
    public void LoopThatNeverAdvancesTheClockTripsTheProtectionStop()
    {
        MidiSequence sequence = new LoopBuilder()
            .Start(0).End(0, 0)
            .Build();

        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions
        {
            SendAllNotesOffOnStop = false,
            InfiniteLoopRepeatCount = 1_000_000,
        });
        seq.Load(sequence);

        var done = new ManualResetEventSlim();
        seq.Faulted += _ => done.Set();
        seq.Play();

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "the guard never fired");
        Assert.Equal(SequencerFault.LoopLimitExceeded, seq.Fault);
        Assert.Equal(PlaybackState.Stopped, seq.State);
    }

    [Fact]
    public void NestingMoreLoopsThanTheStackHoldsStops()
    {
        var builder = new LoopBuilder();
        for (int i = 0; i < 9; i++) builder.Start(0);

        var sink = new RecordingSink();
        using var seq = new Sequencer(sink, new PlaybackOptions { SendAllNotesOffOnStop = false });
        seq.Load(builder.Build());

        var done = new ManualResetEventSlim();
        seq.Faulted += _ => done.Set();
        seq.Play();

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "the nest guard never fired");
        Assert.Equal(SequencerFault.LoopNestOverflow, seq.Fault);
    }
}
