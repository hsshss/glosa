using System.Buffers.Binary;
using Glosa.Core.Archives;
using Glosa.Core.Rcp;
using Glosa.Core.Text;

namespace Glosa.Core.Smf;

/// <summary>
/// The data is not an SMF.
/// </summary>
/// <remarks>
/// An <see cref="IOException"/>, because to the caller it is a file that could not be read
/// as a song, and every place that skips an unreadable song already catches those.
/// (<see cref="InvalidDataException"/> would say it better but cannot be derived from.)
/// </remarks>
public sealed class SmfFormatException(string message) : IOException(message);

/// <summary>
/// Standard MIDI File reader.
/// </summary>
/// <remarks>
/// The <c>FF 21</c> port meta event is honoured, text is decoded as Shift-JIS, SysEx bytes
/// are kept verbatim, and any number of tracks is accepted.
/// </remarks>
public static class SmfReader
{
    private const uint MThd = 0x4D546864;
    private const uint MTrk = 0x4D54726B;
    private const long DefaultTempo = 500_000; // 120 BPM, per the SMF spec

    /// <summary>
    /// A song running past this is taken to be broken. Longer than any song, and well inside
    /// the <see cref="TimeSpan"/> the player shows times in.
    /// </summary>
    private const double MaxDurationUs = 365.0 * 24 * 60 * 60 * 1_000_000;

    /// <summary>Reads a file on disk, or one inside an archive (see <see cref="SongStore"/>).</summary>
    /// <param name="infiniteLoopRepeatCount">
    /// For Recomposer data, how many times a loop marked endless plays (see
    /// <see cref="RcpConverter.ToSmf"/>). An SMF has no loops and ignores it.
    /// </param>
    public static MidiSequence Read(
        string path, int infiniteLoopRepeatCount = RcpConverter.DefaultInfiniteLoopRepeatCount)
        => Read(SongStore.ReadAllBytes(path), infiniteLoopRepeatCount);

    private const int MacBinaryHeader = 128;

    /// <summary>
    /// The SMF inside a MacBinary file, or the data as it came.
    /// </summary>
    /// <remarks>
    /// MacBinary is a 128-byte header carrying the Finder's name, type and creator, then the
    /// data fork, then the resource fork. The song is the data fork. The header is recognised by the
    /// fields MacBinary I and II agree on — a zero first byte, a name of 1 to 63 bytes, a
    /// zero at 74 — and by <c>MThd</c> (or a Recomposer header) sitting where the data fork
    /// starts, which is what makes the guess safe; the CRC only exists from version II on.
    /// The data fork's length bounds the slice, so the resource fork behind it is not read
    /// as chunks.
    /// </remarks>
    private static ReadOnlySpan<byte> UnwrapMacBinary(ReadOnlySpan<byte> data)
    {
        if (data.Length < MacBinaryHeader + 4) return data;
        if (IsSong(data)) return data;
        if (data[0] != 0 || data[1] is 0 or > 63 || data[74] != 0) return data;
        if (!IsSong(data[MacBinaryHeader..])) return data;

        long fork = BinaryPrimitives.ReadUInt32BigEndian(data[83..]);
        int length = (int)Math.Min(fork, data.Length - MacBinaryHeader);
        return data.Slice(MacBinaryHeader, length > 0 ? length : data.Length - MacBinaryHeader);
    }

    /// <summary>
    /// The SMF inside an RMI file, or the data as it came.
    /// </summary>
    /// <remarks>
    /// RMI is Microsoft's RIFF wrapper: <c>RIFF</c>, a length, <c>RMID</c>, then chunks, one
    /// of which is <c>data</c> and holds the SMF unchanged. The others (<c>LIST</c> with the
    /// title and copyright, sometimes a <c>DISP</c>) repeat what the SMF says or say nothing
    /// a player needs. Chunks are padded to an even length, which the walk honours; a
    /// <c>data</c> chunk longer than the file is cut to what is there.
    /// </remarks>
    private static ReadOnlySpan<byte> UnwrapRiff(ReadOnlySpan<byte> data)
    {
        if (!IsRmi(data)) return data;

        for (int at = 12; at + 8 <= data.Length;)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            int body = at + 8;
            if (data.Slice(at, 4).SequenceEqual("data"u8))
                return data.Slice(body, (int)Math.Min(size, (uint)(data.Length - body)));
            if (size > (uint)(data.Length - body)) break;
            at = body + (int)size + (int)(size & 1);
        }
        return data;
    }

    private static bool IsRmi(ReadOnlySpan<byte> data)
        => data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..].StartsWith("RMID"u8);

    private static bool IsSong(ReadOnlySpan<byte> data)
        => (data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == MThd)
           || RcpConverter.IsRecomposer(data)
           || IsRmi(data);

    /// <summary>
    /// Reads an SMF, bare or in an RMI or MacBinary wrapper, or Recomposer data by way of
    /// <see cref="RcpConverter"/>. Which one is told by the header, not the file name.
    /// </summary>
    public static MidiSequence Read(
        ReadOnlySpan<byte> data, int infiniteLoopRepeatCount = RcpConverter.DefaultInfiniteLoopRepeatCount)
    {
        data = UnwrapRiff(UnwrapMacBinary(data));
        if (RcpConverter.IsRecomposer(data))
            return Read(RcpConverter.ToSmf(data, infiniteLoopRepeatCount), infiniteLoopRepeatCount);

        List<Range> bodies = ReadChunks(data, out int format, out int division);

        // Measured first, so the events and the payload go into blocks of exactly their size.
        var measure = new Measure();
        foreach (Range body in bodies) ReadTrack(data[body], 0, ref measure);

        var store = new Store(measure.Events, measure.PayloadBytes);
        var starts = new int[bodies.Count + 1];
        for (int track = 0; track < bodies.Count; track++)
        {
            starts[track] = store.EventCount;
            ReadTrack(data[bodies[track]], track, ref store);
        }
        starts[^1] = store.EventCount;

        MidiEvent[] merged = Merge(store.Events, starts);
        long duration = ApplyTiming(merged, division, store.Payload);

        Metadata said = store.Said;
        return new MidiSequence(format, bodies.Count, division, merged,
                                store.Payload, duration,
                                said.Title, said.Copyright, said.Comment, said.Texts);
    }

    /// <summary>Reads what a playlist needs of a file on disk or in an archive.</summary>
    /// <inheritdoc cref="ReadSummary(ReadOnlySpan{byte}, int, int)"/>
    public static SongSummary ReadSummary(
        string path, int openingMessages,
        int infiniteLoopRepeatCount = RcpConverter.DefaultInfiniteLoopRepeatCount)
        => ReadSummary(SongStore.ReadAllBytes(path), openingMessages, infiniteLoopRepeatCount);

    /// <summary>
    /// Reads a song's length, title and opening messages without building it for playing.
    /// </summary>
    /// <param name="openingMessages">How many messages to keep (<see cref="SongSummary.Opening"/>).</param>
    public static SongSummary ReadSummary(
        ReadOnlySpan<byte> data, int openingMessages,
        int infiniteLoopRepeatCount = RcpConverter.DefaultInfiniteLoopRepeatCount)
    {
        data = UnwrapRiff(UnwrapMacBinary(data));
        if (RcpConverter.IsRecomposer(data))
            return ReadSummary(RcpConverter.ToSmf(data, infiniteLoopRepeatCount), openingMessages,
                               infiniteLoopRepeatCount);

        List<Range> bodies = ReadChunks(data, out _, out int division);

        var summary = new Summarise(openingMessages);
        for (int track = 0; track < bodies.Count; track++)
            ReadTrack(data[bodies[track]], track, ref summary);
        return summary.Finish(division);
    }

    /// <summary>Reads the header, and finds where each track's events are.</summary>
    private static List<Range> ReadChunks(ReadOnlySpan<byte> data, out int format, out int division)
    {
        var r = new Cursor(data);

        if (r.ReadUInt32() != MThd) throw new SmfFormatException("Not an SMF: missing MThd.");
        uint headerLength = r.ReadUInt32();
        if (headerLength < 6) throw new SmfFormatException("MThd chunk is too short.");

        format = r.ReadUInt16();
        int declaredTracks = r.ReadUInt16();
        division = r.ReadUInt16();
        r.Skip((int)headerLength - 6);

        if (division == 0) throw new SmfFormatException("Division is zero.");

        var bodies = new List<Range>(Math.Max(declaredTracks, 1));
        while (r.Remaining >= 8)
        {
            uint id = r.ReadUInt32();
            uint length = r.ReadUInt32();
            if (length > (uint)r.Remaining) length = (uint)r.Remaining;

            int start = r.Position;
            r.Skip((int)length);
            if (id == MTrk) bodies.Add(new Range(start, start + (int)length));
        }
        return bodies;
    }

    /// <summary>What <see cref="ReadTrack{TSink}"/> hands each event it finds to.</summary>
    private interface ITrackSink
    {
        void Channel(long ticks, int track, int port, uint packed);

        void Meta(long ticks, int track, int port, byte type, ReadOnlySpan<byte> data);

        /// <param name="status"><c>F0</c> or <c>F7</c>.</param>
        /// <param name="data">The bytes after the length, without the <c>F0</c>.</param>
        void SysEx(long ticks, int track, int port, byte status, ReadOnlySpan<byte> data);
    }

    /// <summary>Walks one track, handing each event to <paramref name="sink"/>.</summary>
    private static void ReadTrack<TSink>(ReadOnlySpan<byte> body, int track, ref TSink sink)
        where TSink : struct, ITrackSink
    {
        var r = new Cursor(body);
        long ticks = 0;
        byte runningStatus = 0;
        int port = 0;

        while (r.Remaining > 0)
        {
            ticks += r.ReadVariableLength();
            if (r.Remaining == 0) break;

            byte b = r.PeekByte();
            byte status;
            if (b >= 0x80) { status = b; r.Skip(1); if (b < 0xF0) runningStatus = b; }
            else
            {
                if (runningStatus == 0)
                    throw new SmfFormatException("Running status used before any status byte.");
                status = runningStatus;
            }

            if (status == 0xFF)
            {
                byte metaType = r.ReadByte();
                int length = r.ReadVariableLength();
                ReadOnlySpan<byte> data = r.ReadBytes(length);

                // Applies to this track from here on.
                if (metaType == MetaType.MidiPort && data.Length >= 1) port = data[0];

                sink.Meta(ticks, track, port, metaType, data);
                if (metaType == MetaType.EndOfTrack) break;
            }
            else if (status is 0xF0 or 0xF7)
            {
                int length = r.ReadVariableLength();
                sink.SysEx(ticks, track, port, status, r.ReadBytes(length));
            }
            else
            {
                byte data1 = r.ReadByte();
                byte data2 = 0;
                bool twoBytes = (status & 0xF0) is not (0xC0 or 0xD0);
                if (twoBytes) data2 = r.ReadByte();

                sink.Channel(ticks, track, port, status | (uint)data1 << 8 | (uint)data2 << 16);
            }
        }
    }

    /// <summary>How long a SysEx is once kept: the <c>F0</c> the file omits is put back.</summary>
    private static int StoredLength(byte status, ReadOnlySpan<byte> data)
        => status == 0xF0 ? data.Length + 1 : data.Length;

    /// <summary>Counts what <see cref="Store"/> will need room for.</summary>
    private struct Measure : ITrackSink
    {
        public int Events;
        public int PayloadBytes;

        public void Channel(long ticks, int track, int port, uint packed) => Events++;

        public void Meta(long ticks, int track, int port, byte type, ReadOnlySpan<byte> data)
        {
            Events++;
            PayloadBytes += data.Length;
        }

        public void SysEx(long ticks, int track, int port, byte status, ReadOnlySpan<byte> data)
        {
            Events++;
            PayloadBytes += StoredLength(status, data);
        }
    }

    /// <summary>Where the second walk puts what it reads, sized by the first.</summary>
    private struct Store(int events, int payload) : ITrackSink
    {
        public MidiEvent[] Events { get; } = new MidiEvent[events];

        public int EventCount { get; private set; }

        /// <summary>SysEx and meta bytes, one after another.</summary>
        public byte[] Payload { get; } = new byte[payload];

        private int _payloadCount;

        public Metadata Said { get; } = new();

        public void Channel(long ticks, int track, int port, uint packed)
            => Events[EventCount++] = new MidiEvent(ticks, track, port, packed);

        public void Meta(long ticks, int track, int port, byte type, ReadOnlySpan<byte> data)
        {
            Gather(Said, type, data);
            Events[EventCount++] = new MidiEvent(ticks, track, port, MidiEventKind.Meta,
                                                 type, Append(data), data.Length);
        }

        public void SysEx(long ticks, int track, int port, byte status, ReadOnlySpan<byte> data)
        {
            int offset = _payloadCount;
            // Re-attach the F0 the file omits so the message can go straight out.
            if (status == 0xF0) Append([0xF0]);
            Append(data);
            Events[EventCount++] = new MidiEvent(ticks, track, port, MidiEventKind.SysEx,
                                                 status, offset, StoredLength(status, data));
        }

        /// <returns>Where the bytes start in <see cref="Payload"/>.</returns>
        private int Append(ReadOnlySpan<byte> data)
        {
            int offset = _payloadCount;
            data.CopyTo(Payload.AsSpan(offset));
            _payloadCount += data.Length;
            return offset;
        }
    }

    /// <summary>Keeps what <see cref="ReadSummary(ReadOnlySpan{byte}, int, int)"/> answers with.</summary>
    private struct Summarise(int openingMessages) : ITrackSink
    {
        private readonly int _limit = Math.Max(openingMessages, 0);

        private string _title = string.Empty;

        /// <summary>Whether the song has any event at all, and the tick of its last.</summary>
        private bool _any;
        private long _lastTicks;

        /// <summary>Tempo changes, in the order the tracks were read.</summary>
        private readonly List<(long Ticks, long Tempo)> _tempos = [];

        /// <summary>The earliest messages so far in merge order, the latest on top.</summary>
        private readonly PriorityQueue<Opened, (long Ticks, int Track, int Seq)> _opening =
            new(Comparer<(long Ticks, int Track, int Seq)>.Create((a, b) => b.CompareTo(a)));

        private int _seq;

        /// <summary>A message kept for the opening, with its own copy of an exclusive's bytes.</summary>
        private readonly record struct Opened(long Ticks, int Track, int Port, uint Packed,
                                              byte Status, byte[]? Data);

        public void Channel(long ticks, int track, int port, uint packed)
        {
            Seen(ticks);
            if (Wanted(ticks, track, out var key))
                Keep(new Opened(ticks, track, port, packed, 0, null), key);
        }

        public void Meta(long ticks, int track, int port, byte type, ReadOnlySpan<byte> data)
        {
            Seen(ticks);
            if (type == MetaType.TrackName) TakeTitle(ref _title, data);
            else if (type == MetaType.Tempo && TempoOf(data) is { } tempo) _tempos.Add((ticks, tempo));
        }

        public void SysEx(long ticks, int track, int port, byte status, ReadOnlySpan<byte> data)
        {
            Seen(ticks);
            if (!Wanted(ticks, track, out var key)) return;

            // Re-attach the F0 the file omits, as the full read does.
            var copy = new byte[StoredLength(status, data)];
            if (status == 0xF0) copy[0] = 0xF0;
            data.CopyTo(copy.AsSpan(copy.Length - data.Length));
            Keep(new Opened(ticks, track, port, 0, status, copy), key);
        }

        private void Seen(long ticks)
        {
            _any = true;
            _lastTicks = Math.Max(_lastTicks, ticks);
        }

        /// <summary>Whether a message here would be among the earliest kept so far.</summary>
        private bool Wanted(long ticks, int track, out (long Ticks, int Track, int Seq) key)
        {
            key = (ticks, track, _seq++);
            if (_limit == 0) return false;
            if (_opening.Count < _limit) return true;
            _opening.TryPeek(out _, out var latest);
            return key.CompareTo(latest) < 0;
        }

        private readonly void Keep(Opened message, (long Ticks, int Track, int Seq) key)
        {
            if (_opening.Count == _limit) _opening.Dequeue();
            _opening.Enqueue(message, key);
        }

        public readonly SongSummary Finish(int division)
        {
            // Changes at the same tick take effect in track order, as they do once merged.
            var clock = new Clock(division);
            foreach ((long ticks, long tempo) in _tempos.OrderBy(t => t.Ticks))
                clock.Change(ticks, tempo);

            long duration = 0;
            if (_any)
            {
                double timeUs = clock.At(_lastTicks);
                if (timeUs > MaxDurationUs) throw new SmfFormatException("The song is too long.");
                duration = (long)timeUs;
            }

            // Latest first off the heap, so the opening is filled from the back.
            var kept = new Opened[_opening.Count];
            for (int i = kept.Length - 1; i >= 0; i--) kept[i] = _opening.Dequeue();

            var payload = new byte[kept.Sum(m => m.Data?.Length ?? 0)];
            var opening = new MidiEvent[kept.Length];
            int at = 0;
            for (int i = 0; i < kept.Length; i++)
            {
                Opened m = kept[i];
                if (m.Data is null)
                {
                    opening[i] = new MidiEvent(m.Ticks, m.Track, m.Port, m.Packed);
                    continue;
                }
                m.Data.CopyTo(payload, at);
                opening[i] = new MidiEvent(m.Ticks, m.Track, m.Port, MidiEventKind.SysEx,
                                           m.Status, at, m.Data.Length);
                at += m.Data.Length;
            }

            return new SongSummary(duration, _title, opening, payload);
        }
    }

    /// <summary>What the meta events say about the song, gathered as the tracks are read.</summary>
    /// <remarks>
    /// One object rather than a ref parameter each: they are all "the first one of these
    /// anywhere in the file", so they are gathered the same way and travel together.
    /// </remarks>
    private sealed class Metadata
    {
        public string Title = string.Empty;
        public string Copyright = string.Empty;
        public string Comment = string.Empty;
        public List<string> Texts { get; } = [];
    }

    /// <summary>Takes what a meta event says about the song, where it is the first to.</summary>
    private static void Gather(Metadata said, byte metaType, ReadOnlySpan<byte> data)
    {
        switch (metaType)
        {
            case MetaType.TrackName:
                TakeTitle(ref said.Title, data);
                break;
            case MetaType.Text or MetaType.Marker or MetaType.CuePoint
                                or MetaType.Copyright or MetaType.Instrument:
                string text = Cp932.Decode(data);
                if (text.Length == 0) break;

                said.Texts.Add(text);
                // Kept apart as well as in the heap: the scan wants every word in the
                // file, a display wants the one line the song leads with.
                if (metaType == MetaType.Copyright && said.Copyright.Length == 0)
                    said.Copyright = text;
                else if (metaType == MetaType.Text && said.Comment.Length == 0)
                    said.Comment = text;
                break;
        }
    }

    /// <summary>The song's title is the first track name that says anything.</summary>
    private static void TakeTitle(ref string title, ReadOnlySpan<byte> data)
    {
        if (title.Length > 0) return;

        // A name of nothing but spacing is not a name, and taking it would shadow the real
        // one a later track may carry.
        string name = Cp932.Decode(data);
        if (!Text.TitleText.IsBlank(name)) title = name;
    }

    /// <summary>Stable k-way merge: equal ticks keep track order, matching SMF semantics.</summary>
    /// <remarks>
    /// Through a heap, since the track count is whatever the file says. Track <c>n</c> is
    /// <c>events[starts[n]..starts[n + 1]]</c>; a lone track is handed back as it is.
    /// </remarks>
    private static MidiEvent[] Merge(MidiEvent[] events, int[] starts)
    {
        int tracks = starts.Length - 1;
        int filled = 0;
        for (int i = 0; i < tracks; i++)
            if (starts[i + 1] > starts[i]) filled++;
        if (filled <= 1) return events;

        var result = new MidiEvent[events.Length];
        int[] cursors = starts[..tracks];
        var next = new PriorityQueue<int, (long Ticks, int Track)>(filled);
        for (int i = 0; i < tracks; i++)
            if (cursors[i] < starts[i + 1]) next.Enqueue(i, (events[cursors[i]].Ticks, i));

        for (int written = 0; next.TryDequeue(out int best, out _);)
        {
            result[written++] = events[cursors[best]++];
            if (cursors[best] < starts[best + 1])
                next.Enqueue(best, (events[cursors[best]].Ticks, best));
        }

        return result;
    }

    /// <summary>Walks the merged events applying tempo changes, filling in absolute times.</summary>
    private static long ApplyTiming(MidiEvent[] events, int division, byte[] payload)
    {
        var clock = new Clock(division);

        for (int i = 0; i < events.Length; i++)
        {
            MidiEvent e = events[i];
            double timeUs = clock.At(e.Ticks);
            if (timeUs > MaxDurationUs) throw new SmfFormatException("The song is too long.");
            events[i] = e.WithTime((long)timeUs);

            if (e.Kind == MidiEventKind.Meta && e.MetaType == MetaType.Tempo
                && TempoOf(payload.AsSpan(e.DataOffset, e.DataLength)) is { } tempo)
                clock.Change(e.Ticks, tempo);
        }

        return events.Length == 0 ? 0 : events[^1].TimeUs;
    }

    /// <summary>Microseconds a quarter note, or null for a tempo event too short to say.</summary>
    private static long? TempoOf(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3) return null;
        long tempo = (long)data[0] << 16 | (long)data[1] << 8 | data[2];
        return tempo > 0 ? tempo : DefaultTempo;
    }

    /// <summary>Tells the time at a tick, counted from the last tempo change.</summary>
    private struct Clock
    {
        /// <summary>Microseconds a tick under an SMPTE division; else 0.</summary>
        private readonly double _usPerTickSmpte;
        private readonly int _ppqn;
        private long _tempo = DefaultTempo;
        private long _fromTicks;
        private double _fromUs;

        public Clock(int division)
        {
            _ppqn = division & 0x7FFF;
            if ((division & 0x8000) == 0) return;

            int fps = 256 - ((division >> 8) & 0xFF); // stored as a negative signed byte
            int ticksPerFrame = division & 0xFF;
            if (fps <= 0 || ticksPerFrame <= 0)
                throw new SmfFormatException("Invalid SMPTE division.");
            // 29 is 30 drop-frame, which runs at 30000/1001 frames a second.
            double frames = fps == 29 ? 30000.0 / 1001 : fps;
            _usPerTickSmpte = 1_000_000.0 / (frames * ticksPerFrame);
        }

        public readonly double At(long ticks)
            => _usPerTickSmpte > 0
                ? ticks * _usPerTickSmpte
                : _fromUs + (ticks - _fromTicks) * (double)_tempo / _ppqn;

        /// <summary>The tempo from <paramref name="ticks"/> on.</summary>
        public void Change(long ticks, long tempo)
        {
            if (_usPerTickSmpte > 0) return;
            _fromUs = At(ticks);
            _fromTicks = ticks;
            _tempo = tempo;
        }
    }

    /// <summary>Forward-only reader over the file bytes.</summary>
    private ref struct Cursor(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos = 0;

        public readonly int Remaining => _data.Length - _pos;

        public readonly int Position => _pos;

        public byte PeekByte() => _pos < _data.Length
            ? _data[_pos]
            : throw new SmfFormatException("Unexpected end of data.");

        public byte ReadByte()
        {
            if (_pos >= _data.Length) throw new SmfFormatException("Unexpected end of data.");
            return _data[_pos++];
        }

        public ushort ReadUInt16()
        {
            ReadOnlySpan<byte> s = ReadBytes(2);
            return BinaryPrimitives.ReadUInt16BigEndian(s);
        }

        public uint ReadUInt32()
        {
            ReadOnlySpan<byte> s = ReadBytes(4);
            return BinaryPrimitives.ReadUInt32BigEndian(s);
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || _pos + count > _data.Length)
                throw new SmfFormatException("Unexpected end of data.");
            ReadOnlySpan<byte> s = _data.Slice(_pos, count);
            _pos += count;
            return s;
        }

        public void Skip(int count)
        {
            if (count <= 0) return;
            _pos = Math.Min(_pos + count, _data.Length);
        }

        public int ReadVariableLength()
        {
            int value = 0;
            for (int i = 0; i < 4; i++)
            {
                byte b = ReadByte();
                value = (value << 7) | (b & 0x7F);
                if ((b & 0x80) == 0) return value;
            }
            throw new SmfFormatException("Variable-length quantity longer than 4 bytes.");
        }
    }
}
