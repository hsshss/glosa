using Glosa.Core.Emulation;

namespace Glosa.Tests;

public class DetectionDefaultsTests
{
    private static readonly DetectionDefaults Set = new("SC-55", "SC-88PRO", "MU2000");

    [Theory]
    [InlineData("THRU", "SC-55")]
    [InlineData("GS", "SC-88PRO")]
    [InlineData("XG", "MU2000")]
    // A DEF may spell them in another case.
    [InlineData("gs", "SC-88PRO")]
    [InlineData("Xg", "MU2000")]
    public void PlaysANameWithNoModelAsTheOneSet(string detected, string played)
        => Assert.Equal(played, Set.Resolve(detected));

    [Theory]
    [InlineData("SC-88")]
    [InlineData("MU50")]
    // GM stays: it says nothing of which family either, and is not one of the three.
    [InlineData("GM")]
    public void LeavesAModelAlone(string detected)
        => Assert.Equal(detected, Set.Resolve(detected));

    [Theory]
    [InlineData("THRU")]
    [InlineData("GS")]
    [InlineData("XG")]
    public void LeavesTheAnswerAsItIsWhenNothingIsSet(string detected)
        => Assert.Equal(detected, DetectionDefaults.None.Resolve(detected));

    [Fact]
    public void SetsEachOneOnItsOwn()
    {
        var gsOnly = DetectionDefaults.None with { Gs = "SC-55mk2" };

        Assert.Equal("SC-55mk2", gsOnly.Resolve("GS"));
        Assert.Equal("THRU", gsOnly.Resolve("THRU"));
        Assert.Equal("XG", gsOnly.Resolve("XG"));
    }
}
