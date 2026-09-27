using Glosa.Midi;

namespace Glosa.Core.Playback;

/// <summary>Routes events straight to the opened outputs, one per port index.</summary>
/// <remarks>A port with nothing on it is a null entry, so the list stays indexed by port.</remarks>
public sealed class PortSink(IReadOnlyList<IMidiOutput?> ports) : IEventSink
{
    private readonly IReadOnlyList<IMidiOutput?> _ports = ports;

    public void SendShort(int port, uint packedMessage)
    {
        IMidiOutput? target = Resolve(port);
        target?.SendShort(packedMessage);
    }

    public void SendLong(int port, ReadOnlySpan<byte> sysEx)
    {
        IMidiOutput? target = Resolve(port);
        target?.SendLong(sysEx);
    }

    public void WaitUntilSent(int port) => Resolve(port)?.WaitUntilSent();

    public bool IsCable(int port) => Resolve(port)?.IsCable == true;

    /// <summary>
    /// Finds the output a port goes to, or nothing for a port the player does not have
    /// (<see cref="IEventSink.PortCount"/>) or one with no device on it.
    /// </summary>
    private IMidiOutput? Resolve(int port)
        => (uint)port < (uint)Math.Min(_ports.Count, IEventSink.PortCount) ? _ports[port] : null;
}
