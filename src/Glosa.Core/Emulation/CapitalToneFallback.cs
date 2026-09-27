using Glosa.Core.Playback;

namespace Glosa.Core.Emulation;

/// <summary>
/// Plays a variation tone the machine does not have on one it does, as the SC-55 did
/// (Capital Tone Fallback), for the later Sound Canvases, which leave such a part silent.
/// </summary>
/// <remarks>
/// Sees what the machine will be sent: the song, the emulation's rewrites, and the player's
/// initialisation. Only program changes for a tone the machine lacks are touched; the parts
/// are followed so the program change is judged against the bank, map and rhythm assignment
/// the machine is going to use.
///
/// The rules, as the SC-55's firmware applies them:
///
/// - **A tone**: the bank rounded down to a multiple of 8 — the sub-capital — if the program
///   has a tone there, and bank 0 — the capital — if not. Banks 64 and up are the special use
///   area and programs 121–128 sound effects, which are not stood in for: a missing one stays
///   missing.
/// - **A drum set**: the program rounded down to a multiple of 8, and the STANDARD set if that
///   is missing too. Only programs 1–48: from 49 on a set is not a variation of another
///   (ORCHESTRA, SFX, and the later models' ETHNIC and the like), as SC-55 v2.00 has it.
///
/// Whether a tone is there is judged on the machine's own tables for the part's map
/// (<see cref="SoundCanvasTones"/>).
///
/// A tone is moved by sending the bank it stands in with in front of the program change. The
/// machine keeps that bank until told another, so the song's own is put back in front of the
/// next program change that needs it.
/// </remarks>
public sealed class CapitalToneFallback(IEventSink output) : IEventSink
{
    /// <summary>Banks from here on are the special use area, where nothing is stood in for.</summary>
    private const int SpecialBanks = 64;

    /// <summary>Programs from here on are sound effects, which are not variations of each other.</summary>
    private const int EffectPrograms = 120;

    /// <summary>Drum sets from here on stand for nothing else (SC-55 v2.00).</summary>
    private const int DrumSetEnd = 48;

    /// <summary>MIDI channel of each GS part block: block 0 is the rhythm part on channel 10.</summary>
    private static readonly int[] BlockToChannel = [9, 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15];

    private readonly IEventSink _output = output;
    private readonly Part[,] _parts = NewParts();
    private volatile SoundCanvasTones? _tones;

    /// <summary>
    /// The machine's tables, or null to let everything through. Set with the playback thread
    /// stopped; the parts are followed either way, so it can be set between songs.
    /// </summary>
    public SoundCanvasTones? Tones
    {
        get => _tones;
        set => _tones = value;
    }

    private sealed class Part(int channel)
    {
        /// <summary>The song's CC#0.</summary>
        public int Bank;

        /// <summary>The bank this sent in the song's place, which the machine holds; null when it holds the song's.</summary>
        public int? StandIn;

        /// <summary>CC#32, which picks the map.</summary>
        public int Map;

        /// <summary>TONE MAP-0 NUMBER: the map a CC#32 of 0 means. 0 while it is the factory's.</summary>
        public int Map0;

        public bool Rhythm = channel == 9;

        public void Reset(int channel)
        {
            Bank = 0;
            StandIn = null;
            Map = 0;
            Rhythm = channel == 9;
        }
    }

    private static Part[,] NewParts()
    {
        var parts = new Part[IEventSink.PortCount, 16];
        for (int port = 0; port < IEventSink.PortCount; port++)
            for (int channel = 0; channel < 16; channel++)
                parts[port, channel] = new Part(channel);
        return parts;
    }

    public void SendShort(int port, uint packedMessage)
    {
        if ((uint)port < IEventSink.PortCount)
            packedMessage = Follow(port, packedMessage);
        _output.SendShort(port, packedMessage);
    }

    public void SendLong(int port, ReadOnlySpan<byte> sysEx)
    {
        if ((uint)port < IEventSink.PortCount) Watch(port, sysEx);
        _output.SendLong(port, sysEx);
    }

    public void WaitUntilSent(int port) => _output.WaitUntilSent(port);

    public bool IsCable(int port) => _output.IsCable(port);

    /// <returns>The message to send, which is the one given unless a drum set is stood in for.</returns>
    private uint Follow(int port, uint message)
    {
        int channel = (int)(message & 0x0F);
        Part part = _parts[port, channel];
        int data1 = (int)(message >> 8) & 0x7F;
        int data2 = (int)(message >> 16) & 0x7F;

        switch (message & 0xF0)
        {
            case 0xB0 when data1 == 0x00:
                part.Bank = data2;
                part.StandIn = null;
                return message;

            case 0xB0 when data1 == 0x20:
                part.Map = data2;
                return message;

            case 0xC0:
                return ProgramChange(port, channel, part, data1, message);

            default:
                return message;
        }
    }

    private uint ProgramChange(int port, int channel, Part part, int program, uint message)
    {
        ToneSlots? map = _tones?.MapFor(part.Map, part.Map0);

        if (part.Rhythm)
        {
            if (map is null || program >= DrumSetEnd || map.HasDrumSet(program)) return message;
            int subCapital = program & 0x78;
            int set = map.HasDrumSet(subCapital) ? subCapital : 0;
            return (message & 0xFF00FF) | (uint)set << 8;
        }

        int bank = part.Bank;
        if (map is not null && bank < SpecialBanks && program < EffectPrograms
            && !map.HasTone(program, bank))
        {
            int subCapital = bank & 0x78;
            bank = map.HasTone(program, subCapital) ? subCapital : 0;
        }

        // What the machine holds: the bank last sent in the song's place, or the song's own.
        if (bank != (part.StandIn ?? part.Bank))
            _output.SendShort(port, (uint)(0xB0 | channel | bank << 16));
        part.StandIn = bank != part.Bank ? bank : null;
        return message;
    }

    /// <summary>
    /// Follows the exclusives that change what a program change picks: the resets, the
    /// rhythm part assignment, and the part's map.
    /// </summary>
    /// <remarks>
    /// A reset puts every port back, not only the one it came on. It is the whole machine's,
    /// and the player resets a machine on two ports through the first of them only; ports on
    /// different machines are reset one after another anyway.
    ///
    /// A part's settings are the port's it came in on, and only for the part group it came
    /// in on (<c>40 xx xx</c>): which port leads to the other group (<c>50 xx xx</c>) is a
    /// matter of the cables.
    /// </remarks>
    private void Watch(int port, ReadOnlySpan<byte> data)
    {
        // GM System On (and GM2's): F0 7E dev 09 01|03 F7.
        if (data.Length >= 6 && data[0] == 0xF0 && data[1] == 0x7E && data[3] == 0x09
            && data[4] is 0x01 or 0x03)
        {
            ResetParts();
            return;
        }

        // A GS DT1: F0 41 dev 42 12 address(3) data... sum F7.
        if (data.Length < 11 || data[0] != 0xF0 || data[1] != 0x41 || data[3] != 0x42
            || data[4] != 0x12)
            return;

        int address = data[5] << 16 | data[6] << 8 | data[7];

        // GS Reset, and the SC-88's System Mode Set, which resets as well.
        if (address is 0x40007F or 0x00007F)
        {
            ResetParts();
            return;
        }

        // The data runs up to the checksum, the byte before the F7.
        int end = data.IndexOf((byte)0xF7);
        if (end < 0) end = data.Length;
        for (int i = 8; i < end - 1; i++)
            Write(port, address + (i - 8), data[i]);
    }

    private void Write(int port, int address, byte value)
    {
        Part part = _parts[port, BlockToChannel[(address >> 8) & 0x0F]];

        switch (address & 0xFFF0FF)
        {
            case 0x401015: part.Rhythm = value != 0; break;   // USE FOR RHYTHM PART
            case 0x404000: part.Map = value; break;           // TONE MAP NUMBER (= CC#32)
            case 0x404001: part.Map0 = value; break;          // TONE MAP-0 NUMBER
        }
    }

    private void ResetParts()
    {
        for (int port = 0; port < IEventSink.PortCount; port++)
            for (int channel = 0; channel < 16; channel++)
                _parts[port, channel].Reset(channel);
    }
}
