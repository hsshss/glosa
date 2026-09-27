using Glosa.Core.Definition;
using Glosa.Core.Emulation;

namespace Glosa.Tests;

public class EmulationResolverTests
{
    /// <summary>
    /// Made-up modules: Vela200 sits under XG, and three Kite models under GS, two of them
    /// by way of Kite-1.
    /// </summary>
    private const string Groups = """
        [group]
        Kite-1=GS
        Kite-2=Kite-1
        Kite-3=Kite-1
        Vela200=XG
        """;

    [Fact]
    public void FindsAnExactPair()
    {
        DefDocument def = DefDocument.ParseText($"""
            {Groups}

            [convindex]
            Vela200:Kite-1=Direct
            """);

        ConvIndexResult r = EmulationResolver.Resolve(def, "Vela200", "Kite-1");

        Assert.Equal("Vela200:Kite-1", r.Key);
        Assert.Equal("Direct", r.Commands);
    }

    [Fact]
    public void ClimbsTheTargetThroughGroup()
    {
        DefDocument def = DefDocument.ParseText($"""
            {Groups}

            [convindex]
            Vela200:GS=ViaGroup
            """);

        // Kite-2 -> Kite-1 -> GS
        ConvIndexResult r = EmulationResolver.Resolve(def, "Vela200", "Kite-2");

        Assert.Equal("Vela200:GS", r.Key);
        Assert.Equal("ViaGroup", r.Commands);
    }

    [Fact]
    public void ResetsTheTargetAfterClimbingTheUseModule()
    {
        // The only case that tells the two readings apart: if the target were left at GS
        // after the inner walk, XG:GS would win. Resetting it to Kite-3 picks the
        // specific rule instead.
        DefDocument def = DefDocument.ParseText($"""
            {Groups}

            [convindex]
            XG:Kite-3=Specific
            XG:GS=General
            """);

        ConvIndexResult r = EmulationResolver.Resolve(def, "Vela200", "Kite-3");

        Assert.Equal("XG:Kite-3", r.Key);
        Assert.Equal("Specific", r.Commands);
    }

    [Fact]
    public void ResolvesAliasesBeforeLookingUp()
    {
        DefDocument def = DefDocument.ParseText("""
            [alias]
            Kite1=Kite-1

            [convindex]
            GM:Kite-1=Aliased
            """);

        Assert.Equal("Aliased", EmulationResolver.Resolve(def, "GM", "Kite1").Commands);
    }

    [Fact]
    public void ReportsNoMatch()
    {
        DefDocument def = DefDocument.ParseText("[convindex]\nA:B=X\n");
        ConvIndexResult r = EmulationResolver.Resolve(def, "Unknown", "Other");

        Assert.False(r.Found);
        Assert.Equal(string.Empty, r.Commands);
    }
}

public class MidiScriptTests
{
    private static DefSection Messages(string body)
        => DefDocument.ParseText($"[midimessage]\n{body}")["midimessage"]!;

    [Fact]
    public void ParsesHexAndDecimalBytesIntoOneMessage()
    {
        IReadOnlyList<ScriptAction> a =
            MidiScript.Expand("$F0 65 $F7", ScriptPort.A, Messages(""));

        ScriptAction only = Assert.Single(a);
        Assert.Equal(new byte[] { 0xF0, 65, 0xF7 }, only.Bytes);
    }

    /// <summary>
    /// Seven levels over a message, each splicing the one below twenty times: 20⁷ messages
    /// if nothing stopped it. <paramref name="bottom"/> is what the lowest level holds.
    /// </summary>
    internal static string Exploding(string bottom = "$90 $3C $40")
    {
        var lines = new List<string> { $"L0={bottom}" };
        for (int level = 1; level <= 7; level++)
            lines.Add($"L{level}=" + string.Join(" ", Enumerable.Repeat($"M:L{level - 1}", 20)));
        return string.Join("\n", lines);
    }

    [Fact]
    public void AScriptThatWouldGrowWithoutBoundIsCutShort()
    {
        int budget = MidiScript.MaxCost;
        IReadOnlyList<ScriptAction> a = MidiScript.Expand("M:L7", ScriptPort.A, Messages(Exploding()), ref budget);

        Assert.True(budget < 0);
        Assert.NotEmpty(a);
        Assert.True(a.Sum(MidiScript.Cost) <= MidiScript.MaxCost);
    }

    [Fact]
    public void SplicesOfNothingAreCountedToo()
    {
        // Nothing comes of it, but the walk alone would be 20⁷ splices.
        int budget = MidiScript.MaxCost;
        IReadOnlyList<ScriptAction> a = MidiScript.Expand("M:L7", ScriptPort.A, Messages(Exploding("W:0")), ref budget);

        Assert.True(budget < 0);
        Assert.Empty(a);
    }

    [Fact]
    public void AnOrdinaryScriptIsNotCut()
    {
        int budget = MidiScript.MaxCost;
        MidiScript.Expand("M:L2", ScriptPort.A, Messages(Exploding()), ref budget);

        // Three splices down, and 400 messages of three bytes at the bottom.
        Assert.Equal(MidiScript.MaxCost - (1 + 20 + 400) - 400 * 4, budget);
    }

    [Fact]
    public void SplicesNamedMessages()
    {
        DefSection m = Messages("ToGs=$F0 $41 $10 $42 $12 $40 $00 $7F $00 $41 $F7 W:150");
        IReadOnlyList<ScriptAction> a = MidiScript.Expand("M:ToGs", ScriptPort.A, m);

        Assert.Equal(2, a.Count);
        Assert.Equal(ScriptActionKind.Message, a[0].Kind);
        Assert.Equal(11, a[0].Bytes.Length);
        Assert.Equal(ScriptActionKind.Wait, a[1].Kind);
        Assert.Equal(150, a[1].WaitMs);
    }

    [Fact]
    public void EmitsAChannelBroadcastForTheCPrefix()
    {
        DefSection m = Messages("MapOne=$B0 $20 $01");
        IReadOnlyList<ScriptAction> a = MidiScript.Expand("C:MapOne", ScriptPort.A, m);

        ScriptAction only = Assert.Single(a);
        Assert.Equal(ScriptActionKind.ChannelBroadcast, only.Kind);
        Assert.Equal(new byte[] { 0xB0, 0x20, 0x01 }, only.Bytes);
    }

    [Fact]
    public void StopsAChannelBroadcastAtTheFirstNonByte()
    {
        // TMIDI's broadcast routine parses bytes only; anything else ends the message.
        DefSection m = Messages("X=$B0 $20 W:50 $01");
        IReadOnlyList<ScriptAction> a = MidiScript.Expand("C:X", ScriptPort.A, m);

        Assert.Equal(new byte[] { 0xB0, 0x20 }, Assert.Single(a).Bytes);
    }

    [Fact]
    public void EmitsResetActions()
    {
        IReadOnlyList<ScriptAction> a = MidiScript.Expand("R:2 W:200", ScriptPort.A, Messages(""));

        Assert.Equal(ScriptActionKind.Reset, a[0].Kind);
        Assert.Equal(2, a[0].ResetKind);
    }

    /// <summary>What an <c>R:n</c> sends on channel 1, in order.</summary>
    private static readonly uint[] PlainResetOnChannelOne =
    [
        0x0078B0, 0x0079B0,
        0x0065B0, 0x0064B0, 0x0206B0,
        0x0065B0, 0x0164B0, 0x4006B0, 0x0026B0,
        0x0065B0, 0x0264B0, 0x4006B0, 0x0026B0,
        0x7F64B0, 0x7F65B0,
        0x0001B0, 0x005DB0, 0x005BB0, 0x6407B0, 0x7F0BB0, 0x4000E0,
        0x400AB0, 0x0020B0, 0x0000B0,
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void APlainResetPutsEveryChannelBack(int kind)
    {
        uint[] sent = [.. MidiScript.ResetMessages(kind)];

        Assert.Equal(16 * PlainResetOnChannelOne.Length, sent.Length);
        Assert.Equal(PlainResetOnChannelOne, sent[..PlainResetOnChannelOne.Length]);
        // Channel 16 is the same, on its own channel.
        Assert.Equal(PlainResetOnChannelOne.Select(m => m | 0x0F),
                     sent[^PlainResetOnChannelOne.Length..]);
    }

    [Fact]
    public void TheCmResetSetsTheChannelsUpForCm32L()
    {
        uint[] sent = [.. MidiScript.ResetMessages(2)];
        uint[] first = [.. sent.Where(m => (m & 0x0F) == 0)];
        uint[] eleventh = [.. sent.Where(m => (m & 0x0F) == 10)];

        Assert.Contains(0x0C06B0u, first);                 // bend range an octave
        Assert.Contains(0x405BB0u, first);                 // reverb 64
        Assert.Equal([0x400AB0u, 0x0120B0u, 0x7F00B0u, 0x0000C0u], first[^4..]);
        // Channel 11: pan 64, bank 126, program 27.
        Assert.Equal([0x400ABAu, 0x0120BAu, 0x7E00BAu, 0x001BCAu], eleventh[^4..]);
    }

    [Fact]
    public void RunningAScriptSendsItsResets()
    {
        var sink = new RecordingSink();
        MidiScript.Run([ScriptAction.Reset(ScriptPort.B, 1)], sink);

        Assert.Equal(16 * PlainResetOnChannelOne.Length, sink.Short.Count);
        Assert.All(sink.Short, s => Assert.Equal(1, s.Port));
        Assert.Equal(0x0078B0u, sink.Short[0].Message);
    }

    private sealed class RecordingSink : Glosa.Core.Playback.IEventSink
    {
        public List<(int Port, uint Message)> Short { get; } = [];

        public void SendShort(int port, uint packedMessage) => Short.Add((port, packedMessage));

        public void SendLong(int port, ReadOnlySpan<byte> sysEx) { }
    }

    [Fact]
    public void AWaitIsHeldToItsLimit()
    {
        ScriptAction wait = Assert.Single(MidiScript.Expand("W:300000", ScriptPort.A, Messages("")));

        Assert.Equal(MidiScript.MaxWaitMs, wait.WaitMs);
    }

    [Fact]
    public void RunningAScriptSendsShortMessagesPackedAndSysExWhole()
    {
        var sink = new CapturingSink();
        MidiScript.Run(
        [
            ScriptAction.Message(ScriptPort.B, [0xC0, 0x05]),
            ScriptAction.Message(ScriptPort.A, [0xB0, 0x07, 0x64]),
            ScriptAction.Message(ScriptPort.C, [0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7]),
            ScriptAction.Message(ScriptPort.A, []),
        ], sink);

        Assert.Equal([(1, 0x05C0u), (0, 0x6407B0u)], sink.Shorts);
        (int port, byte[] data) = Assert.Single(sink.Longs);
        Assert.Equal(2, port);
        Assert.Equal(new byte[] { 0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7 }, data);
    }

    [Fact]
    public void RunningAScriptWaitsWhereItSays()
    {
        var sink = new CapturingSink();
        long sentAt = -1;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        MidiScript.Run(
        [
            ScriptAction.Message(ScriptPort.A, [0x90, 0x3C, 0x40]),
            ScriptAction.Wait(ScriptPort.A, 100),
            ScriptAction.Message(ScriptPort.A, [0x80, 0x3C, 0x00]),
        ], new ClockedSink(sink, () => sentAt = clock.ElapsedMilliseconds));

        Assert.Equal(2, sink.Shorts.Count);
        Assert.True(sentAt >= 90, $"the second message went out after {sentAt} ms");
    }

    /// <summary>Passes messages on, noting when the last one went.</summary>
    private sealed class ClockedSink(CapturingSink inner, Action sent) : Glosa.Core.Playback.IEventSink
    {
        public void SendShort(int port, uint packedMessage) { inner.SendShort(port, packedMessage); sent(); }

        public void SendLong(int port, ReadOnlySpan<byte> sysEx) { inner.SendLong(port, sysEx); sent(); }
    }

    [Fact]
    public void BreaksMessagesAtWaits()
    {
        IReadOnlyList<ScriptAction> a =
            MidiScript.Expand("$90 $40 W:50 $80 $40", ScriptPort.A, Messages(""));

        Assert.Equal(3, a.Count);
        Assert.Equal(new byte[] { 0x90, 0x40 }, a[0].Bytes);
        Assert.Equal(ScriptActionKind.Wait, a[1].Kind);
        Assert.Equal(new byte[] { 0x80, 0x40 }, a[2].Bytes);
    }

    [Fact]
    public void BreaksMessagesAtAnythingThatIsNotAByte()
    {
        // Only the space separates bytes. A tab, like any other stray character, ends the
        // message being built instead of joining the two runs.
        IReadOnlyList<ScriptAction> a =
            MidiScript.Expand("$90\t$40", ScriptPort.A, Messages(""));

        Assert.Equal(2, a.Count);
        Assert.Equal(new byte[] { 0x90 }, a[0].Bytes);
        Assert.Equal(new byte[] { 0x40 }, a[1].Bytes);
    }

    [Fact]
    public void RecognisesADirectiveFromItsFirstLetterAlone()
    {
        // Two characters are skipped without checking the second, so the colon is optional
        // and "W200" waits for nothing at all.
        IReadOnlyList<ScriptAction> a =
            MidiScript.Expand("R2 W200", ScriptPort.A, Messages(""));

        ScriptAction only = Assert.Single(a);
        Assert.Equal(ScriptActionKind.Reset, only.Kind);
        Assert.Equal(0, only.ResetKind);          // "R2" skips the '2' as well
    }

    [Fact]
    public void ResolvesANamedMessageAfterSkippingTwoCharacters()
    {
        // "Map" skips 'M' and 'a', so the name it looks up is "p".
        DefSection m = Messages("p=$B0 $20 $01");
        IReadOnlyList<ScriptAction> a = MidiScript.Expand("Map", ScriptPort.A, m);

        Assert.Equal(new byte[] { 0xB0, 0x20, 0x01 }, Assert.Single(a).Bytes);
    }
}

public class PatchEntryTests
{
    [Fact]
    public void ParsesLsbMsbPcInThatOrder()
    {
        PatchEntry e = PatchEntry.Parse("1:24:5", "2:0:6", out bool readable);

        Assert.True(readable);
        Assert.Equal(1, e.SrcLsb);
        Assert.Equal(24, e.SrcMsb);
        Assert.Equal(5, e.SrcPc);
        Assert.Equal(2, e.DstLsb);
        Assert.Equal(0, e.DstMsb);
        Assert.Equal(6, e.DstPc);
        Assert.False(e.SrcDrum);
    }

    [Fact]
    public void TurnsAsteriskIntoTheWildcard()
    {
        PatchEntry e = PatchEntry.Parse("*:24:0", "0:0:*", out bool readable);

        Assert.True(readable);
        Assert.Equal(PatchEntry.Wildcard, e.SrcLsb);
        Assert.Equal(PatchEntry.Wildcard, e.DstPc);
    }

    [Fact]
    public void RecognisesTheDrumPrefix()
    {
        PatchEntry e = PatchEntry.Parse("DM:*:*:36", "*:*:35", out bool readable);

        Assert.True(readable);
        Assert.True(e.SrcDrum);
        Assert.True(e.DstDrum);
        Assert.Equal(36, e.SrcPc);
        Assert.Equal(35, e.DstPc);
    }

    [Fact]
    public void TakesTheDrumPrefixWithoutItsColon()
    {
        // Two letters are tested and three characters skipped, so the third is never looked
        // at.
        PatchEntry e = PatchEntry.Parse("dm 0:1:2", "3:4:5", out bool readable);
        Assert.True(e.SrcDrum);
        Assert.Equal(0, e.SrcLsb);
        Assert.Equal(1, e.SrcMsb);
        Assert.Equal(2, e.SrcPc);
        Assert.False(readable);          // the colon is missing, so it is worth a warning
    }

    [Fact]
    public void RegistersALineThatIsNotARuleAsAllZeroes()
    {
        // TMIDI validates nothing, so a stray line in a patch section becomes a rule.
        PatchEntry e = PatchEntry.Parse("comment", "テスト用", out bool readable);

        Assert.False(readable);
        Assert.Equal(0, e.SrcLsb);
        Assert.Equal(0, e.SrcMsb);
        Assert.Equal(0, e.SrcPc);
        Assert.Equal(0, e.DstLsb);
        Assert.Equal(0, e.DstMsb);
        Assert.Equal(0, e.DstPc);
    }

    [Fact]
    public void ReadsAFieldWithAtoiAndStopsAtTheFirstNonDigit()
    {
        PatchEntry e = PatchEntry.Parse("12ab:3", "4", out bool readable);

        Assert.False(readable);          // two of the six fields are missing
        Assert.Equal(12, e.SrcLsb);
        Assert.Equal(3, e.SrcMsb);
        Assert.Equal(0, e.SrcPc);
        Assert.Equal(4, e.DstLsb);
    }

    [Fact]
    public void LeavesATrailingCommentAlone()
    {
        // The shipped DEF writes the instrument name after the last field, and atoi stops
        // there. That is the normal spelling, not something to warn about.
        PatchEntry e = PatchEntry.Parse("*:*:8", "*:*:40	Test Kit", out bool readable);

        Assert.True(readable);
        Assert.Equal(40, e.DstPc);

        e = PatchEntry.Parse("0:40:12", "0:8:21 ; Test Lead", out readable);

        Assert.True(readable);
        Assert.Equal(21, e.DstPc);
    }

    [Fact]
    public void AppendsSectionsInOrder()
    {
        DefDocument def = DefDocument.ParseText("""
            [first]
            0:0:1=0:0:2

            [second]
            0:0:3=0:0:4
            """);

        var set = new PatchMapSet();
        Assert.Equal(1, set.AddMelodic(def["first"]).Added);
        Assert.Equal(1, set.AddMelodic(def["second"]).Added);

        Assert.Equal(2, set.Melodic.Count);
        Assert.Equal(1, set.Melodic[0].SrcPc);
        Assert.Equal(3, set.Melodic[1].SrcPc);
    }
}

public class EmulationBuilderTests
{
    [Fact]
    public void AccumulatesFlagsAcrossSections()
    {
        // Each key defaults to its current value, so a later section adds to an earlier one.
        DefDocument def = DefDocument.ParseText("""
            [convindex]
            A:B=First Second

            [First]
            DisableBankSelectLSB=1

            [Second]
            GSToXGEmu=1
            """);

        EmulationSettings s = new EmulationBuilder(def).Build("A", "B").Settings;

        Assert.True(s.DisableBankSelectLsb);
        Assert.True(s.GsToXgEmu);
    }

    [Fact]
    public void SaysWhenTheInitialisationWasCutShort()
    {
        static DefDocument Def(string script) => DefDocument.ParseText($"""
            [convindex]
            A:B=Only

            [Only]
            MIDI_A={script}

            [midimessage]
            {MidiScriptTests.Exploding().Replace("\n", "\n            ")}
            """);

        var steps = new BuildSteps();
        Assert.True(new EmulationBuilder(Def("M:L7")).Build("A", "B", observer: steps).InitCut);
        Assert.NotEmpty(steps.Warnings);
        Assert.False(new EmulationBuilder(Def("M:L1")).Build("A", "B").InitCut);
    }

    [Fact]
    public void ReadsTheInheritedBaseSectionFirst()
    {
        DefDocument def = DefDocument.ParseText("""
            [convindex]
            A:B=Derived

            [Base]
            MapSelect=2

            [Derived]
            BaseSection=Base
            DisableExclusive=1
            """);

        var steps = new BuildSteps();
        EmulationSetup setup = new EmulationBuilder(def).Build("A", "B", observer: steps);

        Assert.Equal(2, setup.Settings.MapSelect);
        Assert.True(setup.Settings.DisableExclusive);
        Assert.Equal(["Derived", "Base"], steps.Sections);
    }

    [Fact]
    public void AlwaysAppliesTheFilterSection()
    {
        DefDocument def = DefDocument.ParseText("""
            [convindex]
            A:B=Only

            [Only]
            comment=c
            """);

        var steps = new BuildSteps();
        new EmulationBuilder(def).Build("A", "B", filterSection: "NONE", steps);

        // "NONE" names no section, yet TMIDI still reads it — hence the log line.
        Assert.Contains("NONE", steps.Sections);
    }

    [Fact]
    public void WarnsAboutAPatchLineThatIsNotARule()
    {
        DefDocument def = DefDocument.ParseText("""
            [convindex]
            A:B=Only

            [Only]
            PatchMap=MyPatch

            [MyPatch]
            comment=テスト用
            0:0:10=0:0:20
            """);

        var steps = new BuildSteps();
        EmulationSetup setup = new EmulationBuilder(def).Build("A", "B", observer: steps);

        // The unreadable line is still a rule, as in TMIDI.
        Assert.Equal(2, setup.Patches.Melodic.Count);
        // In the language the tests run in.
        Assert.Contains(string.Format(Glosa.Core.Strings.RuleUnreadable, "comment"), steps.Warnings);
    }

    [Fact]
    public void IgnoresSectionsNamedForTheSmfKnife()
    {
        DefDocument def = DefDocument.ParseText("""
            [convindex]
            A:B=SMFK:Cut Real

            [SMFK:Cut]
            DisableExclusive=1

            [Real]
            comment=c
            """);

        var steps = new BuildSteps();
        EmulationSetup setup = new EmulationBuilder(def).Build("A", "B", observer: steps);

        Assert.False(setup.Settings.DisableExclusive);
        Assert.Equal("Real", Assert.Single(steps.Sections));
    }

    [Fact]
    public void StopsParsingCommandsAtAComment()
    {
        Assert.Equal(["One", "Two"],
            EmulationBuilder.SplitCommands("One Two ;Three Four"));
    }

    [Fact]
    public void ProducesInitActionsInOrder()
    {
        DefDocument def = DefDocument.ParseText("""
            [midimessage]
            GmSystemOn=$F0 $7E $7F $09 $01 $F7 W:120

            [convindex]
            A:B=Init

            [Init]
            MIDI_A=M:GmSystemOn
            """);

        EmulationSetup setup = new EmulationBuilder(def).Build("A", "B");

        Assert.Equal(2, setup.InitActions.Count);
        Assert.Equal(ScriptPort.A, setup.InitActions[0].Port);
        Assert.Equal(new byte[] { 0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7 },
                     setup.InitActions[0].Bytes);
        Assert.Equal(120, setup.InitActions[1].WaitMs);
    }

}

public class PortFanOutTests
{
    [Fact]
    public void MidiKeyReachesEveryPort()
    {
        DefDocument def = DefDocument.ParseText("""
            [convindex]
            A:B=All

            [All]
            MIDI=$B0 $20 $01
            """);

        IReadOnlyList<ScriptAction> actions =
            new EmulationBuilder(def).Build("A", "B").InitActions;

        // TMIDI passes -1 and broadcasts over all six ports, despite the shipped
        // DEF's comment describing the key as "A and B".
        Assert.Equal(6, actions.Count);
        Assert.Equal(Enumerable.Range(0, 6).Select(i => (ScriptPort)i),
                     actions.Select(a => a.Port));
    }

    [Fact]
    public void MidiKeyFollowsAConfiguredPortCount()
    {
        DefDocument def = DefDocument.ParseText("""
            [convindex]
            A:B=All

            [All]
            MIDI=$B0 $20 $01
            """);

        var builder = new EmulationBuilder(def) { PortCount = 3 };
        IReadOnlyList<ScriptAction> actions = builder.Build("A", "B").InitActions;

        Assert.Equal(3, actions.Count);
    }

    [Fact]
    public void EmitsWaitsOnceEvenWhenBroadcasting()
    {
        DefDocument def = DefDocument.ParseText("""
            [convindex]
            A:B=All

            [All]
            MIDI=$B0 $20 $01 W:20
            """);

        IReadOnlyList<ScriptAction> actions =
            new EmulationBuilder(def).Build("A", "B").InitActions;

        Assert.Equal(6, actions.Count(a => a.Kind == ScriptActionKind.Message));
        Assert.Equal(1, actions.Count(a => a.Kind == ScriptActionKind.Wait));
    }

    [Fact]
    public void RepeatsResetsOnEveryPort()
    {
        DefDocument def = DefDocument.ParseText("""
            [convindex]
            A:B=All

            [All]
            MIDI=R:1
            """);

        IReadOnlyList<ScriptAction> actions =
            new EmulationBuilder(def).Build("A", "B").InitActions;

        Assert.Equal(6, actions.Count(a => a.Kind == ScriptActionKind.Reset));
    }

    [Fact]
    public void BroadcastsChannelsWithinEachPort()
    {
        DefDocument def = DefDocument.ParseText("""
            [midimessage]
            MapOne=$B0 $20 $01

            [convindex]
            A:B=All

            [All]
            MIDI=C:MapOne
            """);

        var builder = new EmulationBuilder(def) { PortCount = 2 };
        IReadOnlyList<ScriptAction> actions = builder.Build("A", "B").InitActions;

        Assert.Equal(32, actions.Count);

        // Port A takes all sixteen channels before port B starts.
        Assert.All(actions.Take(16), a => Assert.Equal(ScriptPort.A, a.Port));
        Assert.All(actions.Skip(16), a => Assert.Equal(ScriptPort.B, a.Port));
        Assert.Equal(Enumerable.Range(0, 16).Select(ch => (byte)(0xB0 | ch)),
                     actions.Take(16).Select(a => a.Bytes[0]));
        Assert.Equal(Enumerable.Range(0, 16).Select(ch => (byte)(0xB0 | ch)),
                     actions.Skip(16).Select(a => a.Bytes[0]));
    }

    [Fact]
    public void KeepsSinglePortKeysOnTheirOwnPort()
    {
        DefDocument def = DefDocument.ParseText("""
            [convindex]
            A:B=One

            [One]
            MIDI_B=$B0 $20 $01
            MIDI_C=$B0 $20 $02
            """);

        IReadOnlyList<ScriptAction> actions =
            new EmulationBuilder(def).Build("A", "B").InitActions;

        Assert.Equal([ScriptPort.B, ScriptPort.C], actions.Select(a => a.Port));
    }
}

public class BacklightTests
{
    [Theory]
    [InlineData("ROLAND", 0xFF, 0x8C, 0x00)]
    [InlineData("yamaha", 0xC0, 0xFF, 0x00)]
    [InlineData("KORG", 0x00, 0xFF, 0x80)]
    [InlineData("OTHER", 0xFF, 0xFF, 0x80)]
    public void NamesTheMakersColour(string name, int r, int g, int b)
    {
        BacklightColor color = Backlight.Parse(name);
        Assert.Equal((r, g, b), (color.R, color.G, color.B));
    }

    [Fact]
    public void ReadsAnythingElseAsAColorRef()
    {
        // 0x00BBGGRR, so the low byte is red.
        Assert.Equal(Backlight.Parse("0x0080FF"), new BacklightColor(0xFF, 0x80, 0x00));
        Assert.Equal(Backlight.Parse("255"), new BacklightColor(0xFF, 0, 0));
    }

    [Fact]
    public void FallsBackWhenTheModuleIsNotListed()
    {
        Assert.Equal(Backlight.Other, Backlight.Parse(null));
        Assert.Equal(Backlight.Other, Backlight.Parse(""));
    }

    [Fact]
    public void ResolvesThroughAliases()
    {
        DefDocument def = DefDocument.ParseText("""
            [alias]
            Kite1=Kite-1

            [LiquidBackLight]
            Kite-1=ROLAND
            """);

        Assert.Equal(Backlight.Roland, Backlight.Resolve(def, "Kite1"));
    }
}

/// <summary>The sections an <see cref="EmulationBuilder"/> read, and what it warned of.</summary>
internal sealed class BuildSteps : IEmulationBuildObserver
{
    public List<string> Sections { get; } = [];

    public List<string> Warnings { get; } = [];

    public void ConvIndex(string key, string commands) { }

    public void Section(string name, string comment) => Sections.Add(name);

    public void Port(ScriptPort target, byte[] bytes) { }

    public void PatchMap(string name) { }

    public void PatchDrumMap(string name) { }

    public void Warning(string message) => Warnings.Add(message);
}
