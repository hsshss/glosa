using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Glosa.Core.Rcp;

/// <summary>
/// Turns Recomposer data (RCP, R36, G18, G36) into a Standard MIDI File, so the SMF reader
/// does the rest.
/// </summary>
/// <remarks>
/// Two layouts exist. <c>RCM-PC98V2.0</c> (RCP, R36) has 4-byte events: command, step,
/// gate, velocity. <c>RCP3.0</c> (G18, G36) has 6-byte events whose step and gate are
/// 16-bit. Everything past the event layout is the same language, so both are decoded to
/// one shape and converted by one path.
///
/// The format follows shingo45endo's rcm2smf (MIT License); the tempo graduation table
/// comes from there, and it in turn from Recomposer's <c>CVS.EXE</c>. Loops the data says
/// run forever follow <see cref="ToSmf"/>'s repeat count.
/// </remarks>
public static class RcpConverter
{
    /// <summary>The default endless loop repeat count.</summary>
    public const int DefaultInfiniteLoopRepeatCount = 2;

    private static ReadOnlySpan<byte> Rcp2Signature => "RCM-PC98V2.0(C)COME ON MUSIC"u8;
    private static ReadOnlySpan<byte> Rcp3Signature => "COME ON MUSIC RECOMPOSER RCP3.0"u8;

    public static bool IsRecomposer(ReadOnlySpan<byte> data)
        => data.StartsWith(Rcp2Signature) || data.StartsWith(Rcp3Signature);

    /// <summary>Converts, or throws <see cref="InvalidDataException"/>.</summary>
    /// <param name="infiniteLoopRepeatCount">
    /// How many times the body of a loop marked endless plays; zero still plays it once.
    /// </param>
    /// <remarks>
    /// Loops are unrolled here, track by track, rather than handed to the sequencer as loop
    /// marks.
    /// </remarks>
    public static byte[] ToSmf(ReadOnlySpan<byte> data,
                               int infiniteLoopRepeatCount = DefaultInfiniteLoopRepeatCount)
    {
        Song song = data.StartsWith(Rcp2Signature) ? ReadRcp2(data)
                  : data.StartsWith(Rcp3Signature) ? ReadRcp3(data)
                  : throw new InvalidDataException(Strings.RcpNotRecomposer);

        return new Writer(song, Math.Max(1, infiniteLoopRepeatCount)).Build(data);
    }

    // ---- reading -----------------------------------------------------------------------

    /// <summary>One event, in the 4-byte layout's terms whichever layout it came from.</summary>
    /// <param name="At">Where its bytes are in the file.</param>
    private readonly record struct Event(int Cmd, int St, int Gt, int Vel, int At);

    private sealed class Track
    {
        public int MidiCh;              // 0-31, or -1 for none
        public int KeyShift;            // raw byte; 0x80 set means leave the notes alone
        public int StShift;
        public int Mode;                // bit 0: muted
        public byte[] Memo = [];
        public List<Event> Events = [];
    }

    private sealed class Song
    {
        public byte[] Title = [];
        public List<byte[]> Memo = [];
        public int TimeBase;
        public int Tempo;
        public int PlayBias;
        public byte[][] UserExclusive = [];
        public int EventLength;
        public List<Track> Tracks = [];
    }

    private static Song ReadRcp2(ReadOnlySpan<byte> b)
    {
        const int HeaderLength = 44, EventLength = 4, TracksAt = 0x586;
        if (b.Length < TracksAt) throw new InvalidDataException(Strings.RcpHeaderShort);

        var song = new Song
        {
            Title = b[0x20..0x60].ToArray(),
            TimeBase = (b[0x1E7] << 8) | b[0x1C0],
            Tempo = b[0x1C1],
            PlayBias = (sbyte)b[0x1C5],
            EventLength = EventLength,
        };
        for (int i = 0; i < 12; i++) song.Memo.Add(b.Slice(0x60 + 28 * i, 28).ToArray());
        song.UserExclusive = new byte[8][];
        for (int i = 0; i < 8; i++) song.UserExclusive[i] = b.Slice(0x406 + 48 * i + 24, 24).ToArray();

        // Zero means the 18 tracks of the first versions; 2.5F and later write the count.
        int count = b[0x1E6] == 0 ? 18 : b[0x1E6];
        bool counted = b[0x1E6] != 0;

        int at = TracksAt;
        var unsignedShifts = new List<int>();
        for (int i = 0; i < count && at + HeaderLength < b.Length; i++)
        {
            if (b[at..].StartsWith("RCFW"u8)) break;

            int size = BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
            if (size < HeaderLength || at + size > b.Length) break;

            // A track longer than 64 KB overflows the size field. Its end mark is trusted
            // instead when the size does not land on one.
            int end = at + size;
            if (b[end - EventLength] is not (0xFE or 0xFF))
            {
                end = at + HeaderLength;
                while (end + EventLength <= b.Length)
                {
                    end += EventLength;
                    if (b[end - EventLength] is 0xFE or 0xFF) break;
                }
                size = end - at;
            }

            var track = new Track
            {
                MidiCh = (sbyte)b[at + 4],
                KeyShift = b[at + 5],
                StShift = (sbyte)b[at + 6],
                Mode = b[at + 7],
                Memo = b.Slice(at + 8, 36).ToArray(),
            };
            unsignedShifts.Add(b[at + 6]);
            track.Events = new List<Event>((end - at - HeaderLength) / EventLength);
            for (int e = at + HeaderLength; e + EventLength <= end; e += EventLength)
                track.Events.Add(new Event(b[e], b[e + 1], b[e + 2], b[e + 3], e));
            song.Tracks.Add(track);
            at += size;
        }

        // ST+ is signed from 2.5F on. Older files are read as signed too unless some track
        // is past what the signed field could have said.
        if (!counted && song.Tracks.Any(t => t.StShift is < -99 or > 99))
            for (int i = 0; i < song.Tracks.Count; i++) song.Tracks[i].StShift = unsignedShifts[i];

        return song;
    }

    private static Song ReadRcp3(ReadOnlySpan<byte> b)
    {
        const int HeaderLength = 46, EventLength = 6, TracksAt = 0xC98;
        if (b.Length < TracksAt) throw new InvalidDataException(Strings.G36HeaderShort);

        var song = new Song
        {
            Title = b[0x20..0x60].ToArray(),
            TimeBase = BinaryPrimitives.ReadUInt16LittleEndian(b[0x20A..]),
            Tempo = BinaryPrimitives.ReadUInt16LittleEndian(b[0x20C..]),
            PlayBias = (sbyte)b[0x211],
            EventLength = EventLength,
        };
        for (int i = 0; i < 12; i++) song.Memo.Add(b.Slice(0xA0 + 30 * i, 30).ToArray());
        song.UserExclusive = new byte[8][];
        for (int i = 0; i < 8; i++) song.UserExclusive[i] = b.Slice(0xB18 + 48 * i + 23, 25).ToArray();

        int count = BinaryPrimitives.ReadUInt16LittleEndian(b[0x208..]);
        int at = TracksAt;
        for (int i = 0; i < count && at + HeaderLength < b.Length; i++)
        {
            if (b[at..].StartsWith("RCFW"u8)) break;

            long size = BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
            if (size < HeaderLength || at + size > b.Length) break;

            var track = new Track
            {
                MidiCh = (sbyte)b[at + 6],
                KeyShift = b[at + 7],
                StShift = (sbyte)b[at + 8],
                Mode = b[at + 9],
                Memo = b.Slice(at + 10, 36).ToArray(),
            };
            track.Events = new List<Event>((int)((size - HeaderLength) / EventLength));
            for (int e = at + HeaderLength; e + EventLength <= at + size; e += EventLength)
            {
                // Command, velocity, step (16), gate (16), put in the 4-byte order.
                track.Events.Add(new Event(b[e], b[e + 2] | (b[e + 3] << 8), b[e + 4] | (b[e + 5] << 8),
                                           b[e + 1], e));
            }
            song.Tracks.Add(track);
            at += (int)size;
        }

        return song;
    }

    // ---- loops and repeated measures ----------------------------------------------------

    private static class Cmd
    {
        public const int UserExclusive0 = 0x90, UserExclusive7 = 0x97;
        public const int TrackExclusive = 0x98, ExternalCommand = 0x99;
        public const int YamahaBase = 0xD0, YamahaDevice = 0xD1, YamahaParameter = 0xD2, XgParameter = 0xD3;
        public const int Mks7 = 0xDC;
        public const int RolandBase = 0xDD, RolandParameter = 0xDE, RolandDevice = 0xDF;
        public const int BankProgramLsb = 0xE1, BankProgram = 0xE2;
        public const int KeyScan = 0xE5, MidiChannel = 0xE6, Tempo = 0xE7;
        public const int ChannelPressure = 0xEA, Control = 0xEB, Program = 0xEC;
        public const int KeyPressure = 0xED, PitchBend = 0xEE;
        public const int MusicKey = 0xF5, Comment = 0xF6, Continued = 0xF7;
        public const int LoopEnd = 0xF8, LoopStart = 0xF9;
        public const int SameMeasure = 0xFC, MeasureEnd = 0xFD, TrackEnd = 0xFE;
    }

    /// <summary>An event with its continuation data gathered and its repeats played out.</summary>
    /// <param name="DataAt">Where the gathered data starts in <see cref="Unrolled.Data"/>.</param>
    private readonly record struct Resolved(int Cmd, int St, int Gt, int Vel,
                                            int DataAt = 0, int DataLength = 0);

    /// <summary>One track played out, reused for every track in turn.</summary>
    private sealed class Unrolled
    {
        public List<Resolved> Events { get; } = [];

        public List<byte> Data { get; } = [];

        public ReadOnlySpan<byte> DataOf(in Resolved e)
            => CollectionsMarshal.AsSpan(Data).Slice(e.DataAt, e.DataLength);

        public void Clear()
        {
            Events.Clear();
            Data.Clear();
        }
    }

    private const int MaxLoopNest = 8;
    private const int MaxSameMeasureChain = 100;

    /// <summary>
    /// A song whose tracks, all told, take more steps than this to unroll is taken to be
    /// runaway data, and stopped with "loop limit exceeded".
    /// </summary>
    /// <remarks>
    /// Every event stepped through counts, loop marks and continuations included: a loop
    /// around nothing writes nothing, and would otherwise turn for as long as its counts
    /// multiply out. As every step writes at most one event or five bytes of data, this
    /// bounds what is written as well. For the whole song, as every track's bytes are held
    /// until the file is written.
    /// </remarks>
    private const int MaxUnrollSteps = 2_000_000;

    /// <summary>
    /// Plays the track's loops and repeated measures out into one straight list.
    /// </summary>
    /// <remarks>
    /// A repeated measure (<c>FC</c>) points back at an earlier measure; that measure plays
    /// until its end mark, or until another <c>FC</c>, and the track carries on after the
    /// pointer. A chain of pointers is followed to the measure that holds notes. A loop
    /// started inside a repeated measure remembers so, since jumping back into it must
    /// also return from it.
    /// </remarks>
    private static void Unroll(ReadOnlySpan<byte> file, List<Event> events, int eventLength,
                               int endless, ref int steps, Unrolled into)
    {
        int headerLength = eventLength == 4 ? 44 : 46;
        into.Clear();
        List<Resolved> flat = into.Events;
        var loops = new Stack<(int Start, int Return, int Left)>();
        int returnTo = -1;

        for (int i = 0; i < events.Count;)
        {
            Step(ref steps);

            Event e = events[i];
            switch (e.Cmd)
            {
                case Cmd.SameMeasure:
                    if (returnTo >= 0)
                    {
                        // A pointer inside a repeated measure ends it.
                        i = returnTo + 1;
                        returnTo = -1;
                        break;
                    }
                    returnTo = i;
                    for (int hops = 0; events[i].Cmd == Cmd.SameMeasure; hops++)
                    {
                        if (hops == MaxSameMeasureChain)
                            throw new InvalidDataException(Strings.RcpSameMeasureCycle);
                        int target = SameMeasureTarget(file.Slice(events[i].At, eventLength),
                                                       eventLength, headerLength);
                        if (target < 0 || target >= events.Count)
                            throw new InvalidDataException(Strings.RcpSameMeasureBroken);
                        i = target;
                    }
                    break;

                case Cmd.LoopStart:
                    if (loops.Count < MaxLoopNest) loops.Push((i, returnTo, -1));
                    i++;
                    break;

                case Cmd.LoopEnd:
                    if (loops.Count == 0) { i++; break; }
                    var loop = loops.Pop();
                    int left = loop.Left < 0 ? (e.St > 0 ? e.St : endless) : loop.Left;
                    left--;
                    if (left > 0)
                    {
                        loops.Push(loop with { Left = left });
                        i = loop.Start + 1;
                        returnTo = loop.Return;
                    }
                    else
                    {
                        i++;
                    }
                    break;

                case Cmd.TrackEnd:
                    flat.Add(new Resolved(e.Cmd, 0, 0, 0));
                    return;

                case Cmd.MeasureEnd:
                    flat.Add(new Resolved(e.Cmd, 0, 0, 0));
                    if (returnTo >= 0) { i = returnTo + 1; returnTo = -1; }
                    else i++;
                    break;

                case Cmd.TrackExclusive or Cmd.ExternalCommand or Cmd.Comment:
                {
                    List<byte> data = into.Data;
                    int from = data.Count;
                    data.AddRange(FirstData(file, e, eventLength));
                    for (i++; i < events.Count && events[i].Cmd == Cmd.Continued; i++)
                    {
                        Step(ref steps);
                        data.AddRange(ContinuedData(file, events[i].At, eventLength));
                    }
                    int end = data.Count;
                    while (end > from && data[end - 1] == 0xF7) end--;
                    data.RemoveRange(end, data.Count - end);
                    flat.Add(new Resolved(e.Cmd, e.St, e.Gt, e.Vel, from, end - from));
                    break;
                }

                case Cmd.Continued:
                    // A continuation with nothing to continue.
                    i++;
                    break;

                default:
                    flat.Add(new Resolved(e.Cmd, e.St, e.Gt, e.Vel));
                    i++;
                    break;
            }
        }
    }

    private static void Step(ref int steps)
    {
        if (--steps < 0) throw new InvalidDataException(Strings.RcpLoopTooLarge);
    }

    /// <summary>The event index a repeated-measure pointer names.</summary>
    private static int SameMeasureTarget(ReadOnlySpan<byte> raw, int eventLength, int headerLength)
    {
        // The offset counts bytes from the start of the track header; RCP3 counts events,
        // from a base that sits 0xF2 bytes early.
        int offset = eventLength == 4
            ? (raw[2] & 0xFC) | (raw[3] << 8)
            : (raw[4] | (raw[5] << 8)) * 6 - 0xF2;
        int from = offset - headerLength;
        return from >= 0 && from % eventLength == 0 ? from / eventLength : -1;
    }

    /// <summary>The data a comment's own event carries; the exclusives carry none.</summary>
    private static ReadOnlySpan<byte> FirstData(ReadOnlySpan<byte> file, Event e, int eventLength)
        => e.Cmd != Cmd.Comment ? [] : ContinuedData(file, e.At, eventLength);

    private static ReadOnlySpan<byte> ContinuedData(ReadOnlySpan<byte> file, int at, int eventLength)
        => eventLength == 4 ? file.Slice(at + 2, 2) : file.Slice(at + 1, 5);

    // ---- writing ----------------------------------------------------------------------

    private sealed class Writer(Song song, int endless)
    {
        private readonly Dictionary<long, (int Gt, int Vel)> _tempoChanges = [];
        private long _longest;

        /// <summary>
        /// Whether the tracks use more than one port. A <c>[ch]</c> in an exclusive then
        /// stands for the 0-31 number rather than the part within the port.
        /// </summary>
        private readonly bool _spread =
            song.Tracks.Select(t => t.MidiCh >= 0 ? t.MidiCh / 16 : 0).Distinct().Count() > 1;

        public byte[] Build(ReadOnlySpan<byte> file)
        {
            // From 0x8000 on, an SMF's division says SMPTE, which is not what was meant.
            int timeBase = song.TimeBase is > 0 and <= 0x7FFF ? song.TimeBase : 48;
            int tempo = song.Tempo > 0 ? song.Tempo : 120;

            var conductor = new SmfTrack();
            conductor.Meta(0, 0x03, Trim(song.Title));
            foreach (byte[] line in song.Memo)
            {
                ReadOnlySpan<byte> text = Trim(line);
                if (text.Length > 0) conductor.Meta(0, 0x01, text);
            }
            conductor.Tempo(0, tempo);

            var bodies = new List<byte[]>(song.Tracks.Count + 1);
            var unrolled = new Unrolled();
            var smf = new SmfTrack();
            int steps = MaxUnrollSteps;
            foreach (Track track in song.Tracks)
            {
                Unroll(file, track.Events, song.EventLength, endless, ref steps, unrolled);

                // A muted track, and one holding nothing but its end mark, stay out.
                if ((track.Mode & 0x01) != 0 || unrolled.Events.Count <= 1) continue;

                smf.Clear();
                Convert(track, unrolled, smf);
                bodies.Add(smf.Encode());
            }

            // First in the file, but known only now.
            AddTempoChanges(conductor, tempo, timeBase);
            conductor.Meta(_longest, 0x2F, []);
            bodies.Insert(0, conductor.Encode());

            int length = 14;
            foreach (byte[] body in bodies) length += 8 + body.Length;

            var output = new byte[length];
            Span<byte> to = output;
            "MThd"u8.CopyTo(to);
            BinaryPrimitives.WriteUInt32BigEndian(to[4..], 6);
            BinaryPrimitives.WriteUInt16BigEndian(to[8..], 1);
            BinaryPrimitives.WriteUInt16BigEndian(to[10..], (ushort)bodies.Count);
            BinaryPrimitives.WriteUInt16BigEndian(to[12..], (ushort)timeBase);
            to = to[14..];
            foreach (byte[] body in bodies)
            {
                "MTrk"u8.CopyTo(to);
                BinaryPrimitives.WriteUInt32BigEndian(to[4..], (uint)body.Length);
                body.CopyTo(to[8..]);
                to = to[(8 + body.Length)..];
            }
            return output;
        }

        /// <summary>Turns one track, played out, into SMF events in <paramref name="smf"/>.</summary>
        private void Convert(Track track, Unrolled unrolled, SmfTrack smf)
        {
            int midiCh = track.MidiCh;
            int channel = midiCh >= 0 ? midiCh % 16 : -1;
            int port = midiCh >= 0 ? midiCh / 16 : 0;
            int keyShift = (track.KeyShift & 0x80) != 0
                ? 0
                : song.PlayBias + track.KeyShift - (track.KeyShift >= 0x40 ? 0x80 : 0);
            var gates = new int[128];
            Array.Fill(gates, -1);
            // Where each note sounding was struck. The track's channel can change while a note
            // is held, and its note-off belongs where the note went.
            var gateChannels = new int[128];
            var gatePorts = new int[128];

            void NoteOff(long tick, int note)
            {
                int to = gatePorts[note];
                if (to != port) smf.Meta(tick, 0x21, [(byte)to]);
                smf.Short(tick, 0x90 | gateChannels[note], note, 0);
                if (to != port) smf.Meta(tick, 0x21, [(byte)port]);
                gates[note] = -1;
            }
            (int Dev, int Model)? rolandDevice = null, yamahaDevice = null;
            (int H, int M)? rolandBase = null, yamahaBase = null;

            smf.Meta(0, 0x03, Trim(track.Memo));
            if (port != 0) smf.Meta(0, 0x21, [(byte)port]);

            long now = track.StShift;
            foreach (Resolved e in unrolled.Events)
            {
                int st = e.St;
                int gt = e.Gt, vel = e.Vel;

                if (e.Cmd < 0x80)
                {
                    if (channel >= 0 && gt > 0 && vel is > 0 and < 0x80)
                    {
                        int note = e.Cmd + keyShift;
                        if (note is >= 0 and < 128)
                        {
                            // A note already sounding is tied, not struck again — unless it
                            // is sounding somewhere else, which is another note.
                            if (gates[note] >= 0 && (gateChannels[note] != channel || gatePorts[note] != port))
                                NoteOff(now, note);
                            if (gates[note] < 0) smf.Short(now, 0x90 | channel, note, vel);
                            gates[note] = gt;
                            gateChannels[note] = channel;
                            gatePorts[note] = port;
                        }
                    }
                }
                else
                {
                    bool on = channel >= 0;
                    switch (e.Cmd)
                    {
                        case Cmd.Control:
                            if (on && Seven(gt, vel)) smf.Short(now, 0xB0 | channel, gt, vel);
                            break;
                        case Cmd.PitchBend:
                            if (on && Seven(gt, vel)) smf.Short(now, 0xE0 | channel, gt, vel);
                            break;
                        case Cmd.ChannelPressure:
                            if (on && Seven(gt)) smf.Short(now, 0xD0 | channel, gt);
                            break;
                        case Cmd.KeyPressure:
                            if (on && Seven(gt, vel)) smf.Short(now, 0xA0 | channel, gt, vel);
                            break;
                        case Cmd.Program:
                            if (on && Seven(gt)) smf.Short(now, 0xC0 | channel, gt);
                            break;
                        case Cmd.BankProgram or Cmd.BankProgramLsb:
                            if (on && Seven(gt, vel))
                            {
                                smf.Short(now, 0xB0 | channel, e.Cmd == Cmd.BankProgram ? 0 : 32, vel);
                                smf.Short(now, 0xC0 | channel, gt);
                            }
                            break;

                        case >= Cmd.UserExclusive0 and <= Cmd.UserExclusive7:
                            if (Seven(gt, vel))
                                Exclusive(smf, now, song.UserExclusive[e.Cmd - Cmd.UserExclusive0],
                                          _spread ? midiCh : channel, gt, vel);
                            break;
                        case Cmd.TrackExclusive:
                            if (Seven(gt, vel) && e.DataLength > 0)
                                Exclusive(smf, now, unrolled.DataOf(e), _spread ? midiCh : channel, gt, vel);
                            break;

                        case Cmd.RolandBase:
                            if (Seven(gt, vel)) rolandBase = (gt, vel);
                            break;
                        case Cmd.RolandDevice:
                            if (Seven(gt, vel)) rolandDevice = (gt, vel);
                            break;
                        case Cmd.RolandParameter:
                            if (Seven(gt, vel))
                            {
                                // Until told otherwise, an MT-32 at its usual address.
                                var (dev, model) = rolandDevice ?? (0x10, 0x16);
                                var (h, m) = rolandBase ?? (0x00, 0x10);
                                Exclusive(smf, now,
                                    [0x41, (byte)dev, (byte)model, 0x12, 0x83, (byte)h, (byte)m, 0x80, 0x81, 0x84],
                                    0, gt, vel);
                            }
                            break;
                        case Cmd.YamahaBase:
                            if (Seven(gt, vel)) yamahaBase = (gt, vel);
                            break;
                        case Cmd.YamahaDevice:
                            if (Seven(gt, vel)) yamahaDevice = (gt, vel);
                            break;
                        case Cmd.YamahaParameter:
                            if (Seven(gt, vel))
                            {
                                var (dev, model) = yamahaDevice ?? (0x10, 0x4C);
                                var (h, m) = yamahaBase ?? (0x00, 0x00);
                                Exclusive(smf, now,
                                    [0x43, (byte)dev, (byte)model, 0x83, (byte)h, (byte)m, 0x80, 0x81, 0x84],
                                    0, gt, vel);
                            }
                            break;
                        case Cmd.XgParameter:
                            if (Seven(gt, vel))
                            {
                                var (dev, model) = yamahaDevice ?? (0x10, 0x4C);
                                var (h, m) = yamahaBase ?? (0x00, 0x00);
                                smf.SysEx(now, [0xF0, 0x43, (byte)dev, (byte)model, (byte)h, (byte)m,
                                                (byte)gt, (byte)vel, 0xF7]);
                            }
                            break;
                        case (>= 0xC0 and <= 0xCF and not 0xC4) or Cmd.Mks7:
                            if (on) DeviceExclusive(smf, now, e.Cmd, channel, gt, vel);
                            break;

                        case Cmd.MidiChannel:
                            if (gt is >= 0 and <= 32)
                            {
                                // The event counts from 1 with 0 for none; the header from 0.
                                int oldPort = port;
                                midiCh = gt - 1;
                                channel = midiCh >= 0 ? midiCh % 16 : -1;
                                if (midiCh >= 0) port = midiCh / 16;
                                if (port != oldPort) smf.Meta(now, 0x21, [(byte)port]);
                            }
                            break;

                        case Cmd.Tempo:
                            if (gt > 0) _tempoChanges[now] = (gt, vel);
                            break;

                        case Cmd.Comment:
                            ReadOnlySpan<byte> text = Trim(unrolled.DataOf(e));
                            if (text.Length > 0) smf.Meta(now, 0x01, text);
                            st = 0;
                            break;

                        case Cmd.MusicKey or Cmd.MeasureEnd:
                            st = 0;
                            break;

                        case Cmd.TrackEnd:
                            // Long enough for the last notes to finish.
                            st = Math.Max(0, gates.Max());
                            break;

                        case Cmd.KeyScan or Cmd.ExternalCommand:
                            // Instructions to Recomposer's screen and to other programs.
                            break;

                        default:
                            // Nothing Recomposer defines: no time passes on it either.
                            st = 0;
                            break;
                    }
                }

                // Notes whose gate runs out before the next event end on the way there, where
                // they were struck: the track may have moved on to another channel, or none.
                for (int note = 0; note < 128; note++)
                {
                    if (gates[note] < 0) continue;
                    if (gates[note] <= st) NoteOff(now + gates[note], note);
                    else gates[note] -= st;
                }

                now += st;
            }

            now = Math.Max(0, now);
            smf.Meta(now, 0x2F, []);
            _longest = Math.Max(_longest, now);
        }


        /// <summary>
        /// Recomposer's tempo changes are a ratio of the song's tempo, 64 being as written,
        /// and may glide there over a number of steps set by a table.
        /// </summary>
        private void AddTempoChanges(SmfTrack conductor, int baseTempo, int timeBase)
        {
            if (_tempoChanges.Count == 0) return;

            // Only the ticks where something happens: a change, or a step of a glide.
            long[] changes = [.. _tempoChanges.Keys.Order()];
            int nextChange = 0;
            int current = baseTempo;
            var glide = new SortedDictionary<long, int>();
            while (true)
            {
                long tick = Math.Min(nextChange < changes.Length ? changes[nextChange] : long.MaxValue,
                                     glide.Count > 0 ? glide.Keys.First() : long.MaxValue);
                if (tick >= _longest) break;

                int before = current;
                if (nextChange < changes.Length && changes[nextChange] == tick)
                {
                    var change = _tempoChanges[tick];
                    nextChange++;
                    int target = (int)Math.Min(int.MaxValue, (long)baseTempo * change.Gt / 64);
                    glide.Clear();
                    if (change.Vel == 0 || change.Vel >= GlideSteps.Length)
                    {
                        current = target;
                    }
                    else
                    {
                        int steps = GlideSteps[change.Vel];
                        // Truncated as a whole: slowing down lands on the slower side of
                        // the fraction, as Recomposer does, not the faster.
                        for (int i = 0; i < steps; i += 2)
                            glide[tick + i * timeBase / 48] =
                                (int)(current + (double)(target - current) * i / steps);
                        glide[tick + steps * timeBase / 48] = target;
                    }
                }
                if (glide.Remove(tick, out int next)) current = next;
                if (current != before) conductor.Tempo(tick, current);
            }
        }
    }

    /// <summary>How many steps (at 48 per beat) a tempo glide of each speed takes.</summary>
    private static readonly int[] GlideSteps =
    [
          0, 255, 225, 208, 195, 186, 178, 171, 165, 160, 156, 151, 148, 144, 141, 138,
        135, 132, 130, 128, 125, 123, 121, 119, 117, 116, 114, 112, 111, 109, 108, 106,
        105, 104, 102, 101, 100,  99,  98,  96,  95,  94,  93,  92,  91,  90,  89,  88,
         87,  86,  86,  85,  84,  83,  82,  81,  81,  80,  79,  78,  78,  77,  76,  76,
         75,  74,  74,  73,  72,  72,  71,  70,  70,  69,  69,  68,  67,  67,  66,  66,
         65,  65,  64,  64,  63,  63,  62,  62,  61,  61,  60,  60,  59,  59,  58,  58,
         57,  57,  56,  56,  56,  55,  55,  54,  54,  53,  53,  53,  52,  52,  51,  51,
         51,  50,  50,  49,  49,  49,  48,  48,  48,  47,  47,  47,  46,  46,  45,  45,
         45,  44,  44,  44,  43,  43,  43,  42,  42,  42,  42,  41,  41,  41,  40,  40,
         40,  39,  39,  39,  38,  38,  38,  38,  37,  37,  37,  36,  36,  36,  36,  35,
         35,  35,  35,  34,  34,  34,  33,  33,  33,  33,  32,  32,  32,  32,  31,  31,
         31,  31,  30,  30,  30,  30,  29,  29,  29,  29,  29,  28,  28,  28,  28,  27,
         27,  27,  27,  26,  26,  26,  26,  26,  25,  25,  25,  25,  25,  24,  24,  24,
         24,  23,  23,  23,  23,  23,  22,  22,  22,  22,  22,  21,  21,  21,  21,  21,
         20,  20,  20,  20,  20,  20,  19,  19,  19,  19,  19,  18,  18,  18,  18,  18,
         17,  17,  17,  17,  17,  17,  16,  16,  16,  16,  16,  16,  15,  15,  15,  15,
    ];

    /// <summary>
    /// Adds an exclusive built from a Recomposer template, or nothing when a value will not
    /// fit in seven bits. 0x80-0x82 stand for the event's gate, velocity and channel; 0x83
    /// starts a Roland checksum and 0x84 places it; 0xF7 ends the template early.
    /// </summary>
    private static void Exclusive(SmfTrack smf, long tick, ReadOnlySpan<byte> template,
                                  int channel, int gt, int vel)
    {
        const int OnStack = 256;
        Span<byte> bytes = template.Length + 2 <= OnStack
            ? stackalloc byte[OnStack]
            : new byte[template.Length + 2];
        int count = 0;
        bytes[count++] = 0xF0;
        int sum = 0;
        foreach (byte t in template)
        {
            int value;
            switch (t)
            {
                case 0x80: value = gt; break;
                case 0x81: value = vel; break;
                case 0x82:
                    if (channel < 0) return;
                    value = channel;
                    break;
                case 0x83: sum = 0; continue;
                case 0x84: value = (0x80 - sum) & 0x7F; break;
                case 0xF7: goto done;
                default: value = t; break;
            }
            if ((value & ~0x7F) != 0) return;
            bytes[count++] = (byte)value;
            sum = (sum + value) & 0x7F;
        }
    done:
        bytes[count++] = 0xF7;
        smf.SysEx(tick, bytes[..count]);
    }

    /// <summary>Adds one of the one-line parameter changes Recomposer knows for Yamaha FM and the MKS-7.</summary>
    private static void DeviceExclusive(SmfTrack smf, long tick, int cmd, int ch, int gt, int vel)
    {
        byte c = (byte)ch, g = (byte)(gt & 0x7F), v = (byte)(vel & 0x7F);
        byte y = (byte)(0x10 | ch);
        switch (cmd)
        {
            case 0xC0: smf.SysEx(tick, [0xF0, 0x43, y, (byte)(0x08 | (gt >> 7)), g, v, 0xF7]); break;   // DX7 function
            case 0xC1: smf.SysEx(tick, [0xF0, 0x43, y, (byte)(0x00 | (gt >> 7)), g, v, 0xF7]); break;   // DX voice
            case 0xC2: smf.SysEx(tick, [0xF0, 0x43, y, (byte)(0x04 | (gt >> 7)), g, v, 0xF7]); break;   // DX performance
            case 0xC3: smf.SysEx(tick, [0xF0, 0x43, y, 0x11, g, v, 0xF7]); break;                       // TX function
            case 0xC5: smf.SysEx(tick, [0xF0, 0x43, y, 0x15, g, (byte)(vel & 0x0F), (byte)((vel >> 4) & 0x0F), 0xF7]); break; // FB-01
            case 0xC6: smf.SysEx(tick, [0xF0, 0x43, 0x75, c, 0x10, g, v, 0xF7]); break;                 // FB-01 system
            case 0xC7: smf.SysEx(tick, [0xF0, 0x43, y, 0x12, g, v, 0xF7]); break;                       // TX81Z VCED
            case 0xC8: smf.SysEx(tick, [0xF0, 0x43, y, 0x13, g, v, 0xF7]); break;                       // TX81Z ACED
            case 0xC9: smf.SysEx(tick, [0xF0, 0x43, y, 0x10, g, v, 0xF7]); break;                       // TX81Z PCED
            case 0xCA: smf.SysEx(tick, [0xF0, 0x43, y, 0x10, 0x7B, g, v, 0xF7]); break;                 // TX81Z system
            case 0xCB: smf.SysEx(tick, [0xF0, 0x43, y, 0x10, 0x7C, g, v, 0xF7]); break;                 // TX81Z effect
            case 0xCC: smf.SysEx(tick, [0xF0, 0x43, y, 0x1B, g, v, 0xF7]); break;                       // DX7II remote
            case 0xCD: smf.SysEx(tick, [0xF0, 0x43, y, 0x18, g, v, 0xF7]); break;                       // DX7II ACED
            case 0xCE: smf.SysEx(tick, [0xF0, 0x43, y, 0x19, g, v, 0xF7]); break;                       // DX7II PCED
            case 0xCF: smf.SysEx(tick, [0xF0, 0x43, y, 0x1A, g, v, 0xF7]); break;                       // TX802 PCED
            case Cmd.Mks7: smf.SysEx(tick, [0xF0, 0x41, 0x32, c, g, v, 0xF7]); break;
        }
    }

    private static bool Seven(params ReadOnlySpan<int> values)
    {
        foreach (int v in values)
            if ((v & ~0x7F) != 0) return false;
        return true;
    }

    /// <summary>Text fields are fixed width, padded with spaces or cut short with NUL.</summary>
    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> text)
    {
        int nul = text.IndexOf((byte)0);
        if (nul >= 0) text = text[..nul];
        return text.Trim((byte)' ');
    }

    /// <summary>The length of an SMF variable-length quantity.</summary>
    private static int VariableLength(long value)
    {
        int count = 1;
        for (value >>= 7; value > 0; value >>= 7) count++;
        return count;
    }

    /// <summary>Writes an SMF variable-length quantity, answering its length.</summary>
    private static int WriteVariableLength(Span<byte> to, long value)
    {
        int count = VariableLength(value);
        to[count - 1] = (byte)(value & 0x7F);
        for (int i = count - 2; i >= 0; i--)
        {
            value >>= 7;
            to[i] = (byte)(0x80 | (value & 0x7F));
        }
        return count;
    }

    /// <summary>A track's events by absolute tick, kept in the order they were made.</summary>
    /// <remarks>
    /// Events share one byte buffer; where an event's bytes start also orders events on the
    /// same tick.
    /// </remarks>
    private sealed class SmfTrack
    {
        private readonly record struct Pending(long Tick, int At, int Length) : IComparable<Pending>
        {
            public int CompareTo(Pending other)
                => Tick != other.Tick ? Tick.CompareTo(other.Tick) : At.CompareTo(other.At);
        }

        private readonly List<Pending> _events = [];
        private readonly List<byte> _bytes = [];

        public void Clear()
        {
            _events.Clear();
            _bytes.Clear();
        }

        public void Short(long tick, int status, int data1)
            => Add(tick, [(byte)status, (byte)data1]);

        public void Short(long tick, int status, int data1, int data2)
            => Add(tick, [(byte)status, (byte)data1, (byte)data2]);

        public void Meta(long tick, byte type, ReadOnlySpan<byte> data)
        {
            int at = _bytes.Count;
            _bytes.Add(0xFF);
            _bytes.Add(type);
            AddVariableLength(data.Length);
            _bytes.AddRange(data);
            Added(tick, at);
        }

        /// <remarks>
        /// Below 4 BPM a beat is longer than the three bytes of the meta event hold, so it is
        /// held at 4.
        /// </remarks>
        public void Tempo(long tick, int bpm)
        {
            int us = 60_000_000 / Math.Max(4, bpm);
            Meta(tick, 0x51, [(byte)(us >> 16), (byte)(us >> 8), (byte)us]);
        }

        /// <param name="message">From its F0 to its F7.</param>
        public void SysEx(long tick, ReadOnlySpan<byte> message)
        {
            int at = _bytes.Count;
            _bytes.Add(0xF0);
            AddVariableLength(message.Length - 1);
            _bytes.AddRange(message[1..]);
            Added(tick, at);
        }

        private void Add(long tick, ReadOnlySpan<byte> bytes)
        {
            int at = _bytes.Count;
            _bytes.AddRange(bytes);
            Added(tick, at);
        }

        // An event pushed before the start by a negative ST+ is played at the start.
        private void Added(long tick, int at)
            => _events.Add(new Pending(Math.Max(0, tick), at, _bytes.Count - at));

        private void AddVariableLength(long value)
        {
            Span<byte> bytes = stackalloc byte[10];
            _bytes.AddRange(bytes[..WriteVariableLength(bytes, value)]);
        }

        /// <summary>The track's body, in tick order.</summary>
        public byte[] Encode()
        {
            Span<Pending> events = CollectionsMarshal.AsSpan(_events);
            events.Sort();

            int length = 0;
            long last = 0;
            foreach (Pending e in events)
            {
                length += VariableLength(e.Tick - last) + e.Length;
                last = e.Tick;
            }

            var body = new byte[length];
            ReadOnlySpan<byte> bytes = CollectionsMarshal.AsSpan(_bytes);
            int to = 0;
            last = 0;
            foreach (Pending e in events)
            {
                to += WriteVariableLength(body.AsSpan(to), e.Tick - last);
                bytes.Slice(e.At, e.Length).CopyTo(body.AsSpan(to));
                to += e.Length;
                last = e.Tick;
            }
            return body;
        }
    }
}
