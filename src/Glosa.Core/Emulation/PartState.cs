namespace Glosa.Core.Emulation;

/// <summary>How a part is being played.</summary>
public enum PartMode
{
    Melodic = 0,
    /// <summary>Rhythm part using drum map 1.</summary>
    Drum1 = 1,
    /// <summary>Rhythm part using drum map 2.</summary>
    Drum2 = 2,
}

/// <summary>Per-channel state the runtime conversion needs.</summary>
public sealed class PartState
{
    /// <summary>Bank select MSB. On a rhythm part this holds the drum set, i.e. the last program.</summary>
    public byte BankMsb { get; set; }

    public byte BankLsb { get; set; }

    /// <summary>Set once a non-zero bank LSB has been selected, or when MapSelect forced one.</summary>
    public byte BankLsbSelected { get; set; }

    public PartMode Mode { get; set; }

    public bool IsDrum => Mode != PartMode.Melodic;

    public void Reset()
    {
        BankMsb = 0;
        BankLsb = 0;
        BankLsbSelected = 0;
        Mode = PartMode.Melodic;
    }
}

/// <summary>Channel state for every port, added on demand.</summary>
/// <param name="drumChannel">
/// The channel that starts out rhythmic, from <c>DrumTrack</c>, so a drum map applies to it
/// from the first note. Out of range disables it.
/// </param>
public sealed class PartStateTable(int drumChannel = 9)
{
    public const int ChannelsPerPort = 16;

    private readonly int _drumChannel = drumChannel;
    private readonly List<PartState[]> _ports = [];

    public int PortCount => _ports.Count;

    public PartState this[int port, int channel]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(port);
            while (_ports.Count <= port) _ports.Add(CreatePort());
            return _ports[port][channel & 0x0F];
        }
    }

    public void Reset()
    {
        foreach (PartState[] port in _ports)
            for (int i = 0; i < port.Length; i++)
            {
                port[i].Reset();
                port[i].Mode = StartingMode(i);
            }
    }

    private PartState[] CreatePort()
    {
        var parts = new PartState[ChannelsPerPort];
        for (int i = 0; i < parts.Length; i++)
            parts[i] = new PartState { Mode = StartingMode(i) };
        return parts;
    }

    /// <summary>The drum channel starts on map 1, the rest melodic.</summary>
    private PartMode StartingMode(int channel)
        => channel == _drumChannel ? PartMode.Drum1 : PartMode.Melodic;
}
