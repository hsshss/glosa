using Glosa.Core.Emulation;

namespace Glosa.Tests;

public class ChordNameTests
{
    private static byte[] Notes(params int[] pitches)
    {
        var sounding = new byte[128];
        foreach (int p in pitches) sounding[p] = 1;
        return sounding;
    }

    [Theory]
    [InlineData("C", 60, 64, 67)]
    [InlineData("Cm", 60, 63, 67)]
    [InlineData("Cdim", 60, 63, 66)]
    [InlineData("Caug", 60, 64, 68)]
    [InlineData("Csus4", 60, 65, 67)]
    [InlineData("C7", 60, 64, 67, 70)]
    [InlineData("Cmaj7", 60, 64, 67, 71)]
    [InlineData("Cm7", 60, 63, 67, 70)]
    [InlineData("C6", 60, 64, 67, 69)]
    [InlineData("C5", 60, 67)]
    [InlineData("C9", 60, 62, 64, 67, 70)]
    [InlineData("F", 65, 69, 72)]
    public void NamesTheShape(string expected, params int[] pitches)
        => Assert.Equal(expected, ChordName.Detect(Notes(pitches)));

    [Fact]
    public void TheLowestNoteDecidesTheRoot()
    {
        // E G C is C major with E in the bass, not an E chord.
        Assert.Equal("C/E", ChordName.Detect(Notes(64, 67, 72)));
    }

    [Fact]
    public void OctavesDoNotChangeTheName()
        => Assert.Equal("C", ChordName.Detect(Notes(48, 60, 64, 67, 72, 79)));

    [Fact]
    public void ASingleNoteIsNamedAfterItself()
        => Assert.Equal("D#", ChordName.Detect(Notes(63)));

    [Fact]
    public void NothingSoundingHasNoName()
        => Assert.Equal(string.Empty, ChordName.Detect(new byte[128]));

    [Fact]
    public void ASetThatIsNotAChordGetsNoName()
        => Assert.Equal(string.Empty, ChordName.Detect(Notes(60, 61, 62, 63, 64, 65)));
}

public class GeneralMidiTests
{
    [Fact]
    public void NamesTheCapitalTones()
    {
        Assert.Equal("Acoustic Grand Piano", GeneralMidi.InstrumentName(0));
        Assert.Equal("Gunshot", GeneralMidi.InstrumentName(127));
        Assert.Equal("-", GeneralMidi.InstrumentName(128));
    }

    [Fact]
    public void ShowsTheBankWhenThePartIsNotOnTheCapitalOne()
    {
        Assert.Equal("Brass Section", GeneralMidi.PartLabel(0, 0, 61, rhythm: false));
        Assert.Equal("Brass Section (8:0)", GeneralMidi.PartLabel(8, 0, 61, rhythm: false));
    }

    [Fact]
    public void RhythmPartsAreNamedAfterTheirKit()
    {
        Assert.Equal("Drum Kit", GeneralMidi.PartLabel(0, 0, 0, rhythm: true));
        Assert.Equal("Drum Kit 25", GeneralMidi.PartLabel(0, 0, 25, rhythm: true));
    }
}
