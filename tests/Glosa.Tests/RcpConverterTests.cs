using Glosa.Core.Rcp;
using Glosa.Core.Smf;

namespace Glosa.Tests;

public class RcpConverterTests
{
    /// <summary>(tick, status, data1, data2) of every channel message, in order.</summary>
    private static (long Tick, int Status, int Data1, int Data2)[] Channel(MidiSequence s)
        => [.. s.Events.Where(e => e.Kind == MidiEventKind.Channel)
                       .Select(e => (e.Ticks, (int)(e.Packed & 0xFF),
                                     (int)((e.Packed >> 8) & 0xFF), (int)((e.Packed >> 16) & 0xFF)))];

    private static long[] NoteOns(MidiSequence s)
        => [.. Channel(s).Where(e => (e.Status & 0xF0) == 0x90 && e.Data2 > 0).Select(e => e.Tick)];

    private static MidiSequence Read(RcpBuilder rcp, int endless = 2)
        => SmfReader.Read(rcp.Build(), endless);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANoteSoundsForItsGateAndTheNextOneWaitsItsStep(bool g36)
    {
        MidiSequence s = Read(new RcpBuilder(g36).Track(0, t => t
            .Note(60, st: 48, gt: 24, vel: 100)
            .Note(62, st: 48, gt: 48, vel: 90)
            .End()));

        Assert.Equal(
            [(0L, 0x90, 60, 100), (24L, 0x90, 60, 0), (48L, 0x90, 62, 90), (96L, 0x90, 62, 0)],
            Channel(s));
    }

    [Fact]
    public void TheTitleAndTheMemoAreKept()
    {
        MidiSequence s = Read(new RcpBuilder
        {
            Title = "レコンポーザの曲",
            Memo = ["GS 音源用", "", "by 誰か"],
        }.Track(0, t => t.Note(60, 48, 24).End()));

        Assert.Equal("レコンポーザの曲", s.Title);
        Assert.Equal(["GS 音源用", "by 誰か"], s.Texts);
    }

    [Fact]
    public void ANoteHeldIntoTheSameNoteIsTiedNotStruckAgain()
    {
        MidiSequence s = Read(new RcpBuilder().Track(0, t => t
            .Note(60, st: 48, gt: 60)
            .Note(60, st: 48, gt: 24)
            .End()));

        Assert.Equal([(0L, 0x90, 60, 100), (72L, 0x90, 60, 0)], Channel(s));
    }

    [Fact]
    public void ACountedLoopPlaysItsBodyThatManyTimes()
    {
        MidiSequence s = Read(new RcpBuilder().Track(0, t => t
            .LoopStart().Note(60, 48, 24).LoopEnd(3)
            .Note(64, 48, 24)
            .End()));

        Assert.Equal([0L, 48, 96, 144], NoteOns(s));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(4, 4)]
    [InlineData(0, 1)]   // the counter still lets one pass through
    public void AnEndlessLoopPlaysAsOftenAsTheSettingSays(int setting, int passes)
    {
        MidiSequence s = Read(new RcpBuilder().Track(0, t => t
            .LoopStart().Note(60, 48, 24).LoopEnd(0)
            .End()), setting);

        Assert.Equal(passes, NoteOns(s).Length);
    }

    [Fact]
    public void EachTrackKeepsItsOwnLoops()
    {
        // One track repeats a bar four times while the other plays two bars straight through.
        MidiSequence s = Read(new RcpBuilder()
            .Track(0, t => t.LoopStart().Note(36, 48, 24).LoopEnd(4).End())
            .Track(1, t => t.Note(60, 96, 48).Note(62, 96, 48).End()));

        long[] bass = [.. Channel(s).Where(e => e.Status == 0x90 && e.Data2 > 0).Select(e => e.Tick)];
        long[] melody = [.. Channel(s).Where(e => e.Status == 0x91 && e.Data2 > 0).Select(e => e.Tick)];
        Assert.Equal([0L, 48, 96, 144], bass);
        Assert.Equal([0L, 96], melody);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARepeatedMeasurePlaysTheMeasureItPointsAt(bool g36)
    {
        MidiSequence s = Read(new RcpBuilder(g36).Track(0, t => t
            .Note(60, 48, 24).Note(62, 48, 24).MeasureEnd()     // bar 1, events 0-2
            .SameMeasure(0)                                     // bar 2 = bar 1
            .Note(64, 48, 24)
            .End()));

        Assert.Equal([(0L, 60), (48L, 62), (96L, 60), (144L, 62), (192L, 64)],
                     Channel(s).Where(e => e.Data2 > 0).Select(e => (e.Tick, e.Data1)));
    }

    [Fact]
    public void ChannelsPast16GoToTheSecondPort()
    {
        MidiSequence s = Read(new RcpBuilder()
            .Track(0, t => t.Note(60, 48, 24).End())
            .Track(17, t => t.Note(60, 48, 24).End()));

        Assert.Equal([0, 1], s.Events.Where(e => e.Kind == MidiEventKind.Channel)
                                     .Select(e => e.Port).Distinct().Order());
        Assert.Contains(Channel(s), e => e.Status == 0x91);
    }

    [Fact]
    public void AMutedTrackAndATrackWithNoChannelStaySilent()
    {
        MidiSequence s = Read(new RcpBuilder()
            .Track(0, t => t.Note(60, 48, 24).End(), mode: 1)
            .Track(-1, t => t.Note(62, 48, 24).End())
            .Track(2, t => t.Note(64, 48, 24).End()));

        Assert.Equal([0x92], Channel(s).Select(e => e.Status).Distinct());
    }

    [Fact]
    public void KeyShiftAndPlayBiasMoveNotesButNotARhythmTrack()
    {
        MidiSequence s = Read(new RcpBuilder { PlayBias = 2 }
            .Track(0, t => t.Note(60, 48, 24).End(), keyShift: 0x7F)     // -1
            .Track(9, t => t.Note(36, 48, 24).End(), keyShift: 0x80 | 5)); // rhythm

        Assert.Contains(Channel(s), e => e == (0L, 0x90, 61, 100));
        Assert.Contains(Channel(s), e => e == (0L, 0x99, 36, 100));
    }

    [Fact]
    public void ControllersAndProgramsAreSent()
    {
        MidiSequence s = Read(new RcpBuilder().Track(3, t => t
            .Command(0xE2, 0, 5, 8)        // bank MSB 8, program 5
            .Command(0xEB, 0, 7, 100)      // volume
            .Command(0xEE, 0, 0x00, 0x40)  // pitch bend centre
            .Note(60, 48, 24)
            .End()));

        Assert.Equal([(0L, 0xB3, 0, 8), (0L, 0xC3, 5, 0), (0L, 0xB3, 7, 100), (0L, 0xE3, 0, 0x40)],
                     Channel(s).Take(4));
    }

    [Fact]
    public void ATempoChangeIsARatioOfTheSongsTempo()
    {
        MidiSequence s = Read(new RcpBuilder { Tempo = 120 }.Track(0, t => t
            .Note(60, 48, 24)
            .Command(0xE7, 0, 128, 0)      // 128/64: twice as fast
            .Note(62, 48, 24)
            .End()));

        MidiEvent second = s.Events.First(e => e.Kind == MidiEventKind.Channel && ((e.Packed >> 8) & 0xFF) == 62);
        Assert.Equal(500_000, second.TimeUs);
        MidiEvent off = s.Events.Last(e => e.Kind == MidiEventKind.Channel);
        Assert.Equal(500_000 + 125_000, off.TimeUs);
    }

    [Fact]
    public void ATempoTooSlowForTheMetaEventIsHeldAtFour()
    {
        MidiSequence s = Read(new RcpBuilder { Tempo = 2 }.Track(0, t => t.Note(60, 48, 24).End()));

        Assert.Equal((0L, 4), Tempos(s)[0]);
    }

    [Fact]
    public void AFastG36TempoChangeDoesNotOverflow()
    {
        // 60000 × 60000 is past what an int holds.
        MidiSequence s = Read(new RcpBuilder(g36: true) { Tempo = 60000 }.Track(0, t => t
            .Note(60, 48, 24)
            .Command(0xE7, 0, 60000, 0)
            .Note(62, 48, 24)
            .End()));

        Assert.True(Tempos(s)[1].Bpm > 60000, $"the change came out at {Tempos(s)[1].Bpm} BPM");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATimeBaseAnSmfWouldTakeForSmpteIsTheDefault(bool g36)
    {
        MidiSequence s = Read(new RcpBuilder(g36) { TimeBase = 0x8030 }.Track(0, t => t.Note(60, 48, 24).End()));

        Assert.Equal(48, s.Division);
    }

    [Fact]
    public void AUserExclusiveFillsInGateVelocityAndChecksum()
    {
        // A GS parameter set: address 40 01 30, value from the velocity, Roland checksum.
        MidiSequence s = Read(new RcpBuilder()
            .UserExclusive(0, 0x41, 0x10, 0x42, 0x12, 0x83, 0x40, 0x01, 0x30, 0x81, 0x84, 0xF7)
            .Track(0, t => t.Command(0x90, 0, 0, 0x04).Note(60, 48, 24).End()));

        MidiEvent sysex = s.Events.Single(e => e.Kind == MidiEventKind.SysEx);
        Assert.Equal(new byte[] { 0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x01, 0x30, 0x04, 0x0B, 0xF7 },
                     s.GetData(sysex).ToArray());
    }

    [Fact]
    public void RecomposerDataIsKnownByItsHeaderNotItsName()
    {
        Assert.True(RcpConverter.IsRecomposer(new RcpBuilder().Track(0, t => t.End()).Build()));
        Assert.True(RcpConverter.IsRecomposer(new RcpBuilder(g36: true).Track(0, t => t.End()).Build()));
        Assert.False(RcpConverter.IsRecomposer("MThd"u8));
    }

    [Fact]
    public void RunawayDataIsRefused()
    {
        // Loops of 255 nested six deep: far more than any song.
        MidiSequence Build() => Read(new RcpBuilder().Track(0, t =>
        {
            for (int i = 0; i < 6; i++) t.LoopStart();
            t.Note(60, 1, 1);
            for (int i = 0; i < 6; i++) t.LoopEnd(255);
            t.End();
        }));

        Assert.Throws<InvalidDataException>(Build);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopsAroundNothingAreRefusedAsWell(bool g36)
    {
        // Nothing inside to write, so only the turns themselves can say it runs away.
        MidiSequence Build() => Read(new RcpBuilder(g36).Track(0, t =>
        {
            for (int i = 0; i < 8; i++) t.LoopStart();
            for (int i = 0; i < 8; i++) t.LoopEnd(255);
            t.End();
        }));

        Assert.Throws<InvalidDataException>(Build);
    }

    [Fact]
    public void TheLimitIsForTheWholeSong()
    {
        static void Loops(RcpTrack t)
        {
            t.LoopStart().LoopStart();
            t.Note(60, 1, 1);
            t.LoopEnd(255).LoopEnd(255);
            t.End();
        }

        var one = new RcpBuilder().Track(0, Loops);
        var many = new RcpBuilder();
        for (int i = 0; i < 20; i++) many.Track(i % 16, Loops);

        Assert.Equal(255 * 255, NoteOns(Read(one)).Length);
        Assert.Throws<InvalidDataException>(() => Read(many));
    }

    [Fact]
    public void ATruncatedFileIsRefused()
    {
        byte[] rcp = new RcpBuilder().Track(0, t => t.End()).Build();

        Assert.Throws<InvalidDataException>(() => SmfReader.Read(rcp.AsSpan(0, 0x100)));
    }

    /// <summary>(tick, port, bytes) of every exclusive, in order.</summary>
    private static (long Tick, int Port, byte[] Data)[] SysEx(MidiSequence s, int? track = null)
        => [.. s.Events.Where(e => e.Kind == MidiEventKind.SysEx && (track is null || e.Track == track))
                       .Select(e => (e.Ticks, e.Port, s.GetData(e).ToArray()))];

    /// <summary>(tick, BPM) of every tempo, the song's own first.</summary>
    private static (long Tick, int Bpm)[] Tempos(MidiSequence s)
        => [.. s.Events.Where(e => e.Kind == MidiEventKind.Meta && e.MetaType == MetaType.Tempo)
                       .Select(e =>
                       {
                           ReadOnlySpan<byte> d = s.GetData(e);
                           return (e.Ticks, (int)Math.Round(60_000_000.0 / ((d[0] << 16) | (d[1] << 8) | d[2])));
                       })];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PressureAndProgramsGoToTheTracksChannel(bool g36)
    {
        MidiSequence s = Read(new RcpBuilder(g36).Track(2, t => t
            .Command(0xEC, 12, 40, 0)         // program 40
            .Command(0xEA, 12, 0x50, 0)       // channel pressure
            .Command(0xED, 24, 60, 0x30)      // key pressure on 60
            .Command(0xE1, 0, 7, 3)           // bank LSB 3, program 7
            .Note(60, 48, 24)
            .End()));

        Assert.Equal(
            [(0L, 0xC2, 40, 0), (12L, 0xD2, 0x50, 0), (24L, 0xA2, 60, 0x30),
             (48L, 0xB2, 32, 3), (48L, 0xC2, 7, 0)],
            Channel(s).Take(5));
    }

    [Fact]
    public void AValuePastSevenBitsSendsNothingButStillTakesItsStep()
    {
        MidiSequence s = Read(new RcpBuilder().Track(0, t => t
            .Command(0xEA, 12, 0x80, 0)
            .Command(0xEC, 12, 0x90, 0)
            .Command(0xED, 12, 60, 0x80)
            .Note(60, 48, 24)
            .End()));

        Assert.Equal([(36L, 0x90, 60, 100), (60L, 0x90, 60, 0)], Channel(s));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATrackExclusiveFillsItsTemplateInFromTheEvent(bool g36)
    {
        // A GS parameter set whose value is the gate, with its checksum worked out.
        MidiSequence s = Read(new RcpBuilder(g36).Track(0, t => t
            .Note(60, 48, 24)
            .TrackExclusive(0, 0x50, 0, 0x41, 0x10, 0x42, 0x12, 0x83, 0x40, 0x01, 0x33, 0x80, 0x84)
            .Note(62, 48, 24)
            .End()));

        (long tick, int port, byte[] data) = Assert.Single(SysEx(s));
        Assert.Equal(48, tick);
        Assert.Equal(0, port);
        Assert.Equal(new byte[] { 0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x01, 0x33, 0x50, 0x3C, 0xF7 }, data);
    }

    [Fact]
    public void AnEndMarkInsideAnExclusiveTemplateEndsIt()
    {
        MidiSequence s = Read(new RcpBuilder().Track(0, t => t
            .TrackExclusive(0, 0, 0, 0x7E, 0x7F, 0x09, 0x01, 0xF7, 0x55, 0x66)
            .Note(60, 48, 24)
            .End()));

        Assert.Equal(new byte[] { 0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7 }, Assert.Single(SysEx(s)).Data);
    }

    [Fact]
    public void AnExclusiveWhoseGateOrVelocityIsPastSevenBitsIsDropped()
    {
        MidiSequence s = Read(new RcpBuilder()
            .UserExclusive(0, 0x41, 0x10, 0x42, 0x12, 0x80, 0x81, 0xF7)
            .Track(0, t => t
                .Command(0x90, 0, 0x80, 0x00)
                .TrackExclusive(0, 0x00, 0x80, 0x41, 0x10)
                .Note(60, 48, 24)
                .End()));

        Assert.Empty(SysEx(s));
    }

    [Fact]
    public void TheChannelInAnExclusiveIsThePartWithinThePort()
    {
        // Only the second port is used, so the part within it is all its module needs.
        MidiSequence s = Read(new RcpBuilder()
            .UserExclusive(1, 0x43, 0x10, 0x4C, 0x08, 0x82, 0x01, 0x80, 0xF7)
            .Track(21, t => t.Command(0x91, 0, 0x20, 0).Note(60, 48, 24).End()));

        Assert.Equal(new byte[] { 0xF0, 0x43, 0x10, 0x4C, 0x08, 0x05, 0x01, 0x20, 0xF7 },
                     Assert.Single(SysEx(s)).Data);
    }

    [Fact]
    public void OnASongSpreadOverPortsTheChannelInAnExclusiveCountsAcrossThem()
    {
        MidiSequence s = Read(new RcpBuilder()
            .UserExclusive(1, 0x43, 0x10, 0x4C, 0x08, 0x82, 0x01, 0x80, 0xF7)
            .Track(0, t => t.Note(60, 48, 24).End())
            .Track(21, t => t.Command(0x91, 0, 0x20, 0).Note(60, 48, 24).End()));

        (_, int port, byte[] data) = Assert.Single(SysEx(s));
        Assert.Equal(1, port);
        Assert.Equal(new byte[] { 0xF0, 0x43, 0x10, 0x4C, 0x08, 0x15, 0x01, 0x20, 0xF7 }, data);
    }

    [Fact]
    public void AnExclusiveOnATrackWithNoChannelIsSentUnlessItNamesTheChannel()
    {
        MidiSequence s = Read(new RcpBuilder()
            .UserExclusive(0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7)
            .UserExclusive(1, 0x43, 0x10, 0x4C, 0x08, 0x82, 0x01, 0x80, 0xF7)
            .Track(-1, t => t.Command(0x90, 0, 0, 0).Command(0x91, 0, 0x20, 0).Note(60, 48, 24).End()));

        Assert.Equal(new byte[] { 0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 },
                     Assert.Single(SysEx(s)).Data);
    }

    [Fact]
    public void ARolandParameterGoesToAnMt32UntilTheTrackSaysOtherwise()
    {
        MidiSequence s = Read(new RcpBuilder()
            .Track(0, t => t
                .Command(0xDE, 0, 0x05, 0x20)      // the default device and base address
                .Command(0xDD, 0, 0x40, 0x01)      // base address 40 01
                .Command(0xDF, 0, 0x10, 0x42)      // device 10, model 42
                .Command(0xDE, 12, 0x30, 0x04)
                .Note(60, 48, 24)
                .End())
            .Track(1, t => t
                .Command(0xDE, 0, 0x05, 0x20)      // not what the other track set
                .Note(60, 48, 24)
                .End()));

        byte[] mt32 = [0xF0, 0x41, 0x10, 0x16, 0x12, 0x00, 0x10, 0x05, 0x20, 0x4B, 0xF7];
        Assert.Equal([mt32, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x01, 0x30, 0x04, 0x0B, 0xF7]],
                     SysEx(s, track: 1).Select(e => e.Data));
        Assert.Equal([mt32], SysEx(s, track: 2).Select(e => e.Data));
    }

    [Fact]
    public void AYamahaParameterGoesToAnXgModuleUntilTheTrackSaysOtherwise()
    {
        MidiSequence s = Read(new RcpBuilder().Track(0, t => t
            .Command(0xD2, 0, 0x7E, 0x00)          // the default device and base address
            .Command(0xD0, 0, 0x02, 0x01)          // base address 02 01
            .Command(0xD1, 0, 0x11, 0x4C)          // device 11, model 4C
            .Command(0xD2, 0, 0x00, 0x05)          // with a checksum
            .Command(0xD3, 0, 0x40, 0x7F)          // without one
            .Note(60, 48, 24)
            .End()));

        Assert.Equal(
            [
                [0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0x02, 0xF7],
                [0xF0, 0x43, 0x11, 0x4C, 0x02, 0x01, 0x00, 0x05, 0x78, 0xF7],
                [0xF0, 0x43, 0x11, 0x4C, 0x02, 0x01, 0x40, 0x7F, 0xF7],
            ],
            SysEx(s).Select(e => e.Data));
    }

    [Theory]
    [InlineData(0xC0, 0x85, 0x10, "F04313090510F7")]      // DX7 function: the gate's top bit joins the group
    [InlineData(0xC1, 0x05, 0x10, "F04313000510F7")]      // DX voice
    [InlineData(0xC2, 0x85, 0x10, "F04313050510F7")]      // DX performance
    [InlineData(0xC3, 0x05, 0x10, "F04313110510F7")]      // TX function
    [InlineData(0xC5, 0x05, 0x5A, "F0431315050A05F7")]    // FB-01: the value split into nibbles
    [InlineData(0xC6, 0x05, 0x10, "F0437503100510F7")]    // FB-01 system
    [InlineData(0xCA, 0x05, 0x10, "F04313107B0510F7")]    // TX81Z system
    [InlineData(0xCF, 0x05, 0x10, "F043131A0510F7")]      // TX802 performance
    [InlineData(0xDC, 0x05, 0x10, "F04132030510F7")]      // MKS-7
    public void AModuleExclusiveIsBuiltForTheTracksChannel(int cmd, int gt, int vel, string expected)
    {
        MidiSequence s = Read(new RcpBuilder().Track(3, t => t
            .Command(cmd, 0, gt, vel)
            .Note(60, 48, 24)
            .End()));

        Assert.Equal(Convert.FromHexString(expected), Assert.Single(SysEx(s)).Data);
    }

    [Fact]
    public void AModuleExclusiveNeedsAChannel()
    {
        MidiSequence s = Read(new RcpBuilder().Track(-1, t => t
            .Command(0xC1, 0, 0x05, 0x10)
            .Command(0xDC, 0, 0x05, 0x10)
            .Note(60, 48, 24)
            .End()));

        Assert.Empty(SysEx(s));
    }

    [Fact]
    public void ACommandRecomposerDoesNotDefineSendsNothingAndTakesNoTime()
    {
        MidiSequence s = Read(new RcpBuilder().Track(0, t => t
            .Command(0xC4, 48, 0x05, 0x10)
            .Note(60, 48, 24)
            .End()));

        Assert.Empty(SysEx(s));
        Assert.Equal([0L], NoteOns(s));
    }

    [Fact]
    public void AChannelChangeMovesTheNotesAfterIt()
    {
        MidiSequence s = Read(new RcpBuilder().Track(0, t => t
            .Note(60, 48, 24)
            .Command(0xE6, 0, 4, 0)                // counted from 1: channel 4
            .Note(62, 48, 24)
            .End()));

        Assert.Equal([(0L, 0x90, 60, 100), (24L, 0x90, 60, 0), (48L, 0x93, 62, 100), (72L, 0x93, 62, 0)],
                     Channel(s));
    }

    [Fact]
    public void AChannelChangePast16MovesTheTrackToTheNextPort()
    {
        MidiSequence s = Read(new RcpBuilder().Track(0, t => t
            .Note(60, 48, 24)
            .Command(0xE6, 0, 18, 0)               // port B, channel 2
            .Note(62, 48, 24)
            .Command(0xE6, 0, 1, 0)                // back to port A, channel 1
            .Note(64, 48, 24)
            .End()));

        Assert.Equal([(0L, 0, 0x90), (48L, 1, 0x91), (96L, 0, 0x90)],
                     s.Events.Where(e => e.Kind == MidiEventKind.Channel && e.Data2 > 0)
                             .Select(e => (e.Ticks, e.Port, (int)e.Status)));
    }

    [Fact]
    public void AChannelChangeToNoneSilencesTheTrackUntilItIsGivenOneAgain()
    {
        MidiSequence s = Read(new RcpBuilder().Track(0, t => t
            .Note(60, 48, 24)
            .Command(0xE6, 0, 0, 0)
            .Note(62, 48, 24)
            .Command(0xE6, 0, 1, 0)
            .Note(64, 48, 24)
            .End()));

        Assert.Equal([(0L, 60), (96L, 64)],
                     Channel(s).Where(e => e.Data2 > 0).Select(e => (e.Tick, e.Data1)));
    }

    /// <summary>(tick, port, status, note) of every note-off, in order.</summary>
    private static (long Tick, int Port, int Status, int Note)[] NoteOffs(MidiSequence s)
        => [.. s.Events.Where(e => e.Kind == MidiEventKind.Channel && (e.Status & 0xF0) == 0x90 && e.Data2 == 0)
                       .Select(e => (e.Ticks, e.Port, (int)e.Status, (int)e.Data1))];

    [Theory]
    [InlineData(4, 0)]      // channel 4
    [InlineData(17, 1)]     // port B, channel 1
    [InlineData(0, 0)]      // none
    public void ANoteHeldAcrossAChannelChangeEndsWhereItWasStruck(int to, int port)
    {
        MidiSequence s = Read(new RcpBuilder().Track(0, t => t
            .Note(60, st: 0, gt: 48)
            .Command(0xE6, 0, to, 0)
            .Note(62, st: 96, gt: 24)
            .End()));

        Assert.Contains((48L, 0, 0x90, 60), NoteOffs(s));
        // What follows the change is where the change put it.
        if (to > 0)
            Assert.Contains((24L, port, 0x90 | (to - 1) % 16, 62), NoteOffs(s));
    }

    [Fact]
    public void TheSameNoteStruckOnTheNewChannelIsANoteOfItsOwn()
    {
        MidiSequence s = Read(new RcpBuilder().Track(0, t => t
            .Note(60, st: 0, gt: 96)
            .Command(0xE6, 24, 4, 0)
            .Note(60, st: 48, gt: 24)
            .End()));

        Assert.Equal([(0L, 0x90, 60, 100), (24L, 0x90, 60, 0), (24L, 0x93, 60, 100), (48L, 0x93, 60, 0)],
                     Channel(s));
    }

    [Theory]
    [InlineData(48)]
    [InlineData(96)]
    public void ATempoChangeCanGlideThereInSteps(int timeBase)
    {
        // The quickest glide there is, 15 steps at 48 to the beat, from 120 to 240.
        MidiSequence s = Read(new RcpBuilder { Tempo = 120, TimeBase = timeBase }.Track(0, t => t
            .Command(0xE7, 0, 128, 255)
            .Note(60, 2 * timeBase, timeBase)
            .End()));

        long At(int step) => step * timeBase / 48;
        Assert.Equal(
            [(0L, 120), (At(2), 136), (At(4), 152), (At(6), 168), (At(8), 184), (At(10), 200),
             (At(12), 216), (At(14), 232), (At(15), 240)],
            Tempos(s));
    }

    [Fact]
    public void SlowingDownGlidesOnTheSlowerSideOfEachStep()
    {
        MidiSequence s = Read(new RcpBuilder { Tempo = 100 }.Track(0, t => t
            .Command(0xE7, 0, 32, 255)             // to half the song's tempo
            .Note(60, 96, 48)
            .End()));

        Assert.Equal(
            [(0L, 100), (2L, 93), (4L, 86), (6L, 80), (8L, 73), (10L, 66), (12L, 60), (14L, 53), (15L, 50)],
            Tempos(s));
    }

    [Fact]
    public void ATempoChangeCutsAGlideShort()
    {
        MidiSequence s = Read(new RcpBuilder { Tempo = 120 }.Track(0, t => t
            .Command(0xE7, 6, 128, 255)
            .Command(0xE7, 0, 64, 0)               // back to as written, at once
            .Note(60, 96, 48)
            .End()));

        Assert.Equal([(0L, 120), (2L, 136), (4L, 152), (6L, 120)], Tempos(s));
    }

    [Fact]
    public void AGlideGoesNoFurtherThanTheSong()
    {
        MidiSequence s = Read(new RcpBuilder { Tempo = 120 }.Track(0, t => t
            .Command(0xE7, 0, 128, 255)
            .Note(60, 7, 7)
            .End()));

        Assert.Equal([(0L, 120), (2L, 136), (4L, 152), (6L, 168)], Tempos(s));
    }
}
