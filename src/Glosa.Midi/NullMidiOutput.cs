namespace Glosa.Midi;

/// <summary>An output that discards everything. Used for dry runs and tests.</summary>
public sealed class NullMidiOutput : IMidiOutput
{
    public MidiDeviceInfo Device { get; } = new("null", "(none)");
    public bool IsOpen { get; private set; }

    public void Open() => IsOpen = true;
    public void SendShort(uint packedMessage) { }
    public void SendLong(ReadOnlySpan<byte> sysEx) { }
    public void Reset() { }
    public void Close(bool reset = true) => IsOpen = false;
    public void Dispose() => Close();
    public int DroppedLongMessages => 0;
    public string? CloseError => null;
}
