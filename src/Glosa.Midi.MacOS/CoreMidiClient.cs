using static Glosa.Midi.MacOS.NativeMethods;

namespace Glosa.Midi.MacOS;

/// <summary>The process's one CoreMIDI client, and the output port every output sends through.</summary>
internal static unsafe class CoreMidiClient
{
    private static readonly Lock Gate = new();
    private static uint _client;
    private static uint _outputPort;

    /// <exception cref="MidiDeviceException">The MIDI server would not take a client.</exception>
    public static uint Client
    {
        get
        {
            lock (Gate)
            {
                if (_client != 0) return _client;

                nint name = CreateString("Glosa");
                try
                {
                    uint client;
                    ThrowIfError(MIDIClientCreate(name, 0, 0, &client), "MIDIClientCreate");
                    _client = client;
                }
                finally { CFRelease(name); }
                return _client;
            }
        }
    }

    /// <summary>The port every output sends through; the destination is named on each send.</summary>
    /// <exception cref="MidiDeviceException">The port could not be made.</exception>
    public static uint OutputPort
    {
        get
        {
            uint client = Client;
            lock (Gate)
            {
                if (_outputPort != 0) return _outputPort;

                nint name = CreateString("Glosa out");
                try
                {
                    uint port;
                    ThrowIfError(MIDIOutputPortCreate(client, name, &port), "MIDIOutputPortCreate");
                    _outputPort = port;
                }
                finally { CFRelease(name); }
                return _outputPort;
            }
        }
    }

    /// <summary>
    /// Makes the client now if it can, so that the calling thread's run loop keeps the device
    /// list up to date. Failing is not fatal: opening a device asks again, and says why.
    /// </summary>
    public static void TryStart()
    {
        try { _ = Client; }
        catch (MidiDeviceException) { }
    }

    /// <summary>The endpoints CoreMIDI lists, with their display names.</summary>
    /// <remarks>An endpoint with no name is listed under its index, so it can still be picked.</remarks>
    public static IReadOnlyList<(MidiDeviceInfo Info, uint Endpoint)> List(
        Func<nuint> count, Func<nuint, uint> get)
    {
        nuint n = count();
        var list = new List<(MidiDeviceInfo, uint)>((int)n);
        for (nuint i = 0; i < n; i++)
        {
            uint endpoint = get(i);
            if (endpoint == 0) continue;
            string id = i.ToString();
            list.Add((new MidiDeviceInfo(id, NameOf(endpoint) ?? $"#{id}"), endpoint));
        }
        return list;
    }

    /// <summary>The endpoint by id or by name, as the WinMM backend finds its devices.</summary>
    public static (MidiDeviceInfo Info, uint Endpoint)? Find(
        IReadOnlyList<(MidiDeviceInfo Info, uint Endpoint)> list, string deviceId)
    {
        foreach (var entry in list)
            if (entry.Info.Id == deviceId
                || string.Equals(entry.Info.Name, deviceId, StringComparison.OrdinalIgnoreCase))
                return entry;
        return null;
    }
}
