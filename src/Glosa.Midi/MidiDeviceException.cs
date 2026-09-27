namespace Glosa.Midi;

public sealed class MidiDeviceException : Exception
{
    public MidiDeviceException(string message) : base(message) { }
    public MidiDeviceException(string message, Exception inner) : base(message, inner) { }
}
