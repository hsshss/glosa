using Glosa.Core.Smf;
using Glosa.Core.Text;

namespace Glosa.Tests;

public class SmfReaderTests
{
    [Fact]
    public void ReadsHeaderAndCountsTracks()
    {
        byte[] smf = new SmfBuilder(division: 480)
            .Track(t => t.Meta(0, MetaType.TrackName, "Song"u8.ToArray()).End(0))
            .Track(t => t.Short(0, 0x90, 60, 100).Short(480, 0x80, 60, 0).End(0))
            .Build();

        MidiSequence seq = SmfReader.Read(smf);

        Assert.Equal(1, seq.Format);
        Assert.Equal(2, seq.TrackCount);
        Assert.Equal(480, seq.Division);
        Assert.Equal("Song", seq.Title);
    }

    [Theory]
    // Frames a second, ticks a frame, the tick the note is on, and when that is.
    [InlineData(24, 10, 240, 1_000_000)]
    [InlineData(25, 40, 1000, 1_000_000)]
    [InlineData(30, 4, 120, 1_000_000)]
    // 30 drop-frame runs at 30000/1001 frames a second: 120 ticks are 1.001 seconds.
    [InlineData(29, 4, 120, 1_001_000)]
    public void ASmpteDivisionTimesTicksByTheClock(int fps, int ticksPerFrame, int tick, long us)
    {
        // Frames a second as a negative byte, then ticks a frame; the tempo does not count.
        int division = ((256 - fps) << 8) | ticksPerFrame;
        MidiSequence seq = SmfReader.Read(new SmfBuilder(division)
            .Track(t => t.Meta(0, MetaType.Tempo, [0x0F, 0x42, 0x40]).Short(tick, 0x90, 60, 100).End(0))
            .Build());

        MidiEvent note = seq.Events.Single(e => e.Kind == MidiEventKind.Channel);
        Assert.InRange(note.TimeUs, us - 1, us + 1);
    }

    [Fact]
    public void KeepsWhatTheSongSaysAboutItself()
    {
        byte[] smf = new SmfBuilder(division: 480)
            .Track(t => t
                .Meta(0, MetaType.TrackName, "Song"u8.ToArray())
                .Meta(0, MetaType.Text, "arranged by someone"u8.ToArray())
                .Meta(0, MetaType.Text, "second line"u8.ToArray())
                .Meta(0, MetaType.Copyright, "(c) 1997"u8.ToArray())
                .End(0))
            .Build();

        MidiSequence seq = SmfReader.Read(smf);

        Assert.Equal("(c) 1997", seq.Copyright);

        // The first of a block of them, not the block: one line is what a display holds.
        Assert.Equal("arranged by someone", seq.Comment);

        // The scan still wants every word in the file, in the order they appear.
        Assert.Equal(["arranged by someone", "second line", "(c) 1997"], seq.Texts);
    }

    [Fact]
    public void SaysNothingAboutItselfWhenTheFileDoesNot()
    {
        byte[] smf = new SmfBuilder(division: 480)
            .Track(t => t.Short(0, 0x90, 60, 100).End(0))
            .Build();

        MidiSequence seq = SmfReader.Read(smf);

        Assert.Equal(string.Empty, seq.Copyright);
        Assert.Equal(string.Empty, seq.Comment);
    }

    [Fact]
    public void AppliesDefaultTempoOf120Bpm()
    {
        // One quarter note at 480 ppqn with no tempo event is 500 ms.
        byte[] smf = new SmfBuilder(division: 480)
            .Track(t => t.Short(0, 0x90, 60, 100).Short(480, 0x80, 60, 0).End(0))
            .Build();

        MidiSequence seq = SmfReader.Read(smf);
        MidiEvent noteOff = seq.Events.Single(e => e.Status == 0x80);

        Assert.Equal(500_000, noteOff.TimeUs);
    }

    [Fact]
    public void HonoursTempoChanges()
    {
        // 480 ppqn; first quarter at 120 BPM (500 ms), then 60 BPM doubles the next one.
        byte[] smf = new SmfBuilder(division: 480)
            .Track(t => t
                .Short(0, 0x90, 60, 100)
                .Meta(480, MetaType.Tempo, [0x0F, 0x42, 0x40])   // 1,000,000 us per quarter
                .Short(480, 0x80, 60, 0)
                .End(0))
            .Build();

        MidiSequence seq = SmfReader.Read(smf);
        MidiEvent noteOff = seq.Events.Single(e => e.Status == 0x80);

        Assert.Equal(1_500_000, noteOff.TimeUs);
    }

    [Fact]
    public void DecodesVariableLengthDeltaTimes()
    {
        // 0x8F 0x00 encodes 1920.
        byte[] smf = new SmfBuilder(division: 480)
            .Track(t => t.Short(0, 0x90, 60, 100).Short(1920, 0x80, 60, 0).End(0))
            .Build();

        MidiSequence seq = SmfReader.Read(smf);
        Assert.Equal(1920, seq.Events.Single(e => e.Status == 0x80).Ticks);
    }

    [Fact]
    public void ExpandsRunningStatus()
    {
        var track = new TrackBuilder();
        track.Short(0, 0x90, 60, 100);
        track.RunningStatus(0, 62, 100);   // same 0x90, status byte omitted
        track.End(0);

        MidiSequence seq = SmfReader.Read(new SmfBuilder(480).Track(track).Build());
        MidiEvent[] notes = [.. seq.Events.Where(e => e.Kind == MidiEventKind.Channel)];

        Assert.Equal(2, notes.Length);
        Assert.All(notes, n => Assert.Equal(0x90, n.Status));
        Assert.Equal(60, notes[0].Data1);
        Assert.Equal(62, notes[1].Data1);
    }

    [Fact]
    public void RoutesEventsByMidiPortMeta()
    {
        byte[] smf = new SmfBuilder(division: 480)
            .Track(t => t.Meta(0, MetaType.MidiPort, [0x00]).Short(0, 0x90, 60, 100).End(0))
            .Track(t => t.Meta(0, MetaType.MidiPort, [0x01]).Short(0, 0x91, 62, 100).End(0))
            .Build();

        MidiSequence seq = SmfReader.Read(smf);

        Assert.Equal(0, seq.Events.Single(e => e.Status == 0x90).Port);
        Assert.Equal(1, seq.Events.Single(e => e.Status == 0x91).Port);
        Assert.Equal(1, seq.MaxPort);
    }

    [Fact]
    public void AcceptsMoreThanSixteenTracks()
    {
        var builder = new SmfBuilder(division: 480);
        for (int i = 0; i < 32; i++)
        {
            int track = i;
            builder.Track(t => t
                .Meta(0, MetaType.MidiPort, [(byte)(track / 16)])
                .Short(0, (byte)(0x90 | (track % 16)), 60, 100)
                .End(0));
        }

        MidiSequence seq = SmfReader.Read(builder.Build());

        Assert.Equal(32, seq.TrackCount);
        Assert.Equal(1, seq.MaxPort);
    }

    [Fact]
    public void RestoresLeadingF0OnSysEx()
    {
        // GS Reset, stored in the file without its leading F0.
        byte[] body = [0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7];
        byte[] smf = new SmfBuilder(division: 480)
            .Track(t => t.SysEx(0, body).End(0))
            .Build();

        MidiSequence seq = SmfReader.Read(smf);
        MidiEvent e = seq.Events.Single(x => x.Kind == MidiEventKind.SysEx);

        Assert.Equal(
            new byte[] { 0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7 },
            seq.GetData(e).ToArray());
    }

    [Fact]
    public void DecodesShiftJisTrackName()
    {
        Cp932.Register();
        byte[] name = Cp932.Encoding.GetBytes("ふみぃ");
        byte[] smf = new SmfBuilder(division: 480)
            .Track(t => t.Meta(0, MetaType.TrackName, name).End(0))
            .Build();

        Assert.Equal("ふみぃ", SmfReader.Read(smf).Title);
    }

    [Fact]
    public void ATrackNameOfNothingButSpacesIsNotTaken()
    {
        // The padding is what a sequencer wrote to fill a column, not a name, and taking it
        // would leave the song looking untitled while hiding the name on the next track.
        byte[] smf = new SmfBuilder(division: 480)
            .Track(t => t.Meta(0, MetaType.TrackName, "      "u8.ToArray()).End(0))
            .Track(t => t.Meta(0, MetaType.TrackName, "Song"u8.ToArray()).End(0))
            .Build();

        Assert.Equal("Song", SmfReader.Read(smf).Title);
    }

    [Fact]
    public void MergesTracksInTimeOrder()
    {
        byte[] smf = new SmfBuilder(division: 480)
            .Track(t => t.Short(480, 0x90, 60, 100).End(0))
            .Track(t => t.Short(0, 0x91, 62, 100).End(0))
            .Build();

        MidiEvent[] channel = [.. SmfReader.Read(smf).Events
            .Where(e => e.Kind == MidiEventKind.Channel)];

        Assert.Equal(0x91, channel[0].Status);
        Assert.Equal(0x90, channel[1].Status);
    }

    [Fact]
    public void EqualTicksKeepTrackOrderAcrossManyTracks()
    {
        var builder = new SmfBuilder(division: 480);
        for (int i = 0; i < 300; i++)
        {
            int track = i;
            // Every third track empty, the rest sounding at the same two ticks.
            builder.Track(t =>
            {
                if (track % 3 != 0) t.Short(0, 0x90, (byte)(track % 128), 1).Short(10, 0x80, (byte)(track % 128), 0);
                t.End(0);
            });
        }

        MidiSequence song = SmfReader.Read(builder.Build());
        MidiEvent[] channel = [.. song.Events.Where(e => e.Kind == MidiEventKind.Channel)];
        int[] sounding = [.. Enumerable.Range(0, 300).Where(i => i % 3 != 0)];

        Assert.Equal(300, song.TrackCount);
        Assert.Equal([.. sounding, .. sounding], channel.Select(e => e.Track));
        Assert.Equal([.. sounding.Select(_ => 0L), .. sounding.Select(_ => 10L)], channel.Select(e => e.Ticks));
    }

    [Fact]
    public void ASongLongerThanAYearIsRefused()
    {
        // One tick is a quarter note at the slowest tempo there is: some 17 seconds.
        byte[] smf = new SmfBuilder(division: 1)
            .Track(t => t.Meta(0, MetaType.Tempo, [0xFF, 0xFF, 0xFF]).End(0x0FFFFFFF))
            .Build();

        Assert.Throws<SmfFormatException>(() => SmfReader.Read(smf));
    }

    [Fact]
    public void RejectsNonSmf()
        => Assert.Throws<SmfFormatException>(() => SmfReader.Read("not a midi file"u8));

    /// <summary>
    /// A 128-byte MacBinary header, the SMF as the data fork, and a resource fork behind it
    /// that would read as a chunk of nonsense if it were not cut off.
    /// </summary>
    private static byte[] MacBinary(byte[] smf, int nameLength = 8)
    {
        var header = new byte[128];
        header[1] = (byte)nameLength;
        "SONG.MID"u8.CopyTo(header.AsSpan(2));
        "Midi"u8.CopyTo(header.AsSpan(65));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(83), (uint)smf.Length);
        byte[] resource = [.. "MTrk"u8, 0x7F, 0xFF, 0xFF, 0xFF, 0x90, 0x90];
        return [.. header, .. smf, .. resource];
    }

    [Fact]
    public void ReadsTheSmfInsideAMacBinaryFile()
    {
        byte[] smf = new SmfBuilder(division: 480)
            .Track(t => t.Meta(0, MetaType.TrackName, "Mac"u8.ToArray()).Short(0, 0x90, 60, 100)
                         .Short(480, 0x80, 60, 0).End(0))
            .Build();

        MidiSequence plain = SmfReader.Read(smf);
        MidiSequence wrapped = SmfReader.Read(MacBinary(smf));

        Assert.Equal("Mac", wrapped.Title);
        Assert.Equal(plain.TrackCount, wrapped.TrackCount);
        Assert.Equal(plain.DurationUs, wrapped.DurationUs);
        Assert.Equal(plain.Events.Length, wrapped.Events.Length);
    }

    /// <summary>RIFF, RMID, the chunks given, each padded to an even length.</summary>
    private static byte[] Riff(params (string Id, byte[] Body)[] chunks)
    {
        var body = new List<byte>("RMID"u8.ToArray());
        foreach ((string id, byte[] data) in chunks)
        {
            body.AddRange(System.Text.Encoding.ASCII.GetBytes(id));
            body.AddRange(BitConverter.GetBytes((uint)data.Length));
            body.AddRange(data);
            if (data.Length % 2 != 0) body.Add(0);
        }
        return [.. "RIFF"u8, .. BitConverter.GetBytes((uint)body.Count), .. body];
    }

    [Fact]
    public void ReadsTheSmfInsideAnRmiFile()
    {
        byte[] smf = new SmfBuilder(division: 480)
            .Track(t => t.Meta(0, MetaType.TrackName, "RMI"u8.ToArray()).Short(0, 0x90, 60, 100)
                         .Short(480, 0x80, 60, 0).End(0))
            .Build();

        // An odd-length chunk ahead of the data, to be stepped over with its pad byte.
        MidiSequence wrapped = SmfReader.Read(Riff(("DISP", [1, 0, 0, 0, 0x41]), ("data", smf),
                                                   ("LIST", "INFOINAM"u8.ToArray())));

        Assert.Equal("RMI", wrapped.Title);
        Assert.Equal(SmfReader.Read(smf).Events.Length, wrapped.Events.Length);
    }

    [Fact]
    public void ARiffThatIsNotRmiIsNotReadAsOne()
    {
        byte[] wave = [.. "RIFF"u8, 4, 0, 0, 0, .. "WAVE"u8];

        Assert.Throws<SmfFormatException>(() => SmfReader.Read(wave));
    }

    [Fact]
    public void AHeaderThatIsNotMacBinaryIsNotUnwrapped()
    {
        byte[] smf = new SmfBuilder(division: 480).Track(t => t.End(0)).Build();

        // A name length of zero is not a MacBinary header, whatever follows it.
        Assert.Throws<SmfFormatException>(() => SmfReader.Read(MacBinary(smf, nameLength: 0)));
    }
    /// <summary>Songs that try each thing the summary has to agree with the full read on.</summary>
    private static byte[] SummarySong(string name) => name switch
    {
        "one track" => new SmfBuilder(480, format: 0)
            .Track(t => t.Meta(0, MetaType.TrackName, "Solo"u8).Short(0, 0x90, 60, 100)
                         .Meta(240, MetaType.Tempo, [0x07, 0xA1, 0x20]).Short(480, 0x80, 60, 0).End(0))
            .Build(),
        "tempo on its own track" => new SmfBuilder(480)
            .Track(t => t.Meta(0, MetaType.Tempo, [0x09, 0x27, 0xC0])
                         .Meta(960, MetaType.Tempo, [0x06, 0x1A, 0x80]).End(0))
            .Track(t => t.SysEx(0, [0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7])
                         .Short(0, 0x90, 60, 100).Short(700, 0x80, 60, 0)
                         .Short(333, 0x90, 62, 100).Short(1111, 0x80, 62, 0).End(7))
            .Build(),
        // The later track's change is the one in force once they are merged.
        "tempos on the same tick" => new SmfBuilder(96)
            .Track(t => t.Meta(50, MetaType.Tempo, [0x0F, 0x42, 0x40]).End(0))
            .Track(t => t.Short(50, 0x90, 60, 100).Meta(0, MetaType.Tempo, [0x03, 0xD0, 0x90])
                         .Short(77, 0x80, 60, 0).End(0))
            .Build(),
        "smpte" => new SmfBuilder(((256 - 25) << 8) | 40)
            .Track(t => t.Meta(0, MetaType.Tempo, [0x0F, 0x42, 0x40]).Short(1234, 0x90, 60, 100).End(5))
            .Build(),
        // A blank name is passed over for the next track's.
        "blank name first" => new SmfBuilder(480)
            .Track(t => t.Meta(0, MetaType.TrackName, "   "u8).Short(10, 0xC0, 5).End(0))
            .Track(t => t.Meta(0, MetaType.TrackName, "Real"u8).Escape(0, [0x43, 0x10, 0xF7]).End(3))
            .Build(),
        "no events" => new SmfBuilder(480).Track(new TrackBuilder()).Build(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [InlineData("one track")]
    [InlineData("tempo on its own track")]
    [InlineData("tempos on the same tick")]
    [InlineData("smpte")]
    [InlineData("blank name first")]
    [InlineData("no events")]
    public void ASummaryAgreesWithTheFullRead(string name)
    {
        byte[] smf = SummarySong(name);
        MidiSequence full = SmfReader.Read(smf);
        SongSummary summary = SmfReader.ReadSummary(smf, openingMessages: 3);

        Assert.Equal(full.DurationUs, summary.DurationUs);
        Assert.Equal(full.Title, summary.Title);
        Assert.Equal(full.Events.Where(e => e.Kind is MidiEventKind.Channel or MidiEventKind.SysEx)
                                .Take(3).Select(e => Describe(e, full.GetData(e))),
                     summary.Opening.Select(e => Describe(e, summary.GetData(e))));

        static string Describe(MidiEvent e, ReadOnlySpan<byte> data)
            => $"{e.Ticks} {e.Track} {e.Port} {e.Kind} {e.Packed:X} {Convert.ToHexString(data)}";
    }

    [Fact]
    public void ASummaryKeepsOnlyTheOpening()
    {
        byte[] smf = SummarySong("tempo on its own track");

        Assert.Equal(2, SmfReader.ReadSummary(smf, openingMessages: 2).Opening.Length);
        Assert.Empty(SmfReader.ReadSummary(smf, openingMessages: 0).Opening);
        Assert.Equal(5, SmfReader.ReadSummary(smf, openingMessages: 100).Opening.Length);
    }

    [Fact]
    public void ASummaryRefusesWhatTheFullReadRefuses()
    {
        byte[] tooLong = new SmfBuilder(division: 1)
            .Track(t => t.Meta(0, MetaType.Tempo, [0xFF, 0xFF, 0xFF]).End(0x0FFFFFFF))
            .Build();

        Assert.Throws<SmfFormatException>(() => SmfReader.ReadSummary(tooLong, 10));
        Assert.Throws<SmfFormatException>(() => SmfReader.ReadSummary("not a midi file"u8, 10));
    }
}
