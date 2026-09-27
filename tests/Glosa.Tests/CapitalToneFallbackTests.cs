using Glosa.Core.Emulation;

namespace Glosa.Tests;

public class CapitalToneFallbackTests
{
    private static (CapitalToneFallback Fallback, CapturingSink Sink) Build(string module)
    {
        var sink = new CapturingSink();
        var fallback = new CapitalToneFallback(sink) { Tones = SoundCanvasTones.Of(module) };
        return (fallback, sink);
    }

    private static uint Bank(int channel, int bank) => (uint)(0xB0 | channel | bank << 16);

    private static uint Map(int channel, int map) => (uint)(0xB0 | channel | 0x20 << 8 | map << 16);

    private static uint Program(int channel, int program) => (uint)(0xC0 | channel | program << 8);

    private static void Send(CapitalToneFallback fallback, params uint[] messages)
    {
        foreach (uint message in messages) fallback.SendShort(0, message);
    }

    // ---- tones ------------------------------------------------------------------

    [Fact]
    public void AToneTheMachineHasGoesThroughAsItIs()
    {
        // Program 15 has a bank 9 on the SC-55mkII.
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-55mk2");
        Send(fallback, Bank(0, 9), Program(0, 14));

        Assert.Equal([Bank(0, 9), Program(0, 14)], sink.Messages);
    }

    [Fact]
    public void AMissingVariationPlaysOnTheSubCapital()
    {
        // The patcher's own example: program 17 bank 18 is missing, and bank 16 is the
        // SC-55mkII's 60's Organ 1.
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-55mk2");
        Send(fallback, Bank(0, 18), Program(0, 16));

        Assert.Equal([Bank(0, 18), Bank(0, 16), Program(0, 16)], sink.Messages);
    }

    [Fact]
    public void WithNoSubCapitalItPlaysOnTheCapital()
    {
        // Program 8 has nothing but bank 0 below the special area.
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-55mk2");
        Send(fallback, Bank(0, 9), Program(0, 7));

        Assert.Equal([Bank(0, 9), Bank(0, 0), Program(0, 7)], sink.Messages);
    }

    [Fact]
    public void TheSongsBankIsPutBackForTheNextProgramChange()
    {
        // The machine holds the bank sent in the song's place; program 15 has the song's 9.
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-55mk2");
        Send(fallback, Bank(0, 9), Program(0, 7), Program(0, 14));

        Assert.Equal([Bank(0, 9), Bank(0, 0), Program(0, 7), Bank(0, 9), Program(0, 14)],
                     sink.Messages);
    }

    [Fact]
    public void TheBankStoodInIsNotSentAgainWhileTheMachineHoldsIt()
    {
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-55mk2");
        Send(fallback, Bank(0, 9), Program(0, 7), Program(0, 8));

        Assert.Equal([Bank(0, 9), Bank(0, 0), Program(0, 7), Program(0, 8)], sink.Messages);
    }

    [Fact]
    public void TheSpecialAreaAndTheSoundEffectsAreNotStoodInFor()
    {
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-55mk2");
        Send(fallback, Bank(0, 64), Program(0, 0), Bank(0, 3), Program(0, 120));

        Assert.Equal([Bank(0, 64), Program(0, 0), Bank(0, 3), Program(0, 120)], sink.Messages);
    }

    [Fact]
    public void EachPartIsItsOwn()
    {
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-55mk2");
        Send(fallback, Bank(0, 9), Program(1, 7));

        Assert.Equal([Bank(0, 9), Program(1, 7)], sink.Messages);
    }

    // ---- maps -------------------------------------------------------------------

    [Fact]
    public void ALaterModelJudgesOnTheMapThePartIsOn()
    {
        // Program 17 bank 18 is on the SC-88Pro's own map, and not on its SC-55 map.
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-88PRO");
        Send(fallback, Bank(0, 18), Program(0, 16), Map(0, 1), Program(0, 16));

        Assert.Equal([Bank(0, 18), Program(0, 16), Map(0, 1), Bank(0, 16), Program(0, 16)],
                     sink.Messages);
    }

    [Fact]
    public void TheMapSetByExclusiveCounts()
    {
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-88PRO");
        // TONE MAP NUMBER of part 1 (block 1) = 1, the SC-55 map.
        fallback.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x41, 0x00, 0x01, 0x7E, 0xF7]);
        Send(fallback, Bank(0, 18), Program(0, 16));

        Assert.Equal([Bank(0, 18), Bank(0, 16), Program(0, 16)], sink.Messages);
    }

    [Fact]
    public void AMapTheMachineDoesNotHaveIsLeftAlone()
    {
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-88");
        Send(fallback, Map(0, 3), Bank(0, 9), Program(0, 7));

        Assert.Equal([Map(0, 3), Bank(0, 9), Program(0, 7)], sink.Messages);
    }

    // ---- drum sets ----------------------------------------------------------------

    [Fact]
    public void AMissingDrumSetPlaysAsTheOneItIsAVariationOf()
    {
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-55mk2");
        Send(fallback, Program(9, 10), Program(9, 3), Program(9, 25));

        // ROOM for 11, STANDARD for 4, and 26 (TR-808) is there.
        Assert.Equal([Program(9, 8), Program(9, 0), Program(9, 25)], sink.Messages);
    }

    [Fact]
    public void DrumSetsFrom49OnAreNotStoodInFor()
    {
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-55mk2");
        Send(fallback, Program(9, 49), Program(9, 60));

        Assert.Equal([Program(9, 49), Program(9, 60)], sink.Messages);
    }

    [Fact]
    public void APartMadeARhythmPartByExclusivePicksDrumSets()
    {
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-55mk2");
        // USE FOR RHYTHM PART of part 1 (block 1, channel 1) = map 1.
        fallback.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x11, 0x15, 0x01, 0x19, 0xF7]);
        Send(fallback, Program(0, 3));

        Assert.Equal([Program(0, 0)], sink.Messages);
    }

    // ---- resets -------------------------------------------------------------------

    [Fact]
    public void AResetPutsThePartsBack()
    {
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-55mk2");
        fallback.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x11, 0x15, 0x01, 0x19, 0xF7]);
        Send(fallback, Bank(1, 9), Program(1, 7));
        sink.Shorts.Clear();

        // GS Reset: the machine is on bank 0, which program 15 has, and channel 1 is melodic
        // again. Not reset, the song's bank 9 would be put back and set 4 stood in for.
        fallback.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7]);
        Send(fallback, Program(1, 14), Program(0, 3));

        Assert.Equal([Program(1, 14), Program(0, 3)], sink.Messages);
    }

    [Fact]
    public void AResetReachesEveryPort()
    {
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-55mk2");
        fallback.SendShort(1, Bank(0, 9));
        fallback.SendShort(1, Program(0, 7));
        sink.Shorts.Clear();

        // GM System On on port A. Not reset, port B's bank 9 would be put back.
        fallback.SendLong(0, [0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7]);
        fallback.SendShort(1, Program(0, 14));

        Assert.Equal([Program(0, 14)], sink.Messages);
    }

    // ---- off ----------------------------------------------------------------------

    [Fact]
    public void WithNoTablesNothingChanges()
    {
        var sink = new CapturingSink();
        var fallback = new CapitalToneFallback(sink);
        Send(fallback, Bank(0, 18), Program(0, 16), Program(9, 3));

        Assert.Equal([Bank(0, 18), Program(0, 16), Program(9, 3)], sink.Messages);
    }

    [Fact]
    public void TurnedOffItStillPutsBackTheBankItStoodIn()
    {
        (CapitalToneFallback fallback, CapturingSink sink) = Build("SC-55mk2");
        Send(fallback, Bank(0, 9), Program(0, 7));
        sink.Shorts.Clear();

        fallback.Tones = null;
        Send(fallback, Program(0, 7));

        Assert.Equal([Bank(0, 9), Program(0, 7)], sink.Messages);
    }

    // ---- tables -------------------------------------------------------------------

    [Theory]
    [InlineData("SC-55mk2", "SC-55mk2")]
    [InlineData("SC-55ST", "SC-55mk2")]
    [InlineData("SC-33", "SC-33")]
    [InlineData("sc-88vl", "SC-88")]
    [InlineData("SC-88_2", "SC-88")]
    [InlineData("SC-88PRO", "SC-88PRO")]
    [InlineData("SC-8820", "SC-8850")]
    [InlineData("SC-8850", "SC-8850")]
    public void EverySoundCanvasFromTheMk2OnHasTables(string module, string model)
        => Assert.Equal(model, SoundCanvasTones.Of(module)?.Model);

    [Theory]
    [InlineData("SC-55")]
    [InlineData("THRU")]
    [InlineData("MU2000")]
    public void OthersHaveNone(string module) => Assert.Null(SoundCanvasTones.Of(module));

    [Theory]
    [InlineData("SC-55mk2", 0)]
    [InlineData("SC-88", 2)]
    [InlineData("SC-88PRO", 3)]
    [InlineData("SC-8850", 4)]
    public void TheNativeMapIsTheLast(string module, int native)
        => Assert.Equal(native, SoundCanvasTones.Of(module)!.Native);
}
