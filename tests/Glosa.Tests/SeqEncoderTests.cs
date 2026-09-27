using Glosa.Midi.Linux;

namespace Glosa.Tests;

/// <summary>
/// A long message's bytes, as the whole messages and SysEx pieces the ALSA backend sends.
/// </summary>
public class SeqEncoderTests
{
    private static List<string> Write(SeqEncoder encoder, params byte[] bytes)
    {
        var chunks = new List<SeqChunk>();
        encoder.Write(bytes, chunks);
        return Show(chunks);
    }

    private static List<string> Show(List<SeqChunk> chunks)
        => [.. chunks.Select(c => (c.IsSysEx ? "X:" : "M:") + Convert.ToHexString(c.Bytes))];

    [Fact]
    public void AWholeSysExIsOnePiece()
    {
        Assert.Equal(["X:F041104212400000007F0041F7"],
                     Write(new SeqEncoder(), 0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x00, 0x00, 0x7F, 0x00, 0x41, 0xF7));
    }

    [Fact]
    public void ALongSysExIsCutIntoPiecesOfTheChunkSize()
    {
        byte[] sysEx = [0xF0, .. Enumerable.Repeat((byte)0x11, 600), 0xF7];
        var chunks = new List<SeqChunk>();
        new SeqEncoder().Write(sysEx, chunks);

        Assert.All(chunks, c => Assert.True(c.IsSysEx));
        Assert.Equal([SeqEncoder.ChunkSize, SeqEncoder.ChunkSize, 602 - 2 * SeqEncoder.ChunkSize],
                     chunks.Select(c => c.Bytes.Length));
        Assert.Equal(sysEx, chunks.SelectMany(c => c.Bytes));
    }

    [Fact]
    public void ASysExSplitAcrossCallsGoesOutAsItArrives()
    {
        var encoder = new SeqEncoder();
        Assert.Equal(["X:F04110"], Write(encoder, 0xF0, 0x41, 0x10));
        // The rest, as an F7 escape carries it: no F0 of its own.
        Assert.Equal(["X:4212F7"], Write(encoder, 0x42, 0x12, 0xF7));
        Assert.Equal(["M:903C64"], Write(encoder, 0x90, 0x3C, 0x64));
    }

    [Fact]
    public void ASysExCutShortAtTheStartOfACallSendsNoEmptyPiece()
    {
        var encoder = new SeqEncoder();
        Write(encoder, 0xF0, 0x41, 0x10);

        Assert.Equal(["M:903C64"], Write(encoder, 0x90, 0x3C, 0x64));
    }

    [Fact]
    public void ChannelMessagesInALongMessageAreWholeMessagesWithRunningStatus()
    {
        Assert.Equal(["M:903C64", "M:904064", "M:C005"],
                     Write(new SeqEncoder(), 0x90, 0x3C, 0x64, 0x40, 0x64, 0xC0, 0x05));
    }

    [Fact]
    public void RealtimeGoesOutFromTheMiddleOfASysEx()
    {
        Assert.Equal(["M:F8", "X:F04142F7"], Write(new SeqEncoder(), 0xF0, 0x41, 0xF8, 0x42, 0xF7));
    }

    [Fact]
    public void AnotherStatusCutsASysExShort()
    {
        Assert.Equal(["X:F04142", "M:B07B00"], Write(new SeqEncoder(), 0xF0, 0x41, 0x42, 0xB0, 0x7B, 0x00));
    }

    [Fact]
    public void SystemCommonMessagesDoNotRunOn()
    {
        Assert.Equal(["M:F305"], Write(new SeqEncoder(), 0xF3, 0x05, 0x06));
    }

    [Fact]
    public void UndefinedAndStrayBytesAreDropped()
    {
        Assert.Equal(["M:F6"], Write(new SeqEncoder(), 0x3C, 0xF7, 0xF4, 0x10, 0xF9, 0xF6));
    }

    [Fact]
    public void ResetClosesAnOpenSysEx()
    {
        var encoder = new SeqEncoder();
        Write(encoder, 0xF0, 0x41);

        var chunks = new List<SeqChunk>();
        encoder.Reset(chunks);
        Assert.Equal(["X:F7"], Show(chunks));

        // Nothing is open afterwards, and running status is gone.
        chunks.Clear();
        encoder.Reset(chunks);
        Assert.Empty(chunks);
        Assert.Empty(Write(encoder, 0x3C, 0x64));
    }
}
