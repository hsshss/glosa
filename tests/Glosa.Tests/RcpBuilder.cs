using System.Buffers.Binary;
using Glosa.Core.Text;

namespace Glosa.Tests;

/// <summary>
/// Builds small Recomposer files in either layout: RCP (4-byte events) or G36 (6-byte).
/// </summary>
internal sealed class RcpBuilder(bool g36 = false)
{
    private readonly List<(int Ch, int Key, int StShift, int Mode, string Memo, RcpTrack Events)> _tracks = [];
    private readonly byte[][] _userExclusive = new byte[8][];

    public string Title { get; init; } = "";
    public string[] Memo { get; init; } = [];
    public int TimeBase { get; init; } = 48;
    public int Tempo { get; init; } = 120;
    public int PlayBias { get; init; }

    public RcpBuilder Track(int midiCh, Action<RcpTrack> build,
                            int keyShift = 0, int stShift = 0, int mode = 0, string memo = "")
    {
        var events = new RcpTrack();
        build(events);
        _tracks.Add((midiCh, keyShift, stShift, mode, memo, events));
        return this;
    }

    public RcpBuilder UserExclusive(int index, params byte[] bytes)
    {
        _userExclusive[index] = bytes;
        return this;
    }

    public byte[] Build() => g36 ? BuildG36() : BuildRcp();

    private byte[] BuildRcp()
    {
        var head = new byte[0x586];
        "RCM-PC98V2.0(C)COME ON MUSIC\r\n\0\0"u8.CopyTo(head);
        Text(Title, 64).CopyTo(head, 0x20);
        for (int i = 0; i < Math.Min(12, Memo.Length); i++) Text(Memo[i], 28).CopyTo(head, 0x60 + 28 * i);
        head[0x1C0] = (byte)TimeBase;
        head[0x1E7] = (byte)(TimeBase >> 8);
        head[0x1C1] = (byte)Tempo;
        head[0x1C2] = 4;
        head[0x1C3] = 4;
        head[0x1C5] = (byte)(sbyte)PlayBias;
        head[0x1E6] = 18;
        for (int i = 0; i < 8; i++)
        {
            Text("", 24).CopyTo(head, 0x406 + 48 * i);
            (_userExclusive[i] ?? [0xF7]).CopyTo(head, 0x406 + 48 * i + 24);
        }

        var file = new List<byte>(head);
        foreach (var t in _tracks)
        {
            var body = new List<byte>();
            foreach (RcpTrack.Item e in t.Events.Items)
            {
                if (e.SameMeasureTarget is { } target)
                {
                    int offset = 44 + 4 * target;
                    body.AddRange([0xFC, 0, (byte)(offset & 0xFC), (byte)(offset >> 8)]);
                }
                else
                {
                    body.AddRange([(byte)e.Cmd, (byte)e.St, (byte)e.Gt, (byte)e.Vel]);
                    // Two bytes to a continuation, where the gate and velocity would be.
                    foreach (byte[] part in (e.Data ?? []).Chunk(2))
                        body.AddRange([0xF7, 0, .. Padded(part, 2)]);
                }
            }

            var header = new byte[44];
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)(44 + body.Count));
            header[2] = (byte)(_tracks.IndexOf(t) + 1);
            header[4] = (byte)(sbyte)t.Ch;
            header[5] = (byte)t.Key;
            header[6] = (byte)(sbyte)t.StShift;
            header[7] = (byte)t.Mode;
            Text(t.Memo, 36).CopyTo(header, 8);
            file.AddRange(header);
            file.AddRange(body);
        }
        return [.. file];
    }

    private byte[] BuildG36()
    {
        var head = new byte[0xC98];
        "COME ON MUSIC RECOMPOSER RCP3.00"u8.CopyTo(head);
        Text(Title, 64).CopyTo(head, 0x20);
        for (int i = 0; i < Math.Min(12, Memo.Length); i++) Text(Memo[i], 30).CopyTo(head, 0xA0 + 30 * i);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(0x208), (ushort)_tracks.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(0x20A), (ushort)TimeBase);
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(0x20C), (ushort)Tempo);
        head[0x20E] = 4;
        head[0x20F] = 4;
        head[0x211] = (byte)(sbyte)PlayBias;
        for (int i = 0; i < 8; i++)
        {
            Text("", 23).CopyTo(head, 0xB18 + 48 * i);
            (_userExclusive[i] ?? [0xF7]).CopyTo(head, 0xB18 + 48 * i + 23);
        }

        var file = new List<byte>(head);
        foreach (var t in _tracks)
        {
            var body = new List<byte>();
            foreach (RcpTrack.Item e in t.Events.Items)
            {
                if (e.SameMeasureTarget is { } target)
                {
                    int n = target + 48;
                    body.AddRange([0xFC, 0, 0, 0, (byte)n, (byte)(n >> 8)]);
                }
                else
                {
                    body.AddRange([(byte)e.Cmd, (byte)e.Vel, (byte)e.St, (byte)(e.St >> 8),
                                   (byte)e.Gt, (byte)(e.Gt >> 8)]);
                    // Five bytes to a continuation: everything after the command.
                    foreach (byte[] part in (e.Data ?? []).Chunk(5))
                        body.AddRange([0xF7, .. Padded(part, 5)]);
                }
            }

            var header = new byte[46];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(46 + body.Count));
            header[4] = (byte)(_tracks.IndexOf(t) + 1);
            header[6] = (byte)(sbyte)t.Ch;
            header[7] = (byte)t.Key;
            header[8] = (byte)(sbyte)t.StShift;
            header[9] = (byte)t.Mode;
            Text(t.Memo, 36).CopyTo(header, 10);
            file.AddRange(header);
            file.AddRange(body);
        }
        return [.. file];
    }

    /// <summary>A continuation's data, filled out with the end mark the reader drops.</summary>
    private static byte[] Padded(byte[] part, int width)
        => [.. part, .. Enumerable.Repeat((byte)0xF7, width - part.Length)];

    /// <summary>Space-padded Shift-JIS, as Recomposer writes its text fields.</summary>
    private static byte[] Text(string text, int width)
    {
        byte[] bytes = Cp932.Encoding.GetBytes(text);
        var field = Enumerable.Repeat((byte)' ', width).ToArray();
        bytes.AsSpan(0, Math.Min(width, bytes.Length)).CopyTo(field);
        return field;
    }
}

/// <summary>One track's events, in Recomposer's terms.</summary>
internal sealed class RcpTrack
{
    internal readonly record struct Item(int Cmd, int St, int Gt, int Vel, int? SameMeasureTarget = null,
                                         byte[]? Data = null);

    public List<Item> Items { get; } = [];

    /// <summary>The index the next event will have, for pointing a repeated measure at.</summary>
    public int Next => Items.Count;

    public RcpTrack Note(int key, int st, int gt, int vel = 100) => Add(key, st, gt, vel);
    public RcpTrack Command(int cmd, int st, int gt, int vel) => Add(cmd, st, gt, vel);
    /// <summary>A track exclusive, its template carried by the continuations after it.</summary>
    public RcpTrack TrackExclusive(int st, int gt, int vel, params byte[] template)
    {
        Items.Add(new Item(0x98, st, gt, vel, Data: template));
        return this;
    }

    public RcpTrack LoopStart() => Add(0xF9, 0, 0, 0);
    public RcpTrack LoopEnd(int count) => Add(0xF8, count, 0, 0);
    public RcpTrack MeasureEnd() => Add(0xFD, 0, 0, 0);
    public RcpTrack End() => Add(0xFE, 0, 0, 0);

    public RcpTrack SameMeasure(int target)
    {
        Items.Add(new Item(0xFC, 0, 0, 0, target));
        return this;
    }

    private RcpTrack Add(int cmd, int st, int gt, int vel)
    {
        Items.Add(new Item(cmd, st, gt, vel));
        return this;
    }
}
