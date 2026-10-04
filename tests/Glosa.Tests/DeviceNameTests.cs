using Glosa.Midi;

namespace Glosa.Tests;

public class DeviceNameTests
{
    /// <summary>Two machines of one name, with another between them, as Windows lists them.</summary>
    private static readonly MidiDeviceInfo[] Devices =
    [
        new("0", "SC-55"),
        new("1", "Other"),
        new("2", "SC-55"),
        new("3", "Solo"),
    ];

    [Fact]
    public void TheNthPortNamingAMachineTakesTheNthDeviceByThatName()
    {
        DeviceName?[] names = DeviceName.ForPorts(["SC-55", "Other", "SC-55"], Devices);

        Assert.Equal(new DeviceName("SC-55", 0), names[0]);
        Assert.Equal(new DeviceName("Other", 0), names[1]);
        Assert.Equal(new DeviceName("SC-55", 1), names[2]);
        Assert.Equal("0", names[0]!.Value.Find(Devices)?.Id);
        Assert.Equal("2", names[2]!.Value.Find(Devices)?.Id);
    }

    [Fact]
    public void AnUnusedPortIsNullAndIsNotCounted()
    {
        DeviceName?[] names = DeviceName.ForPorts(["", "SC-55", "", "SC-55"], Devices);

        Assert.Null(names[0]);
        Assert.Null(names[2]);
        Assert.Equal(0, names[1]!.Value.Nth);
        Assert.Equal(1, names[3]!.Value.Nth);
    }

    [Fact]
    public void PortsPastTheDevicesThereAreTakeTheFirst()
    {
        DeviceName?[] names = DeviceName.ForPorts(["Solo", "SC-55", "Solo", "SC-55", "SC-55"], Devices);

        Assert.Equal(0, names[0]!.Value.Nth);
        Assert.Equal(0, names[2]!.Value.Nth);
        Assert.Equal(0, names[4]!.Value.Nth);
        Assert.Equal(1, names[3]!.Value.Nth);
    }

    [Fact]
    public void AMachineThatIsNotThereIsNamedButNotFound()
    {
        DeviceName?[] names = DeviceName.ForPorts(["Away", "Away"], Devices);

        Assert.Equal(new DeviceName("Away", 0), names[0]);
        Assert.Equal(new DeviceName("Away", 0), names[1]);
        Assert.Null(names[0]!.Value.Find(Devices));
    }

    [Fact]
    public void TheSecondPortFallsBackToTheFirstWhenOneOfTwoIsUnplugged()
    {
        MidiDeviceInfo[] left = [.. Devices.Where(device => device.Id != "2")];

        DeviceName?[] names = DeviceName.ForPorts(["SC-55", "SC-55"], left);

        Assert.Equal(names[0], names[1]);
        Assert.Equal("0", names[1]!.Value.Find(left)?.Id);
    }

    [Fact]
    public void AnNthNoLongerThereIsNotFound()
    {
        // Named while both were there; by the time it is opened, one has gone.
        var second = new DeviceName("SC-55", 1);

        Assert.Null(second.Find([.. Devices.Where(device => device.Id != "2")]));
    }

    [Fact]
    public void NamesMatchWithoutRegardToCase()
    {
        DeviceName?[] names = DeviceName.ForPorts(["sc-55", "SC-55"], Devices);

        Assert.Equal(new DeviceName("SC-55", 1), names[1]);
        Assert.Equal(new DeviceName("sc-55", 0), names[0]);
        Assert.Equal(new DeviceName("sc-55", 0).GetHashCode(), new DeviceName("SC-55", 0).GetHashCode());
        Assert.Equal("2", new DeviceName("sc-55", 1).Find(Devices)?.Id);
    }

    [Fact]
    public void TheSecondAndLaterShowWhichTheyAre()
    {
        Assert.Equal("SC-55", new DeviceName("SC-55", 0).ToString());
        Assert.Equal("SC-55 #2", new DeviceName("SC-55", 1).ToString());
    }

    [Fact]
    public void ANameNoDeviceHasFindsTheDeviceItIsTheOtherNameOf()
    {
        // Stored while another port shared the name; that port has gone since.
        MidiDeviceInfo[] left = [new("0", "UM-ONE MIDI 1", "UM-ONE MIDI 1 (UM-ONE)")];

        Assert.Equal("0", new DeviceName("UM-ONE MIDI 1 (UM-ONE)", 0).Find(left)?.Id);
    }

    [Fact]
    public void ADeviceOfThatNameComesBeforeOneThatIsOnlyCalledSo()
    {
        MidiDeviceInfo[] devices = [new("0", "Synth (A)", "Synth"), new("1", "Synth", "Synth (B)")];

        Assert.Equal("1", new DeviceName("Synth", 0).Find(devices)?.Id);
    }

    [Fact]
    public void TwoPortsStoredUnderTheOtherNameTakeOneMachineEach()
    {
        MidiDeviceInfo[] twins = [new("0", "UM-ONE MIDI 1", "UM-ONE MIDI 1 (UM-ONE)"),
                                  new("1", "UM-ONE MIDI 1", "UM-ONE MIDI 1 (UM-ONE)")];

        DeviceName?[] names = DeviceName.ForPorts(["UM-ONE MIDI 1 (UM-ONE)", "UM-ONE MIDI 1 (UM-ONE)"], twins);

        Assert.Equal("0", names[0]!.Value.Find(twins)?.Id);
        Assert.Equal("1", names[1]!.Value.Find(twins)?.Id);
    }

    /// <summary>A MIDI port and an audio plugin's port going by the same name, the port listed first.</summary>
    private static readonly MidiDeviceInfo[] PortAndPlugin =
    [
        new("3", "SC-8850 1"),
        new("brack:0:sc", "SC-8850 1", Kind: MidiDeviceKind.AudioPlugin),
    ];

    [Fact]
    public void AnAudioPluginIsStoredAsOneAndFindsOnlyItselfBesideAPortOfItsName()
    {
        string plugin = DeviceName.KeyOf(PortAndPlugin[1]);

        Assert.Equal("plugin:SC-8850 1", plugin);
        Assert.Equal("brack:0:sc", DeviceName.ForPorts([plugin], PortAndPlugin)[0]!.Value.Find(PortAndPlugin)?.Id);
        Assert.Equal("3", DeviceName.ForPorts(["SC-8850 1"], PortAndPlugin)[0]!.Value.Find(PortAndPlugin)?.Id);
    }

    [Fact]
    public void APortAndAPluginOfOneNameAreNotCountedTogether()
    {
        DeviceName?[] names = DeviceName.ForPorts(["plugin:SC-8850 1", "SC-8850 1"], PortAndPlugin);

        Assert.Equal("brack:0:sc", names[0]!.Value.Find(PortAndPlugin)?.Id);
        Assert.Equal("3", names[1]!.Value.Find(PortAndPlugin)?.Id);
    }

    [Fact]
    public void AnAudioPluginThatIsNotThereIsNotTakenForAPortOfItsName()
        => Assert.Null(DeviceName.ForPorts(["plugin:SC-8850 1"], [PortAndPlugin[0]])[0]!.Value.Find([PortAndPlugin[0]]));
}
