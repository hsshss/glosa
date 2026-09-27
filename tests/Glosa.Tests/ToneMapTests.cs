using Glosa.Core.Emulation;

namespace Glosa.Tests;

public class ToneMapTests
{
    private static ToneMap Of(string module, string type = "XG")
        => ToneMap.Of(module, type) ?? throw new InvalidOperationException(module);

    [Theory]
    [InlineData("SC-88PRO", "SC-55", 0x01)]
    [InlineData("SC-88PRO", "SC-55mk2", 0x01)]
    [InlineData("SC-88PRO", "SC-33", 0x01)]
    [InlineData("SC-88PRO", "SC-88", 0x02)]
    [InlineData("SC-8850", "SC-88PRO", 0x03)]
    [InlineData("SC-8850", "SC-55", 0x01)]
    [InlineData("SC-88", "SC-55", 0x01)]
    [InlineData("MU2000", "MU50", 0x00)]
    [InlineData("MU2000", "MU80", 0x00)]
    [InlineData("MU128", "MU90", 0x00)]
    [InlineData("MU2000", "MU100", 0x01)]
    [InlineData("MU100", "MU128", 0x01)]
    public void AnEarlierModelsSongGetsItsMap(string use, string target, byte map)
        => Assert.Equal(map, Of(use).For(target));

    [Theory]
    [InlineData("SC-88PRO", "SC-88PRO")]
    [InlineData("SC-88PRO", "SC-8850")]   // a map the machine does not have
    [InlineData("SC-88", "SC-88PRO")]
    [InlineData("SC-88PRO", "GS")]
    [InlineData("SC-88PRO", "THRU")]
    [InlineData("SC-88PRO", "MU80")]      // another maker's machine
    [InlineData("MU128", "SC-55")]
    [InlineData("MU128", "XG")]
    public void AnyOtherSongGetsTheModulesOwn(string use, string target)
    {
        ToneMap maps = Of(use);
        Assert.Equal(maps.Native, maps.For(target));
    }

    [Theory]
    [InlineData("SC-88", 0x02)]
    [InlineData("SC-88PRO", 0x03)]
    [InlineData("SC-8850", 0x04)]
    [InlineData("MU100", 0x01)]
    [InlineData("MU2000", 0x01)]
    public void TheOwnMapIsTheFactorySetting(string use, byte native)
        => Assert.Equal(native, Of(use).Native);

    [Theory]
    [InlineData("SC-55")]
    [InlineData("MU90")]
    [InlineData("THRU")]
    [InlineData("CM-64")]
    public void AModuleWithNoEarlierMapsHasNothingToChoose(string use)
        => Assert.Null(ToneMap.Of(use, "GS"));

    [Fact]
    public void AYamahaResetTheGsWayIsInTg300bModeAndHasNoVoiceMap()
    {
        Assert.Null(ToneMap.Of("MU128", InitializeType.GS));
        Assert.NotNull(ToneMap.Of("MU128", InitializeType.GM));
        Assert.NotNull(ToneMap.Of("SC-88PRO", InitializeType.GS));
    }

    private static byte[][] Sent(IReadOnlyList<ScriptAction> actions)
        => [.. actions.Where(a => a.Kind == ScriptActionKind.Message).Select(a => a.Bytes)];

    [Fact]
    public void RolandPutsEveryPartOnTheMapAndPicksItsToneAgain()
    {
        IReadOnlyList<ScriptAction> actions = Of("SC-88PRO").Select(0x01, [0]);
        byte[][] sent = Sent(actions);

        // TONE MAP NUMBER (= CC#32) on all 16 parts, then TONE NUMBER (CC#0 0, program 0).
        Assert.Equal(32, sent.Length);
        Assert.All(sent[..16], m => Assert.Equal((0x40, 0x00, 0x01), (m[5], m[7], m[8])));
        Assert.All(sent[16..], m => Assert.Equal((0x40, 0x10, 0x00, 0x00), (m[5], m[6] & 0xF0, m[8], m[9])));
        Assert.Equal(16, sent[..16].Select(m => m[6]).Distinct().Count());
        Assert.Equal(2, actions.Count(a => a.Kind == ScriptActionKind.Wait));
    }

    [Fact]
    public void TheMessagesAreTheAddressMapsOwn()
    {
        byte[][] sent = Sent(Of("SC-88PRO").Select(0x01, [0]));

        // Part 1 (block 1): 40 41 00 = 01, and 40 11 00 = 00 00.
        Assert.Contains(sent, m => m.SequenceEqual(new byte[]
            { 0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x41, 0x00, 0x01, 0x7E, 0xF7 }));
        Assert.Contains(sent, m => m.SequenceEqual(new byte[]
            { 0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x11, 0x00, 0x00, 0x00, 0x2F, 0xF7 }));
    }

    [Fact]
    public void EveryRolandMessageChecksToZero()
    {
        foreach (byte[] m in Sent(Of("SC-8850").Select(0x03, [0], bothGroups: true)))
            Assert.Equal(0, m[5..^1].Sum(b => b) % 128);
    }

    [Theory]
    [InlineData("SC-88PRO")]
    [InlineData("SC-8850")]
    public void ASongOnTwoPortsHasTheOtherGroupSetFromTheSamePort(string use)
    {
        byte[][] sent = Sent(Of(use).Select(0x01, [0], bothGroups: true));

        Assert.Equal(64, sent.Length);
        Assert.Equal(32, sent.Count(m => m[5] == 0x50));
    }

    [Fact]
    public void ASongOnOnePortLeavesTheOtherGroupAlone()
        => Assert.DoesNotContain(Sent(Of("SC-88PRO").Select(0x01, [0])), m => m[5] == 0x50);

    [Fact]
    public void RolandLeavesNothingInTheMachine()
    {
        ToneMap maps = Of("SC-88PRO");

        Assert.False(maps.Lasting);
        Assert.Empty(maps.Restore());
        Assert.False(maps.Needs(maps.Native));
        Assert.True(maps.Needs(0x01));
        Assert.Null(maps.Settings(maps.Native));
    }

    [Fact]
    public void AnSc55SongHasItsOwnBankLsbDropped()
    {
        EmulationSettings settings = Of("SC-88PRO").Settings(0x01)!;

        Assert.Equal(1, settings.MapSelect);
        Assert.Equal(1, settings.DefaultBankSelectLsb);
        Assert.True(settings.DisableBankSelectLsb);
    }

    [Fact]
    public void ALaterModelsSongKeepsTheMapItPicks()
    {
        EmulationSettings settings = Of("SC-8850").Settings(0x02)!;

        Assert.Equal(2, settings.MapSelect);
        Assert.Equal(2, settings.DefaultBankSelectLsb);
        Assert.False(settings.DisableBankSelectLsb);
    }

    [Fact]
    public void TheSongsProgramChangesAreKeptOnTheMap()
    {
        var sink = new CapturingSink();
        var filter = new EmulationFilter(sink, Of("SC-88PRO").Settings(0x01)!, new PatchMapSet());

        filter.SendShort(0, 0x0000B0);        // CC#0 = 0
        filter.SendShort(0, 0x0220B0);        // the song's own CC#32 = 2, dropped
        filter.SendShort(0, 0x0018C0);        // program 24

        Assert.Equal([0x0000B0u, 0x0120B0u, 0x0018C0u], sink.Messages);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    public void YamahaSetsTheVoiceMapOncePerPort(byte map)
    {
        IReadOnlyList<ScriptAction> actions = Of("MU128").Select(map, [0, 1]);

        Assert.All(actions, a => Assert.Equal<byte>([0xF0, 0x43, 0x10, 0x49, 0x00, 0x00, 0x12, map, 0xF7], a.Bytes));
        Assert.Equal([ScriptPort.A, ScriptPort.B], actions.Select(a => a.Port));
    }

    [Fact]
    public void YamahaIsPutBackOnItsOwnMap()
    {
        ToneMap maps = Of("MU2000");

        Assert.True(maps.Lasting);
        Assert.True(maps.Needs(maps.Native));
        Assert.Null(maps.Settings(0x00));
        Assert.Equal([[0xF0, 0x43, 0x10, 0x49, 0x00, 0x00, 0x12, 0x01, 0xF7]], maps.Restore());
    }

    [Fact]
    public void NoPortsNothingSent() => Assert.Empty(Of("SC-88").Select(0x01, []));
}
