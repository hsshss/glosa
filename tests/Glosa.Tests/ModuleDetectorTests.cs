using Glosa.Core.Definition;
using Glosa.Core.Emulation;

namespace Glosa.Tests;

public class ModuleDetectorTests
{
    /// <summary>
    /// A made-up family of modules: the two reserved keys, then keywords, with the longer
    /// spellings of one family listed before the shorter ones as a real section would.
    /// </summary>
    private static DefDocument Def(string extra = "") => DefDocument.ParseText($"""
        [keyword]
        delimiter=()/.（）
        length=4096
        ND-1050=ND-1050
        1050=ND-1050
        ND10EX=ND-10EX
        10Ex=ND-10EX
        ND10=ND-10
        10=ND-10
        Vela200=Vela200
        ZQ=ZQ
        {extra}
        """);

    private static string? Found(string text)
        => ModuleDetector.Scan(Def(), text)?.Module;

    [Fact]
    public void MatchesAWholeWordOnly()
    {
        Assert.Equal("ND-10", Found("cool 10 song"));
        // The keyword has to cover the token: 10 must not match inside 1050.
        Assert.Equal("ND-1050", Found("cool 1050 song"));
        Assert.Null(Found("cool 1051 song"));
    }

    [Fact]
    public void IgnoresCase()
    {
        Assert.Equal("ND-10EX", Found("made for nd10ex"));
        Assert.Equal("ND-10EX", Found("made for 10EX"));
    }

    [Fact]
    public void TheEarliestTokenWins()
    {
        // Two keywords present; the one that comes first in the text decides.
        Assert.Equal("Vela200", Found("Vela200 or ND-1050"));
        Assert.Equal("ND-1050", Found("ND-1050 or Vela200"));
    }

    [Fact]
    public void WithinOneTokenTheDefOrderDecides()
    {
        // "ND10EX" is listed before "ND10", and both are whole-word candidates for
        // different tokens; a single token can only match its own spelling.
        Assert.Equal("ND-10EX", Found("ND10EX"));
        Assert.Equal("ND-10", Found("ND10"));
    }

    [Fact]
    public void DelimitersBecomeWordBreaks()
    {
        Assert.Equal("ND-10", Found("song(10)"));
        Assert.Equal("Vela200", Found("title/Vela200/version"));
    }

    [Fact]
    public void TheIdeographicSpaceBreaksWordsToo()
        => Assert.Equal("ZQ", Found("曲名　ZQ　対応"));

    [Fact]
    public void DoubleByteDelimitersBreakWords()
        => Assert.Equal("ND-10", Found("曲名（10）"));

    [Fact]
    public void ControlCharactersBreakWords()
        => Assert.Equal("Vela200", Found("readme\r\nVela200\tここまで"));

    [Fact]
    public void TheReservedKeysAreNotKeywords()
    {
        Assert.Null(Found("length"));
        Assert.Null(Found("delimiter"));
    }

    [Fact]
    public void NothingMatchingMeansNoDetection()
        => Assert.Null(ModuleDetector.Scan(Def(), "just a song"));

    [Fact]
    public void TheLengthLimitStopsTheScan()
    {
        DefDocument def = DefDocument.ParseText("""
            [keyword]
            length=10
            delimiter=
            Vela200=Vela200
            """);

        Assert.Null(ModuleDetector.Scan(def, new string('x', 20) + " Vela200"));
        Assert.Equal("Vela200", ModuleDetector.Scan(def, "Vela200")?.Module);
    }

    [Fact]
    public void TheFileNameIsSearchedBeforeTheTitle()
    {
        ModuleDetection found = ModuleDetector.Detect(
            Def(), Path.Combine("songs", "Vela200.mid"), title: "ND-1050 version");

        Assert.Equal("Vela200", found.Module);
        Assert.Equal(ModuleSource.Keyword, found.Source);
    }

    [Fact]
    public void AKeywordGluedToOtherCharactersIsNotAWord()
    {
        // The hyphen is not a delimiter, so the whole file name is one token.
        ModuleDetection found = ModuleDetector.Detect(
            Def(), Path.Combine("songs", "for-Vela200.mid"), fallback: "GM");

        Assert.Equal(ModuleSource.Default, found.Source);
    }

    [Fact]
    public void TheTitleIsSearchedBeforeTheDocument()
    {
        ModuleDetection found = ModuleDetector.Detect(
            Def(), Path.Combine("songs", "song.mid"), title: "Vela200 mix", document: "written on an ND-1050");

        Assert.Equal("Vela200", found.Module);
    }

    [Fact]
    public void NothingFoundFallsBackToTheDefault()
    {
        ModuleDetection found = ModuleDetector.Detect(
            Def(), Path.Combine("songs", "song.mid"), fallback: "GM");

        Assert.Equal("GM", found.Module);
        Assert.Equal(ModuleSource.Default, found.Source);
    }
}

public class TitleTextTests
{
    [Theory]
    [InlineData("  padded  ", "padded")]
    [InlineData("a    b", "a b")]
    [InlineData("　全角　空白　", "全角 空白")]
    [InlineData("tabs\tand\nnewlines", "tabs and newlines")]
    [InlineData(" - the Game -  for SC-88Pro/8820 ", "- the Game - for SC-88Pro/8820")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void CollapsesSpacingAndTrimsTheEnds(string input, string expected)
        => Assert.Equal(expected, Glosa.Core.Text.TitleText.Tidy(input));

    [Fact]
    public void LeavesTheTextAloneWhenTurnedOff()
        => Assert.Equal("  as  written  ",
                        Glosa.Core.Text.TitleText.Tidy("  as  written  ", enabled: false));
}
