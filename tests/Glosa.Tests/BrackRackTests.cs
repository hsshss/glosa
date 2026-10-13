#if BRACK
using System.Reflection;
using Brack;
using Glosa.Midi;
using Glosa.Midi.Brack;

namespace Glosa.Tests;

/// <summary>
/// The rack against Brack itself. Tests that play need Brack's test instrument and effect,
/// built beside this repository; without them they pass without running.
/// </summary>
public sealed class BrackRackTests : IDisposable
{
    private static readonly string? Synth = Built("BrackTestSynth");

    /// <summary>Halves what comes to it (<see cref="GainId"/>).</summary>
    private static readonly string? Gain = Built("BrackTestGain");

    private const string GainId = "brack.test.gain";

    private static string? Built(string key) => typeof(BrackRackTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == key)?.Value;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"glosa-brack-{Guid.NewGuid():N}");

    public BrackRackTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string SessionPath => Path.Combine(_folder, "brack-session.json");

    private BrackRack Rack()
    {
        BrackRack? rack = BrackRack.TryCreate(SessionPath, out string? why);
        Assert.True(rack is not null, why);
        rack.WaitLoaded();
        // Inaudible, yet above zero on the meter.
        rack.Engine.MasterGain = 1e-4f;
        return rack;
    }

    private static bool HaveSynth()
    {
        if (Synth is null) return false;
        // A file, or a bundle folder on macOS.
        Assert.True(Path.Exists(Synth), $"Brack's test instrument has not been built: {Synth}");
        return true;
    }

    private static bool HaveGain()
    {
        if (!HaveSynth() || Gain is null) return false;
        Assert.True(Path.Exists(Gain), $"Brack's test effect has not been built: {Gain}");
        return true;
    }

    /// <summary>Plays a held note on the rack's first instrument, returning the output that plays it.</summary>
    private static IMidiOutput Playing(BrackRack rack)
    {
        IMidiOutput output = Output(rack);
        output.Open();
        output.SendShort(0x90 | 69 << 8 | 127 << 16);
        return output;
    }

    /// <summary>Waits until <paramref name="level"/> settles inside a band, the meters falling away slowly.</summary>
    private static void Settles(BrackRack rack, Func<RackMeters, float> level, float low, float high, string what)
        => Assert.True(SpinWait.SpinUntil(() => rack.Meters() is { Running: true } meters
                                                && level(meters) is var peak && peak >= low && peak <= high,
                                          TimeSpan.FromSeconds(10)),
                       $"{what}: {level(rack.Meters())}, not within {low} to {high}");

    private static float InstrumentPeak(RackMeters meters, string id)
        => meters.Instruments.TryGetValue(id, out ChainMeters? chain) ? chain.Output.Max(level => level.Peak) : 0;

    private static IMidiOutput Output(BrackRack rack) => rack.Outputs.Create(rack.Outputs.Enumerate()[0].Id);

    [Fact]
    public void APluginAddedIsAnOutputByItsNameThatSoundsWhenPlayed()
    {
        if (!HaveSynth()) return;
        using BrackRack rack = Rack();
        rack.Add(Synth!);

        MidiDeviceInfo device = Assert.Single(rack.Outputs.Enumerate());
        Assert.Equal(rack.Plugins[0].Name, device.Name);
        Assert.Equal(MidiDeviceKind.AudioPlugin, device.Kind);

        using IMidiOutput output = rack.Outputs.Create(device.Id);
        output.Open();
        EngineStatus before = rack.Engine.GetStatus();
        Assert.Equal(EngineMode.Device, before.Mode);
        Assert.All(before.OutputPeaks, peak => Assert.Equal(0, peak));

        output.SendShort(0x90 | 60 << 8 | 100 << 16);
        Assert.True(SpinWait.SpinUntil(() => rack.Engine.GetStatus().OutputPeaks.Any(peak => peak > 0), TimeSpan.FromSeconds(5)),
                    "the note made no sound");
    }

    [Fact]
    public void TheLogFromBeforeItIsTakenComesFirstAndTheRestAsItComes()
    {
        if (!HaveSynth()) return;
        using BrackRack rack = Rack();
        rack.Add(Synth!);
        IMidiOutput output = Output(rack);
        output.Open();

        var taken = new System.Collections.Concurrent.ConcurrentQueue<(BrackLogLevel Level, string Message)>();
        rack.TakeLog((level, message) => taken.Enqueue((level, message)));
        Assert.Contains(taken, line => line.Level == BrackLogLevel.Info && line.Message.StartsWith("audio: "));

        // The test instrument's word for crashing in its audio processing.
        output.SendLong([0xF0, 0x7D, 0x63, 0x01, 0xF7]);
        Assert.True(SpinWait.SpinUntil(() => taken.Any(line => line.Level == BrackLogLevel.Error), TimeSpan.FromSeconds(10)),
                    "the crash was not in the log");
        output.Close();
    }

    [Fact]
    public void TheMetersShowTheSoundWhileTheAudioRuns()
    {
        if (!HaveSynth()) return;
        using BrackRack rack = Rack();
        rack.Add(Synth!);
        Assert.False(rack.Meters().Running);

        string id = rack.Plugins[0].Id;
        IMidiOutput output = Output(rack);
        output.Open();
        output.SendShort(0x90 | 60 << 8 | 100 << 16);
        Assert.True(SpinWait.SpinUntil(() => rack.Meters() is { Running: true } meters
                                             && meters.Output.Any(level => level.Peak > 0)
                                             && meters.MasterInput.Any(level => level.Peak > 0)
                                             && meters.Instruments[id] is { Active: true, MidiMessages: > 0 } chain
                                             && chain.Output.Count == 2 && chain.Output.Any(level => level.Peak > 0),
                                       TimeSpan.FromSeconds(5)),
                    "the meters showed no sound");
        RackMeters running = rack.Meters();
        Assert.True(running.OutputRate > 0 && running.PluginRate > 0 && running.Channels >= 2,
                    $"{running.OutputRate} Hz, plugins {running.PluginRate} Hz, {running.Channels} channels");

        output.Close();
        Assert.True(SpinWait.SpinUntil(() => !rack.Meters().Running, TimeSpan.FromSeconds(10)), "the meters did not stop");
        Assert.Empty(rack.Meters().Instruments);
    }

    [Fact]
    public void AnEffectAddedToAPlayingInstrumentProcessesItsSoundUnlessBypassed()
    {
        if (!HaveGain()) return;
        using BrackRack rack = Rack();
        // The second, playing the same note without the effect, is what the first is measured against.
        rack.Add(Synth!);
        rack.Add(Synth!);
        string id = rack.Plugins[0].Id, dry = rack.Plugins[1].Id;
        using IMidiOutput output = Playing(rack);
        using IMidiOutput beside = rack.Outputs.Create(rack.Outputs.Enumerate()[1].Id);
        beside.Open();
        beside.SendShort(0x90 | 69 << 8 | 127 << 16);

        // Straight after the note, which may arrive while Brack holds the chain to insert the effect.
        rack.AddEffect(id, 0, Gain!, GainId);
        RackPlugin effect = Assert.Single(rack.Plugins[0].Effects);
        Assert.Equal((RackPluginState.Ready, false), (effect.State, effect.Bypass));
        Assert.Empty(rack.Plugins[1].Effects);
        Settles(rack, meters => Against(meters, id, dry), 0.4f, 0.6f, "halved");

        rack.SetBypass(effect.Id, true);
        Assert.True(rack.Plugins[0].Effects[0].Bypass);
        Settles(rack, meters => Against(meters, id, dry), 0.9f, 1.1f, "bypassed");

        static float Against(RackMeters meters, string id, string dry)
            => InstrumentPeak(meters, dry) is > 0.1f and var reference ? InstrumentPeak(meters, id) / reference : 0;
    }

    [Fact]
    public void TheMastersEffectProcessesTheMix()
    {
        if (!HaveGain()) return;
        using BrackRack rack = Rack();
        rack.Add(Synth!);
        using IMidiOutput output = Playing(rack);

        rack.AddEffect(null, 0, Gain!, GainId);

        Assert.Equal(RackPluginState.Ready, Assert.Single(rack.MasterEffects).State);
        Assert.Empty(rack.Plugins[0].Effects);
        Assert.True(SpinWait.SpinUntil(() => rack.Meters() is { Running: true } meters
                                             && meters.MasterInput.Max(level => level.Peak) is > 0.1f and var input
                                             && meters.MasterOutput.Max(level => level.Peak) is var processed
                                             && processed > input * 0.4f && processed < input * 0.6f,
                                       TimeSpan.FromSeconds(10)),
                    "the master's effect did not halve the mix");
    }

    [Fact]
    public void TheEffectsTheirBypassAndTheirNamesAreKeptAndNamedBeforeTheRackHasLoaded()
    {
        if (!HaveGain()) return;
        using (BrackRack rack = Rack())
        {
            rack.Add(Synth!);
            string id = rack.Plugins[0].Id;
            rack.AddEffect(id, 0, Gain!, GainId);
            rack.AddEffect(id, 1, Gain!, GainId);
            rack.AddEffect(null, 0, Gain!, GainId);
            RackPlugin[] effects = [.. rack.Plugins[0].Effects];
            Assert.Empty(rack.Rename(effects[0].Id, "First"));
            rack.Rename(effects[1].Id, "Second");
            rack.SetBypass(effects[1].Id, true);
            rack.Move(effects[1].Id, 0);
            rack.Rename(rack.MasterEffects[0].Id, "Master");
        }

        using BrackRack again = BrackRack.TryCreate(SessionPath, out _)!;
        // Listed at once, loaded or not.
        Assert.Equal([("Second", true), ("First", false)], again.Plugins[0].Effects.Select(effect => (effect.Name, effect.Bypass)));
        Assert.Equal(["Master"], again.MasterEffects.Select(effect => effect.Name));
        again.WaitLoaded();
        Assert.Equal([("Second", true), ("First", false)], again.Plugins[0].Effects.Select(effect => (effect.Name, effect.Bypass)));
        Assert.All(again.Plugins[0].Effects.Concat(again.MasterEffects),
                   effect => Assert.Equal(RackPluginState.Ready, effect.State));
        Assert.Single(again.Outputs.Enumerate());
    }

    [Fact]
    public void AnInstrumentRemovedTakesItsEffectsWithIt()
    {
        if (!HaveGain()) return;
        using BrackRack rack = Rack();
        rack.Add(Synth!);
        rack.AddEffect(rack.Plugins[0].Id, 0, Gain!, GainId);
        string effect = rack.Plugins[0].Effects[0].Id;

        rack.Remove(rack.Plugins[0].Id);

        Assert.Empty(rack.Plugins);
        Assert.Throws<BrackException>(() => rack.SetBypass(effect, true));
    }

    [Fact]
    public void TheAudioRunsOnForAWhileAfterTheLastOutputClosesAndThenStops()
    {
        if (!HaveSynth()) return;
        using BrackRack rack = Rack();
        rack.Add(Synth!);

        IMidiOutput output = Output(rack);
        output.Open();
        output.Close();

        Assert.Equal(EngineMode.Device, rack.Engine.GetStatus().Mode);
        Assert.True(SpinWait.SpinUntil(() => rack.Engine.GetStatus().Mode == EngineMode.Stopped, TimeSpan.FromSeconds(10)),
                    "the audio did not stop");
    }

    [Fact]
    public void TheRackIsKeptAndItsFileNamesTheOutputsBeforeItHasLoaded()
    {
        if (!HaveSynth()) return;
        string name;
        using (BrackRack rack = Rack())
        {
            rack.Add(Synth!);
            name = rack.Plugins[0].Name;
        }

        using BrackRack again = BrackRack.TryCreate(SessionPath, out _)!;
        // Listed at once, loaded or not.
        Assert.Equal([name], again.Outputs.Enumerate().Select(device => device.Name));
        again.WaitLoaded();
        Assert.Equal(RackPluginState.Ready, Assert.Single(again.Plugins).State);
    }

    [Fact]
    public void ARackWithPluginsIsSavedOnClosingEvenWithNoChangeCounted()
    {
        if (!HaveSynth()) return;
        using (BrackRack rack = Rack()) rack.Add(Synth!);

        // Not Rack(), whose gain is a change.
        BrackRack again = BrackRack.TryCreate(SessionPath, out _)!;
        again.WaitLoaded();
        File.Delete(SessionPath);
        again.Dispose();

        Assert.True(File.Exists(SessionPath));
    }

    [Fact]
    public void AnEmptyRackUnchangedIsNotSavedOnClosing()
    {
        // Not Rack(), whose gain is a change.
        using (BrackRack rack = BrackRack.TryCreate(SessionPath, out _)!) rack.WaitLoaded();

        Assert.False(File.Exists(SessionPath));
    }

    [Fact]
    public void ACrashedPluginReloadedPlaysAgainUnderItsName()
    {
        if (!HaveSynth()) return;
        using BrackRack rack = Rack();
        rack.Add(Synth!);
        string id = rack.Plugins[0].Id;
        rack.Rename(id, "Lead");
        using IMidiOutput output = Output(rack);
        output.Open();

        // The test instrument's word for crashing in its audio processing.
        output.SendLong([0xF0, 0x7D, 0x63, 0x01, 0xF7]);
        Assert.True(SpinWait.SpinUntil(() => rack.Find(id)?.State == RackPluginState.Crashed, TimeSpan.FromSeconds(10)),
                    "the plugin did not crash");
        rack.Reload(id);

        Assert.Equal(("Lead", RackPluginState.Ready), (rack.Plugins[0].Name, rack.Plugins[0].State));
        output.SendShort(0x90 | 60 << 8 | 100 << 16);
        Assert.True(SpinWait.SpinUntil(() => rack.Engine.GetStatus().OutputPeaks.Any(peak => peak > 0), TimeSpan.FromSeconds(5)),
                    "the note made no sound");
    }

    [Fact]
    public void ANewRackConvertsAtTheBestAndItsBufferIsLongAndTheBufferAndThePluginsRateAreKept()
    {
        using (BrackRack rack = Rack())
        {
            Assert.Equal(20, Math.Round(rack.Buffer.TotalMilliseconds));
            Assert.Equal(0, rack.PluginSampleRate);
            Assert.Equal(ResamplerQuality.Ultra, rack.Engine.GetConfig().ResamplerQuality);

            rack.Buffer = TimeSpan.FromMilliseconds(40);
            rack.PluginSampleRate = 96000;
        }

        using BrackRack again = Rack();
        Assert.Equal((40, 96000), (Math.Round(again.Buffer.TotalMilliseconds), again.PluginSampleRate));
    }

    [Fact]
    public void LoadingThePluginsOneAtATimeIsKept()
    {
        using (BrackRack rack = Rack())
        {
            Assert.False(rack.LoadPluginsSerially);
            rack.LoadPluginsSerially = true;
        }

        using BrackRack again = Rack();
        Assert.True(again.LoadPluginsSerially);
    }

    [Fact]
    public void TheMasterGainIsKept()
    {
        // Not Rack(), whose gain is a change.
        using (BrackRack rack = BrackRack.TryCreate(SessionPath, out _)!)
        {
            rack.WaitLoaded();
            rack.MasterGain = 0.5f;
        }

        using BrackRack again = BrackRack.TryCreate(SessionPath, out _)!;
        again.WaitLoaded();
        Assert.Equal(0.5f, again.MasterGain);
    }

    [Fact]
    public void SendingToAPluginTakenOutOfTheRackFails()
    {
        if (!HaveSynth()) return;
        using BrackRack rack = Rack();
        rack.Add(Synth!);

        IMidiOutput output = Output(rack);
        output.Open();
        rack.Remove(rack.Plugins[0].Id);

        Assert.Throws<MidiDeviceException>(() => output.SendShort(0x90 | 60 << 8 | 100 << 16));
        output.Close();
    }

    [Fact]
    public void ARenamedPluginsOutputsTakeTheNewNameAndItIsKept()
    {
        if (!HaveSynth()) return;
        string before;
        using (BrackRack rack = Rack())
        {
            rack.Add(Synth!);
            before = rack.Plugins[0].Name;

            Assert.Equal([(before, "Lead")],
                         rack.Rename(rack.Plugins[0].Id, "Lead").Select(devices => (devices.From.Name, devices.To.Name)));
            Assert.Equal(["Lead"], rack.Outputs.Enumerate().Select(device => device.Name));
        }

        using BrackRack again = Rack();
        Assert.Equal(["Lead"], again.Outputs.Enumerate().Select(device => device.Name));
    }

    [Fact]
    public void APluginAddedUnderANameTheRackHasTakesTheLowestNumberFree()
    {
        if (!HaveSynth()) return;
        using BrackRack rack = Rack();
        rack.Add(Synth!);
        string name = rack.Plugins[0].Name;
        rack.Add(Synth!);
        rack.Add(Synth!);
        Assert.Equal([name, $"{name} (2)", $"{name} (3)"], rack.Plugins.Select(plugin => plugin.Name));

        rack.Remove(rack.Plugins[1].Id);
        rack.Add(Synth!);

        Assert.Equal([name, $"{name} (3)", $"{name} (2)"], rack.Plugins.Select(plugin => plugin.Name));
    }

    [Fact]
    public void AMovedPluginsOutputsAreListedInItsNewPlace()
    {
        if (!HaveSynth()) return;
        using BrackRack rack = Rack();
        rack.Add(Synth!);
        rack.Add(Synth!);
        rack.Rename(rack.Plugins[0].Id, "First");
        rack.Rename(rack.Plugins[1].Id, "Second");

        rack.Move(rack.Plugins[1].Id, 0);

        Assert.Equal(["Second", "First"], rack.Outputs.Enumerate().Select(device => device.Name));
    }

    [Fact]
    public void EachNotePortIsAnOutputNumberedOnlyWhenThereIsMoreThanOne()
    {
        RackPlugin Plugin(string id, string name, int ports, RackPluginState state = RackPluginState.Ready)
            => new(id, name, "VST3", "x64", state, string.Empty, true, ports, false, []);

        IReadOnlyList<MidiDeviceInfo> devices = BrackOutputFactory.Devices(
            [Plugin("sc", "SC-VA", 2), Plugin("piano", "Piano", 1), Plugin("fx", "No MIDI", 0),
             Plugin("loading", "Strings", 0, RackPluginState.Loading), Plugin("gone", "Organ", 0, RackPluginState.Failed)]);

        Assert.Equal(["SC-VA 1", "SC-VA 2", "Piano", "Strings", "Organ"], devices.Select(device => device.Name));
        Assert.Equal(devices.Count, devices.Select(device => device.Id).Distinct().Count());
        Assert.All(devices, device => Assert.Equal(MidiDeviceKind.AudioPlugin, device.Kind));
    }

    [Fact]
    public void APluginWhoseFileIsGoneStaysInTheRackAndWillNotOpen()
    {
        File.WriteAllText(SessionPath, """
            {"format": "brack-session", "version": 1,
             "plugins": [{"id": "gone", "path": "C:\\nowhere\\Gone.clap", "name": "Gone"}]}
            """);
        using BrackRack rack = Rack();

        Assert.Equal(RackPluginState.Failed, Assert.Single(rack.Plugins).State);
        Assert.Equal(["Gone"], rack.Outputs.Enumerate().Select(device => device.Name));
        Assert.Throws<MidiDeviceException>(Output(rack).Open);
    }

    [Fact]
    public void AFileThatWillNotLoadIsSetAsideRatherThanWrittenOver()
    {
        File.WriteAllText(SessionPath, "not a session");
        using BrackRack rack = Rack();

        Assert.Empty(rack.Plugins);
        SetAsideFile aside = Assert.IsType<SetAsideFile>(rack.SetAside);
        Assert.Equal(SessionPath + ".broken", aside.Path);
        Assert.NotEmpty(aside.Why);
        Assert.Equal("not a session", File.ReadAllText(aside.Path));
    }
}
#endif
