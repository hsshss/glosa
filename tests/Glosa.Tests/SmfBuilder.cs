using System.Buffers.Binary;

namespace Glosa.Tests;

/// <summary>Builds small SMF byte streams so the reader can be tested without fixture files.</summary>
internal sealed class SmfBuilder(int division, int format = 1)
{
    private readonly List<TrackBuilder> _tracks = [];

    public SmfBuilder Track(TrackBuilder track)
    {
        _tracks.Add(track);
        return this;
    }

    public SmfBuilder Track(Action<TrackBuilder> build)
    {
        var t = new TrackBuilder();
        build(t);
        _tracks.Add(t);
        return this;
    }

    public byte[] Build()
    {
        var output = new List<byte>();
        output.AddRange("MThd"u8);
        output.AddRange(BigEndian(6));
        output.AddRange(BigEndian16((ushort)format));
        output.AddRange(BigEndian16((ushort)_tracks.Count));
        output.AddRange(BigEndian16((ushort)division));

        foreach (TrackBuilder track in _tracks)
        {
            byte[] body = track.ToArray();
            output.AddRange("MTrk"u8);
            output.AddRange(BigEndian((uint)body.Length));
            output.AddRange(body);
        }
        return [.. output];
    }

    private static byte[] BigEndian(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return b;
    }

    private static byte[] BigEndian16(ushort value)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        return b;
    }
}

internal sealed class TrackBuilder
{
    private readonly List<byte> _bytes = [];

    public TrackBuilder Short(int delta, byte status, byte data1, byte data2)
    {
        WriteVlq(delta);
        _bytes.Add(status);
        _bytes.Add(data1);
        _bytes.Add(data2);
        return this;
    }

    /// <summary>A message with one data byte: program change or channel pressure.</summary>
    public TrackBuilder Short(int delta, byte status, byte data1)
    {
        WriteVlq(delta);
        _bytes.Add(status);
        _bytes.Add(data1);
        return this;
    }

    /// <summary>Emits data bytes only, relying on the previous status byte.</summary>
    public TrackBuilder RunningStatus(int delta, byte data1, byte data2)
    {
        WriteVlq(delta);
        _bytes.Add(data1);
        _bytes.Add(data2);
        return this;
    }

    public TrackBuilder Meta(int delta, byte type, ReadOnlySpan<byte> data)
    {
        WriteVlq(delta);
        _bytes.Add(0xFF);
        _bytes.Add(type);
        WriteVlq(data.Length);
        foreach (byte b in data) _bytes.Add(b);
        return this;
    }

    /// <summary>Writes an F0 event; <paramref name="body"/> excludes the leading F0.</summary>
    public TrackBuilder SysEx(int delta, ReadOnlySpan<byte> body)
    {
        WriteVlq(delta);
        _bytes.Add(0xF0);
        WriteVlq(body.Length);
        foreach (byte b in body) _bytes.Add(b);
        return this;
    }

    /// <summary>
    /// Writes an F7 event, whose bytes go out as they are: the later part of a SysEx split
    /// across events, among other things.
    /// </summary>
    public TrackBuilder Escape(int delta, ReadOnlySpan<byte> data)
    {
        WriteVlq(delta);
        _bytes.Add(0xF7);
        WriteVlq(data.Length);
        foreach (byte b in data) _bytes.Add(b);
        return this;
    }

    public TrackBuilder End(int delta) => Meta(delta, Core.Smf.MetaType.EndOfTrack, []);

    public byte[] ToArray() => [.. _bytes];

    private void WriteVlq(int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        int count = 0;
        buffer[count++] = (byte)(value & 0x7F);
        value >>= 7;
        while (value > 0)
        {
            buffer[count++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }
        for (int i = count - 1; i >= 0; i--) _bytes.Add(buffer[i]);
    }
}
