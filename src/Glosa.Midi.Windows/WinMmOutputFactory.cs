using static Glosa.Midi.Windows.NativeMethods;

namespace Glosa.Midi.Windows;

/// <summary>Enumerates the output devices exposed by the Win32 multimedia API.</summary>
public sealed unsafe class WinMmOutputFactory : IMidiOutputFactory
{
    public string BackendName => "WinMM";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public IReadOnlyList<MidiDeviceInfo> Enumerate()
    {
        uint count = midiOutGetNumDevs();
        var list = new List<MidiDeviceInfo>((int)count);
        for (uint i = 0; i < count; i++)
        {
            MidiOutCaps caps;
            if (midiOutGetDevCaps(i, &caps, (uint)sizeof(MidiOutCaps)) != MMSYSERR_NOERROR)
                continue;
            list.Add(new MidiDeviceInfo(i.ToString(), NameOf(caps.szPname)));
        }
        return list;
    }

    /// <summary>Opens by device number, or by the name the driver reports.</summary>
    /// <remarks>
    /// The number is the device's own, which is its place in the list only until a device
    /// whose capabilities cannot be read is left out of it.
    /// </remarks>
    public IMidiOutput Create(string deviceId)
    {
        IReadOnlyList<MidiDeviceInfo> devices = Enumerate();
        for (int i = 0; i < devices.Count; i++)
        {
            if (devices[i].Id == deviceId
                || string.Equals(devices[i].Name, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return new WinMmOutput(devices[i], uint.Parse(devices[i].Id));
            }
        }
        throw new MidiDeviceException($"WinMM device '{deviceId}' is not present.");
    }
}
