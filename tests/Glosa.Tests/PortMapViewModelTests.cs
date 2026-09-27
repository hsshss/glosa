using Glosa.App;
using Glosa.App.Services;
using Glosa.App.ViewModels;
using Glosa.Midi;

namespace Glosa.Tests;

public class PortMapViewModelTests
{
    private static readonly MidiDeviceInfo Sc88 = new("0", "SC-88");
    private static readonly MidiDeviceInfo Mu80 = new("1", "MU80");
    private static readonly MidiDeviceInfo Sc88Again = new("2", "SC-88");

    private int _changes;

    private PortMapViewModel Open(PortMap map, params MidiDeviceInfo[] devices)
        => new(map, devices, _ => _changes++);

    [Fact]
    public void StoredPortsFindTheirDevicesByName()
    {
        PortMapViewModel map = Open(new PortMap { Ports = { ["A"] = "MU80", ["B"] = "SC-88" } }, Sc88, Mu80);

        Assert.Equal(Mu80, map.Ports[0].Device);
        Assert.Equal(Sc88, map.Ports[1].Device);
        Assert.Null(map.Ports[2].Device);
        Assert.Equal(0, _changes);
    }

    [Fact]
    public void TwoPortsOfOneNameTakeTheTwoDevicesOfThatName()
    {
        PortMapViewModel map = Open(new PortMap { Ports = { ["A"] = "SC-88", ["B"] = "SC-88" } },
                                    Sc88, Mu80, Sc88Again);

        Assert.Equal(Sc88, map.Ports[0].Device);
        Assert.Equal(Sc88Again, map.Ports[1].Device);
    }

    [Fact]
    public void AMissingDeviceKeepsItsNameAndSaysSo()
    {
        PortMapViewModel map = Open(new PortMap { Ports = { ["A"] = "SC-88" } }, Mu80);

        Assert.Null(map.Ports[0].Device);
        Assert.Equal("SC-88", map.Ports[0].Name);
        Assert.Equal(string.Format(Strings.DeviceMissing, "SC-88"), map.Ports[0].Placeholder);
        Assert.Equal(Strings.PortUnused, map.Ports[1].Placeholder);
    }

    [Fact]
    public void ChoosingADeviceByHandIsWrittenThroughAndTold()
    {
        var model = new PortMap { Ports = { ["A"] = "SC-88" } };
        PortMapViewModel map = Open(model, Sc88, Mu80);

        map.Ports[2].Device = Mu80;
        map.Ports[0].Device = null;

        Assert.Equal(new Dictionary<string, string> { ["C"] = "MU80" }, model.Ports);
        Assert.Equal(2, _changes);
    }

    [Fact]
    public void TheOpenListDropsTheUnusedTailButKeepsGapsAndMissingDevices()
    {
        PortMapViewModel map = Open(new PortMap { Ports = { ["A"] = "SC-88", ["C"] = "Gone" } }, Sc88);

        DeviceName?[] open = map.OpenList();

        Assert.Equal([new DeviceName("SC-88", 0), null, null], open);
    }

    [Fact]
    public void AMapWithNoPortsOpensNothing()
        => Assert.Empty(Open(new PortMap(), Sc88).OpenList());

    [Fact]
    public void DescribeNamesEachPortUpToTheLastUsed()
    {
        PortMapViewModel map = Open(new PortMap { Ports = { ["A"] = "SC-88", ["C"] = "Gone" } }, Sc88);

        Assert.Equal($"A=SC-88, B={Strings.None}, C={string.Format(Strings.DeviceMissing, "Gone")}",
                     map.Describe());
    }

    [Fact]
    public void LayingADeviceIsWrittenThroughWithoutTelling()
    {
        var model = new PortMap();
        PortMapViewModel map = Open(model, Sc88);

        map.Lay(0, Sc88);

        Assert.Equal("SC-88", model.Ports["A"]);
        Assert.Equal(Sc88, map.Ports[0].Device);
        Assert.Equal(0, _changes);
    }

    [Fact]
    public void NothingChosenAsTheOutputModuleIsNotStored()
    {
        var model = new PortMap { UseModule = "SC-88PRO" };
        PortMapViewModel map = Open(model);

        map.UseModule = null;
        Assert.Equal("SC-88PRO", model.UseModule);

        map.RestoreUseModule();
        Assert.Equal("SC-88PRO", map.UseModule);

        map.UseModule = "MU80";
        Assert.Equal("MU80", model.UseModule);
    }

    [Fact]
    public void TheResetPortsAreWrittenOnlyWhenEditedByHand()
    {
        var model = new PortMap { ResetPorts = ["A", "C"] };
        PortMapViewModel map = Open(model);

        Assert.True(map.Ports[0].Reset);
        Assert.False(map.Ports[1].Reset);
        Assert.True(map.Ports[2].Reset);

        map.Ports[1].Reset = true;
        map.Ports[0].Reset = false;

        Assert.Equal(["B", "C"], model.ResetPorts);
    }

    [Fact]
    public void TheModulesAndTitleAreWrittenThrough()
    {
        var model = new PortMap { Modules = ["SC-55"] };
        PortMapViewModel map = Open(model);

        map.Modules.Add("SC-88");
        map.Title = "Roland";

        Assert.Equal(["SC-55", "SC-88"], model.Modules);
        Assert.Equal("Roland", model.Title);
        Assert.True(map.Claims("sc-88"));
        Assert.False(map.Claims("MU80"));
    }

    [Fact]
    public void ThePortStateShowsAsAMark()
    {
        PortSlotViewModel slot = Open(new PortMap()).Ports[0];

        slot.ShowState((OutputState.Open, null));
        Assert.Equal("●", slot.StateMark);

        slot.ShowState((OutputState.Failed, "busy"));
        Assert.Equal("×", slot.StateMark);
        Assert.Equal(string.Format(Strings.PortCannotOpen, "busy"), slot.StateText);

        slot.ShowState((OutputState.Closed, null));
        Assert.Equal(string.Empty, slot.StateMark);
        Assert.Null(slot.StateText);
    }
}
