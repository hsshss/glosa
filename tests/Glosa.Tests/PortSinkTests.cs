using Glosa.Core.Playback;
using Glosa.Midi;

namespace Glosa.Tests;

/// <summary>An output that only remembers what it was handed.</summary>
internal sealed class FakeOutput(string name) : IMidiOutput
{
    public List<uint> Shorts { get; } = [];

    public List<byte[]> Longs { get; } = [];

    public MidiDeviceInfo Device { get; } = new(name, name);

    public bool IsCable { get; init; }

    public bool IsOpen { get; private set; }

    public void Open() => IsOpen = true;

    public void SendShort(uint packedMessage) => Shorts.Add(packedMessage);

    public void SendLong(ReadOnlySpan<byte> sysEx) => Longs.Add(sysEx.ToArray());

    public void Reset() { }

    /// <summary>How many times it was asked to wait for what it was handed.</summary>
    public int Waits { get; private set; }

    public void WaitUntilSent() => Waits++;

    public void Close(bool reset = true) => IsOpen = false;

    public void Dispose() => Close();

    public int DroppedLongMessages => 0;

    public string? CloseError => null;
}

public class PortSinkTests
{
    private static (PortSink Sink, FakeOutput A, FakeOutput C) Build()
    {
        var a = new FakeOutput("A");
        var c = new FakeOutput("C");
        // Port B is a socket with nothing in it.
        return (new PortSink([a, null, c]), a, c);
    }

    [Fact]
    public void SendsToThePortItIsAddressedTo()
    {
        (PortSink sink, FakeOutput a, FakeOutput c) = Build();

        sink.SendShort(0, 0x643C90);
        sink.SendShort(2, 0x643E90);

        Assert.Equal(0x643C90u, Assert.Single(a.Shorts));
        Assert.Equal(0x643E90u, Assert.Single(c.Shorts));
    }

    [Fact]
    public void SaysWhichPortsGoDownACable()
    {
        var sink = new PortSink([new FakeOutput("A") { IsCable = true }, null, new FakeOutput("C")]);

        Assert.True(sink.IsCable(0));
        Assert.False(sink.IsCable(1));
        Assert.False(sink.IsCable(2));
        Assert.False(sink.IsCable(IEventSink.PortCount));
    }

    [Fact]
    public void TheEmulationLayersPassOnWhichPortsGoDownACable()
    {
        var ports = new PortSink([new FakeOutput("A") { IsCable = true }, new FakeOutput("B")]);
        var filter = new Glosa.Core.Emulation.EmulationFilter(
            new Glosa.Core.Emulation.CapitalToneFallback(ports),
            new Glosa.Core.Emulation.EmulationSettings(), new Glosa.Core.Emulation.PatchMapSet());

        Assert.True(filter.IsCable(0));
        Assert.False(filter.IsCable(1));
    }

    [Fact]
    public void DropsWhatIsAddressedToAPortWithNothingOnIt()
    {
        (PortSink sink, FakeOutput a, FakeOutput c) = Build();

        sink.SendShort(1, 0x643C90);
        sink.SendLong(1, [0xF0, 0x7E, 0x7F, 0x09, 0x01, 0xF7]);

        Assert.Empty(a.Shorts);
        Assert.Empty(a.Longs);
        Assert.Empty(c.Shorts);
        Assert.Empty(c.Longs);
    }

    [Fact]
    public void DropsWhatIsAddressedPastTheLastAssignedPort()
    {
        (PortSink sink, FakeOutput a, FakeOutput c) = Build();

        sink.SendShort(3, 0x643C90);
        sink.SendShort(IEventSink.PortCount, 0x643C90);

        Assert.Empty(a.Shorts);
        Assert.Empty(c.Shorts);
    }

    [Fact]
    public void DropsWhatIsAddressedToANegativePort()
    {
        (PortSink sink, FakeOutput a, _) = Build();

        sink.SendShort(-1, 0x643C90);

        Assert.Empty(a.Shorts);
    }

    [Fact]
    public void WaitsOnlyForTheOutputThePortGoesTo()
    {
        (PortSink sink, FakeOutput a, FakeOutput c) = Build();

        sink.WaitUntilSent(2);
        sink.WaitUntilSent(1);
        sink.WaitUntilSent(IEventSink.PortCount);

        Assert.Equal(0, a.Waits);
        Assert.Equal(1, c.Waits);
    }

    [Fact]
    public void SendsNothingWhenNoPortHasADevice()
    {
        var sink = new PortSink([]);

        sink.SendShort(0, 0x643C90);
        sink.SendLong(0, [0xF0, 0xF7]);
    }
}
