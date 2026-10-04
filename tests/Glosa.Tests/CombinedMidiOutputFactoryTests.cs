using Glosa.Midi;

namespace Glosa.Tests;

public class CombinedMidiOutputFactoryTests
{
    private sealed class Backend(string name, params string[] ids) : IMidiOutputFactory
    {
        public string BackendName => name;

        public IReadOnlyList<MidiDeviceInfo> Enumerate() => [.. ids.Select(id => new MidiDeviceInfo(id, $"{name} {id}"))];

        public IMidiOutput Create(string deviceId) => new Output(new MidiDeviceInfo(deviceId, name));
    }

    private sealed class Output(MidiDeviceInfo device) : IMidiOutput
    {
        public MidiDeviceInfo Device => device;
        public bool IsOpen => false;
        public void Open() { }
        public void SendShort(uint packedMessage) { }
        public void SendLong(ReadOnlySpan<byte> sysEx) { }
        public void Reset() { }
        public void Close(bool reset = true) { }
        public void Dispose() { }
        public int DroppedLongMessages => 0;
        public string? CloseError => null;
    }

    [Fact]
    public void ListsEveryBackendsDevicesInTheOrderGiven()
    {
        var combined = new CombinedMidiOutputFactory(new Backend("System", "0", "1"), new Backend("Plugins", "p:a"));

        Assert.Equal(["System 0", "System 1", "Plugins p:a"], combined.Enumerate().Select(device => device.Name));
        Assert.Equal("System + Plugins", combined.BackendName);
    }

    [Fact]
    public void OpensADeviceThroughTheBackendThatListsIt()
    {
        var combined = new CombinedMidiOutputFactory(new Backend("System", "0"), new Backend("Plugins", "p:a"));

        Assert.Equal("Plugins", combined.Create("p:a").Device.Name);
        Assert.Equal("System", combined.Create("0").Device.Name);
        Assert.Throws<MidiDeviceException>(() => combined.Create("p:gone"));
    }
}
