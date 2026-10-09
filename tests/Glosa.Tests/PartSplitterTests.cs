using Glosa.Core.Emulation;

namespace Glosa.Tests;

public class PartSplitterTests
{
    private const int A = 0, B = 1, C = 2, D = 3;

    private static (PartSplitter Splitter, CapturingSink Sink) Build()
    {
        var sink = new CapturingSink();
        return (new PartSplitter(sink), sink);
    }

    private static uint NoteOn(int channel, int key = 60) => (uint)(0x90 | channel | key << 8 | 100 << 16);

    /// <summary>A GS DT1 to device 10, with its checksum.</summary>
    private static byte[] Gs(params byte[] addressAndData)
    {
        int sum = addressAndData.Sum(b => (int)b);
        return [0xF0, 0x41, 0x10, 0x42, 0x12, .. addressAndData, (byte)((128 - sum % 128) % 128), 0xF7];
    }

    private static byte[] Xg(params byte[] addressAndData) => [0xF0, 0x43, 0x10, 0x4C, .. addressAndData, 0xF7];

    /// <summary>The exclusives sent, as "port:hex".</summary>
    private static string[] Longs(CapturingSink sink)
        => [.. sink.Longs.Select(l => $"{l.Port}:{Convert.ToHexString(l.Data)}")];

    private static string Long(int port, byte[] data) => $"{port}:{Convert.ToHexString(data)}";

    // ---- GS ---------------------------------------------------------------------

    [Fact]
    public void WithNothingSaidEverythingGoesWhereItWasSent()
    {
        (PartSplitter splitter, CapturingSink sink) = Build();
        byte[] partVolume = Gs(0x40, 0x11, 0x19, 0x64);

        for (int channel = 0; channel < 16; channel++)
        {
            splitter.SendShort(A, NoteOn(channel));
            splitter.SendShort(B, NoteOn(channel));
        }
        splitter.SendShort(6, NoteOn(0));
        splitter.SendLong(A, partVolume);
        splitter.SendLong(B, partVolume);

        Assert.Equal([.. Enumerable.Range(0, 16).SelectMany(ch => new[] { (A, NoteOn(ch)), (B, NoteOn(ch)) }),
                      (6, NoteOn(0))],
                     sink.Shorts);
        Assert.Equal([Long(A, partVolume), Long(B, partVolume)], Longs(sink));
    }

    [Fact]
    public void TheOtherPartGroupGoesToThePartnerPortAsItsOwn()
    {
        (PartSplitter splitter, CapturingSink sink) = Build();

        splitter.SendLong(A, Gs(0x50, 0x11, 0x19, 0x64));
        splitter.SendLong(B, Gs(0x50, 0x11, 0x19, 0x64));

        // 40 11 19 64 with its checksum, 32.
        byte[] own = Convert.FromHexString("F04110421240111964" + "32F7");
        Assert.Equal([Long(B, own), Long(A, own)], Longs(sink));
    }

    [Fact]
    public void APartOnTheOtherGroupCanListenToPortA()
    {
        // _AKD_PMF.MID's track 9: B7 listens to port A, A7 and B7 on channel 6.
        (PartSplitter splitter, CapturingSink sink) = Build();
        splitter.SendLong(A, Gs(0x00, 0x01, 0x16, 0x00));
        splitter.SendLong(A, Gs(0x40, 0x16, 0x02, 0x10));
        splitter.SendLong(A, Gs(0x50, 0x16, 0x02, 0x05));
        splitter.SendLong(A, Gs(0x40, 0x16, 0x02, 0x05));

        splitter.SendShort(A, NoteOn(5));
        splitter.SendShort(B, NoteOn(5));

        Assert.Equal([(A, NoteOn(5)), (B, NoteOn(5))], sink.Shorts);
        Assert.Empty(sink.Longs);
    }

    [Fact]
    public void APartListeningElsewhereIsSentOnItsHomeChannel()
    {
        (PartSplitter splitter, CapturingSink sink) = Build();
        splitter.SendLong(A, Gs(0x40, 0x13, 0x02, 0x07));

        // A3 (block 3, channel 3 at home) now receives channel 8, beside A8; nothing receives
        // channel 3.
        splitter.SendShort(A, NoteOn(7));
        splitter.SendShort(A, NoteOn(2));

        Assert.Equal([(A, NoteOn(2)), (A, NoteOn(7))], sink.Shorts);
    }

    [Fact]
    public void ARxChannelAmongOtherDataIsSentAsTheHomeChannel()
    {
        (PartSplitter splitter, CapturingSink sink) = Build();

        // 40 16 00..03: tone number, RX CHANNEL 6 → 3, RX PITCH BEND.
        splitter.SendLong(A, Gs(0x40, 0x16, 0x00, 0x00, 0x30, 0x02, 0x01));
        splitter.SendShort(A, NoteOn(2));

        Assert.Equal([Long(A, Gs(0x40, 0x16, 0x00, 0x00, 0x30, 0x05, 0x01))], Longs(sink));
        Assert.Equal([(A, NoteOn(2)), (A, NoteOn(5))], sink.Shorts);
    }

    [Fact]
    public void PatchCommonIsTheWholeUnitsInModeOneAndOneGroupsInModeTwo()
    {
        (PartSplitter splitter, CapturingSink sink) = Build();
        byte[] reverbMacro = Gs(0x40, 0x01, 0x30, 0x04);
        byte[] gsReset = Convert.FromHexString("F041104212" + "40007F0041F7");

        splitter.SendLong(A, reverbMacro);
        splitter.SendLong(A, Gs(0x50, 0x01, 0x30, 0x04));
        Assert.Equal([Long(A, reverbMacro), Long(B, reverbMacro)], Longs(sink));

        sink.Longs.Clear();
        splitter.SendLong(A, Gs(0x00, 0x00, 0x7F, 0x01));
        Assert.Equal([Long(A, gsReset), Long(B, gsReset)], Longs(sink));

        sink.Longs.Clear();
        splitter.SendLong(A, reverbMacro);
        Assert.Equal([Long(A, reverbMacro)], Longs(sink));

        sink.Longs.Clear();
        splitter.SendLong(A, Gs(0x50, 0x01, 0x30, 0x04));
        Assert.Equal([Long(B, reverbMacro)], Longs(sink));
    }

    [Fact]
    public void InModeTwoAGsResetIsOneGroupsOnly()
    {
        (PartSplitter splitter, CapturingSink sink) = Build();
        splitter.SendLong(A, Gs(0x00, 0x00, 0x7F, 0x01));
        splitter.SendLong(A, Gs(0x40, 0x16, 0x02, 0x10));
        splitter.SendLong(A, Gs(0x50, 0x16, 0x02, 0x10));

        splitter.SendLong(A, Gs(0x40, 0x00, 0x7F, 0x00));
        splitter.SendShort(A, NoteOn(5));
        splitter.SendShort(B, NoteOn(5));

        Assert.Equal([(A, NoteOn(5))], sink.Shorts);
    }

    [Fact]
    public void AGsResetPutsTheChannelsBackButNotThePorts()
    {
        (PartSplitter splitter, CapturingSink sink) = Build();
        splitter.SendLong(A, Gs(0x00, 0x01, 0x16, 0x00));
        splitter.SendLong(A, Gs(0x50, 0x16, 0x02, 0x02));
        splitter.SendLong(A, Gs(0x40, 0x16, 0x02, 0x10));

        splitter.SendLong(A, Gs(0x40, 0x00, 0x7F, 0x00));
        splitter.SendShort(A, NoteOn(5));
        splitter.SendShort(B, NoteOn(5));
        splitter.SendShort(A, NoteOn(2));

        Assert.Equal([(A, NoteOn(5)), (B, NoteOn(5)), (A, NoteOn(2))], sink.Shorts);
    }

    [Fact]
    public void GmSystemOnIsTheWholeUnitsInModeOne()
    {
        (PartSplitter splitter, CapturingSink sink) = Build();
        byte[] gmOn = [0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7];
        splitter.SendLong(A, Gs(0x50, 0x16, 0x02, 0x10));

        splitter.SendLong(A, gmOn);
        splitter.SendShort(B, NoteOn(5));

        Assert.Equal([Long(A, gmOn), Long(B, gmOn)], Longs(sink));
        Assert.Equal([(B, NoteOn(5))], sink.Shorts);
    }

    [Fact]
    public void OtherRolandMessagesStayWhereTheyWereSent()
    {
        (PartSplitter splitter, CapturingSink sink) = Build();
        byte[] display = [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x00, 0x00, 0x41, 0x2F, 0xF7];

        splitter.SendLong(B, display);

        Assert.Equal([Long(B, display)], Longs(sink));
    }

    // ---- XG ---------------------------------------------------------------------

    [Fact]
    public void AnXgPartNumberPicksThePort()
    {
        (PartSplitter splitter, CapturingSink sink) = Build();

        splitter.SendLong(A, Xg(0x08, 0x12, 0x07, 0x64));

        Assert.Equal([Long(B, Xg(0x08, 0x02, 0x07, 0x64))], Longs(sink));
    }

    [Fact]
    public void AnXgPartCanReceiveFromAnotherPort()
    {
        (PartSplitter splitter, CapturingSink sink) = Build();

        // Part 6 receives port B channel 6.
        splitter.SendLong(A, Xg(0x08, 0x05, 0x04, 0x15));
        splitter.SendShort(B, NoteOn(5));
        splitter.SendShort(A, NoteOn(5));

        Assert.Empty(sink.Longs);
        Assert.Equal([(A, NoteOn(5)), (B, NoteOn(5))], sink.Shorts);
    }

    [Fact]
    public void TheInsertionPartIsOnlyOnTheMachineThatHasIt()
    {
        (PartSplitter splitter, CapturingSink sink) = Build();

        splitter.SendLong(A, Xg(0x03, 0x00, 0x0C, 0x13));

        Assert.Equal([Long(A, Xg(0x03, 0x00, 0x0C, 0x7F)), Long(B, Xg(0x03, 0x00, 0x0C, 0x03)),
                      Long(C, Xg(0x03, 0x00, 0x0C, 0x7F)), Long(D, Xg(0x03, 0x00, 0x0C, 0x7F))],
                     Longs(sink));
    }

    [Fact]
    public void XgSystemOnGoesToEveryXgPortAndPutsThePartsBack()
    {
        (PartSplitter splitter, CapturingSink sink) = Build();
        byte[] xgOn = Xg(0x00, 0x00, 0x7E, 0x00);
        splitter.SendLong(A, Xg(0x08, 0x05, 0x04, 0x15));

        splitter.SendLong(A, xgOn);
        splitter.SendShort(A, NoteOn(5));
        splitter.SendShort(B, NoteOn(5));

        Assert.Equal([Long(A, xgOn), Long(B, xgOn), Long(C, xgOn), Long(D, xgOn)], Longs(sink));
        Assert.Equal([(A, NoteOn(5)), (B, NoteOn(5))], sink.Shorts);
    }

    // ---- reset ------------------------------------------------------------------

    [Fact]
    public void ResetPutsEverythingBack()
    {
        (PartSplitter splitter, CapturingSink sink) = Build();
        splitter.SendLong(A, Gs(0x00, 0x00, 0x7F, 0x01));
        splitter.SendLong(A, Gs(0x00, 0x01, 0x16, 0x00));
        splitter.SendLong(A, Gs(0x40, 0x16, 0x02, 0x10));
        splitter.SendLong(A, Xg(0x08, 0x12, 0x04, 0x00));
        sink.Longs.Clear();

        splitter.Reset();
        splitter.SendShort(A, NoteOn(5));
        splitter.SendShort(B, NoteOn(5));
        splitter.SendShort(B, NoteOn(2));
        splitter.SendLong(A, Gs(0x40, 0x01, 0x30, 0x04));

        Assert.Equal([(A, NoteOn(5)), (B, NoteOn(5)), (B, NoteOn(2))], sink.Shorts);
        Assert.Equal([Long(A, Gs(0x40, 0x01, 0x30, 0x04)), Long(B, Gs(0x40, 0x01, 0x30, 0x04))],
                     Longs(sink));
    }
}
