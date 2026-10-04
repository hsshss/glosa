#if BRACK
using Brack;

namespace Glosa.Midi.Brack;

/// <summary>The note ports of a <see cref="BrackRack"/>'s plugins as output devices.</summary>
internal sealed class BrackOutputFactory(BrackRack rack) : IMidiOutputFactory
{
    /// <summary>Keeps the ids apart from other backends'.</summary>
    private const string IdPrefix = "brack:";

    public string BackendName => "Brack";

    public IReadOnlyList<MidiDeviceInfo> Enumerate() => Devices(rack.Plugins);

    /// <remarks>Note ports are known once the rack has loaded.</remarks>
    public void WaitUntilListed() => rack.WaitLoaded();

    /// <summary>
    /// A device per note port, named after the plugin, numbered from 1 when there are several.
    /// A plugin not loaded is listed by name; a loaded one without note ports is left out.
    /// </summary>
    /// <remarks>Ids are "brack:&lt;port&gt;:&lt;plugin id&gt;".</remarks>
    internal static IReadOnlyList<MidiDeviceInfo> Devices(IReadOnlyList<RackPlugin> plugins)
        => [.. plugins.SelectMany(Ports)];

    private static IEnumerable<MidiDeviceInfo> Ports(RackPlugin plugin) => plugin switch
    {
        { State: RackPluginState.Ready, NotePorts: 0 } => [],
        { NotePorts: > 1 } => Enumerable.Range(0, plugin.NotePorts)
                                        .Select(port => Device(plugin, port, $"{plugin.Name} {port + 1}")),
        _ => [Device(plugin, 0, plugin.Name)],
    };

    private static MidiDeviceInfo Device(RackPlugin plugin, int port, string name)
        => new($"{IdPrefix}{port}:{plugin.Id}", name, Kind: MidiDeviceKind.AudioPlugin);

    public IMidiOutput Create(string deviceId)
    {
        if (Enumerate().FirstOrDefault(device => device.Id == deviceId) is not { Id: not null } device)
            throw new MidiDeviceException($"no plugin '{deviceId}' in the rack");

        string portAndPlugin = deviceId[IdPrefix.Length..];
        int colon = portAndPlugin.IndexOf(':');
        return new BrackOutput(rack, device, portAndPlugin[(colon + 1)..], int.Parse(portAndPlugin[..colon]));
    }
}

/// <summary>MIDI into a plugin's note port, as is (Brack translates it for VST3).</summary>
/// <remarks>Plays a fixed way behind when it is sent, to the frame (<see cref="BrackRack.Send"/>).</remarks>
internal sealed class BrackOutput(BrackRack rack, MidiDeviceInfo device, string pluginId, int notePort) : IMidiOutput
{
    public MidiDeviceInfo Device => device;

    public bool IsOpen { get; private set; }

    public int DroppedLongMessages => 0;

    public string? CloseError { get; private set; }

    /// <remarks>Waits for the rack to load, and starts the audio.</remarks>
    public void Open()
    {
        if (IsOpen) return;

        rack.WaitLoaded();
        if (rack.Find(pluginId) is not { } plugin)
            throw new MidiDeviceException($"{device.Name}: the plugin is no longer in the rack");
        if (plugin.State != RackPluginState.Ready)
            throw new MidiDeviceException($"{device.Name}: {plugin.Status}");

        try
        {
            rack.Hold();
        }
        catch (BrackException ex)
        {
            throw new MidiDeviceException($"{device.Name}: {ex.Message}");
        }
        CloseError = null;
        IsOpen = true;
    }

    public void SendShort(uint packedMessage)
    {
        byte status = (byte)packedMessage;
        int data = UmpEncoder.DataBytes(status);
        if (data < 0) return;

        Span<byte> message = [status, (byte)(packedMessage >> 8), (byte)(packedMessage >> 16)];
        Send(message[..(1 + data)]);
    }

    public void SendLong(ReadOnlySpan<byte> sysEx) => Send(sysEx);

    private void Send(ReadOnlySpan<byte> message)
    {
        MidiSendResult result = rack.Send(pluginId, notePort, message);
        if (result != MidiSendResult.Sent)
            throw new MidiDeviceException($"{device.Name}: {rack.WhyNotTaken(pluginId, result)}");
    }

    public void Reset()
    {
        for (int ch = 0; ch < 16; ch++)
        {
            SendShort((uint)(0xB0 | ch | 0x40 << 8));   // Sustain off
            SendShort((uint)(0xB0 | ch | 0x7B << 8));   // All Notes Off
        }
    }

    public void Close(bool reset = true)
    {
        if (!IsOpen) return;
        try
        {
            if (reset) Reset();
        }
        catch (MidiDeviceException ex)
        {
            CloseError = ex.Message;
        }
        finally
        {
            IsOpen = false;
            rack.Release();
        }
    }

    public void Dispose() => Close();
}
#endif
