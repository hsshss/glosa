using Glosa.Core.Emulation;

namespace Glosa.Tests;

public class InitializeTypeTests
{
    private static byte[][] Messages(IReadOnlyList<ScriptAction> actions, int port)
        => [.. actions.Where(a => a.Kind == ScriptActionKind.Message && (int)a.Port == port)
                      .Select(a => a.Bytes)];

    [Fact]
    public void GsIsTheGsReset()
    {
        IReadOnlyList<ScriptAction> init = InitializeType.InitFor("GS", [0]);

        Assert.Equal([[0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7]],
                     Messages(init, 0));
        Assert.Equal(200, Assert.Single(init, a => a.Kind == ScriptActionKind.Wait).WaitMs);
    }

    [Fact]
    public void Sc88AddsTheSystemModeSetAfterTheGsReset()
    {
        byte[][] sent = Messages(InitializeType.InitFor("SC88", [0]), 0);

        Assert.Equal(2, sent.Length);
        Assert.Equal(0x40, sent[0][5]);                                    // GS reset
        Assert.Equal([0xF0, 0x41, 0x10, 0x42, 0x12, 0x00, 0x00, 0x7F, 0x00, 0x01, 0xF7], sent[1]);
    }

    [Theory]
    [InlineData("XG", new byte[] { 0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7 })]
    [InlineData("GM", new byte[] { 0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7 })]
    public void XgAndGmAreTheirSystemOn(string type, byte[] message)
        => Assert.Equal([message], Messages(InitializeType.InitFor(type, [0]), 0));

    [Fact]
    public void Mt32IsTheChannelReset()
    {
        ScriptAction reset = Assert.Single(InitializeType.InitFor("MT32", [0]));

        Assert.Equal(ScriptActionKind.Reset, reset.Kind);
        Assert.Equal(0, reset.ResetKind);
    }

    [Fact]
    public void EveryPortGetsItAndItIsWaitedForOnce()
    {
        IReadOnlyList<ScriptAction> init = InitializeType.InitFor("GS", [0, 2]);

        Assert.Single(Messages(init, 0));
        Assert.Single(Messages(init, 2));
        Assert.Single(init, a => a.Kind == ScriptActionKind.Wait);
    }

    [Fact]
    public void NoPortsNothingSent() => Assert.Empty(InitializeType.InitFor("GS", []));
}
