using static Glosa.Midi.Linux.NativeMethods;

namespace Glosa.Midi.Linux;

/// <summary>A sequencer port: a client's number, and the port's number within it.</summary>
internal readonly record struct SeqAddress(int Client, int Port)
{
    public override string ToString() => $"{Client}:{Port}";
}

/// <summary>
/// The process's sequencer client for sending, the one port every output sends from, and the
/// list of ports the system offers.
/// </summary>
internal static unsafe class AlsaSeqClient
{
    /// <summary>
    /// Held around every call on the handle. alsa-lib writes a SysEx event through a scratch
    /// buffer the handle keeps, and the outputs share the handle.
    /// </summary>
    public static readonly Lock Gate = new();

    private static nint _seq;
    private static int _port = -1;

    /// <exception cref="MidiDeviceException">
    /// There is no sequencer to open — the kernel's <c>snd-seq</c> is not loaded, or
    /// <c>/dev/snd/seq</c> cannot be opened by this user.
    /// </exception>
    public static nint Handle
    {
        get
        {
            lock (Gate)
            {
                if (_seq == 0) _seq = Open(OpenOutput, 0);
                return _seq;
            }
        }
    }

    /// <summary>The port every output sends from.</summary>
    /// <exception cref="MidiDeviceException">The port could not be made.</exception>
    public static int OutputPort
    {
        get
        {
            lock (Gate)
            {
                nint seq = Handle;
                if (_port >= 0) return _port;

                // Nothing can read or connect to it: it is only where events come from.
                int port = snd_seq_create_simple_port(seq, "Glosa out", 0,
                                                      TypeMidiGeneric | TypeApplication);
                ThrowIfError(port, "snd_seq_create_simple_port");
                _port = port;
                return _port;
            }
        }
    }

    /// <summary>A new sequencer handle under the player's name, which the caller closes.</summary>
    public static nint Open(int streams, int mode)
    {
        nint seq;
        ThrowIfError(snd_seq_open(&seq, "default", streams, mode), "snd_seq_open");
        snd_seq_set_client_name(seq, "Glosa");
        return seq;
    }

    /// <summary>
    /// The ports of other clients that can do all of <paramref name="caps"/>, with the names
    /// they are shown under and whether they are hardware; none when there is no sequencer.
    /// </summary>
    public static IReadOnlyList<(MidiDeviceInfo Info, SeqAddress Address, bool Hardware)> List(uint caps)
    {
        var ports = new List<(string Name, string Client, SeqAddress Address, bool Hardware)>();
        lock (Gate)
        {
            nint seq;
            try { seq = Handle; }
            catch (MidiDeviceException) { return []; }

            int self = snd_seq_client_id(seq);
            nint client, port;
            snd_seq_client_info_malloc(&client);
            snd_seq_port_info_malloc(&port);
            try
            {
                snd_seq_client_info_set_client(client, -1);
                while (snd_seq_query_next_client(seq, client) >= 0)
                {
                    int number = snd_seq_client_info_get_client(client);
                    if (number == self) continue;
                    string clientName = ReadString(snd_seq_client_info_get_name(client))?.Trim() ?? "";

                    snd_seq_port_info_set_client(port, number);
                    snd_seq_port_info_set_port(port, -1);
                    while (snd_seq_query_next_port(seq, port) >= 0)
                    {
                        uint capability = snd_seq_port_info_get_capability(port);
                        uint type = snd_seq_port_info_get_type(port);
                        if ((capability & caps) != caps || (capability & CapNoExport) != 0) continue;
                        if ((type & (TypeMidiGeneric | TypeSynth | TypeApplication)) == 0) continue;

                        var address = new SeqAddress(number, snd_seq_port_info_get_port(port));
                        string name = ReadString(snd_seq_port_info_get_name(port))?.Trim() ?? "";
                        ports.Add((name.Length > 0 ? name : address.ToString(), clientName, address,
                                   (type & TypeHardware) != 0));
                    }
                }
            }
            finally
            {
                snd_seq_port_info_free(port);
                snd_seq_client_info_free(client);
            }
        }

        // The client's name is added only where it tells ports of one name apart. Two of the
        // same machine have the same client name too, and are told apart by their order, as
        // on the other systems. Whether it is added changes as other ports come and go, so
        // the name it would have the other way is kept as well, for a name stored then.
        return [.. ports.Select(p =>
        {
            bool clash = ports.Where(other => other.Name == p.Name).Select(other => other.Client)
                              .Distinct().Count() > 1;
            string withClient = $"{p.Name} ({p.Client})";
            return (new MidiDeviceInfo(p.Address.ToString(), clash ? withClient : p.Name,
                                       clash ? p.Name : withClient), p.Address, p.Hardware);
        })];
    }

    /// <summary>The port by id or by name, as the other backends find their devices.</summary>
    public static (MidiDeviceInfo Info, SeqAddress Address, bool Hardware)? Find(
        IReadOnlyList<(MidiDeviceInfo Info, SeqAddress Address, bool Hardware)> list, string deviceId)
    {
        foreach (var entry in list)
            if (entry.Info.Id == deviceId
                || string.Equals(entry.Info.Name, deviceId, StringComparison.OrdinalIgnoreCase))
                return entry;
        return null;
    }
}
