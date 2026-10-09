using Glosa.Core.Playback;

namespace Glosa.Core.Emulation;

/// <summary>
/// Lays a song written for one machine with more than 16 parts out over 16-part machines,
/// one on each port: the second part group of an SC-88 or SC-88Pro, and an XG machine's parts
/// from 17 on.
/// </summary>
/// <remarks>
/// Sees what the machines will be sent: the song, the emulation's rewrites, and the player's
/// initialisation.
///
/// Every part the ports can reach is a slot: slot <c>k</c> is the part on port <c>k / 16</c>
/// that plays channel <c>k % 16</c>, its home channel. Each slot keeps the port and channel it
/// receives on, as the song set them; a channel message goes to every slot that receives it,
/// on that slot's port and home channel. The machines' own parts are kept on their home
/// channels.
///
/// The GS rules follow the SC-88Pro owner's manual ("Individual Parameter Transmission"): a
/// pair of ports (A/B, C/D, E/F) is one SC-88, whose MIDI IN A takes <c>4x xx xx</c> as group
/// A and <c>5x xx xx</c> as group B, and MIDI IN B the other way round. In MODE-1 (single
/// module, the default) the patch common block and the resets are the whole unit's; in
/// MODE-2 (double module) each group's.
///
/// An XG part number (<c>08 pp xx</c>) decides the part whichever port it came in on, as on
/// an MU. Parts 0–63 are slots 0–63, ports A–D (<see cref="XgPorts"/>); what is not a part's
/// is the whole machine's and goes to all four.
/// </remarks>
public sealed class PartSplitter(IEventSink output) : IEventSink
{
    /// <summary>Ports an XG machine's parts are spread over: A–D, parts 0x00–0x3F.</summary>
    public const int XgPorts = 4;

    private const int Slots = IEventSink.PortCount * 16;

    /// <summary>A slot's receive channel when it receives nothing.</summary>
    private const byte NoChannel = 0xFF;

    /// <summary>MIDI channel of each GS part block: block 0 is the rhythm part on channel 10.</summary>
    private static readonly int[] BlockToChannel = [9, 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 12, 13, 14, 15];

    private readonly IEventSink _output = output;
    private readonly Slot[] _slots = [.. Enumerable.Range(0, Slots).Select(Slot.Home)];
    private readonly GsMode[] _modes = new GsMode[IEventSink.PortCount / 2];

    /// <summary>The port and channel a part receives on.</summary>
    private readonly record struct Slot(byte RxPort, byte RxChannel)
    {
        /// <summary>Slot <paramref name="k"/> as it starts: its own port, its home channel.</summary>
        public static Slot Home(int k) => new((byte)(k / 16), (byte)(k % 16));
    }

    /// <summary>What a pair of ports is: one 32-part unit, or two 16-part modules.</summary>
    private enum GsMode
    {
        Single,  // MODE-1
        Double,  // MODE-2
    }

    /// <summary>Puts every part back on its own port and channel, and every pair in MODE-1.</summary>
    public void Reset()
    {
        ResetSlots(0, Slots);
        Array.Fill(_modes, GsMode.Single);
    }

    public void SendShort(int port, uint packedMessage)
    {
        uint status = packedMessage & 0xFF;
        if (status is < 0x80 or >= 0xF0 || (uint)port >= IEventSink.PortCount)
        {
            _output.SendShort(port, packedMessage);
            return;
        }

        int channel = (int)(status & 0x0F);
        uint withoutChannel = packedMessage & ~0x0Fu;
        for (int k = 0; k < Slots; k++)
            if (_slots[k].RxPort == port && _slots[k].RxChannel == channel)
                _output.SendShort(k / 16, withoutChannel | (uint)(k % 16));
    }

    public void SendLong(int port, ReadOnlySpan<byte> sysEx)
    {
        int end = sysEx.IndexOf((byte)0xF7);
        if ((uint)port >= IEventSink.PortCount || sysEx.Length < 2 || sysEx[0] != 0xF0 || end < 0)
        {
            _output.SendLong(port, sysEx);
            return;
        }

        switch (sysEx[1])
        {
            // DT1: F0 41 dev 42 12 address(3) data... sum F7.
            case 0x41 when end >= 10 && sysEx[3] == 0x42 && sysEx[4] == 0x12:
                Gs(port, sysEx[..(end + 1)]);
                break;

            // Parameter change: F0 43 1n 4C address(3) data... F7.
            case 0x43 when end >= 8 && (sysEx[2] & 0xF0) == 0x10 && sysEx[3] == 0x4C:
                Xg(sysEx[..(end + 1)]);
                break;

            // GM System On (and GM2's): F0 7E dev 09 01|03 F7.
            case 0x7E when end >= 5 && sysEx[3] == 0x09 && sysEx[4] is 0x01 or 0x03:
                ToUnit(port, port, sysEx, resets: true);
                break;

            // Master volume: F0 7F dev 04 01 ll mm F7.
            case 0x7F when end >= 5 && sysEx[3] == 0x04 && sysEx[4] == 0x01:
                ToUnit(port, port, sysEx, resets: false);
                break;

            default:
                _output.SendLong(port, sysEx);
                break;
        }
    }

    /// <remarks>
    /// A message for one port can go out on another now, so the pair is waited on and held to
    /// a cable together.
    /// </remarks>
    public void WaitUntilSent(int port)
    {
        _output.WaitUntilSent(port);
        if ((uint)port < IEventSink.PortCount) _output.WaitUntilSent(port ^ 1);
    }

    public bool IsCable(int port)
        => _output.IsCable(port) || (uint)port < IEventSink.PortCount && _output.IsCable(port ^ 1);

    private void Gs(int port, ReadOnlySpan<byte> message)
    {
        int pairBase = port & ~1;
        int address = message[5] << 16 | message[6] << 8 | message[7];
        ReadOnlySpan<byte> data = message[8..^2];

        // System Mode Set. The machines are 16-part ones, and an SC-55 ignores it, so each is
        // reset instead.
        if (address == 0x00007F)
        {
            _modes[port >> 1] = data[0] == 0 ? GsMode.Single : GsMode.Double;
            ResetSlots(pairBase * 16, 32);
            ReadOnlySpan<byte> gsReset =
                [0xF0, 0x41, message[2], 0x42, 0x12, 0x40, 0x00, 0x7F, 0x00, 0x41, 0xF7];
            _output.SendLong(port, gsReset);
            _output.SendLong(port ^ 1, gsReset);
            return;
        }

        // CHANNEL MSG RX PORT: block 00–0F is group A's, 10–1F group B's; 0 is the pair's port
        // A, 1 its port B. The routing is done here, so the machines are not told.
        if (address is >= 0x000100 and <= 0x00011F)
        {
            for (int i = 0; i < data.Length && message[7] + i <= 0x1F; i++)
            {
                int block = message[7] + i;
                int k = (pairBase + (block >> 4)) * 16 + BlockToChannel[block & 0x0F];
                _slots[k] = _slots[k] with { RxPort = (byte)(pairBase + (data[i] & 1)) };
            }
            return;
        }

        if (message[5] is < 0x40 or > 0x5F)
        {
            _output.SendLong(port, message);
            return;
        }

        bool otherGroup = message[5] >= 0x50;
        int target = otherGroup ? port ^ 1 : port;
        byte[]? changed = null;
        if (otherGroup)
        {
            changed = message.ToArray();
            changed[5] -= 0x10;
            address -= 0x100000;
        }

        // Patch common, GS Reset among it.
        if (address <= 0x400F7F)
        {
            if (changed is not null) Checksum(changed);
            ToUnit(port, target, changed ?? message, resets: address == 0x40007F);
            return;
        }

        // RX CHANNEL (40 1x 02): the machine's part stays on its home channel, the channel the
        // slot is sent on.
        for (int i = 0; i < data.Length; i++)
        {
            int at = AddressAt(address, i);
            if ((at & 0xFFF0FF) != 0x401002) continue;

            int k = target * 16 + BlockToChannel[(at >> 8) & 0x0F];
            _slots[k] = _slots[k] with { RxChannel = data[i] < 0x10 ? data[i] : NoChannel };
            if (data.Length == 1) return;
            changed ??= message.ToArray();
            changed[8 + i] = (byte)(k % 16);
        }

        if (changed is not null) Checksum(changed);
        _output.SendLong(target, changed ?? message);
    }

    /// <summary>
    /// Sends what is the whole unit's in MODE-1 and one group's in MODE-2: GS patch common,
    /// the resets, master volume.
    /// </summary>
    /// <param name="target">The port of the group it is addressed to.</param>
    /// <param name="resets">Whether it puts the receive channels back where it arrives.</param>
    private void ToUnit(int port, int target, ReadOnlySpan<byte> message, bool resets)
    {
        if (_modes[port >> 1] == GsMode.Double)
        {
            To(target, message, resets);
        }
        // In MODE-1 the other group's patch common is greyed out (SC-88Pro owner's manual).
        else if (target == port)
        {
            To(port, message, resets);
            To(port ^ 1, message, resets);
        }
    }

    private void To(int port, ReadOnlySpan<byte> message, bool resets)
    {
        // The receive port is a system parameter, which a reset leaves.
        if (resets)
            for (int k = port * 16; k < port * 16 + 16; k++)
                _slots[k] = _slots[k] with { RxChannel = (byte)(k % 16) };
        _output.SendLong(port, message);
    }

    private void Xg(ReadOnlySpan<byte> message)
    {
        int address = message[4] << 16 | message[5] << 8 | message[6];
        ReadOnlySpan<byte> data = message[7..^1];

        // XG System On, All Parameter Reset.
        if (address is 0x00007E or 0x00007F)
        {
            ResetSlots(0, XgPorts * 16);
            ToXgPorts(message);
            return;
        }

        int part = message[5];
        if (message[4] == 0x08 && part < XgPorts * 16)
        {
            byte[] changed = message.ToArray();
            changed[5] = (byte)(part & 0x0F);

            // RCV CHANNEL (08 pp 04): the machine's part stays on its home channel, the channel
            // the slot is sent on.
            for (int i = 0; i < data.Length; i++)
            {
                if (AddressAt(address, i) != (0x080004 | part << 8)) continue;

                byte value = data[i];
                _slots[part] = value < 0x40
                    ? new Slot((byte)(value >> 4), (byte)(value & 0x0F))
                    : _slots[part] with { RxChannel = NoChannel };
                if (data.Length == 1) return;
                changed[7 + i] = (byte)(part & 0x0F);
            }

            _output.SendLong(part >> 4, changed);
            return;
        }

        // VARIATION PART (02 01 5B), INSERTION PART (03 nn 0C): the machine with the part has
        // it, and the others none.
        for (int i = 0; i < data.Length; i++)
        {
            int at = AddressAt(address, i);
            if (at != 0x02015B && (at & 0xFF00FF) != 0x03000C || data[i] >= XgPorts * 16) continue;

            byte[] changed = message.ToArray();
            for (int port = 0; port < XgPorts; port++)
            {
                changed[7 + i] = port == data[i] >> 4 ? (byte)(data[i] & 0x0F) : (byte)0x7F;
                _output.SendLong(port, changed);
            }
            return;
        }

        ToXgPorts(message);
    }

    private void ToXgPorts(ReadOnlySpan<byte> message)
    {
        for (int port = 0; port < XgPorts; port++) _output.SendLong(port, message);
    }

    private void ResetSlots(int first, int count)
    {
        for (int k = first; k < first + count; k++) _slots[k] = Slot.Home(k);
    }

    /// <summary>
    /// The address of the <paramref name="i"/>th data byte of a message to
    /// <paramref name="address"/>: each of its three bytes holds 7 bits.
    /// </summary>
    private static int AddressAt(int address, int i)
    {
        int packed = (address >> 16 << 14 | (address >> 8 & 0x7F) << 7 | address & 0x7F) + i;
        return (packed >> 14 & 0x7F) << 16 | (packed >> 7 & 0x7F) << 8 | packed & 0x7F;
    }

    /// <summary>Puts a Roland checksum on a DT1 whose address or data has been changed.</summary>
    private static void Checksum(byte[] message)
    {
        int sum = 0;
        for (int i = 5; i < message.Length - 2; i++) sum += message[i];
        message[^2] = (byte)((128 - sum % 128) % 128);
    }
}
