using Glosa.Core.Definition;
using Glosa.Core.Emulation;
using Glosa.Core.Playback;
using Glosa.Core.Smf;

namespace Glosa.Tests;

/// <summary>Captures what the filter decided to send.</summary>
internal sealed class CapturingSink : IEventSink
{
    public List<(int Port, uint Msg)> Shorts { get; } = [];
    public List<(int Port, byte[] Data)> Longs { get; } = [];

    public void SendShort(int port, uint packedMessage) => Shorts.Add((port, packedMessage));
    public void SendLong(int port, ReadOnlySpan<byte> sysEx) => Longs.Add((port, sysEx.ToArray()));

    public IEnumerable<uint> Messages => Shorts.Select(s => s.Msg);
}

public class EmulationFilterTests
{
    private static (EmulationFilter Filter, CapturingSink Sink) Build(
        Action<EmulationSettings>? configure = null, PatchMapSet? patches = null)
    {
        var sink = new CapturingSink();
        var settings = new EmulationSettings();
        configure?.Invoke(settings);
        var filter = new EmulationFilter(sink, settings, patches ?? new PatchMapSet());
        return (filter, sink);
    }

    private static PatchMapSet Patches(string def, string melodic = "m", string? drum = null)
    {
        DefDocument doc = DefDocument.ParseText(def);
        var set = new PatchMapSet();
        set.AddMelodic(doc[melodic]);
        if (drum is not null) set.AddDrum(doc[drum]);
        return set;
    }

    private static uint Msg(int status, int data1 = 0, int data2 = 0)
        => (uint)(status | data1 << 8 | data2 << 16);

    [Fact]
    public void PassesMessagesThroughWhenNothingIsConfigured()
    {
        (EmulationFilter filter, CapturingSink sink) = Build();
        filter.SendShort(0, Msg(0x90, 60, 100));

        Assert.Equal(Msg(0x90, 60, 100), Assert.Single(sink.Messages));
    }

    // ---- bank select suppression -------------------------------------------------

    [Fact]
    public void DropsBankSelectLsbWhenSuppressed()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.DisableBankSelectLsb = true);
        filter.SendShort(0, Msg(0xB0, 0x20, 3));

        Assert.Empty(sink.Shorts);
    }

    [Fact]
    public void DropsBankSelectMsbWhenSuppressed()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.DisableBankSelectMsb = true);
        filter.SendShort(0, Msg(0xB0, 0x00, 3));

        Assert.Empty(sink.Shorts);
    }

    [Fact]
    public void SubstitutesDefaultBankSelectLsbForZero()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.DefaultBankSelectLsb = 2);
        filter.SendShort(0, Msg(0xB0, 0x20, 0));

        Assert.Equal(Msg(0xB0, 0x20, 2), Assert.Single(sink.Messages));
    }

    [Fact]
    public void MapSelectPinsTheMapBeforeAProgramChange()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.MapSelect = 3);
        filter.SendShort(0, Msg(0xC0, 5));

        Assert.Collection(sink.Messages,
            m => Assert.Equal(Msg(0xB0, 0x20, 3), m),   // bank LSB sent first
            m => Assert.Equal(Msg(0xC0, 5), m));
    }

    // ---- controller scaling ------------------------------------------------------

    [Fact]
    public void MapSelectOverwritesThePartBankLsbOnEveryProgramChange()
    {
        // The CC#32 is only sent while the part has not chosen a map, but the part's own
        // bank LSB is overwritten either way - which is what the next lookup reads.
        (EmulationFilter filter, CapturingSink sink) =
            Build(s => { s.MapSelect = 2; s.DefaultBankSelectLsb = 2; });

        filter.SendShort(0, Msg(0xB0, 0x20, 7));      // a map is now selected
        sink.Shorts.Clear();
        filter.SendShort(0, Msg(0xC0, 1));

        Assert.Equal(Msg(0xC0, 1), Assert.Single(sink.Messages));   // no CC#32 pinned
        Assert.Equal(2, filter.Parts[0, 0].BankLsb);
    }

    [Fact]
    public void RecordsTheSelectedMapOnlyWhenDefaultBankSelectLsbIsSet()
    {
        // Without DefaultBankSelectLSB a part never counts as having chosen a map, so every
        // program change pins one.
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.MapSelect = 3);

        filter.SendShort(0, Msg(0xB0, 0x20, 7));
        sink.Shorts.Clear();
        filter.SendShort(0, Msg(0xC0, 1));

        Assert.Equal([Msg(0xB0, 0x20, 3), Msg(0xC0, 1)], sink.Messages);
    }

    [Fact]
    public void QuartersVariationDepthWhenConvertingXgToGs()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.XgToGsEmu = true);
        filter.SendShort(0, Msg(0xB0, 0x5E, 100));

        Assert.Equal(Msg(0xB0, 0x5E, 25), Assert.Single(sink.Messages));
    }

    [Fact]
    public void ScalesVolumeByTheMasterSetting()
    {
        (EmulationFilter filter, CapturingSink sink) = Build();
        filter.MasterVolume = 64;
        filter.SendShort(0, Msg(0xB0, 0x07, 127));

        Assert.Equal(Msg(0xB0, 0x07, 64), Assert.Single(sink.Messages));
    }

    [Fact]
    public void TracksBankSelectsOnThePart()
    {
        (EmulationFilter filter, _) = Build();
        filter.SendShort(0, Msg(0xB0, 0x00, 8));
        filter.SendShort(0, Msg(0xB0, 0x20, 3));

        Assert.Equal(8, filter.Parts[0, 0].BankMsb);
        Assert.Equal(3, filter.Parts[0, 0].BankLsb);
    }

    // ---- XG drum bank ------------------------------------------------------------

    [Fact]
    public void TurnsXgDrumBankIntoAGsRhythmPart()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.XgToGsEmu = true);
        filter.SendShort(0, Msg(0xB2, 0x00, 127));       // channel 3

        byte[] sysEx = Assert.Single(sink.Longs).Data;
        // Channel 3 is GS part block 3; map 2 because it is not channel 10.
        Assert.Equal(new byte[] { 0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x13, 0x15, 0x02 },
                     sysEx[..9]);
        Assert.Equal(0xF7, sysEx[^1]);
        Assert.Equal(PartMode.Drum2, filter.Parts[0, 2].Mode);
    }

    [Fact]
    public void UsesDrumMapOneForChannelTen()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.XgToGsEmu = true);
        filter.SendShort(0, Msg(0xB9, 0x00, 127));       // channel 10

        byte[] sysEx = Assert.Single(sink.Longs).Data;
        Assert.Equal(0x10, sysEx[6]);                    // block 0
        Assert.Equal(0x01, sysEx[8]);                    // map 1
    }

    [Fact]
    public void RewritesXgDrumBankToTheKorgBank()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.XgToX5dEmu = true);
        filter.SendShort(0, Msg(0xB0, 0x00, 127));

        Assert.Equal(Msg(0xB0, 0x00, 0x3E), Assert.Single(sink.Messages));
    }

    [Fact]
    public void ZeroesXgDrumBankForGm()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.XgToGmEmu = true);
        filter.SendShort(0, Msg(0xB0, 0x00, 127));

        Assert.Equal(Msg(0xB0, 0x00, 0), Assert.Single(sink.Messages));
        Assert.Equal(PartMode.Drum2, filter.Parts[0, 0].Mode);
    }

    // ---- GM drum channel forcing -------------------------------------------------

    [Fact]
    public void MovesRhythmNotesToChannelTenForGm()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.GsToGmEmu = true);
        filter.Parts[0, 2].Mode = PartMode.Drum1;
        filter.SendShort(0, Msg(0x92, 36, 100));

        Assert.Equal(Msg(0x99, 36, 100), Assert.Single(sink.Messages));
    }

    [Fact]
    public void DropsNonNotesOnMapTwoPartsForGm()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.GsToGmEmu = true);
        filter.Parts[0, 2].Mode = PartMode.Drum2;
        filter.SendShort(0, Msg(0xB2, 0x07, 100));

        Assert.Empty(sink.Shorts);
    }

    // ---- melodic patch map -------------------------------------------------------

    [Fact]
    public void RewritesProgramChangeThroughThePatchMap()
    {
        PatchMapSet patches = Patches("""
            [m]
            *:24:0=0:0:1
            """);
        (EmulationFilter filter, CapturingSink sink) = Build(patches: patches);
        filter.SendShort(0, Msg(0xB0, 0x00, 24));        // bank MSB 24
        filter.SendShort(0, Msg(0xC0, 0));

        Assert.Collection(sink.Messages,
            m => Assert.Equal(Msg(0xB0, 0x00, 24), m),
            m => Assert.Equal(Msg(0xB0, 0x20, 0), m),    // destination bank LSB
            m => Assert.Equal(Msg(0xB0, 0x00, 0), m),    // destination bank MSB
            m => Assert.Equal(Msg(0xC0, 1), m));         // rewritten program
    }

    [Fact]
    public void TakesTheFirstMatchingRule()
    {
        PatchMapSet patches = Patches("""
            [m]
            *:*:0=0:0:7
            *:*:0=0:0:9
            """);
        (EmulationFilter filter, CapturingSink sink) = Build(patches: patches);
        filter.SendShort(0, Msg(0xC0, 0));

        Assert.Equal(7u, (sink.Messages.Last() >> 8) & 0xFF);
    }

    [Fact]
    public void LeavesFieldsMarkedWithAnAsteriskAlone()
    {
        PatchMapSet patches = Patches("""
            [m]
            *:*:5=*:*:6
            """);
        (EmulationFilter filter, CapturingSink sink) = Build(patches: patches);
        filter.SendShort(0, Msg(0xC0, 5));

        // Only the program changes; no bank selects are generated.
        Assert.Equal(Msg(0xC0, 6), Assert.Single(sink.Messages));
    }

    [Fact]
    public void SkipsPastAnExactDrumRuleOnAMelodicPart()
    {
        // The exact-match shape is a four-byte compare, so a fully specified DM: rule is not
        // even a match for a melodic part - and the rule after it is still reachable.
        (EmulationFilter filter, CapturingSink sink) = Build(patches: Patches("""
            [m]
            DM:0:0:5=0:0:40
            0:0:5=0:0:41
            """));
        filter.SendShort(0, Msg(0xC0, 5));

        Assert.Contains(Msg(0xC0, 41), sink.Messages);
    }

    [Fact]
    public void DoesNotApplyADrumRuleToAMelodicPart()
    {
        PatchMapSet patches = Patches("""
            [m]
            DM:*:*:5=*:*:6
            """);
        (EmulationFilter filter, CapturingSink sink) = Build(patches: patches);
        filter.SendShort(0, Msg(0xC0, 5));

        Assert.Equal(Msg(0xC0, 5), Assert.Single(sink.Messages));
    }

    // ---- drum patch map ----------------------------------------------------------

    [Fact]
    public void StartsTheDrumTrackAsARhythmPart()
    {
        // DrumTrack names the channel that is rhythmic before any song says so. Without it
        // the drum map never applies to channel 10, and a melodic rule can catch its
        // program changes instead.
        (EmulationFilter filter, _) = Build();

        Assert.Equal(PartMode.Drum1, filter.Parts[0, 9].Mode);
        Assert.Equal(PartMode.Melodic, filter.Parts[0, 8].Mode);
    }

    [Fact]
    public void FollowsAConfiguredDrumTrack()
    {
        (EmulationFilter filter, _) = Build(s => s.DrumTrack = 15);

        Assert.Equal(PartMode.Drum1, filter.Parts[0, 15].Mode);
        Assert.Equal(PartMode.Melodic, filter.Parts[0, 9].Mode);
    }

    [Fact]
    public void RemapsDrumNotes()
    {
        PatchMapSet patches = Patches("[m]\n[d]\n*:*:36=*:*:35", drum: "d");
        (EmulationFilter filter, CapturingSink sink) = Build(patches: patches);
        filter.Parts[0, 9].Mode = PartMode.Drum1;
        filter.SendShort(0, Msg(0x99, 36, 100));

        Assert.Equal(Msg(0x99, 35, 100), Assert.Single(sink.Messages));
    }

    [Fact]
    public void SwitchesDrumSetWhenTheRuleNamesOne()
    {
        PatchMapSet patches = Patches("[m]\n[d]\n*:25:36=*:26:35", drum: "d");
        (EmulationFilter filter, CapturingSink sink) = Build(patches: patches);
        filter.Parts[0, 9].Mode = PartMode.Drum1;
        filter.Parts[0, 9].BankMsb = 25;                 // current drum set
        filter.SendShort(0, Msg(0x99, 36, 100));

        Assert.Collection(sink.Messages,
            m => Assert.Equal(Msg(0xC9, 26), m),         // drum set change
            m => Assert.Equal(Msg(0x99, 35, 100), m));
    }

    [Fact]
    public void LeavesMelodicPartsOutOfTheDrumMap()
    {
        PatchMapSet patches = Patches("[m]\n[d]\n*:*:36=*:*:35", drum: "d");
        (EmulationFilter filter, CapturingSink sink) = Build(patches: patches);
        filter.SendShort(0, Msg(0x90, 36, 100));

        Assert.Equal(Msg(0x90, 36, 100), Assert.Single(sink.Messages));
    }

    // ---- key shift ---------------------------------------------------------------

    [Fact]
    public void TransposesNotesAndClamps()
    {
        (EmulationFilter filter, CapturingSink sink) = Build();
        filter.KeyShift = 12;
        filter.SendShort(0, Msg(0x90, 60, 100));
        filter.SendShort(0, Msg(0x90, 120, 100));

        Assert.Equal(72u, (sink.Shorts[0].Msg >> 8) & 0xFF);
        Assert.Equal(127u, (sink.Shorts[1].Msg >> 8) & 0xFF);   // clamped
        Assert.Equal(100u, (sink.Shorts[0].Msg >> 16) & 0xFF);  // velocity untouched
    }

    [Fact]
    public void LeavesRhythmPartsUntransposed()
    {
        (EmulationFilter filter, CapturingSink sink) = Build();
        filter.KeyShift = 12;
        filter.Parts[0, 9].Mode = PartMode.Drum1;
        filter.SendShort(0, Msg(0x99, 36, 100));

        // On a rhythm part the note number picks an instrument, so shifting it is wrong.
        Assert.Equal(Msg(0x99, 36, 100), Assert.Single(sink.Messages));
    }
}

public class PortRangeTests
{
    private static (EmulationFilter Filter, CapturingSink Sink) Build()
    {
        var sink = new CapturingSink();
        return (new EmulationFilter(sink, new EmulationSettings(), new PatchMapSet()), sink);
    }

    private static uint Msg(int status, int data1 = 0, int data2 = 0)
        => (uint)(status | data1 << 8 | data2 << 16);

    [Fact]
    public void TakesEveryPortThereIs()
    {
        (EmulationFilter filter, CapturingSink sink) = Build();

        for (int port = 0; port < IEventSink.PortCount; port++)
            filter.SendShort(port, Msg(0x90, 60, 100));

        Assert.Equal(IEventSink.PortCount, sink.Shorts.Count);
    }

    [Fact]
    public void DropsMessagesForAPortPastTheLast()
    {
        (EmulationFilter filter, CapturingSink sink) = Build();
        filter.SendShort(IEventSink.PortCount, Msg(0x90, 60, 100));
        filter.SendLong(IEventSink.PortCount, [0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7]);

        Assert.Empty(sink.Shorts);
        Assert.Empty(sink.Longs);
    }

    [Fact]
    public void DropsMessagesForANegativePort()
    {
        (EmulationFilter filter, CapturingSink sink) = Build();
        filter.SendShort(-1, Msg(0x90, 60, 100));

        Assert.Empty(sink.Shorts);
    }
}

public class SysExSuppressionTests
{
    private static (EmulationFilter Filter, CapturingSink Sink) Build(
        Action<EmulationSettings>? configure = null)
    {
        var sink = new CapturingSink();
        var settings = new EmulationSettings();
        configure?.Invoke(settings);
        return (new EmulationFilter(sink, settings, new PatchMapSet()), sink);
    }

    [Fact]
    public void SwallowsXgAllParameterReset()
    {
        (EmulationFilter filter, CapturingSink sink) = Build();
        filter.SendLong(0, [0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7F, 0x00, 0xF7]);

        Assert.Empty(sink.Longs);
    }

    [Fact]
    public void StillPassesXgSystemReset()
    {
        (EmulationFilter filter, CapturingSink sink) = Build();
        filter.SendLong(0, [0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7]);

        Assert.Single(sink.Longs);
        Assert.True(filter.Panel.XgSystemReset);
    }

    [Fact]
    public void HoldsBackGsAddressThreeWhenProSettingIsOn()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.ProSetting = true);
        // F0 41 10 42 12 00 00 03 <data> <sum> F7
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x00, 0x00, 0x03, 0x01, 0x7C, 0xF7]);

        Assert.Empty(sink.Longs);
    }

    [Fact]
    public void SendsGsAddressThreeWhenProSettingIsOff()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.ProSetting = false);
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x00, 0x00, 0x03, 0x01, 0x7C, 0xF7]);

        Assert.Single(sink.Longs);
    }

    [Fact]
    public void StopsResetExclusivesWhenConfigured()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.DisableResetExclusive = true);
        filter.SendLong(0, [0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7]);          // GM System On
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7]);

        Assert.Empty(sink.Longs);
        Assert.True(filter.Panel.GmSystemOn);   // state still tracked
    }

    [Fact]
    public void StopsResetExclusivesWhenOnlyDisableExclusiveIsSet()
    {
        // DisableResetExclusive is checked first, but DisableExclusive still applies on top
        // of it, so it holds back resets as well.
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.DisableExclusive = true);
        filter.SendLong(0, [0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7]);          // GM System On
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7]);
        filter.SendLong(0, [0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7]);

        Assert.Empty(sink.Longs);
        Assert.True(filter.Panel.GmSystemOn);   // state still tracked
    }

    [Fact]
    public void StopsGsAddressThreeWhenAllExclusivesAreDisabled()
    {
        (EmulationFilter filter, CapturingSink sink) =
            Build(s => { s.ProSetting = false; s.DisableExclusive = true; });
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x00, 0x00, 0x03, 0x01, 0x7C, 0xF7]);

        Assert.Empty(sink.Longs);
    }

    [Fact]
    public void DoesNotWalkTheChecksumIntoTheNextParameter()
    {
        // 40 11 14 has no meaning, but 40 11 15 right after it does. Taking the checksum as
        // a second data byte would turn the part into a rhythm part. Block 1 is channel 1,
        // which is melodic to begin with, unlike the drum channel.
        (EmulationFilter filter, _) = Build();
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x11, 0x14, 0x00, 0x1B, 0xF7]);

        Assert.Equal(PartMode.Melodic, filter.Parts[0, 0].Mode);
        Assert.False(filter.Panel.Part(0, 0).Rhythm);
    }

    [Fact]
    public void AppliesTheGsResetPanelDefaults()
    {
        (EmulationFilter filter, _) = Build();
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7]);

        PanelState p = filter.Panel;
        Assert.Equal(4, p.Reverb.Type);
        Assert.Equal(0, p.Reverb.Sub);
        Assert.Equal(2, p.Chorus.Type);
        Assert.Equal(1, p.Chorus.Sub);
        Assert.Equal(0, p.Variation.Type);
        Assert.Equal(2, p.Variation.Sub);
        Assert.Equal(0xFF, p.VariationPart);
        Assert.Equal(0x7F, p.Insertion2.Type);
        Assert.Equal(0x7F, p.Insertion2Part);
    }
}

public class PartReceiveTests
{
    private static (EmulationFilter Filter, CapturingSink Sink) Build(
        Action<EmulationSettings>? configure = null)
    {
        var sink = new CapturingSink();
        var settings = new EmulationSettings();
        configure?.Invoke(settings);
        return (new EmulationFilter(sink, settings, new PatchMapSet()), sink);
    }

    /// <summary>F0 41 10 42 12 &lt;addr&gt; &lt;value&gt; &lt;sum&gt; F7</summary>
    private static byte[] Gs(int a2, int a1, int a0, byte value)
    {
        int sum = (a2 + a1 + a0 + value) & 0x7F;
        return [0xF0, 0x41, 0x10, 0x42, 0x12, (byte)a2, (byte)a1, (byte)a0, value,
                (byte)((0x80 - sum) & 0x7F), 0xF7];
    }

    [Fact]
    public void BendPitchControlBecomesThePitchBendSensitivityRpn()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.GsToGmEmu = true);
        // 40 21 10 is block 1, channel 1: twelve semitones.
        filter.SendLong(0, Gs(0x40, 0x21, 0x10, 0x4C));

        Assert.Equal([0x0064B0u, 0x0065B0u, 0x0C06B0u], sink.Messages);
    }

    [Theory]
    [InlineData(0x3F)]
    [InlineData(0x00)]
    public void BendPitchControlBelowZeroSemitonesSendsNothing(byte value)
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.GsToGmEmu = true);
        filter.SendLong(0, Gs(0x40, 0x21, 0x10, value));

        Assert.Empty(sink.Shorts);
    }

    [Fact]
    public void AGsPanpotBecomesControllerTenWhenConverted()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.GsToXgEmu = true);
        // 40 11 1C is block 1, channel 1.
        filter.SendLong(0, Gs(0x40, 0x11, 0x1C, 0x20));

        Assert.Equal([0x200AB0u], sink.Messages);
        Assert.Equal(0x20, filter.Panel.Part(0, 0).Panpot);
    }

    [Fact]
    public void AGsPanpotIsOnlyShownWhenNotConverted()
    {
        (EmulationFilter filter, CapturingSink sink) = Build();
        filter.SendLong(0, Gs(0x40, 0x11, 0x1C, 0x20));

        Assert.Empty(sink.Shorts);
        Assert.Equal(0x20, filter.Panel.Part(0, 0).Panpot);
    }

    [Fact]
    public void TheBPartsSettingsAreShownOnPortB()
    {
        (EmulationFilter filter, _) = Build();
        filter.SendLong(0, Gs(0x50, 0x11, 0x1C, 0x10));   // B part 1 panpot
        filter.SendLong(0, Gs(0x50, 0x11, 0x15, 0x01));   // B part 1 rhythm

        Assert.Equal(0x10, filter.Panel.Part(1, 0).Panpot);
        Assert.True(filter.Panel.Part(1, 0).Rhythm);
        Assert.NotEqual(0x10, filter.Panel.Part(0, 0).Panpot);
        Assert.False(filter.Panel.Part(0, 0).Rhythm);
    }

    [Fact]
    public void ABPartsRxChannelIsInTheUpperSixteenSlots()
    {
        (EmulationFilter filter, _) = Build();
        filter.SendLong(0, Gs(0x50, 0x10, 0x02, 0x03));   // block 0: channel 10

        Assert.Equal(3, filter.Panel.PartReceive[0x10 | 9] & 0x0F);
    }

    [Fact]
    public void MapsTheRxChannelBlockToAChannelSlot()
    {
        (EmulationFilter filter, _) = Build();
        // 40 10 02 is block 0, which is channel 10 (slot 9), not slot 0.
        filter.SendLong(0, Gs(0x40, 0x10, 0x02, 0x05));

        Assert.Equal(5, filter.Panel.PartReceive[9]);
        Assert.Equal(0, filter.Panel.PartReceive[0]);
    }

    [Fact]
    public void TreatsRxChannelValuesAboveFifteenAsOff()
    {
        (EmulationFilter filter, _) = Build();
        filter.SendLong(0, Gs(0x40, 0x11, 0x02, 0x10));   // block 1 -> slot 0

        Assert.Equal(0x7F, filter.Panel.PartReceive[0]);
    }

    [Fact]
    public void MapsTheRxPortBlockToAChannelSlotAndKeepsTheChannel()
    {
        (EmulationFilter filter, _) = Build();
        filter.SendLong(0, Gs(0x40, 0x11, 0x02, 0x03));   // block 1 -> slot 0, channel 3
        filter.SendLong(0, Gs(0x00, 0x01, 0x01, 0x01));   // CHANNEL MSG RX PORT block 1 -> B

        // Port bit set, channel preserved.
        Assert.Equal(0x13, filter.Panel.PartReceive[0]);
    }

    [Fact]
    public void PutsTheSecondHalfOfTheBlocksInTheUpperSlots()
    {
        (EmulationFilter filter, _) = Build();
        // BLOCK10 is address 00 01 10: block 0 of the upper half -> slot 9 | 0x10 = 25.
        filter.SendLong(0, Gs(0x00, 0x01, 0x10, 0x00));

        // B10 moved to port A, still on its own channel.
        Assert.Equal(0x09, filter.Panel.PartReceive[25]);
    }

    [Fact]
    public void KeepsThePortWhileAPartReceivesNothing()
    {
        (EmulationFilter filter, _) = Build();
        filter.SendLong(0, Gs(0x00, 0x01, 0x13, 0x00));   // B03 (slot 18) -> port A
        filter.SendLong(0, Gs(0x50, 0x13, 0x02, 0x10));   // B03 off
        Assert.Equal(0x7F, filter.Panel.PartReceive[18]);

        filter.SendLong(0, Gs(0x50, 0x13, 0x02, 0x05));   // B03 on channel 6
        Assert.Equal(0x05, filter.Panel.PartReceive[18]);

        filter.SendLong(0, Gs(0x40, 0x13, 0x02, 0x10));   // A03 off, then on again
        filter.SendLong(0, Gs(0x40, 0x13, 0x02, 0x02));
        Assert.Equal(0x02, filter.Panel.PartReceive[2]);
    }

    [Fact]
    public void ShowsWhatPortAPlaysOnTheBPartsListeningToIt()
    {
        (EmulationFilter filter, _) = Build();
        filter.SendLong(0, Gs(0x00, 0x01, 0x13, 0x00));   // B03 -> port A, channel 3

        filter.SendShort(0, 0x643C92);                    // note on, channel 3

        Assert.Equal(1, filter.Panel.Part(0, 2).Sounding);
        Assert.Equal(1, filter.Panel.Part(1, 2).Sounding);
        Assert.Equal(2, filter.Panel.SoundingNotes);
    }

    [Fact]
    public void ShowsNothingOnAPartThatReceivesNothing()
    {
        (EmulationFilter filter, _) = Build();
        filter.SendLong(0, Gs(0x40, 0x13, 0x02, 0x10));   // A03 off

        filter.SendShort(0, 0x643C92);

        Assert.Equal(0, filter.Panel.Part(0, 2).Sounding);
    }

    [Fact]
    public void PutsThePartsBackOnTheirOwnChannelsOnAReset()
    {
        (EmulationFilter filter, _) = Build();
        filter.SendLong(0, Gs(0x00, 0x01, 0x13, 0x00));
        filter.SendLong(0, Gs(0x40, 0x13, 0x02, 0x10));
        filter.SendLong(0, Gs(0x40, 0x00, 0x7F, 0x00));   // GS Reset

        Assert.Equal(0x02, filter.Panel.PartReceive[2]);
        Assert.Equal(0x12, filter.Panel.PartReceive[18]);
    }

    private static MidiSequence Song(params byte[][] sysEx)
    {
        byte[] smf = new SmfBuilder(division: 480)
            .Track(t =>
            {
                foreach (byte[] message in sysEx) t.SysEx(0, message.AsSpan(1));
                t.Short(0, 0x90, 60, 100).End(0);
            })
            .Build();
        return SmfReader.Read(smf);
    }

    [Fact]
    public void CountsPortBWhenASongOnPortAMovesABPartOver()
    {
        Assert.Equal(1, PanelState.PortsPlayed(Song()));
        Assert.Equal(1, PanelState.PortsPlayed(Song(Gs(0x00, 0x01, 0x13, 0x01))));
        Assert.Equal(1, PanelState.PortsPlayed(Song(Gs(0x00, 0x01, 0x03, 0x00))));
        Assert.Equal(2, PanelState.PortsPlayed(Song(Gs(0x00, 0x01, 0x13, 0x00))));

        // XG Receive Channel of part 17 set to port A's channel 1.
        byte[] xg = [0xF0, 0x43, 0x10, 0x4C, 0x08, 0x10, 0x04, 0x00, 0xF7];
        Assert.Equal(2, PanelState.PortsPlayed(Song(xg)));
    }

    [Fact]
    public void CarriesRxChannelAcrossToXg()
    {
        (EmulationFilter filter, CapturingSink sink) = Build(s => s.GsToXgEmu = true);
        filter.SendLong(0, Gs(0x40, 0x11, 0x02, 0x03));   // block 1 -> slot 0

        // The converted message goes out before the original is forwarded, as in
        // TMIDI. The part number and value come from the slot that was written.
        byte[] xg = sink.Longs.First(l => l.Data[1] == 0x43).Data;
        Assert.Equal(new byte[] { 0xF0, 0x43, 0x10, 0x4C, 0x08, 0x00, 0x04, 0x03, 0xF7 }, xg);
    }
}

public class PanelDisplayTests
{
    private static EmulationFilter Build()
        => new(new CapturingSink(), new EmulationSettings(), new PatchMapSet());

    /// <summary>F0 41 10 45 12 10 00 00 &lt;text&gt; &lt;sum&gt; F7</summary>
    private static byte[] RolandText(string text)
    {
        byte[] body = [0x10, 0x00, 0x00, .. text.Select(c => (byte)c)];
        int sum = body.Aggregate(0, (a, b) => a + b) & 0x7F;
        return [0xF0, 0x41, 0x10, 0x45, 0x12, .. body, (byte)((0x80 - sum) & 0x7F), 0xF7];
    }

    [Fact]
    public void CentresAShortRolandLineWithoutCountingTheChecksum()
    {
        EmulationFilter filter = Build();
        filter.SendLong(0, RolandText("ABC"));

        // Six leading spaces: (16 - 3) / 2.
        Assert.Equal("      ABC       ", new string(filter.Panel.LcdText));
        Assert.Equal(3000, filter.Panel.LcdHoldMs);
    }

    [Fact]
    public void CentresAnEvenRolandLineAsTheSc88ProDoes()
    {
        EmulationFilter filter = Build();
        filter.SendLong(0, RolandText("Glosa Demo"));

        // Three leading spaces on an SC-88Pro, not two.
        Assert.Equal("   Glosa Demo   ", new string(filter.Panel.LcdText));
    }

    [Fact]
    public void FramesALongRolandLineWithThePatchName()
    {
        EmulationFilter filter = Build();
        "Piano 1         ".CopyTo(0, filter.Panel.PatchName, 0, 16);
        filter.SendLong(0, RolandText("A LINE LONGER THAN SIXTEEN"));

        Assert.Equal("Piano 1         <A LINE LONGER THAN SIXTEEN<Piano 1         ",
                     filter.Panel.LcdScroll);
        Assert.Equal("Piano 1         ", new string(filter.Panel.LcdText));
        Assert.Equal(300, filter.Panel.LcdHoldMs);
    }

    [Fact]
    public void KeepsTheXgLineSeparateFromTheRolandOne()
    {
        EmulationFilter filter = Build();
        filter.SendLong(0, RolandText("ABC"));
        filter.SendLong(0, [0xF0, 0x43, 0x10, 0x4C, 0x06, 0x00, 0x00, (byte)'X', 0xF7]);

        Assert.Equal('X', filter.Panel.XgLcdText[0]);
        Assert.Equal("      ABC       ", new string(filter.Panel.LcdText));
    }

    [Fact]
    public void RestoresThePatchNameWhenAShortLineExpires()
    {
        EmulationFilter filter = Build();
        "Piano 1         ".CopyTo(0, filter.Panel.PatchName, 0, 16);
        filter.SendLong(0, RolandText("ABC"));

        Assert.Equal(PanelState.NoScroll, filter.Panel.LcdScrollIndex);

        filter.Panel.Advance(2999);
        Assert.Equal("      ABC       ", new string(filter.Panel.LcdText));

        filter.Panel.Advance(1);
        Assert.Equal("Piano 1         ", new string(filter.Panel.LcdText));
        Assert.Equal(0, filter.Panel.LcdScrollIndex);
        Assert.Equal(0, filter.Panel.LcdHoldMs);
    }

    [Fact]
    public void ShiftsALongLineInOneCharacterEvery300Ms()
    {
        EmulationFilter filter = Build();
        "Piano 1         ".CopyTo(0, filter.Panel.PatchName, 0, 16);
        filter.SendLong(0, RolandText("A LINE LONGER THAN SIXTEEN"));

        // The 16 columns on screen are the patch name; the next character in is the separator.
        Assert.Equal(PanelState.LcdColumns, filter.Panel.LcdScrollIndex);

        filter.Panel.Advance(300);
        Assert.Equal("iano 1         <", new string(filter.Panel.LcdText));
        Assert.Equal(300, filter.Panel.LcdHoldMs);

        filter.Panel.Advance(300);
        Assert.Equal("ano 1         <A", new string(filter.Panel.LcdText));
    }

    [Fact]
    public void StopsScrollingOnceTheBufferIsSpent()
    {
        EmulationFilter filter = Build();
        filter.SendLong(0, RolandText("A LINE LONGER THAN SIXTEEN"));

        for (int i = 0; i < 200; i++) filter.Panel.Advance(300);

        Assert.Equal(0, filter.Panel.LcdScrollIndex);
        Assert.Equal(0, filter.Panel.LcdHoldMs);

        // The tail of the scroll buffer is the patch name, blank here, so the line ends empty.
        string settled = new(filter.Panel.LcdText);
        filter.Panel.Advance(10_000);
        Assert.Equal(settled, new string(filter.Panel.LcdText));
    }

    [Fact]
    public void TakesTheXgLineOffAfterItsHold()
    {
        EmulationFilter filter = Build();
        filter.SendLong(0, [0xF0, 0x43, 0x10, 0x4C, 0x06, 0x00, 0x00, (byte)'X', 0xF7]);

        Assert.True(filter.Panel.XgLcdVisible);
        Assert.Equal(0xCE4, filter.Panel.LcdHoldMs);

        filter.Panel.Advance(0xCE4);
        Assert.False(filter.Panel.XgLcdVisible);
        Assert.Equal(0, filter.Panel.LcdHoldMs);
    }

    [Fact]
    public void TakesTheDotMatrixOffWhenItsPageTimeIsUp()
    {
        EmulationFilter filter = Build();
        // 10 20 01: display time, 3 steps. 10 20 00: show page 1, which loads that time.
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x20, 0x01, 0x03, 0x4C, 0xF7]);
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x20, 0x00, 0x01, 0x4F, 0xF7]);

        Assert.True(filter.Panel.BitmapVisible);
        Assert.Equal(3 * PanelState.BitmapHoldStepMs, filter.Panel.BitmapRemainingMs);

        filter.Panel.Advance(3 * PanelState.BitmapHoldStepMs - 1);
        Assert.True(filter.Panel.BitmapVisible);

        filter.Panel.Advance(1);
        Assert.False(filter.Panel.BitmapVisible);
        Assert.Equal(0, filter.Panel.BitmapRemainingMs);
    }

    [Fact]
    public void TheDisplayTimeIsCountedInStepsRatherThanMilliseconds()
    {
        EmulationFilter filter = Build();

        // The spec gives Display Time 0-15 over 0 to 7.2 seconds. Read as milliseconds, every
        // time a song can ask for would be a flash.
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x20, 0x01, 0x03, 0x4C, 0xF7]);
        Assert.Equal(1440, filter.Panel.BitmapHoldMs);

        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x20, 0x01, 0x0F, 0x40, 0xF7]);
        Assert.Equal(7200, filter.Panel.BitmapHoldMs);
    }

    [Fact]
    public void PageZeroIsTheBarDisplayRatherThanAPicture()
    {
        EmulationFilter filter = Build();
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x01, 0x00, 0x1F, 0x50, 0xF7]);
        Assert.True(filter.Panel.BitmapVisible);

        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x20, 0x00, 0x00, 0x50, 0xF7]);
        Assert.False(filter.Panel.BitmapVisible);
        Assert.Equal(0, filter.Panel.BitmapRemainingMs);
    }

    [Fact]
    public void PageOneIsTheOneWrittenStraightToTheDisplay()
    {
        EmulationFilter filter = Build();
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x01, 0x00, 0x1F, 0x50, 0xF7]);
        Assert.Equal(0x10, filter.Panel.Bitmap[0]);

        // Page 3 has had nothing written to it, so the matrix goes blank.
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x20, 0x00, 0x03, 0x4D, 0xF7]);
        Assert.Equal(0, filter.Panel.Bitmap[0]);

        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x20, 0x00, 0x01, 0x4F, 0xF7]);
        Assert.Equal(0x10, filter.Panel.Bitmap[0]);
    }

    [Fact]
    public void ADisplayTimeOfNothingShowsNothing()
    {
        EmulationFilter filter = Build();
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x20, 0x01, 0x00, 0x4F, 0xF7]);
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x01, 0x00, 0x1F, 0x50, 0xF7]);

        // Showing something for no time is not showing it. Left up it would stay for good:
        // the countdown only runs while there is something left of it.
        Assert.False(filter.Panel.BitmapVisible);
    }

    [Fact]
    public void ADotMatrixWithNoPageTimeStillComesDown()
    {
        EmulationFilter filter = Build();

        // 10 01 00: a picture written straight to the display. Most files put one up this
        // way and never send the page control that would set a time for it, so the time
        // has to be there already.
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x01, 0x00, 0x1F, 0x50, 0xF7]);

        Assert.True(filter.Panel.BitmapVisible);
        Assert.Equal(PanelState.DefaultBitmapHoldMs, filter.Panel.BitmapRemainingMs);

        filter.Panel.Advance(PanelState.DefaultBitmapHoldMs - 1);
        Assert.True(filter.Panel.BitmapVisible);

        filter.Panel.Advance(1);
        Assert.False(filter.Panel.BitmapVisible);
    }

    [Fact]
    public void AResetPutsTheDefaultPageTimeBack()
    {
        EmulationFilter filter = Build();
        filter.SendLong(0, [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x20, 0x01, 0x03, 0x4C, 0xF7]);
        Assert.Equal(1440, filter.Panel.BitmapHoldMs);

        filter.Panel.Reset();
        Assert.Equal(PanelState.DefaultBitmapHoldMs, filter.Panel.BitmapHoldMs);
    }

    [Fact]
    public void WritesTheOddRolandRowGroup()
    {
        EmulationFilter filter = Build();
        // 10 01 40: row group 1, column block 4, so only the leading dot of the byte lands.
        filter.SendLong(0,
            [0xF0, 0x41, 0x10, 0x45, 0x12, 0x10, 0x01, 0x40, 0x1F, 0x10, 0xF7]);

        Assert.Equal(0x10, filter.Panel.BitmapPages[1][20]);
        Assert.Equal(0, filter.Panel.BitmapPages[1][21]);
    }

    [Fact]
    public void KeepsTheLastDotOfAnXgBitmapRow()
    {
        EmulationFilter filter = Build();
        // 07 00 2n is the third column block of a row: x = 14, so the two leading dots land
        // and the other five are dropped.
        filter.SendLong(0, [0xF0, 0x43, 0x10, 0x4C, 0x07, 0x00, 0x20, 0x7F, 0xF7]);

        Assert.Equal(0x40, filter.Panel.Bitmap[14]);
        Assert.Equal(0x20, filter.Panel.Bitmap[15]);
        Assert.Equal(0, filter.Panel.Bitmap[16]);
    }
}

public class PanelTrackingTests
{
    private static EmulationFilter Build(Action<EmulationSettings>? configure = null)
    {
        var settings = new EmulationSettings();
        configure?.Invoke(settings);
        return new EmulationFilter(new CapturingSink(), settings, new PatchMapSet());
    }

    private static uint Msg(int status, int data1 = 0, int data2 = 0)
        => (uint)(status | data1 << 8 | data2 << 16);

    [Fact]
    public void RecordsTheControllersAPanelShows()
    {
        EmulationFilter f = Build();
        f.SendShort(0, Msg(0xB0, 7, 100));      // volume
        f.SendShort(0, Msg(0xB0, 0, 8));        // bank MSB
        f.SendShort(0, Msg(0xB0, 32, 2));       // bank LSB
        f.SendShort(0, Msg(0xB0, 10, 0x20));    // pan
        f.SendShort(0, Msg(0xB0, 11, 90));      // expression
        f.SendShort(0, Msg(0xB0, 91, 40));      // reverb send
        f.SendShort(0, Msg(0xC0, 48));          // program
        f.SendShort(0, Msg(0xE0, 0x00, 0x60));  // pitch bend

        DisplayPart p = f.Panel.Part(0, 0);
        Assert.Equal(100, p.Volume);
        Assert.Equal(8, p.BankMsb);
        Assert.Equal(2, p.BankLsb);
        Assert.Equal(0x20, p.Panpot);
        Assert.Equal(90, p.Expression);
        Assert.Equal(40, p.ReverbSend);
        Assert.Equal(48, p.Program);
        Assert.Equal(0x3000, p.PitchBend);
    }

    [Fact]
    public void TheLevelIsTheLoudestNoteThePartIsHolding()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        Assert.Equal(0, part.Level);

        Wide(f);
        f.SendShort(0, Msg(0x90, 60, 40));
        f.SendShort(0, Msg(0x90, 64, 100));
        Assert.Equal(Level(100), part.Level);

        // The loudest note goes; the quieter one it was covering is the level now.
        f.SendShort(0, Msg(0x80, 64, 0));
        Assert.Equal(Level(40), part.Level);

        f.SendShort(0, Msg(0x80, 60, 0));
        Assert.Equal(0, part.Level);
    }

    [Fact]
    public void TheLevelFollowsTheSquareLawTheControlsAreDefinedIn()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        Wide(f);
        f.SendShort(0, Msg(0x90, 60, 127));
        Assert.Equal(127, part.Level);

        // Half volume takes three quarters off, not a half: CC7 is 40*log10(v/127) dB,
        // which is (v/127) squared in amplitude.
        f.SendShort(0, Msg(0xB0, 7, 64));
        Assert.Equal(33, part.Level);           // 127 * (64/127)^2, rounded up

        // Expression multiplies the same way.
        f.SendShort(0, Msg(0xB0, 7, 127));
        f.SendShort(0, Msg(0xB0, 11, 64));
        Assert.Equal(33, part.Level);
    }

    [Fact]
    public void ControlsTheSongHasNotSentCountAsWhatAResetLeaves()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        // Zero means unsaid, not silence, so volume stands at 100 and expression at 127.
        f.SendShort(0, Msg(0x90, 60, 127));
        int unsaid = part.Level;

        f.SendShort(0, Msg(0xB0, 7, 100));
        f.SendShort(0, Msg(0xB0, 11, 127));
        Assert.Equal(unsaid, part.Level);
        Assert.NotEqual(127, unsaid);
    }

    [Fact]
    public void TheMasterVolumeComesOffWhatTheMeterShows()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        Wide(f);
        f.SendShort(0, Msg(0x90, 60, 127));
        Assert.Equal(127, f.Panel.LevelOf(part));

        // Half the master takes three quarters off, the same square law the part's own
        // controls follow: it is a volume knob, and those are defined in decibels.
        f.SendLong(0, [0xF0, 0x7F, 0x7F, 0x04, 0x01, 0x00, 0x40, 0xF7]);
        Assert.Equal(64, f.Panel.MasterVolume);
        Assert.Equal(33, f.Panel.LevelOf(part));

        // The part has not changed; only what comes out of the module past it.
        Assert.Equal(127, part.Level);
    }

    /// <summary>F0 43 10 4C &lt;addr&gt; &lt;data&gt; F7</summary>
    private static byte[] Xg(int a2, int a1, int a0, params byte[] data)
        => [0xF0, 0x43, 0x10, 0x4C, (byte)a2, (byte)a1, (byte)a0, .. data, 0xF7];

    [Fact]
    public void RecordsTheXgEffectTypes()
    {
        EmulationFilter f = Build();
        f.SendLong(0, Xg(0x02, 0x01, 0x00, 0x01, 0x10));   // reverb
        f.SendLong(0, Xg(0x02, 0x01, 0x20, 0x41, 0x02));   // chorus
        f.SendLong(0, Xg(0x02, 0x01, 0x40, 0x05, 0x00));   // variation
        f.SendLong(0, Xg(0x03, 0x00, 0x00, 0x49, 0x01));   // insertion 1
        f.SendLong(0, Xg(0x03, 0x01, 0x00, 0x4A, 0x00));   // insertion 2

        Assert.Equal([0x01, 0x10], Bytes(f.Panel.Reverb));
        Assert.Equal([0x02, 0x41], Bytes(f.Panel.Chorus));
        Assert.Equal([0x00, 0x05], Bytes(f.Panel.Variation));
        Assert.Equal([0x01, 0x49], Bytes(f.Panel.Insertion1));
        Assert.Equal([0x00, 0x4A], Bytes(f.Panel.Insertion2));
    }

    /// <summary>The two bytes of an effect selection, whichever field holds which.</summary>
    private static byte[] Bytes(EffectSlot slot) => [.. new[] { slot.Type, slot.Sub }.Order()];

    [Fact]
    public void RecordsWhichPartsTheXgEffectsAreOn()
    {
        EmulationFilter f = Build();
        f.SendLong(0, Xg(0x02, 0x01, 0x5B, 0x05));   // variation on part 6
        f.SendLong(0, Xg(0x03, 0x00, 0x0C, 0x02));   // insertion 1 on part 3
        f.SendLong(0, Xg(0x03, 0x01, 0x0C, 0x04));   // insertion 2 on part 5
        Assert.Equal(5, f.Panel.VariationPart);
        Assert.Equal(2, f.Panel.Insertion1Part);
        Assert.Equal(4, f.Panel.Insertion2Part);

        // Connected as a system effect, it is on no part in particular.
        f.SendLong(0, Xg(0x02, 0x01, 0x5A, 0x01));
        Assert.Equal(0xFF, f.Panel.VariationPart);
    }

    [Fact]
    public void RecordsEachXgPartsEqualiser()
    {
        EmulationFilter f = Build();
        f.SendLong(0, Xg(0x08, 0x03, 0x72, 0x50));
        f.SendLong(0, Xg(0x08, 0x03, 0x73, 0x30));
        f.SendLong(0, Xg(0x08, 0x13, 0x76, 0x10));   // part 20: port B, channel 4
        f.SendLong(0, Xg(0x08, 0x13, 0x77, 0x40));

        Assert.Equal(0x50, f.Panel.Part(0, 3).EqBassGain);
        Assert.Equal(0x30, f.Panel.Part(0, 3).EqTrebleGain);
        Assert.Equal(0x10, f.Panel.Part(1, 3).EqBassFrequency);
        Assert.Equal(0x40, f.Panel.Part(1, 3).EqTrebleFrequency);
    }

    [Theory]
    [InlineData(0x08, nameof(DisplayPart.VibratoRate))]
    [InlineData(0x09, nameof(DisplayPart.VibratoDepth))]
    [InlineData(0x0A, nameof(DisplayPart.VibratoDelay2))]
    [InlineData(0x20, nameof(DisplayPart.Cutoff))]
    [InlineData(0x21, nameof(DisplayPart.Resonance))]
    [InlineData(0x24, nameof(DisplayPart.VibratoDelay))]
    [InlineData(0x63, nameof(DisplayPart.Attack))]
    [InlineData(0x64, nameof(DisplayPart.Decay))]
    [InlineData(0x66, nameof(DisplayPart.Release))]
    [InlineData(0x30, nameof(DisplayPart.EqBassGain))]
    [InlineData(0x31, nameof(DisplayPart.EqTrebleGain))]
    [InlineData(0x34, nameof(DisplayPart.EqBassFrequency))]
    [InlineData(0x35, nameof(DisplayPart.EqTrebleFrequency))]
    public void ADataEntryLandsWhereTheNrpnPoints(int lsb, string property)
    {
        EmulationFilter f = Build();
        f.SendShort(0, Msg(0xB0, 99, 0x01));
        f.SendShort(0, Msg(0xB0, 98, lsb));
        f.SendShort(0, Msg(0xB0, 6, 0x5A));

        Assert.Equal((byte)0x5A, typeof(DisplayPart).GetProperty(property)!.GetValue(f.Panel.Part(0, 0)));
    }

    [Fact]
    public void ADataEntryAfterAnRpnLandsNowhere()
    {
        EmulationFilter f = Build();
        f.SendShort(0, Msg(0xB0, 99, 0x01));
        f.SendShort(0, Msg(0xB0, 98, 0x08));
        f.SendShort(0, Msg(0xB0, 101, 0x00));
        f.SendShort(0, Msg(0xB0, 100, 0x00));
        byte was = f.Panel.Part(0, 0).VibratoRate;

        f.SendShort(0, Msg(0xB0, 6, 0x5A));

        Assert.Equal(was, f.Panel.Part(0, 0).VibratoRate);
    }

    [Fact]
    public void AllThreeMasterVolumeMessagesReachTheSamePlace()
    {
        EmulationFilter f = Build();
        Assert.Equal(PanelState.FullVolume, f.Panel.MasterVolume);

        // GS 40 00 04.
        f.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x04, 0x40, 0x7C, 0xF7]);
        Assert.Equal(64, f.Panel.MasterVolume);

        // XG 00 00 04.
        f.SendLong(0, [0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x04, 0x20, 0xF7]);
        Assert.Equal(32, f.Panel.MasterVolume);

        // Universal, whose MSB is the byte everything else counts in.
        f.SendLong(0, [0xF0, 0x7F, 0x7F, 0x04, 0x01, 0x7F, 0x10, 0xF7]);
        Assert.Equal(16, f.Panel.MasterVolume);
    }

    [Fact]
    public void AResetOpensTheMasterVolumeAgain()
    {
        EmulationFilter f = Build();
        f.SendLong(0, [0xF0, 0x7F, 0x7F, 0x04, 0x01, 0x00, 0x10, 0xF7]);
        Assert.Equal(16, f.Panel.MasterVolume);

        // A GS Reset opens it without going through the whole panel.
        f.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7]);
        Assert.Equal(PanelState.FullVolume, f.Panel.MasterVolume);

        f.SendLong(0, [0xF0, 0x7F, 0x7F, 0x04, 0x01, 0x00, 0x10, 0xF7]);
        f.Panel.Reset();
        Assert.Equal(PanelState.FullVolume, f.Panel.MasterVolume);
    }

    [Fact]
    public void ThePeakFollowsTheMasterVolumeTheNoteWasStruckUnder()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        Wide(f);
        f.SendLong(0, [0xF0, 0x7F, 0x7F, 0x04, 0x01, 0x00, 0x40, 0xF7]);
        f.SendShort(0, Msg(0x90, 60, 127));
        f.Panel.Advance(33);

        Assert.Equal(33, part.Peak);
    }

    [Fact]
    public void AQuietNoteIsStillMoreThanNoNote()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        f.SendShort(0, Msg(0xB0, 7, 10));
        f.SendShort(0, Msg(0xB0, 11, 10));
        f.SendShort(0, Msg(0x90, 60, 10));
        Assert.Equal(1, part.Level);

        f.SendShort(0, Msg(0x80, 60, 0));
        Assert.Equal(0, part.Level);
    }

    /// <summary>Volume and expression wide open, so the level is the velocity alone.</summary>
    private static void Wide(EmulationFilter f)
    {
        f.SendShort(0, Msg(0xB0, 7, 127));
        f.SendShort(0, Msg(0xB0, 11, 127));
    }

    /// <summary>What <see cref="Wide"/> makes of a velocity: squared, rounded up.</summary>
    private static int Level(int velocity) => (velocity * velocity + 126) / 127;

    [Fact]
    public void ThePeakGoesUpAtOnceAndComesDownSlowly()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        Wide(f);
        f.SendShort(0, Msg(0x90, 60, 100));
        f.Panel.Advance(33);
        Assert.Equal(Level(100), part.Peak);

        // A louder note takes the mark with it straight away.
        f.SendShort(0, Msg(0x90, 64, 120));
        f.Panel.Advance(33);
        Assert.Equal(Level(120), part.Peak);

        f.SendShort(0, Msg(0x80, 60, 0));
        f.SendShort(0, Msg(0x80, 64, 0));
        Assert.Equal(0, part.Level);

        // Held where it was put before it starts to go.
        f.Panel.Advance(PanelState.PeakHoldMs - 100);
        Assert.Equal(Level(120), part.Peak);

        f.Panel.Advance(100);
        Assert.Equal(Level(120), part.Peak);

        // Then at its own rate, which is a second's worth here.
        f.Panel.Advance(1000);
        Assert.Equal(Level(120) - PanelState.PeakFallPerSecond, part.Peak);

        f.Panel.Advance(1000);
        Assert.Equal(0, part.Peak);
    }

    [Fact]
    public void TheHoldStartsAgainEveryTimeTheMarkIsPushedUp()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        Wide(f);
        f.SendShort(0, Msg(0x90, 60, 100));
        f.Panel.Advance(33);
        f.SendShort(0, Msg(0x80, 60, 0));
        f.Panel.Advance(PanelState.PeakHoldMs - 100);

        // Struck again while the mark was still being held, so the wait is the new note's
        // and not what was left of the old one's.
        f.SendShort(0, Msg(0x90, 60, 100));
        f.Panel.Advance(33);
        f.SendShort(0, Msg(0x80, 60, 0));

        f.Panel.Advance(PanelState.PeakHoldMs - 100);
        Assert.Equal(Level(100), part.Peak);
    }

    [Fact]
    public void ThePeakFallsEvenOnVisitsTooShortToOweAWholeUnit()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        Wide(f);
        f.SendShort(0, Msg(0x90, 60, 100));
        f.Panel.Advance(1);
        f.SendShort(0, Msg(0x80, 60, 0));
        f.Panel.Advance(PanelState.PeakHoldMs);     // the wait, out of the way

        // A fraction of a unit each time. Rounded away per visit the mark would simply
        // stay where it was.
        for (int i = 0; i < 1000; i++) f.Panel.Advance(1);

        Assert.Equal(Level(100) - PanelState.PeakFallPerSecond, part.Peak);
    }

    [Fact]
    public void AllNotesOffLetsGoOfWhatThePartIsHolding()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        Wide(f);
        f.SendShort(0, Msg(0x90, 60, 100));
        f.SendShort(0, Msg(0x90, 64, 100));
        Assert.Equal(2, part.Sounding);
        Assert.Equal(2, f.Panel.SoundingNotes);

        f.SendShort(0, Msg(0xB0, 123, 0));

        Assert.Equal(0, part.Level);
        Assert.Equal(0, part.Sounding);
        Assert.Equal(0, f.Panel.SoundingNotes);
        Assert.Equal(0, f.Panel.MelodicNotes[60]);
    }

    [Fact]
    public void AllNotesOffLeavesWhatThePedalIsHolding()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        Wide(f);
        f.SendShort(0, Msg(0xB0, 64, 127));     // pedal down
        f.SendShort(0, Msg(0x90, 60, 100));
        f.SendShort(0, Msg(0xB0, 123, 0));

        // The keys came up but the note is still sounding, which is what the module does.
        Assert.Equal(Level(100), part.Level);
        Assert.Equal(0, part.Sounding);

        // Reset All Controllers follows it out of the sequencer, and that lifts the pedal.
        f.SendShort(0, Msg(0xB0, 121, 0));
        Assert.Equal(0, part.Level);
        Assert.Equal(0, part.Hold);
    }

    [Fact]
    public void AllSoundOffStopsEvenWhatThePedalIsHolding()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        Wide(f);
        f.SendShort(0, Msg(0xB0, 64, 127));
        f.SendShort(0, Msg(0x90, 60, 100));
        f.SendShort(0, Msg(0xB0, 120, 0));

        Assert.Equal(0, part.Level);
    }

    [Fact]
    public void ResetAllControllersLeavesWhereTheSongPutThePart()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        f.SendShort(0, Msg(0xB0, 7, 90));       // volume
        f.SendShort(0, Msg(0xB0, 10, 0x20));    // pan
        f.SendShort(0, Msg(0xB0, 1, 80));       // modulation
        f.SendShort(0, Msg(0xB0, 11, 40));      // expression
        f.SendShort(0, Msg(0xE0, 0x00, 0x60));  // pitch bend

        f.SendShort(0, Msg(0xB0, 121, 0));

        Assert.Equal(90, part.Volume);
        Assert.Equal(0x20, part.Panpot);
        Assert.Equal(0, part.Modulation);
        Assert.Equal(127, part.Expression);
        Assert.Equal(0x2000, part.PitchBend);
    }

    [Fact]
    public void ThePeakComesDownOnceTheNotesAreLetGo()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        Wide(f);
        f.SendShort(0, Msg(0x90, 60, 100));
        f.Panel.Advance(33);
        Assert.Equal(Level(100), part.Peak);

        f.SendShort(0, Msg(0xB0, 123, 0));
        f.Panel.Advance(PanelState.PeakHoldMs);
        f.Panel.Advance(1000);

        Assert.Equal(Level(100) - PanelState.PeakFallPerSecond, part.Peak);
    }

    [Fact]
    public void ANoteHeldByThePedalStillCounts()
    {
        EmulationFilter f = Build();
        DisplayPart part = f.Panel.Part(0, 0);

        f.SendShort(0, Msg(0xB0, 64, 127));     // hold down
        f.SendShort(0, Msg(0x90, 60, 100));
        int down = part.Level;
        f.SendShort(0, Msg(0x80, 60, 0));       // key up, pedal still down

        Assert.True(down > 0);
        Assert.Equal(down, part.Level);
    }

    [Fact]
    public void CountsSoundingNotes()
    {
        EmulationFilter f = Build();
        f.SendShort(0, Msg(0x90, 60, 100));
        f.SendShort(0, Msg(0x90, 64, 100));
        f.SendShort(1, Msg(0x90, 67, 100));     // a second port counts too

        Assert.Equal(3, f.Panel.SoundingNotes);
        Assert.Equal(2, f.Panel.Part(0, 0).Sounding);
        Assert.Equal(100, f.Panel.Part(0, 0).NoteVelocity[60]);
        Assert.Equal(1, f.Panel.Part(0, 0).Notes[60] & 1);
        Assert.Equal(1, f.Panel.MelodicNotes[60]);

        f.SendShort(0, Msg(0x90, 60, 0));       // velocity zero is a note off
        f.SendShort(0, Msg(0x80, 64, 0));

        Assert.Equal(1, f.Panel.SoundingNotes);
        Assert.Equal(0, f.Panel.Part(0, 0).Sounding);
        Assert.Equal(0, f.Panel.MelodicNotes[60]);
        Assert.Equal(2, f.Panel.Part(0, 0).NotesPlayed);
    }

    [Fact]
    public void LeavesRhythmPartsOutOfTheKeyboard()
    {
        // A rhythm part's note numbers pick instruments, so they do not belong on a
        // keyboard display. Channel 10 starts rhythmic.
        EmulationFilter f = Build();
        f.SendShort(0, Msg(0x99, 36, 100));

        Assert.Equal(1, f.Panel.SoundingNotes);
        Assert.Equal(1, f.Panel.Part(0, 9).Sounding);
        Assert.Equal(0, f.Panel.MelodicNotes[36]);
    }

    [Fact]
    public void HoldsNotesWhileThePedalIsDown()
    {
        EmulationFilter f = Build();
        f.SendShort(0, Msg(0xB0, 64, 127));     // pedal down
        f.SendShort(0, Msg(0x90, 60, 100));

        Assert.Equal(3, f.Panel.Part(0, 0).Notes[60]);   // down and held

        f.SendShort(0, Msg(0x80, 60, 0));
        Assert.Equal(2, f.Panel.Part(0, 0).Notes[60]);   // still held by the pedal

        f.SendShort(0, Msg(0xB0, 64, 0));                // pedal up
        Assert.Equal(0, f.Panel.Part(0, 0).Notes[60]);
    }

    [Fact]
    public void CatchesNotesAlreadyDownWhenThePedalArrives()
    {
        EmulationFilter f = Build();
        f.SendShort(0, Msg(0x90, 60, 100));
        f.SendShort(0, Msg(0xB0, 64, 127));

        Assert.Equal(3, f.Panel.Part(0, 0).Notes[60]);
    }

    [Fact]
    public void RoutesDataEntryToTheSelectedNrpn()
    {
        EmulationFilter f = Build();
        f.SendShort(0, Msg(0xB0, 99, 0x01));    // NRPN MSB
        f.SendShort(0, Msg(0xB0, 98, 0x21));    // NRPN LSB -> 01 21, resonance
        f.SendShort(0, Msg(0xB0, 6, 70));

        Assert.Equal(70, f.Panel.Part(0, 0).Resonance);

        f.SendShort(0, Msg(0xB0, 101, 0));      // an RPN select clears the selector
        f.SendShort(0, Msg(0xB0, 6, 12));

        Assert.Equal(70, f.Panel.Part(0, 0).Resonance);
        Assert.Equal(0x7F7F, f.Panel.Part(0, 0).ParameterNumber);
    }

    [Fact]
    public void TracksTheMessageAsWrittenNotAsConverted()
    {
        // GM conversion moves a rhythm part's notes to channel 10, but the panel shows the
        // channel the song used.
        EmulationFilter f = Build(s => s.GsToGmEmu = true);
        f.Parts[0, 5].Mode = PartMode.Drum1;
        f.SendShort(0, Msg(0x95, 60, 100));

        Assert.Equal(1, f.Panel.Part(0, 5).Sounding);
        Assert.Equal(0, f.Panel.Part(0, 9).Sounding);
    }

    [Fact]
    public void LeavesThePanelAloneForAPortPastTheLast()
    {
        EmulationFilter f = Build();
        f.SendShort(IEventSink.PortCount, Msg(0x90, 60, 100));

        for (int port = 0; port < IEventSink.PortCount; port++)
            Assert.Equal(0, f.Panel.Part(port, 0).Sounding);
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Panel.Part(IEventSink.PortCount, 0));
    }
}

public class ResetTests
{
    private static EmulationFilter Build(Action<EmulationSettings>? configure = null)
    {
        var settings = new EmulationSettings();
        configure?.Invoke(settings);
        return new EmulationFilter(new CapturingSink(), settings, new PatchMapSet());
    }

    private static uint Msg(int status, int data1 = 0, int data2 = 0)
        => (uint)(status | data1 << 8 | data2 << 16);

    [Fact]
    public void ClearsWhatTheSongLeftBehind()
    {
        EmulationFilter f = Build();
        f.SendShort(0, Msg(0xB0, 0x00, 24));         // bank MSB
        f.SendShort(0, Msg(0xB0, 0x20, 3));          // bank LSB
        f.SendShort(0, Msg(0x90, 60, 100));          // a note still sounding
        f.SendLong(0, [0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E, 0x00, 0xF7]);

        f.Reset();

        Assert.Equal(0, f.Parts[0, 0].BankMsb);
        Assert.Equal(0, f.Parts[0, 0].BankLsb);
        Assert.Equal(0, f.Panel.SoundingNotes);
        Assert.Equal(0, f.Panel.Part(0, 0).Sounding);
        Assert.Equal(0, f.Panel.MelodicNotes[60]);
        Assert.False(f.Panel.XgSystemReset);
    }

    [Fact]
    public void PutsTheDrumChannelBackOnBothSides()
    {
        // The conversion reads the part mode and the display reads the rhythm flag. They
        // have to agree, so a reset restores both.
        EmulationFilter f = Build();
        f.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x10, 0x15, 0x00, 0x1B, 0xF7]);
        f.SendLong(0, [0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x11, 0x15, 0x02, 0x18, 0xF7]);

        Assert.Equal(PartMode.Melodic, f.Parts[0, 9].Mode);     // channel 10 turned melodic
        Assert.Equal(PartMode.Drum2, f.Parts[0, 0].Mode);       // channel 1 turned rhythmic

        f.Reset();

        Assert.Equal(PartMode.Drum1, f.Parts[0, 9].Mode);
        Assert.True(f.Panel.Part(0, 9).Rhythm);
        Assert.Equal(PartMode.Melodic, f.Parts[0, 0].Mode);
        Assert.False(f.Panel.Part(0, 0).Rhythm);
    }

    [Fact]
    public void FollowsAConfiguredDrumTrackOnReset()
    {
        EmulationFilter f = Build(s => s.DrumTrack = 15);
        f.Reset();

        Assert.Equal(PartMode.Drum1, f.Parts[0, 15].Mode);
        Assert.True(f.Panel.Part(0, 15).Rhythm);
        Assert.Equal(PartMode.Melodic, f.Parts[0, 9].Mode);
        Assert.False(f.Panel.Part(0, 9).Rhythm);
    }

    [Fact]
    public void LeavesThePlayersOwnSettingsAlone()
    {
        EmulationFilter f = Build();
        f.MasterVolume = 64;
        f.KeyShift = 3;

        f.Reset();

        Assert.Equal(64, f.MasterVolume);
        Assert.Equal(3, f.KeyShift);
    }
}
