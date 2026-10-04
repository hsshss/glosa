#if BRACK
using System.Reflection;
using Brack;
using Glosa.Midi;
using Glosa.Midi.Brack;

namespace Glosa.Tests;

/// <summary>
/// The rack against Brack itself. Tests that play need Brack's test instrument, built beside
/// this repository; without it they pass without running.
/// </summary>
public sealed class BrackRackTests : IDisposable
{
    private static readonly string? Synth = typeof(BrackRackTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "BrackTestSynth")?.Value;

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

        IMidiOutput output = Output(rack);
        output.Open();
        output.SendShort(0x90 | 60 << 8 | 100 << 16);
        Assert.True(SpinWait.SpinUntil(() => rack.Meters() is { Running: true } meters && meters.Peaks.Any(peak => peak > 0),
                                       TimeSpan.FromSeconds(5)),
                    "the meters showed no sound");

        output.Close();
        Assert.True(SpinWait.SpinUntil(() => !rack.Meters().Running, TimeSpan.FromSeconds(10)), "the meters did not stop");
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
            => new(id, name, "VST3", "x64", state, string.Empty, true, ports);

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
