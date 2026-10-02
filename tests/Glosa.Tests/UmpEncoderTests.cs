using Glosa.Midi;

namespace Glosa.Tests;

/// <summary>
/// The MIDI 1.0 bytes the CoreMIDI and Windows MIDI Services backends are handed, as the
/// Universal MIDI Packets they send.
/// </summary>
public class UmpEncoderTests
{
    private static List<uint> Short(uint packed)
    {
        var words = new List<uint>();
        UmpEncoder.Short(packed, words);
        return words;
    }

    private static List<uint> Write(UmpEncoder encoder, params byte[] bytes)
    {
        var words = new List<uint>();
        encoder.Write(bytes, words);
        return words;
    }

    [Fact]
    public void AChannelMessageIsOneWordOfTypeTwo()
    {
        Assert.Equal([0x2090_3C64u], Short(0x90 | 0x3C << 8 | 0x64 << 16));
        Assert.Equal([0x20B0_7B00u], Short(0xB0 | 0x7B << 8));
    }

    [Fact]
    public void AOneDataByteMessageLeavesTheSecondByteEmpty()
    {
        // Whatever was left in the packed message's third byte is not sent.
        Assert.Equal([0x20C5_1000u], Short(0xC5 | 0x10 << 8 | 0x55 << 16));
    }

    [Fact]
    public void SystemMessagesAreOneWordOfTypeOne()
    {
        Assert.Equal([0x10F2_0102u], Short(0xF2 | 0x01 << 8 | 0x02 << 16));
        Assert.Equal([0x10F8_0000u], Short(0xF8));
    }

    [Fact]
    public void WhatIsNotAMessageOnItsOwnIsNotSent()
    {
        Assert.Empty(Short(0xF0));
        Assert.Empty(Short(0xF7));
        Assert.Empty(Short(0x3C));
    }

    [Fact]
    public void ASysExOfSixBytesOrFewerIsOnePacket()
    {
        // Universal non-realtime GM System On: four bytes between F0 and F7.
        Assert.Equal([0x3004_7E7Fu, 0x0901_0000u], Write(new UmpEncoder(), 0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7));
    }

    [Fact]
    public void ALongerSysExStartsAndEnds()
    {
        // GS Reset: nine bytes, six in the start and three in the end.
        Assert.Equal(
            [0x3016_4110u, 0x4212_4000u, 0x3033_7F00u, 0x4100_0000u],
            Write(new UmpEncoder(), 0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7));
    }

    [Fact]
    public void TheLastSixBytesAreTheEndNotAContinuationAndAnEmptyEnd()
    {
        byte[] sysEx = [0xF0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 0xF7];
        Assert.Equal(
            [0x3016_0102u, 0x0304_0506u, 0x3036_0708u, 0x090A_0B0Cu],
            Write(new UmpEncoder(), sysEx));
    }

    [Fact]
    public void AMiddleIsAContinuation()
    {
        byte[] sysEx = [0xF0, .. Enumerable.Range(1, 13).Select(b => (byte)b), 0xF7];
        List<uint> words = Write(new UmpEncoder(), sysEx);
        Assert.Equal([0x3016_0102u, 0x3026_0708u, 0x3031_0D00u],
                     [words[0], words[2], words[4]]);
    }

    [Fact]
    public void ASysExSplitAcrossLongMessagesGoesOutAsFarAsItHasGot()
    {
        // An SMF's F0 event, then the F7 escape that finishes it.
        var encoder = new UmpEncoder();
        Assert.Equal([0x3012_4110u, 0x0000_0000u], Write(encoder, 0xF0, 0x41, 0x10));
        Assert.Equal([0x3031_4200u, 0x0000_0000u], Write(encoder, 0x42, 0xF7));
    }

    [Fact]
    public void AnEscapeCanCarryAnythingElse()
    {
        var encoder = new UmpEncoder();
        Assert.Equal([0x10F8_0000u], Write(encoder, 0xF8));
        // Running status, as on a cable.
        Assert.Equal([0x2090_3C64u, 0x2090_3E64u], Write(encoder, 0x90, 0x3C, 0x64, 0x3E, 0x64));
    }

    [Fact]
    public void RealtimeDoesNotInterruptASysEx()
    {
        Assert.Equal(
            [0x10F8_0000u, 0x3003_0102u, 0x0300_0000u],
            Write(new UmpEncoder(), 0xF0, 0x01, 0xF8, 0x02, 0x03, 0xF7));
    }

    [Fact]
    public void AnyOtherStatusCutsASysExShort()
    {
        Assert.Equal(
            [0x3002_0102u, 0x0000_0000u, 0x2090_3C64u],
            Write(new UmpEncoder(), 0xF0, 0x01, 0x02, 0x90, 0x3C, 0x64));
    }

    [Fact]
    public void AResetClosesASysExLeftOpen()
    {
        var encoder = new UmpEncoder();
        Write(encoder, 0xF0, 0x41);

        var words = new List<uint>();
        encoder.Reset(words);
        Assert.Equal([0x3030_0000u, 0x0000_0000u], words);

        // And nothing is left open to close again.
        words.Clear();
        encoder.Reset(words);
        Assert.Empty(words);
    }

    [Fact]
    public void EveryMessageIsOnTheEncodersGroup()
    {
        var encoder = new UmpEncoder(group: 11);
        Assert.Equal(
            [0x1BF8_0000u, 0x2B90_3C64u, 0x3B16_4110u, 0x4212_4000u, 0x3B33_7F00u, 0x4100_0000u],
            Write(encoder, 0xF8, 0x90, 0x3C, 0x64,
                  0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7));

        Write(encoder, 0xF0, 0x41);
        var words = new List<uint>();
        encoder.Reset(words);
        Assert.Equal([0x3B30_0000u, 0x0000_0000u], words);
    }

    [Fact]
    public void AShortMessageIsOnTheGroupItIsGiven()
    {
        var words = new List<uint>();
        UmpEncoder.Short(0xB0 | 0x7B << 8, words, group: 15);
        Assert.Equal([0x2FB0_7B00u], words);
    }

    [Fact]
    public void TheFirstWordSaysHowLongTheMessageIs()
    {
        Assert.Equal(1, UmpEncoder.WordsIn(0x2090_3C64));
        Assert.Equal(1, UmpEncoder.WordsIn(0x10F8_0000));
        Assert.Equal(2, UmpEncoder.WordsIn(0x3016_4110));
    }
}
