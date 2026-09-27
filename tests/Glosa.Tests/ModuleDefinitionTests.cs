using Glosa.Core.Emulation;

namespace Glosa.Tests;

public class ModuleDefinitionTests
{
    /// <summary>
    /// Bounded by anything but a letter or a digit, as the shipped patterns are, except SC-88's
    /// bare 88, which has no bounds at all.
    /// </summary>
    private static ModuleDefinition Define() => ModuleDefinition.Parse("""
        modules:
          - name: THRU
          - name: MU2000
            patterns: ['(?<![0-9A-Z])MU2000(?![0-9A-Z])', '(?<![0-9A-Z])MU2K(?![0-9A-Z])']
          - name: SC-8850
            patterns: ['(?<![0-9A-Z])(SC-?)?88[25]0(?![0-9A-Z])']
          - name: SC-88PRO
            patterns: ['(?<![0-9A-Z])(SC-?)?88 ?PRO(?![0-9A-Z])']
          - name: SC-88
            patterns: ['88']
          - name: SC-55mk2
            patterns: ['(?<![0-9A-Z])(SC-?)?55 ?MK ?(2|II)(?![0-9A-Z])']
          - name: GM
            fallback: true
            patterns: ['(?<![0-9A-Z])GM(?![0-9A-Z])']
          - name: GS
            fallback: true
            patterns: ['(?<![0-9A-Z])GS(?![0-9A-Z])']
        """);

    private static string? Found(string text, MatchPosition position = MatchPosition.First)
        => Define().Scan(text, position)?.Module;

    [Fact]
    public void ListsTheModulesInTheirOrder()
        => Assert.Equal(
            ["THRU", "MU2000", "SC-8850", "SC-88PRO", "SC-88", "SC-55mk2", "GM", "GS"],
            Define().Modules);

    [Fact]
    public void APatternIsBoundedOnlyWhereItSaysSo()
    {
        Assert.Equal("MU2000", Found("for MU2000"));
        Assert.Equal("MU2000", Found("for-MU2000"));
        Assert.Equal("MU2000", Found("MU2000用"));
        Assert.Null(Found("MU20000"));
        Assert.Null(Found("XMU2000"));
        // SC-88's pattern says nothing about what is around it.
        Assert.Equal("SC-88", Found("88鍵"));
        Assert.Equal("SC-88", Found("1988"));
    }

    [Fact]
    public void APatternListedFirstTakesTheCharactersItMatched()
    {
        // SC-88's 88 is inside SC-8850's match, and SC-8850 is listed first.
        Assert.Equal("SC-8850", Found("cool 8850 song"));
        Assert.Equal("SC-8850", Found("cool SC-8850 song", MatchPosition.Last));
        Assert.Equal("SC-88PRO", Found("88 PRO"));
        Assert.Equal("SC-88PRO", Found("88 PRO", MatchPosition.Last));
    }

    [Fact]
    public void AMatchThatOverlapsNothingAboveItStands()
    {
        Assert.Equal("SC-8850", Found("8850 and 88"));
        Assert.Equal("SC-88", Found("8850 and 88", MatchPosition.Last));
    }

    [Fact]
    public void FoldsFullWidthBeforeMatching()
    {
        Assert.Equal("SC-88PRO", Found("ＳＣ－８８Ｐｒｏ用"));
        Assert.Equal("SC-88PRO", Found("ＳＣ−８８ＰＲＯ"));
        Assert.Equal("SC-55mk2", Found("SC-55mkⅡ"));
    }

    [Fact]
    public void IgnoresCase()
        => Assert.Equal("MU2000", Found("made for mu2k"));

    [Fact]
    public void APatternMaySpanASpace()
        => Assert.Equal("SC-88PRO", Found("SC-88 Pro"));

    [Fact]
    public void TheModelNamedFirstWins()
    {
        Assert.Equal("MU2000", Found("MU2000 or SC-8850"));
        Assert.Equal("SC-8850", Found("SC-8850 or MU2000"));
    }

    [Fact]
    public void OrTheModelNamedLast()
    {
        Assert.Equal("SC-8850", Found("MU2000 or SC-8850", MatchPosition.Last));
        Assert.Equal("MU2000", Found("SC-8850 or MU2000", MatchPosition.Last));
    }

    [Fact]
    public void AStandardOnlyCountsWhenNoModelIsNamed()
    {
        // Named first, and still not the answer: a model is named after it.
        Assert.Equal("SC-88PRO", Found("GM/GS SC-88Pro"));
        Assert.Equal("SC-88PRO", Found("SC-88Pro GM/GS", MatchPosition.Last));
        Assert.Equal("GM", Found("GM song"));
    }

    [Fact]
    public void AmongStandardsTheListDecides()
    {
        // GM is listed before GS, so it wins wherever the two are in the text.
        Assert.Equal("GM", Found("GM/GS"));
        Assert.Equal("GM", Found("GS/GM"));
        Assert.Equal("GM", Found("GM/GS", MatchPosition.Last));
    }

    [Fact]
    public void TheReasonIsTheMatchThatDecided()
    {
        ModuleDefinition define = ModuleDefinition.Parse("""
            modules:
              - name: MU2000
                patterns: ['MU2000', 'MU2K']
              - name: GS
                fallback: true
                patterns: ['GS']
            """);

        Assert.Equal("MU2K", define.Scan("MU2K and MU2000")?.Matched);
        Assert.Equal("MU2000", define.Scan("MU2K and MU2000", MatchPosition.Last)?.Matched);
        Assert.Equal("gs", define.Scan("gs and GS")?.Matched);
        Assert.Equal("GS", define.Scan("gs and GS", MatchPosition.Last)?.Matched);
    }

    [Fact]
    public void LinesStartAndEndWhereTheLineBreaksAre()
    {
        ModuleDefinition define = ModuleDefinition.Parse("""
            modules:
              - name: MU2000
                patterns: ['^MU2000$']
            """);

        Assert.Equal("MU2000", define.Scan("song\nMU2000\nreadme")?.Module);
        // A document from MS-DOS ends its lines with CR LF.
        Assert.Equal("MU2000", define.Scan("readme\r\nMU2000\r\nend")?.Module);
        Assert.Null(define.Scan("for MU2000"));
    }

    [Fact]
    public void ABrokenPatternIsReportedAndLeftOut()
    {
        ModuleDefinition define = ModuleDefinition.Parse("""
            modules:
              - name: XG
                patterns: ['XG(', 'XG']
            """);

        Assert.Single(define.Problems);
        Assert.Equal("XG", define.Scan("an XG song")?.Module);
    }

    [Fact]
    public void APatternThatMatchesNothingAtAllIsNoAnswer()
    {
        ModuleDefinition define = ModuleDefinition.Parse("""
            modules:
              - name: GM
                patterns: ['x*']
            """);

        Assert.Null(define.Scan("a song"));
    }

    [Fact]
    public void AModuleSaysHowItIsReset()
    {
        ModuleDefinition define = ModuleDefinition.Parse("""
            modules:
              - name: THRU
              - name: SC-88PRO
                initializeType: sc88
              - name: X
                initializeType: nonsense
            """);

        Assert.Equal("SC88", define.InitializeTypeOf("SC-88PRO"));
        Assert.Equal("SC88", define.InitializeTypeOf("sc-88pro"));
        Assert.Null(define.InitializeTypeOf("THRU"));
        Assert.Null(define.InitializeTypeOf("X"));
        Assert.Null(define.InitializeTypeOf("MU2000"));
        Assert.Single(define.Problems);
    }

    [Fact]
    public void AnEmptyFileDetectsNothing()
    {
        ModuleDefinition define = ModuleDefinition.Parse("");

        Assert.Empty(define.Modules);
        Assert.Null(define.Scan("SC-88PRO"));
    }

    [Fact]
    public void TheTitleIsSearchedBeforeTheFileNameAndTheFileNameBeforeTheFolder()
    {
        Assert.Equal("SC-8850", ModuleDetector.Detect(
            Define(), Path.Combine("songs", "MU2000.mid"), title: "SC-8850 version").Module);
        Assert.Equal("MU2000", ModuleDetector.Detect(
            Define(), Path.Combine("SC-8850", "MU2000.mid")).Module);
    }

    [Fact]
    public void TheSourcesAreSearchedInTheOrderGiven()
    {
        var fileNameFirst = new NameDetection(
            [DetectionSource.FileName, DetectionSource.Title], MatchPosition.First);

        Assert.Equal("MU2000", ModuleDetector.Detect(
            Define(), Path.Combine("songs", "MU2000.mid"), title: "SC-8850 version",
            how: fileNameFirst).Module);
    }

    [Fact]
    public void TheLastMatchCanWinInstead()
    {
        var last = NameDetection.Default with { Position = MatchPosition.Last };

        Assert.Equal("MU2000", ModuleDetector.Detect(
            Define(), Path.Combine("songs", "MU2000.mid"), title: "SC-8850 version",
            how: last).Module);
    }

    [Fact]
    public void ASourceLeftOutIsNotSearched()
    {
        var fileNameOnly = new NameDetection([DetectionSource.FileName], MatchPosition.First);

        Assert.Equal(ModuleSource.Default, ModuleDetector.Detect(
            Define(), Path.Combine("MU2000", "song.mid"), title: "MU2000",
            how: fileNameOnly).Source);
    }

    [Fact]
    public void TheDocumentIsLeftOutUnlessAskedFor()
    {
        string path = Path.Combine("songs", "song.mid");

        Assert.Equal(ModuleSource.Default,
                     ModuleDetector.Detect(Define(), path, document: "MU2000").Source);
        Assert.Equal("MU2000", ModuleDetector.Detect(
            Define(), path, document: "MU2000",
            how: new NameDetection([DetectionSource.Document], MatchPosition.First)).Module);
    }

    [Fact]
    public void OnlySoMuchOfTheDocumentIsRead()
    {
        var documentOnly = new NameDetection([DetectionSource.Document], MatchPosition.First);
        string path = Path.Combine("songs", "song.mid");
        string reaches = new string('x', ModuleDefinition.DefaultDocumentLength - 7) + " MU2000";
        string overruns = new string('x', ModuleDefinition.DefaultDocumentLength - 6) + " MU2000";

        Assert.Equal("MU2000", ModuleDetector.Detect(
            Define(), path, document: reaches, how: documentOnly).Module);
        Assert.Equal(ModuleSource.Default, ModuleDetector.Detect(
            Define(), path, document: overruns, how: documentOnly).Source);
    }

    [Fact]
    public void TheFileSaysHowMuchOfTheDocumentIsRead()
    {
        ModuleDefinition define = ModuleDefinition.Parse("""
            modules:
              - name: MU2000
                patterns: ['MU2000']
            nameDetection:
              maxDocumentLength: 10
            """);
        var documentOnly = new NameDetection([DetectionSource.Document], MatchPosition.First);
        string path = Path.Combine("songs", "song.mid");

        Assert.Equal(10, define.DocumentLength);
        Assert.Equal("MU2000", ModuleDetector.Detect(
            define, path, document: "for MU2000", how: documentOnly).Module);
        Assert.Equal(ModuleSource.Default, ModuleDetector.Detect(
            define, path, document: "made for MU2000", how: documentOnly).Source);
    }

    [Fact]
    public void AMatchDoesNotRunFromOneSourceIntoTheNext()
    {
        ModuleDefinition define = ModuleDefinition.Parse("""
            modules:
              - name: MU2000
                patterns: ['MU.*2000']
            """);

        Assert.Equal(ModuleSource.Default, ModuleDetector.Detect(
            define, Path.Combine("songs", "MU.mid"), title: "2000").Source);
    }

    [Fact]
    public void TheHomeFolderIsTakenOffTheFront()
    {
        string home = Path.Combine(Path.GetTempPath(), "home");

        Assert.Equal(Path.Combine("MIDI", "SC-88Pro"),
                     NameDetection.Folder(Path.Combine(home, "MIDI", "SC-88Pro", "a.mid"), home));
        Assert.Equal("", NameDetection.Folder(Path.Combine(home, "a.mid"), home));
        // Only a whole folder name: home2 is not under home.
        Assert.Equal(Path.Combine(home + "2", "MIDI"),
                     NameDetection.Folder(Path.Combine(home + "2", "MIDI", "a.mid"), home));
        Assert.Equal(Path.Combine(home, "MIDI"),
                     NameDetection.Folder(Path.Combine(home, "MIDI", "a.mid"), ""));
    }

    [Fact]
    public void TheFolderOfASongInAnArchiveIsTheArchive()
    {
        string home = Path.Combine(Path.GetTempPath(), "home");
        string song = Path.Combine(home, "MIDI", "SC-88Pro.lzh", "SONG.MID");

        Assert.Equal(Path.Combine("MIDI", "SC-88Pro.lzh"), NameDetection.Folder(song, home));
    }

    [Fact]
    public void ThePartsGoALineEachInTheirOrder()
    {
        var all = new NameDetection(
            [DetectionSource.Document, DetectionSource.Title, DetectionSource.FolderPath,
             DetectionSource.FileName],
            MatchPosition.First);
        string home = Path.Combine(Path.GetTempPath(), "home");

        Assert.Equal("doc\ntitle\nMIDI\nsong.mid",
                     all.Compose(Path.Combine(home, "MIDI", "song.mid"), "title", "doc", 100, home));
        // Nothing to say, no line for it.
        Assert.Equal("song.mid", all.Compose(Path.Combine(home, "song.mid"), "", "", 100, home));
    }

    [Fact]
    public void AModelInTheTitleBeatsAStandardInTheFileName()
    {
        ModuleDetection found = ModuleDetector.Detect(
            Define(), Path.Combine("songs", "GM song.mid"), title: "SC-8850 version");

        Assert.Equal("SC-8850", found.Module);
        Assert.Equal(ModuleSource.Keyword, found.Source);
    }

    [Fact]
    public void NothingFoundFallsBackToTheDefault()
    {
        ModuleDetection found = ModuleDetector.Detect(Define(), Path.Combine("songs", "song.mid"));

        Assert.Equal("THRU", found.Module);
        Assert.Equal(ModuleSource.Default, found.Source);
    }

    [Theory]
    [InlineData("modules:")]
    [InlineData("nameDetection:")]
    [InlineData("dataDetection:")]
    [InlineData("modules:\n  - name: SC-55\n    patterns:")]
    public void KeysLeftWithoutAValueReadAsTheirDefaults(string yaml)
        => Assert.Empty(ModuleDefinition.Parse(yaml).Problems);

    [Fact]
    public void AnEntryLeftEmptyIsAModuleWithoutAName()
    {
        ModuleDefinition define = ModuleDefinition.Parse("modules:\n  -\n  - name: SC-55");

        Assert.Equal(["SC-55"], define.Modules);
        Assert.Single(define.Problems);
    }
}

/// <summary>The data rules: what the song's own messages say when its words say nothing.</summary>
public class ModuleDataTests
{
    private static readonly byte[] GsReset = [0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7];
    private static readonly byte[] XgOn = [0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7];
    private static readonly byte[] Mt32 = [0x41, 0x10, 0x16, 0x12, 0x7F, 0x00, 0x00, 0x01, 0x00, 0xF7];

    private static ModuleDefinition Define(int messages = 1000) => ModuleDefinition.Parse($"""
        modules:
          - name: SC-88PRO
            patterns: ['(SC-?)?88 ?PRO']
          - name: CM-64
          - name: GS
            fallback: true
          - name: XG
            fallback: true
        dataDetection:
          maxMessages: {messages}
        """);

    /// <summary>A song of some notes, then the exclusive messages given, in that order.</summary>
    private static Glosa.Core.Smf.MidiSequence Song(int notes, params byte[][] exclusives)
    {
        var track = new TrackBuilder();
        for (int i = 0; i < notes; i++) track.Short(0, 0x90, 60, 100);
        foreach (byte[] body in exclusives) track.SysEx(10, body);
        return Glosa.Core.Smf.SmfReader.Read(new SmfBuilder(480).Track(track.End(0)).Build());
    }

    [Fact]
    public void AGsResetIsGs()
    {
        ModuleDetection? found = Define().ScanData(Song(0, GsReset));

        Assert.Equal("GS", found?.Module);
        Assert.Equal(ModuleSource.Data, found?.Source);
        Assert.Equal("F0 41 10 42 12 40 00 7F …", found?.Matched);
    }

    [Fact]
    public void AnyDeviceIdMatches()
        => Assert.Equal("GS", Define().ScanData(
            Song(0, [0x41, 0x11, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7]))?.Module);

    [Fact]
    public void AnMt32MessageIsTheCmFamily()
        => Assert.Equal("CM-64", Define().ScanData(Song(0, Mt32))?.Module);

    [Fact]
    public void TheRuleListedFirstWinsWhereverItsMessageIs()
        => Assert.Equal("XG", Define().ScanData(Song(0, GsReset, Mt32, XgOn))?.Module);

    [Fact]
    public void OnlyTheFirstSoManyMessagesAreLookedAt()
    {
        Assert.Null(Define(messages: 5).ScanData(Song(10, GsReset)));
        Assert.Equal("GS", Define(messages: 11).ScanData(Song(10, GsReset))?.Module);
    }

    /// <summary>A song read only for its opening is answered as the whole song is.</summary>
    [Theory]
    [InlineData(5, 10)]
    [InlineData(11, 10)]
    [InlineData(1000, 0)]
    public void TheOpeningAloneGivesTheSameAnswer(int messages, int notes)
    {
        ModuleDefinition define = Define(messages);
        byte[] smf = new SmfBuilder(480).Track(t =>
        {
            for (int i = 0; i < notes; i++) t.Short(0, 0x90, 60, 100);
            t.SysEx(10, GsReset).SysEx(10, XgOn).End(0);
        }).Build();

        Glosa.Core.Smf.SongSummary song = Glosa.Core.Smf.SmfReader.ReadSummary(smf, define.DataMessages);

        Assert.Equal(define.ScanData(Glosa.Core.Smf.SmfReader.Read(smf))?.Module,
                     define.ScanData(song)?.Module);
    }

    [Fact]
    public void ASongThatSetsNothingUpIsNoAnswer()
        => Assert.Null(Define().ScanData(Song(3)));

    [Fact]
    public void TheWordsComeBeforeTheData()
    {
        ModuleDetection found = ModuleDetector.Detect(
            Define(), Path.Combine("songs", "song.mid"), title: "SC-88Pro version", sequence: Song(0, XgOn));

        Assert.Equal("SC-88PRO", found.Module);
        Assert.Equal(ModuleSource.Keyword, found.Source);
    }

    [Fact]
    public void TheDataDecidesWhenTheWordsSayNothing()
    {
        ModuleDetection found = ModuleDetector.Detect(
            Define(), Path.Combine("songs", "song.mid"), sequence: Song(0, XgOn));

        Assert.Equal("XG", found.Module);
        Assert.Equal(ModuleSource.Data, found.Source);
    }

    /// <summary>The rules built in, in the order that settles them.</summary>
    [Theory]
    [InlineData("GS")]
    [InlineData("XG")]
    [InlineData("CM-64")]
    public void TheShippedRulesKnowTheFamilies(string module)
    {
        ModuleDefinition shipped = ModuleDefinition.Load(
            Path.Combine(AppContext.BaseDirectory, ModuleDefinition.FileName));
        byte[] message = module switch { "GS" => GsReset, "XG" => XgOn, _ => Mt32 };

        Assert.Equal(module, shipped.ScanData(Song(0, message))?.Module);
        Assert.Equal("XG", shipped.ScanData(Song(0, Mt32, GsReset, XgOn))?.Module);
    }

    /// <summary>
    /// The standards named in the words are left to the song's data, and a song that says
    /// only GM, in its words or its messages, is THRU.
    /// </summary>
    [Theory]
    [InlineData("GS対応", "GS")]
    [InlineData("XG/GS/GM対応", "XG")]
    [InlineData("GM対応", "THRU")]
    public void TheShippedFileLeavesTheStandardsToTheData(string title, string module)
    {
        ModuleDefinition shipped = ModuleDefinition.Load(
            Path.Combine(AppContext.BaseDirectory, ModuleDefinition.FileName));
        byte[] gmOn = [0x7E, 0x7F, 0x09, 0x01, 0xF7];
        byte[] message = module switch { "GS" => GsReset, "XG" => XgOn, _ => gmOn };

        ModuleDetection found = ModuleDetector.Detect(
            shipped, Path.Combine("songs", "song.mid"), title, sequence: Song(0, gmOn, message));

        Assert.Equal(module, found.Module);
        Assert.Equal(module == "THRU" ? ModuleSource.Default : ModuleSource.Data, found.Source);
    }

    [Fact]
    public void WithoutTheSongOnlyTheWordsCount()
        => Assert.Equal(ModuleSource.Default,
                        ModuleDetector.Detect(Define(), Path.Combine("songs", "song.mid")).Source);
}

/// <summary>The <c>define.yaml</c> the player ships with.</summary>
public class ShippedDefinitionTests
{
    private static readonly ModuleDefinition Shipped =
        ModuleDefinition.Load(Path.Combine(AppContext.BaseDirectory, ModuleDefinition.FileName));

    [Fact]
    public void ReadsWithoutProblems() => Assert.Empty(Shipped.Problems);

    /// <summary>Every module but THRU says how it is reset.</summary>
    [Theory]
    [InlineData("SC-8850", "SC88")]
    [InlineData("SC-88PRO", "SC88")]
    [InlineData("SC-88", "SC88")]
    [InlineData("SC-55mk2", "GS")]
    [InlineData("SC-55", "GS")]
    [InlineData("CM-64", "MT32")]
    [InlineData("MU2000", "XG")]
    [InlineData("MU50", "XG")]
    [InlineData("SB32", "GM")]
    [InlineData("GS", "GS")]
    [InlineData("THRU", null)]
    public void TheShippedModulesSayHowTheyAreReset(string module, string? type)
        => Assert.Equal(type, Shipped.InitializeTypeOf(module));

    [Fact]
    public void OnlyThruIsNotReset()
        => Assert.Equal(["THRU"], Shipped.Modules.Where(m => Shipped.InitializeTypeOf(m) is null));

    /// <summary>The <c>[tdfindex]</c>, and GS, which the DEF names as a group.</summary>
    [Fact]
    public void ListsTheModulesOfTheTdfIndexAndGs()
        => Assert.Equal(
            ["05R/W", "CM-64", "GM", "GS", "MU100", "MU1000", "MU128", "MU2000", "MU50", "MU80",
             "MU90", "SB32", "SC-33", "SC-55", "SC-55mk2", "SC-88", "SC-8850", "SC-88PRO",
             "TG300B", "THRU", "X5D", "XG"],
            Shipped.Modules.Order(StringComparer.Ordinal));

    /// <summary>
    /// The broad standards come last, in the order that settles them among themselves: the
    /// narrowest first, since a song that names it keeps to it.
    /// </summary>
    [Fact]
    public void TheBroadStandardsComeLast()
        => Assert.Equal(["GM", "GS", "XG"], Shipped.Modules.TakeLast(3));

    /// <summary>Spellings the DEF's <c>[keyword]</c> lists, and where it sends them.</summary>
    [Theory]
    [InlineData("SC8850", "SC-8850")]
    [InlineData("ＳＣ−８８５０", "SC-8850")]
    [InlineData("8820", "SC-8850")]
    [InlineData("SC-88Pro", "SC-88PRO")]
    [InlineData("８８Ｐｒｏ", "SC-88PRO")]
    [InlineData("８Ｐ", "SC-88PRO")]
    [InlineData("SC-88VL", "SC-88")]
    [InlineData("88Map", "SC-88")]
    [InlineData("88", "SC-88")]
    [InlineData("ＳＣ−１５５", "SC-55")]
    [InlineData("CM500", "SC-55")]
    [InlineData("55", "SC-55")]
    [InlineData("SC-55mkⅡ", "SC-55mk2")]
    [InlineData("SC55ST", "SC-55mk2")]
    [InlineData("ＭＴ−３２", "CM-64")]
    [InlineData("CM-32L", "CM-64")]
    [InlineData("05R/W", "05R/W")]
    [InlineData("Ｘ５ＤＲ", "X5D")]
    [InlineData("MU1k", "MU1000")]
    [InlineData("ＭＵ２０００", "MU2000")]
    [InlineData("Blaster", "SB32")]
    public void FindsWhatTheKeywordSectionFinds(string spelling, string module)
        => Assert.Equal(module, Shipped.Scan($"曲名 {spelling} 用")?.Module);

    /// <summary>What the DEF's <c>[keyword]</c> answers that the list does not name.</summary>
    [Theory]
    [InlineData("SC-8820", "SC-8850")]
    [InlineData("SC-88VL", "SC-88")]
    [InlineData("SC-55ST", "SC-55mk2")]
    [InlineData("CM-300", "SC-55")]
    [InlineData("CM-32", "CM-64")]
    [InlineData("CM-32L", "CM-64")]
    [InlineData("MT-32", "CM-64")]
    [InlineData("sc-88pro", "SC-88PRO")]
    [InlineData("SC-88PRO", "SC-88PRO")]
    [InlineData("GS", "GS")]
    public void ANameIsPutIntoTheListsTerms(string name, string listed)
        => Assert.Equal(listed, Shipped.ListName(name));

    [Theory]
    [InlineData("SC-88Pro用.mid", "SC-88PRO")]
    [InlineData("（ＳＣ−８８Ｐｒｏ対応）", "SC-88PRO")]
    [InlineData("GM/GS SC-88Pro対応", "SC-88PRO")]
    [InlineData("MU2000/SC-88Pro対応", "MU2000")]
    [InlineData("SC-88Pro/MU2000対応", "SC-88PRO")]
    [InlineData("XG MU2000用", "MU2000")]
    [InlineData("SC-88Pro向け", "SC-88PRO")]
    [InlineData("for-MU2000.mid", "MU2000")]
    [InlineData("SC-55mkII_GS", "SC-55mk2")]
    [InlineData("88用", "SC-88")]
    [InlineData("55対応", "SC-55")]
    [InlineData("88鍵", null)]
    [InlineData("1988年", null)]
    [InlineData("第55回", null)]
    [InlineData("USB", null)]
    [InlineData("MU500", null)]
    [InlineData("XGA", null)]
    [InlineData("PGM", null)]
    public void TheShippedPatternsDrawTheirOwnBounds(string text, string? module)
        => Assert.Equal(module, Shipped.Scan(text)?.Module);

    /// <summary>A version mark may follow the model straight on; other letters still may not.</summary>
    [Theory]
    [InlineData("SC-88ProVer", "SC-88PRO")]
    [InlineData("SC-88ProVer2.mid", "SC-88PRO")]
    [InlineData("SC-88Prov1.1", "SC-88PRO")]
    [InlineData("SC-88ver", "SC-88")]
    [InlineData("SC-8850Ver", "SC-8850")]
    [InlineData("SC-55mkIIVer", "SC-55mk2")]
    [InlineData("MU2000Ver2", "MU2000")]
    [InlineData("ＭＵ１２８Ｖｅｒ", "MU128")]
    [InlineData("SC-88Proz", null)]
    [InlineData("MU2000X", null)]
    public void AVersionMarkMayFollowTheModel(string text, string? module)
        => Assert.Equal(module, Shipped.Scan(text)?.Module);

    /// <summary>88P is short for the 88Pro, but only where the P ends the word.</summary>
    [Theory]
    [InlineData("SC-88P", "SC-88PRO")]
    [InlineData("88P対応", "SC-88PRO")]
    [InlineData("SC-88 P", "SC-88PRO")]
    [InlineData("88 Piano", "SC-88")]
    public void EightyEightPIsThe88Pro(string text, string? module)
        => Assert.Equal(module, Shipped.Scan(text)?.Module);

    /// <summary>The longer spellings are listed first, so they keep what they matched.</summary>
    [Theory]
    [InlineData("8850 88", MatchPosition.Last, "SC-88")]
    [InlineData("SC-8850", MatchPosition.Last, "SC-8850")]
    [InlineData("SC-88 Pro", MatchPosition.Last, "SC-88PRO")]
    [InlineData("SC-55mkII", MatchPosition.Last, "SC-55mk2")]
    [InlineData("88 MAP", MatchPosition.Last, "SC-88")]
    [InlineData("MU2000/SC-88Pro対応", MatchPosition.Last, "SC-88PRO")]
    public void TheLongerSpellingsComeFirst(string text, MatchPosition position, string module)
        => Assert.Equal(module, Shipped.Scan(text, position)?.Module);
}
