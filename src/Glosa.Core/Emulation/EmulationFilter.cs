using Glosa.Core.Playback;

namespace Glosa.Core.Emulation;

/// <summary>
/// Sits between the sequencer and the output ports, rewriting events for the module that is
/// actually connected and keeping the emulated panel up to date.
/// </summary>
public sealed class EmulationFilter : IEventSink, Glosa.Core.Playback.IPlaybackReset
{
    /// <summary>MIDI channel to GS part block, used when telling GS to make a part rhythmic.</summary>
    private static readonly byte[] ChannelToGsBlock =
        [1, 2, 3, 4, 5, 6, 7, 8, 9, 0, 10, 11, 12, 13, 14, 15];

    private readonly IEventSink _output;
    private readonly SysExInterpreter _sysEx;

    public EmulationFilter(IEventSink output, EmulationSettings settings,
                           PatchMapSet patches, PanelState? panel = null)
    {
        _output = output;
        Settings = settings;
        Patches = patches;
        Panel = panel ?? new PanelState(settings.DrumTrack);
        Parts = new PartStateTable(settings.DrumTrack);
        _sysEx = new SysExInterpreter(this);
    }

    public EmulationSettings Settings { get; }

    public PatchMapSet Patches { get; }

    public PanelState Panel { get; }

    public PartStateTable Parts { get; }

    /// <summary>Scales CC#7. 127 leaves volume alone.</summary>
    public int MasterVolume { get; set; } = 127;

    /// <summary>
    /// Semitones added to note numbers on melodic parts, clamped to 0-127. Rhythm parts are
    /// left alone, since their note numbers select instruments rather than pitches.
    /// </summary>
    public int KeyShift { get; set; }

    public void SendShort(int port, uint packedMessage)
    {
        // A port the player does not have is dropped.
        if ((uint)port >= IEventSink.PortCount) return;

        // The panel follows the song as written, before any conversion or muting.
        Panel.Track(port, packedMessage);

        uint msg = packedMessage;
        int channel = (int)(msg & 0x0F);
        PartState part = Parts[port, channel];
        DisplayPart display = Panel.Part(port, channel);
        bool modified = false;

        if (!ForceDrumChannelForGm(ref msg, part)) return;

        HandleXgDrumBank(port, ref msg, part, display, ref modified);
        ScaleControllers(ref msg, part);

        if ((msg & 0xF0) == 0xC0 && ApplyMelodicPatch(port, ref msg, part))
            modified = true;

        if (IsNote(msg)) ApplyDrumPatch(port, ref msg, part, ref modified);
        ApplyKeyShift(ref msg, part);

        if (!modified && !ApplySuppression(port, ref msg, part)) return;

        _output.SendShort(port, msg);
    }

    public void SendLong(int port, ReadOnlySpan<byte> sysEx)
    {
        if ((uint)port >= IEventSink.PortCount) return;
        _sysEx.Handle(port, sysEx);
    }

    public void WaitUntilSent(int port) => _output.WaitUntilSent(port);

    public bool IsCable(int port) => _output.IsCable(port);

    /// <summary>
    /// Puts the emulated module back where it started, for the next song.
    /// </summary>
    /// <remarks>
    /// Both halves have to go together: the part state the conversion reads and the panel
    /// the display reads each carry the drum channel, and resetting only one leaves them
    /// disagreeing. The player's own settings (<see cref="MasterVolume"/>,
    /// <see cref="KeyShift"/>) are not song state and are left alone.
    /// </remarks>
    public void Reset()
    {
        Parts.Reset();
        Panel.Reset();
    }

    /// <summary>Emits a message that the filter itself generated, bypassing conversion.</summary>
    internal void Emit(int port, uint packedMessage) => _output.SendShort(port, packedMessage);

    internal void EmitLong(int port, ReadOnlySpan<byte> bytes) => _output.SendLong(port, bytes);

    private static bool IsNote(uint msg) => (msg & 0xF0) is 0x90 or 0x80;

    /// <summary>
    /// When emulating a GM device, rhythm parts have to move to channel 10.
    /// </summary>
    /// <returns>False when the message should be dropped entirely.</returns>
    private bool ForceDrumChannelForGm(ref uint msg, PartState part)
    {
        if (!Settings.GsToGmEmu && !Settings.XgToGmEmu) return true;

        if (part.Mode == PartMode.Drum1)
        {
            msg = (msg & 0xFFFFFFF0) | 9;
        }
        else if (part.Mode > PartMode.Drum1)
        {
            // Map 2 parts only carry notes across; anything else has nowhere to go.
            if (!IsNote(msg)) return false;
            msg = (msg & 0xFFFFFFF0) | 9;
        }
        return true;
    }

    /// <summary>
    /// Bank select MSB 127 is how XG data marks a rhythm part. What that turns into depends
    /// on the module being played through.
    /// </summary>
    private void HandleXgDrumBank(int port, ref uint msg, PartState part,
                                  DisplayPart display, ref bool modified)
    {
        if ((msg & 0xFFFFF0) != 0x7F00B0) return;

        if (Settings.XgToGmEmu)
        {
            if (part.Mode == PartMode.Melodic) { part.Mode = PartMode.Drum2; display.Rhythm = true; }
            msg &= 0xFFFF;                       // bank 127 has no meaning on GM
            modified = true;
        }
        else if (Settings.XgToGsEmu)
        {
            part.Mode = PartMode.Drum2;
            display.Rhythm = true;

            // Tell GS to treat the part as rhythmic; channel 10 keeps map 1, others get map 2.
            byte map = (byte)((msg & 0x0F) == 9 ? 1 : 2);
            int block = ChannelToGsBlock[msg & 0x0F];
            _sysEx.SendRolandParameter(port, 0x42, 0x401015 | block << 8, [map]);

            msg &= 0xFFFF;
            modified = true;
        }
        else if (Settings.XgToX5dEmu)
        {
            msg = (msg & 0xFFFF) | 0x3E0000;     // KORG rhythm bank
            part.Mode = PartMode.Drum2;
            display.Rhythm = true;
            modified = true;
        }

        if (Panel.XgSystemReset) part.Mode = PartMode.Drum2;
    }

    /// <summary>Value rewrites that depend on the target module, plus bank tracking.</summary>
    private void ScaleControllers(ref uint msg, PartState part)
    {
        uint controller = msg & 0xFFF0;

        if (controller == 0x5EB0 && Settings.XgToGsEmu)
        {
            // XG variation depth reads four times hotter than the GS delay it becomes.
            uint value = (msg >> 16) & 0xFF;
            msg = (msg & 0xFFFF) | (value / 4) << 16;
        }

        if (controller == 0x07B0 && MasterVolume != 0x7F)
        {
            uint value = (msg >> 16) & 0xFF;
            msg = (msg & 0xFFFF) | (uint)(value * MasterVolume / 0x7F) << 16;
        }

        if (controller == 0x00B0) part.BankMsb = (byte)((msg >> 16) & 0x7F);
        if (controller == 0x20B0) part.BankLsb = (byte)((msg >> 16) & 0x7F);
    }

    /// <summary>
    /// Runs a program change through the melodic patch map.
    /// </summary>
    /// <remarks>
    /// Rules are scanned in file order and the first that matches wins, so the order they
    /// are written in is their priority. Seven shapes are tried per rule, from fully
    /// wildcarded to an exact match.
    /// </remarks>
    private bool ApplyMelodicPatch(int port, ref uint msg, PartState part)
    {
        byte program = (byte)((msg >> 8) & 0xFF);
        if (part.IsDrum) part.BankMsb = program;   // a rhythm part's "bank" is its drum set

        byte keyMsb = part.BankMsb;
        byte keyLsb = part.BankLsb;
        bool keyDrum = part.IsDrum;

        if (!TryMatchMelodic(program, keyMsb, keyLsb, keyDrum, out PatchEntry hit)) return false;

        // A DM: rule only applies to rhythm parts, and a plain rule only to melodic ones.
        if (hit.DstDrum != keyDrum) return false;

        int channel = (int)(msg & 0x0F);
        if (hit.DstLsb != PatchEntry.Wildcard)
            Emit(port, (uint)(0x20B0 | channel | hit.DstLsb << 16));
        if (hit.DstMsb != PatchEntry.Wildcard)
            Emit(port, (uint)(0x00B0 | channel | hit.DstMsb << 16));
        if (hit.DstPc != PatchEntry.Wildcard)
            msg = (msg & 0xFF) | (uint)hit.DstPc << 8;

        return true;
    }

    private bool TryMatchMelodic(byte pc, byte msb, byte lsb, bool drum, out PatchEntry hit)
    {
        const byte Any = PatchEntry.Wildcard;

        foreach (PatchEntry r in Patches.Melodic)
        {
            bool match =
                (r.SrcMsb == Any && r.SrcLsb == Any && r.SrcPc == Any) ||
                (r.SrcMsb == Any && lsb == r.SrcLsb && pc == r.SrcPc) ||
                (r.SrcMsb == Any && lsb == r.SrcLsb && r.SrcPc == Any) ||
                (r.SrcLsb == Any && msb == r.SrcMsb && pc == r.SrcPc) ||
                (r.SrcLsb == Any && msb == r.SrcMsb && r.SrcPc == Any) ||
                (r.SrcLsb == Any && r.SrcMsb == Any && pc == r.SrcPc) ||
                // The exact shape is a four-byte compare in TMIDI, so it also takes the drum
                // flag into account: a fully specified rule for the other kind of part is
                // skipped rather than matched and rejected, leaving the rules after it reachable.
                (r.SrcPc == pc && r.SrcMsb == msb && r.SrcLsb == lsb && r.SrcDrum == drum);

            if (!match) continue;
            hit = r;
            return true;
        }

        hit = default;
        return false;
    }

    /// <summary>Remaps drum notes, and switches drum set when the rule names one.</summary>
    private void ApplyDrumPatch(int port, ref uint msg, PartState part, ref bool modified)
    {
        if (!part.IsDrum) return;

        byte note = (byte)((msg >> 8) & 0xFF);
        byte set = part.BankMsb;

        foreach (PatchEntry r in Patches.Drum)
        {
            bool match = (r.SrcMsb == PatchEntry.Wildcard && r.SrcPc == note)
                      || (r.SrcPc == note && r.SrcMsb == set);
            if (!match) continue;

            msg = (msg & 0xFF00FF) | (uint)r.DstPc << 8;
            if (r.DstMsb != PatchEntry.Wildcard)
                Emit(port, 0xC0u | (msg & 0x0F) | (uint)r.DstMsb << 8);
            modified = true;
            return;
        }
    }

    private void ApplyKeyShift(ref uint msg, PartState part)
    {
        if (KeyShift == 0 || !IsNote(msg) || part.IsDrum) return;

        int note = (int)((msg >> 8) & 0xFF) + KeyShift;
        msg = (msg & 0xFF00FF) | (uint)Math.Clamp(note, 0, 0x7F) << 8;
    }

    /// <summary>
    /// Applies the suppression and substitution settings, only to messages the patch maps
    /// left alone so their rewrites are not undone.
    /// </summary>
    /// <returns>False when the message should not be sent at all.</returns>
    private bool ApplySuppression(int port, ref uint msg, PartState part)
    {
        uint controller = msg & 0xFFF0;

        if (Settings.DisableBankSelectLsb && controller == 0x20B0) return false;
        if (Settings.DisableBankSelectMsb && controller == 0x00B0) return false;

        if (Settings.MapSelect != 0)
        {
            if ((msg & 0xFFFFF0) == 0x20B0)          // bank LSB 0
            {
                part.BankLsbSelected = (byte)Settings.MapSelect;
                msg = (msg & 0xFFFF) | (uint)Settings.MapSelect << 16;
            }
            else if ((msg & 0xF0) == 0xC0)
            {
                // Pin the map before the program lands on it, but only while the part has not
                // chosen one. The part's own bank LSB is overwritten either way.
                if (part.BankLsbSelected == 0)
                    Emit(port, 0x20B0u | (msg & 0x0F) | (uint)Settings.MapSelect << 16);
                part.BankLsb = (byte)Settings.MapSelect;
            }
        }

        if (Settings.DefaultBankSelectLsb != 0 && (msg & 0xFFFFF0) == 0x20B0)
            msg = (msg & 0xFFFF) | (uint)Settings.DefaultBankSelectLsb << 16;

        // Both of the last two steps sit behind DefaultBankSelectLSB in TMIDI, so with
        // that setting left at zero a part never records having chosen a bank LSB here.
        if (Settings.DefaultBankSelectLsb != 0 && controller == 0x20B0)
            part.BankLsbSelected = (byte)(((msg >> 16) & 0xFF) != 0 ? 1 : 0);

        return true;
    }
}
