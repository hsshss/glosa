using Glosa.Core.Playback;
using Glosa.Core.Smf;

namespace Glosa.Core.Emulation;

/// <summary>
/// Per-part values the module would show on its panel.
/// </summary>
/// <remarks>
/// The EQ fields come from the XG multi-part table and only exist on the MU90, MU100 and
/// SW1000XG.
/// </remarks>
public sealed class DisplayPart
{
    /// <summary>GS <c>40 1n 1C</c>.</summary>
    public byte Panpot { get; set; } = 0x40;

    /// <summary>Set by GS "Use for Rhythm Part" (<c>40 1n 15</c>) or an XG drum bank.</summary>
    public bool Rhythm { get; set; }

    /// <summary>XG <c>08 nn 72</c> EQ BASS GAIN. 0-127 maps to -12..0..+12 dB.</summary>
    public byte EqBassGain { get; set; } = 0x40;

    /// <summary>XG <c>08 nn 73</c> EQ TREBLE GAIN. 0-127 maps to -12..0..+12 dB.</summary>
    public byte EqTrebleGain { get; set; } = 0x40;

    /// <summary>XG <c>08 nn 76</c> EQ BASS FREQUENCY. 0x04-0x28 maps to 32-2000 Hz.</summary>
    public byte EqBassFrequency { get; set; } = 0x0C;

    /// <summary>XG <c>08 nn 77</c> EQ TREBLE FREQUENCY. 0x1C-0x3A maps to 500-16000 Hz.</summary>
    public byte EqTrebleFrequency { get; set; } = 0x36;

    // ---- tracked from the channel messages themselves -------------------------------

    /// <summary>CC#7.</summary>
    public byte Volume { get; set; }

    /// <summary>CC#32.</summary>
    public byte BankLsb { get; set; }

    /// <summary>CC#0.</summary>
    public byte BankMsb { get; set; }

    /// <summary>The last program change.</summary>
    public byte Program { get; set; }

    /// <summary>CC#11.</summary>
    public byte Expression { get; set; }

    /// <summary>CC#1.</summary>
    public byte Modulation { get; set; }

    /// <summary>CC#91.</summary>
    public byte ReverbSend { get; set; }

    /// <summary>CC#93.</summary>
    public byte ChorusSend { get; set; }

    /// <summary>CC#94.</summary>
    public byte VariationSend { get; set; }

    /// <summary>CC#74, or NRPN <c>01 20</c>.</summary>
    public byte Cutoff { get; set; }

    /// <summary>CC#71, or NRPN <c>01 21</c>.</summary>
    public byte Resonance { get; set; }

    /// <summary>NRPN <c>01 24</c>.</summary>
    public byte VibratoDelay { get; set; }

    /// <summary>CC#73, or NRPN <c>01 63</c>.</summary>
    public byte Attack { get; set; }

    /// <summary>NRPN <c>01 64</c>.</summary>
    public byte Decay { get; set; }

    /// <summary>CC#72, or NRPN <c>01 66</c>.</summary>
    public byte Release { get; set; }

    /// <summary>NRPN <c>01 08</c>, <c>01 09</c> and <c>01 0A</c>.</summary>
    public byte VibratoRate { get; set; }

    public byte VibratoDepth { get; set; }

    public byte VibratoDelay2 { get; set; }

    /// <summary>CC#64. 0x40 and above holds notes.</summary>
    public byte Hold { get; set; }

    /// <summary>CC#66.</summary>
    public byte Sostenuto { get; set; }

    /// <summary>CC#67.</summary>
    public byte Soft { get; set; }

    /// <summary>
    /// The NRPN the next data entry applies to, as <c>MSB &lt;&lt; 8 | LSB</c>. An RPN
    /// select resets it to <c>0x7F7F</c>, which matches nothing.
    /// </summary>
    public ushort ParameterNumber { get; set; } = 0x7F7F;

    /// <summary>Pitch bend, 14 bits, 0x2000 centred.</summary>
    public ushort PitchBend { get; set; } = 0x2000;

    /// <summary>Notes played since the last reset.</summary>
    public ushort NotesPlayed { get; set; }

    /// <summary>Notes currently sounding on this part.</summary>
    public ushort Sounding { get; set; }

    /// <summary>Per note: bit 0 is held down, bit 1 is held by the pedal.</summary>
    public byte[] Notes { get; } = new byte[128];

    /// <summary>Velocity each note was struck with.</summary>
    public byte[] NoteVelocity { get; } = new byte[128];

    /// <summary>What volume a part is left at by a reset, which is where GM puts it.</summary>
    private const int DefaultVolume = 100;

    /// <summary>The same for expression, which GM leaves wide open.</summary>
    private const int DefaultExpression = 127;

    /// <summary>127 to the fifth: what the three squared terms divide by to land back in range.</summary>
    private const long FullScale = 127L * 127 * 127 * 127 * 127;

    /// <summary>
    /// How loudly the part is playing, 0 to 127.
    /// </summary>
    /// <remarks>
    /// The loudest note it is holding, brought down by the two controls that stand between
    /// a note and the sound. Nothing here decays: a part has no envelope to read, so this
    /// is what the song is asking for rather than what the module makes of it.
    ///
    /// All three terms are squared. GM defines CC7 as <c>40·log10(v/127)</c> dB, which is
    /// <c>(v/127)²</c> in amplitude, and expression multiplies the same way; velocity is
    /// patch business rather than spec, but a square is close to what most of them do.
    /// Read linearly instead, a part playing at middling settings meters about twice as
    /// high as the module's own display shows it.
    ///
    /// A control still at zero is one the song has not sent — the panel keeps them that way
    /// so that parts a song never touches can be told apart — so what a reset would have
    /// left there stands in. Taking it as silence would black out every file that never
    /// sends volume; taking it as full would read a part 1.27 times too loud.
    ///
    /// Rounded up, so a part holding any note at all is above one holding none.
    /// </remarks>
    public int Level
    {
        get
        {
            long loudest = 0;
            for (int note = 0; note < Notes.Length; note++)
                if (Notes[note] != 0 && NoteVelocity[note] > loudest) loudest = NoteVelocity[note];

            if (loudest == 0) return 0;

            long volume = Volume > 0 ? Volume : DefaultVolume;
            long expression = Expression > 0 ? Expression : DefaultExpression;

            long amplitude = loudest * loudest * volume * volume * expression * expression;
            return (int)((amplitude + FullScale - 1) / FullScale);
        }
    }

    /// <summary>
    /// The high-water mark of <see cref="Level"/>, falling back towards it over time.
    /// </summary>
    /// <remarks>
    /// What a meter's peak needle is for: a part that struck once and let go is gone from
    /// the level before the eye has found it, and a mark that leaves slowly says how loud
    /// the moment was. Moved by <see cref="PanelState.Advance"/>, which is the only thing
    /// here that knows how much time has passed.
    /// </remarks>
    public int Peak { get; set; }

    /// <summary>How long <see cref="Peak"/> stays where it is before it starts to fall.</summary>
    public int PeakHoldMs { get; set; }

    public void Reset()
    {
        Panpot = 0x40;
        Rhythm = false;
        EqBassGain = EqTrebleGain = 0x40;
        EqBassFrequency = 0x0C;
        EqTrebleFrequency = 0x36;

        Volume = BankLsb = BankMsb = Program = Expression = Modulation = 0;
        ReverbSend = ChorusSend = VariationSend = 0;
        Cutoff = Resonance = VibratoDelay = Attack = Decay = Release = 0;
        VibratoRate = VibratoDepth = VibratoDelay2 = 0;
        Hold = Sostenuto = Soft = 0;
        ParameterNumber = 0x7F7F;
        PitchBend = 0x2000;
        NotesPlayed = Sounding = 0;
        Peak = PeakHoldMs = 0;
        Array.Clear(Notes);
        Array.Clear(NoteVelocity);
    }
}

/// <summary>A two-byte effect selection, as both GS and XG report it.</summary>
public struct EffectSlot
{
    public byte Type;
    public byte Sub;

    public void Set(byte type, byte sub) { Type = type; Sub = sub; }
}

/// <summary>
/// The emulated module's panel.
/// </summary>
/// <remarks>
/// GS and XG are projected onto one model: the GS reverb, chorus and delay macros land in
/// the same slots as XG's reverb, chorus and variation types, and the GS EFX type shares a
/// slot with XG's insertion effect.
/// </remarks>
public sealed class PanelState
{
    /// <summary>Roland's text line, which is also where the GS patch name is shown.</summary>
    public const int LcdColumns = 16;

    /// <summary>XG's text line, kept apart from the Roland one rather than on top of
    /// <see cref="LcdColumns"/>.</summary>
    public const int XgLcdColumns = 32;

    public const int BitmapBytes = 0x100;

    /// <summary>Value of <see cref="LcdScrollIndex"/> that means the line does not scroll.</summary>
    public const int NoScroll = -1;

    /// <summary>Milliseconds a scrolling line spends on each character step.</summary>
    public const int LcdScrollStepMs = 300;

    /// <summary>
    /// The parts of every port there is (<see cref="IEventSink.PortCount"/>), made up front.
    /// </summary>
    /// <remarks>
    /// The playback thread writes them while the display reads them, so what holds them
    /// never changes shape; a list growing under a reader can throw.
    /// </remarks>
    private readonly DisplayPart[][] _parts;

    public bool GmSystemOn { get; set; }

    /// <summary>GS Reset seen.</summary>
    public bool GsReset { get; set; }

    public bool XgSystemReset { get; set; }

    /// <summary>GS System Mode Set, stored as the value plus one: 1 is Mode 1, 2 is Mode 2.</summary>
    public int SystemMode { get; set; }

    public EffectSlot Reverb;
    public EffectSlot Chorus;

    /// <summary>GS delay macro / XG variation type.</summary>
    public EffectSlot Variation;

    /// <summary>XG variation part; <c>0xFF</c> means the system connection.</summary>
    public byte VariationPart { get; set; } = 0xFF;

    /// <summary>GS EFX type / XG insertion effect 1 and 2.</summary>
    public EffectSlot Insertion1;
    public EffectSlot Insertion2;
    public byte Insertion1Part { get; set; } = 0x7F;
    public byte Insertion2Part { get; set; } = 0x7F;

    /// <summary>Notes sounding across every part.</summary>
    public int SoundingNotes { get; private set; }

    /// <summary>
    /// How many melodic parts are sounding each note, for a keyboard display. Rhythm parts
    /// are left out because their note numbers pick instruments rather than pitches.
    /// </summary>
    public byte[] MelodicNotes { get; } = new byte[128];

    /// <summary>
    /// What the song has set the module's master volume to, 0 to 127.
    /// </summary>
    /// <remarks>
    /// One knob in front of all the parts. Three messages reach it — the universal
    /// <c>F0 7F dd 04 01</c>, GS <c>40 00 04</c> and XG <c>00 00 04</c> — and a reset puts
    /// it back to full. This is the song's knob, not the listener's: the player's own
    /// <c>MasterVolume</c> is applied to what is sent and is deliberately not shown here,
    /// the way nothing else the player does to the data is.
    /// </remarks>
    public int MasterVolume { get; set; } = FullVolume;

    /// <summary>Where the master volume sits when nobody has touched it.</summary>
    public const int FullVolume = 127;

    /// <summary>
    /// Per-part receive setting: bit 4 is the port, bits 0-3 the channel, and <c>0x7F</c>
    /// receives nothing.
    /// </summary>
    /// <remarks>
    /// The 32 parts of a module on ports A and B: 0-15 are A01-A16, 16-31 B01-B16. Each
    /// listens to its own port and channel until the song moves it, which is how a song on
    /// port A alone can play all 32 — the SC-88 lets a B part listen to port A.
    /// </remarks>
    public byte[] PartReceive { get; } = new byte[ModuleParts];

    /// <summary>
    /// The port each part listens on, 0 or 1.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="PartReceive"/> because GS sets the two separately: a part
    /// whose channel is switched off still has a port, and it is on that port again when its
    /// channel is switched back on.
    /// </remarks>
    public byte[] PartPort { get; } = new byte[ModuleParts];

    /// <summary>Parts <see cref="PartReceive"/> covers.</summary>
    public const int ModuleParts = 32;

    /// <summary>Patch name shown on the LCD (GS <c>40 01 00</c>-<c>40 01 0F</c>).</summary>
    public char[] PatchName { get; } = new char[16];

    /// <summary>Text currently on the Roland text line.</summary>
    public char[] LcdText { get; } = new char[LcdColumns];

    /// <summary>Text currently on the XG text line.</summary>
    public char[] XgLcdText { get; } = new char[XgLcdColumns];

    /// <summary>
    /// The whole line a Roland display message built, before it was cropped to
    /// <see cref="LcdText"/>. A message longer than the display is framed by the patch name
    /// so it can be scrolled.
    /// </summary>
    public string LcdScroll { get; set; } = string.Empty;

    /// <summary>
    /// How long the current LCD content should stay, in milliseconds. The Roland and XG text
    /// lines share this one countdown.
    /// </summary>
    public int LcdHoldMs { get; set; }

    /// <summary>
    /// How far <see cref="LcdScroll"/> has been shifted in. <see cref="NoScroll"/> means the
    /// message fits, and the patch name comes back when the hold runs out; zero means nothing
    /// is animating. A long message starts at <see cref="LcdColumns"/>, the first column past
    /// what is already on screen.
    /// </summary>
    public int LcdScrollIndex { get; set; } = NoScroll;

    /// <summary>True while the XG text line is the one on screen.</summary>
    public bool XgLcdVisible { get; set; }

    /// <summary>Dot matrix currently displayed; one byte per dot, non-zero when lit.</summary>
    public byte[] Bitmap { get; } = new byte[BitmapBytes];

    /// <summary>Stored bitmap pages the module can switch between.</summary>
    public byte[][] BitmapPages { get; } = CreatePages(10);

    /// <summary>
    /// How long a bitmap page stays once shown, when nothing has said otherwise.
    /// </summary>
    /// <remarks>
    /// A picture is something a song puts up in passing, not the state of the machine, so
    /// it comes down on its own. Most files never send the page-control message that would
    /// set a time, and without this they would leave the matrix lit for the rest of the
    /// song.
    /// </remarks>
    public const int DefaultBitmapHoldMs = DefaultBitmapHoldSteps * BitmapHoldStepMs;

    /// <summary>
    /// What one step of the page control's display time is worth, in milliseconds.
    /// </summary>
    /// <remarks>
    /// The SC-88Pro's Display Time (<c>10 20 01</c>) is a value of 0 to 15 covering 0 to
    /// 7.2 seconds, so a step is 480ms.
    /// </remarks>
    public const int BitmapHoldStepMs = 480;

    /// <summary>The largest display time the page control can ask for.</summary>
    public const int MaxBitmapHoldSteps = 0x0F;

    /// <summary>The display time the spec gives as the default, in steps.</summary>
    public const int DefaultBitmapHoldSteps = 6;

    /// <summary>
    /// How long a bitmap page should stay once shown, in milliseconds. Set by the Roland
    /// page-control messages; despite the address looking like a page selector, the value
    /// is the hold time applied on the next page switch.
    /// </summary>
    public int BitmapHoldMs { get; set; } = DefaultBitmapHoldMs;

    /// <summary>What is left of <see cref="BitmapHoldMs"/> for the page on screen now.</summary>
    public int BitmapRemainingMs { get; set; }

    /// <summary>True while the dot matrix is the thing on screen.</summary>
    public bool BitmapVisible { get; set; }

    /// <summary>Incremented whenever the display changes, so a view can tell it is stale.</summary>
    public int Revision { get; private set; }

    /// <summary>How fast a peak mark falls, in level units per second.</summary>
    /// <remarks>
    /// Full scale in about two seconds. Slow enough that a single note stays readable,
    /// quick enough that the mark is about what is being played now.
    /// </remarks>
    public const int PeakFallPerSecond = 64;

    /// <summary>How long a peak mark stays where it was put, in milliseconds.</summary>
    /// <remarks>
    /// A mark that starts falling the instant it is set is a mark that is never quite at
    /// the peak by the time it is looked at. A second is long enough to read and short
    /// enough that it is still about the bar underneath it.
    /// </remarks>
    public const int PeakHoldMs = 1000;

    /// <summary>Thousandths of a level unit the peaks are owed but have not been given.</summary>
    private int _peakFallOwed;

    /// <exception cref="ArgumentOutOfRangeException">There is no such port.</exception>
    public DisplayPart Part(int port, int channel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(port, _parts.Length);
        return _parts[port][channel & 0x0F];
    }

    /// <summary>
    /// What a part is putting out, master volume included, 0 to 127.
    /// </summary>
    /// <remarks>
    /// <see cref="DisplayPart.Level"/> is the part on its own; this is the part as the
    /// module's own meter would show it. Squared like the rest of the chain, for the same
    /// reason: the knob is a volume control, and those are defined in decibels.
    /// </remarks>
    public int LevelOf(DisplayPart part)
    {
        int level = part.Level;
        if (level <= 0 || MasterVolume >= FullVolume) return level;
        if (MasterVolume <= 0) return 0;

        long scaled = (long)level * MasterVolume * MasterVolume;
        return (int)((scaled + FullVolume * FullVolume - 1) / (FullVolume * FullVolume));
    }

    public void Invalidate() => Revision++;

    /// <summary>
    /// Lets the display clocks run for <paramref name="elapsedMs"/>, the milliseconds since
    /// the last call.
    /// </summary>
    /// <remarks>
    /// Three clocks: the dot matrix's, the scrolling text line's, and the XG text line's — the
    /// last two sharing <see cref="LcdHoldMs"/>, told apart by <see cref="XgLcdVisible"/>. A
    /// message that fits the display restores the patch name when its three seconds are up; a
    /// longer one shifts in one character every <see cref="LcdScrollStepMs"/> until the scroll
    /// buffer is spent, and then stays.
    /// </remarks>
    public void Advance(int elapsedMs)
    {
        if (elapsedMs <= 0) return;
        bool changed = false;

        if (BitmapRemainingMs != 0)
        {
            BitmapRemainingMs -= elapsedMs;
            if (BitmapRemainingMs < 1)
            {
                BitmapRemainingMs = 0;
                BitmapVisible = false;
                changed = true;
            }
        }

        // Index 0 is the idle state: the line has finished whatever it was doing.
        if (LcdHoldMs != 0 && LcdScrollIndex != 0 && !XgLcdVisible)
        {
            LcdHoldMs -= elapsedMs;
            if (LcdHoldMs < 1)
            {
                LcdHoldMs = 0;
                StepLcdScroll();
                changed = true;
            }
        }

        if (LcdHoldMs != 0 && XgLcdVisible)
        {
            LcdHoldMs -= elapsedMs;
            if (LcdHoldMs < 1)
            {
                LcdHoldMs = 0;
                XgLcdVisible = false;
                changed = true;
            }
        }

        if (FallPeaks(elapsedMs)) changed = true;

        if (changed) Invalidate();
    }

    /// <summary>
    /// Brings every part's peak mark down towards its level, and catches new highs.
    /// </summary>
    /// <remarks>
    /// A mark that has just been set waits <see cref="PeakHoldMs"/> before it starts to
    /// fall. The hold is per part, because each mark was set at its own moment.
    ///
    /// The fall is spent out of a single running remainder rather than rounded per part
    /// per visit: at a 33ms tick the whole drop is a couple of units, and a division that
    /// rounds down each time would leave a mark that never quite comes home.
    /// </remarks>
    private bool FallPeaks(int elapsedMs)
    {
        _peakFallOwed += elapsedMs * PeakFallPerSecond;
        int fall = _peakFallOwed / 1000;
        _peakFallOwed -= fall * 1000;

        bool moved = false;
        foreach (DisplayPart[] port in _parts)
        {
            foreach (DisplayPart part in port)
            {
                int level = LevelOf(part);
                int was = part.Peak;

                if (level >= was)
                {
                    // Still as loud as the mark, so there is nothing yet to fall from.
                    part.Peak = level;
                    part.PeakHoldMs = PeakHoldMs;
                }
                else if (part.PeakHoldMs > 0)
                {
                    part.PeakHoldMs = Math.Max(0, part.PeakHoldMs - elapsedMs);
                }
                else
                {
                    part.Peak = Math.Max(level, was - fall);
                }

                if (part.Peak != was) moved = true;
            }
        }

        return moved;
    }

    private void StepLcdScroll()
    {
        if (LcdScrollIndex < 0)
        {
            PatchName.CopyTo(LcdText, 0);
            LcdScrollIndex = 0;
            return;
        }

        if (LcdScrollIndex >= LcdScroll.Length)
        {
            LcdScrollIndex = 0;
            return;
        }

        for (int i = 0; i < LcdColumns - 1; i++) LcdText[i] = LcdText[i + 1];
        LcdText[LcdColumns - 1] = LcdScroll[LcdScrollIndex];
        LcdScrollIndex++;
        LcdHoldMs = LcdScrollStepMs;
    }

    private readonly int _drumChannel;

    /// <param name="drumChannel">
    /// From <c>DrumTrack</c>. Everywhere else the display's rhythm flag and the part's mode
    /// are set together, so the drum channel starts with both.
    /// </param>
    public PanelState(int drumChannel = 9)
    {
        _drumChannel = drumChannel;
        _parts = [.. Enumerable.Range(0, IEventSink.PortCount).Select(_ => CreateParts())];
        Array.Fill(LcdText, ' ');
        Array.Fill(XgLcdText, ' ');
        Array.Fill(PatchName, ' ');
        ResetReceive();
    }

    /// <summary>Applies the state a GS Reset leaves behind.</summary>
    public void ApplyGsReset()
    {
        MasterVolume = FullVolume;
        Reverb.Set(4, 0);
        Chorus.Set(2, 1);
        Variation.Set(0, 2);
        VariationPart = 0xFF;
        Insertion1.Set(0, 0);
        Insertion1Part = 0x7F;
        Insertion2.Set(0x7F, 0x7F);
        Insertion2Part = 0x7F;
        ResetReceive();
        Invalidate();
    }

    /// <summary>Every part back on its own port and channel, as a reset leaves them.</summary>
    public void ResetReceive()
    {
        for (int i = 0; i < ModuleParts; i++)
        {
            PartReceive[i] = (byte)i;
            PartPort[i] = (byte)(i >> 4);
        }
    }

    /// <summary>
    /// How many ports' parts <paramref name="song"/> plays, one to
    /// <see cref="IEventSink.PortCount"/>.
    /// </summary>
    /// <remarks>
    /// The ports it sends to, and port B's as well when a song on port A alone moves a B
    /// part over to listen to it (see <see cref="PartReceive"/>): GS CHANNEL MSG RX PORT
    /// (<c>00 01 10</c>-<c>00 01 1F</c>) set to A, or an XG Receive Channel
    /// (<c>08 10 04</c>-<c>08 1F 04</c>) on one of port A's channels.
    /// </remarks>
    public static int PortsPlayed(MidiSequence song)
    {
        int ports = Math.Clamp(song.MaxPort + 1, 1, IEventSink.PortCount);
        if (ports >= 2) return ports;

        foreach (MidiEvent e in song.Events)
            if (e.Kind == MidiEventKind.SysEx && MovesPartBToA(song.GetData(e))) return 2;
        return ports;
    }

    private static bool MovesPartBToA(ReadOnlySpan<byte> data)
    {
        // F0 41 dd 42 12 00 01 xx <values...> sum F7: one value per part from xx on.
        if (data.Length >= 11 && data[1] == 0x41 && data[3] == 0x42 && data[4] == 0x12
            && data[5] == 0x00 && data[6] == 0x01)
        {
            for (int i = 8; i < data.Length - 2; i++)
            {
                int part = data[7] + i - 8;
                if (part is >= 0x10 and < 0x20 && data[i] == 0) return true;
            }
            return false;
        }

        // F0 43 1n 4C 08 pp 04 vv F7
        return data.Length >= 9 && data[1] == 0x43 && (data[2] & 0xF0) == 0x10
               && data[3] == 0x4C && data[4] == 0x08 && data[5] is >= 0x10 and < 0x20
               && data[6] == 0x04 && data[7] < 0x10;
    }

    public void Reset()
    {
        GmSystemOn = false;
        GsReset = false;
        XgSystemReset = false;
        SystemMode = 0;
        Reverb = default;
        Chorus = default;
        Variation = default;
        VariationPart = 0xFF;
        Insertion1 = default;
        Insertion2 = default;
        Insertion1Part = Insertion2Part = 0x7F;
        SoundingNotes = 0;
        Array.Clear(MelodicNotes);
        ResetReceive();
        MasterVolume = FullVolume;
        Array.Fill(PatchName, ' ');
        Array.Fill(LcdText, ' ');
        Array.Fill(XgLcdText, ' ');
        LcdScroll = string.Empty;
        Array.Clear(Bitmap);
        foreach (byte[] page in BitmapPages) Array.Clear(page);
        BitmapHoldMs = DefaultBitmapHoldMs;
        BitmapRemainingMs = 0;
        BitmapVisible = false;
        LcdHoldMs = 0;
        LcdScrollIndex = NoScroll;
        XgLcdVisible = false;
        foreach (DisplayPart[] port in _parts)
            for (int i = 0; i < port.Length; i++)
            {
                port[i].Reset();
                port[i].Rhythm = i == _drumChannel;
            }
        Invalidate();
    }

    /// <summary>
    /// Records what a channel message does to the panel.
    /// </summary>
    /// <remarks>
    /// This runs on every channel message before any conversion or muting, so what the
    /// panel shows is the song as written, not what reached the device. None of it
    /// affects the output.
    ///
    /// On ports A and B the message goes to every part listening for it
    /// (<see cref="PartReceive"/>), which may be none, or two.
    /// </remarks>
    public void Track(int port, uint packedMessage)
    {
        uint kind = packedMessage >> 4 & 0x0F;
        if (kind == 0x0F) return;

        int channel = (int)(packedMessage & 0x0F);
        if (port >= ModuleParts / 16)
        {
            Track(Part(port, channel), kind, packedMessage);
        }
        else
        {
            int source = port << 4 | channel;
            for (int i = 0; i < ModuleParts; i++)
                if (PartReceive[i] == source) Track(Part(i >> 4, i & 0x0F), kind, packedMessage);
        }

        Invalidate();
    }

    private void Track(DisplayPart part, uint kind, uint packedMessage)
    {
        byte data1 = (byte)((packedMessage >> 8) & 0x7F);
        byte data2 = (byte)((packedMessage >> 16) & 0x7F);
        int note = data1;

        switch (kind)
        {
            case 0x0B: TrackController(part, data1, data2); break;

            // A note on with velocity zero is a note off.
            case 0x09 when (packedMessage & 0x7F0000) == 0:
            case 0x08:
                if (SoundingNotes != 0) SoundingNotes--;
                if (part.Sounding != 0) part.Sounding--;
                if (part.Notes[note] != 0) part.Notes[note] &= 0xFE;
                if (MelodicNotes[note] != 0 && !part.Rhythm) MelodicNotes[note]--;
                break;

            case 0x09:
                SoundingNotes++;
                part.Notes[note] |= 1;
                if (part.Hold > 0x3F) part.Notes[note] |= 2;
                part.NoteVelocity[note] = data2;
                if (!part.Rhythm) MelodicNotes[note]++;
                part.NotesPlayed++;
                part.Sounding++;
                break;

            case 0x0C: part.Program = data1; break;

            case 0x0E: part.PitchBend = (ushort)((packedMessage & 0x7F0000) >> 9 | data1); break;
        }
    }

    private void TrackController(DisplayPart part, byte number, byte value)
    {
        switch (number)
        {
            // All Sound Off stops the part wherever it is; All Notes Off is the keys
            // coming up, so the pedal keeps whatever it was already holding.
            case 120: ReleaseAll(part, keepHeld: false); return;
            case 123: ReleaseAll(part, keepHeld: true); return;
            case 121: ResetControllers(part); return;

            case 7: part.Volume = value; return;
            case 0: part.BankMsb = value; return;
            case 32: part.BankLsb = value; return;
            case 1: part.Modulation = value; return;
            case 10: part.Panpot = value; return;
            case 11: part.Expression = value; return;
            case 91: part.ReverbSend = value; return;
            case 93: part.ChorusSend = value; return;
            case 94: part.VariationSend = value; return;

            case 64:
                part.Hold = value;
                // Lifting the pedal releases what it was holding; pressing it catches
                // everything currently down.
                for (int i = 0; i < part.Notes.Length; i++)
                {
                    if (value < 0x40) part.Notes[i] &= 0xFD;
                    else if ((part.Notes[i] & 1) != 0) part.Notes[i] |= 2;
                }
                return;

            case 66: part.Sostenuto = value; return;
            case 67: part.Soft = value; return;

            // An RPN select only clears the parameter number; RPNs are not tracked.
            case 100:
            case 101: part.ParameterNumber = 0x7F7F; return;

            case 98: part.ParameterNumber = (ushort)(part.ParameterNumber & 0xFF00 | value); return;
            case 99: part.ParameterNumber = (ushort)(part.ParameterNumber & 0x00FF | value << 8); return;

            case 71: part.Resonance = value; return;
            case 72: part.Release = value; return;
            case 73: part.Attack = value; return;
            case 74: part.Cutoff = value; return;

            case 6: TrackDataEntry(part, value); return;
        }
    }

    /// <summary>
    /// Lets go of every note the part is holding.
    /// </summary>
    /// <remarks>
    /// The same bookkeeping a note off does, for all 128 at once: the tallies are sums
    /// over every part, so they are stepped down by hand rather than recounted.
    ///
    /// <paramref name="keepHeld"/> is the difference between the two messages. A note the
    /// pedal has caught is still sounding after All Notes Off, and its key was counted off
    /// when it came up, so only the notes actually held down move the tallies.
    /// </remarks>
    private void ReleaseAll(DisplayPart part, bool keepHeld)
    {
        for (int note = 0; note < part.Notes.Length; note++)
        {
            byte was = part.Notes[note];
            if (was == 0) continue;

            if ((was & 1) != 0)
            {
                if (SoundingNotes != 0) SoundingNotes--;
                if (part.Sounding != 0) part.Sounding--;
                if (MelodicNotes[note] != 0 && !part.Rhythm) MelodicNotes[note]--;
            }

            part.Notes[note] = keepHeld ? (byte)(was & 0xFE) : (byte)0;
        }
    }

    /// <summary>
    /// Reset All Controllers, as the spec lists it.
    /// </summary>
    /// <remarks>
    /// Volume, pan and the effect sends are not on that list. They are where the song put
    /// the part, not something it was bending at the time. Expression goes to 127 rather
    /// than to zero: zero means the song has said nothing, and this message is the song
    /// saying something.
    ///
    /// The pedal goes up through the same path CC64 takes, so it releases what it had
    /// caught instead of leaving notes held by a pedal that is no longer down.
    /// </remarks>
    private void ResetControllers(DisplayPart part)
    {
        part.Modulation = 0;
        part.Expression = 127;
        part.Sostenuto = 0;
        part.Soft = 0;
        part.PitchBend = 0x2000;
        part.ParameterNumber = 0x7F7F;
        TrackController(part, 64, 0);
    }

    /// <summary>A data entry lands wherever the last NRPN select pointed.</summary>
    private static void TrackDataEntry(DisplayPart part, byte value)
    {
        switch (part.ParameterNumber)
        {
            case 0x0108: part.VibratoRate = value; break;
            case 0x0109: part.VibratoDepth = value; break;
            case 0x010A: part.VibratoDelay2 = value; break;
            case 0x0120: part.Cutoff = value; break;
            case 0x0121: part.Resonance = value; break;
            case 0x0124: part.VibratoDelay = value; break;
            case 0x0163: part.Attack = value; break;
            case 0x0164: part.Decay = value; break;
            case 0x0166: part.Release = value; break;

            // The drum-part equalisers share the slots the XG multi-part table writes.
            case 0x0130: part.EqBassGain = value; break;
            case 0x0131: part.EqTrebleGain = value; break;
            case 0x0134: part.EqBassFrequency = value; break;
            case 0x0135: part.EqTrebleFrequency = value; break;
        }
    }

    private DisplayPart[] CreateParts()
    {
        var parts = new DisplayPart[PartStateTable.ChannelsPerPort];
        for (int i = 0; i < parts.Length; i++)
            parts[i] = new DisplayPart { Rhythm = i == _drumChannel };
        return parts;
    }

    private static byte[][] CreatePages(int count)
    {
        var pages = new byte[count][];
        for (int i = 0; i < count; i++) pages[i] = new byte[BitmapBytes];
        return pages;
    }
}
