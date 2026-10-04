using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Glosa.App.Services;
using Glosa.Core.Playback;
using Glosa.Midi;

namespace Glosa.App.ViewModels;

/// <summary>
/// One named set of port assignments, as the window edits it.
/// </summary>
/// <remarks>
/// A thin cover over the stored <see cref="PortMap"/>: every change is written straight
/// through to it, so saving is a matter of handing the models over.
/// </remarks>
public sealed partial class PortMapViewModel : ViewModelBase
{
    /// <summary>Ports offered, which is every port there is.</summary>
    public const int PortCount = IEventSink.PortCount;

    private readonly Action<PortMapViewModel> _changed;
    private readonly IReadOnlyList<MidiDeviceInfo> _devices;

    /// <param name="devices">The devices listed now, which the ports' names are found among.</param>
    public PortMapViewModel(PortMap map, IReadOnlyList<MidiDeviceInfo> devices,
                            Action<PortMapViewModel> changed)
    {
        Model = map;
        Title = map.Title;
        UseModule = map.UseModule.Length > 0 ? map.UseModule : null;
        Modules = [.. map.Modules];
        Modules.CollectionChanged += (_, _) => Model.Modules = [.. Modules];
        _devices = devices;

        for (int i = 0; i < PortCount; i++)
        {
            var slot = new PortSlotViewModel(i, devices, OnPortsChanged);
            // Restore rather than set: loading an assignment is not a change to it, and the
            // player has nothing to reopen yet.
            if (map.Ports.TryGetValue(PortMap.PortKey(i), out string? name) && name.Length > 0)
                slot.Restore(null, name);
            slot.RestoreReset(map.ResetPorts.Contains(PortMap.PortKey(i)));
            slot.OnResetEdited(WriteResetPorts);
            Ports.Add(slot);
        }

        Resolve();
        _changed = changed;
    }

    public PortMap Model { get; }

    /// <summary>What the map is called, which is how it is picked in the menu.</summary>
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>
    /// The output module this map plays through — the machine its ports reach.
    /// </summary>
    /// <remarks>
    /// Nullable only because the combo box it is bound to can say "nothing chosen". Every
    /// map names one, so an empty answer is not stored (<see cref="OnUseModuleChanged(string?)"/>).
    /// </remarks>
    [ObservableProperty]
    public partial string? UseModule { get; set; }

    /// <summary>The target modules that hand the outputs to this map.</summary>
    public ObservableCollection<string> Modules { get; }

    /// <summary>This map's own outputs, A first. Ports are what FF 21 selects between.</summary>
    public ObservableCollection<PortSlotViewModel> Ports { get; } = [];

    /// <summary>True for the one map in force, which the list marks.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>The mark against the map in force, the same one the playlist uses.</summary>
    public string Marker => IsActive ? "▶" : string.Empty;

    /// <summary>Whether this map claims songs written for <paramref name="module"/>.</summary>
    public bool Claims(string module)
        => Modules.Any(m => m.Equals(module, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a port names an audio plugin, here now or not.</summary>
    public bool UsesAudioPlugin
        => Model.Ports.Values.Any(name => DeviceName.Parse(name).Kind == MidiDeviceKind.AudioPlugin);

    /// <summary>
    /// The device each port names, whether or not it is here now; null for an unused port.
    /// </summary>
    /// <remarks>Which of two devices of the same name: <see cref="DeviceName.ForPorts"/>.</remarks>
    public DeviceName? NameOf(int port)
        => DeviceName.ForPorts([.. Ports.Select(p => p.Name)], _devices)[port];

    /// <summary>
    /// Puts every port on the device its name leads to now, or on none when it is not here.
    /// </summary>
    /// <remarks>
    /// Also after a port is set by hand: only the name is stored, and
    /// <see cref="DeviceName.ForPorts"/> settles which of two devices of that name it gets.
    /// </remarks>
    public void Resolve()
    {
        DeviceName?[] names = DeviceName.ForPorts([.. Ports.Select(p => p.Name)], _devices);
        for (int i = 0; i < Ports.Count; i++)
            if (names[i] is { } name) Ports[i].Restore(name.Find(_devices), name.Key);
    }

    /// <summary>The devices to open, with the unused tail dropped.</summary>
    /// <remarks>
    /// Gaps in the middle are kept: the sink routes by index. A port whose device is not here
    /// is a gap.
    /// </remarks>
    public DeviceName?[] OpenList()
    {
        DeviceName?[] names = [.. Ports.Select((p, i) => p.Device is null ? null : NameOf(i))];

        // The tail is measured by what the ports are set to, not by what answered: a port
        // whose machine is away is still in use.
        int last = Ports.Select((p, i) => (p, i)).Where(x => x.p.Name.Length > 0)
                        .Select(x => x.i).DefaultIfEmpty(-1).Max();

        return names[..(last + 1)];
    }

    /// <summary>How the outputs read in a message: <c>A=…, B=…</c>.</summary>
    public string Describe()
        => string.Join(", ", Ports.Take(OpenList().Length)
                                  .Select((p, i) => $"{p.Label}={Named(i)}"));

    private string Named(int port)
        => NameOf(port) is not { } name ? Strings.None
         : Ports[port].Device is null ? string.Format(Strings.DeviceMissing, name)
         : name.ToString();

    /// <summary>
    /// Repoints ports from one stored key (<see cref="DeviceName.Key"/>) to another, for a
    /// renamed device; <see cref="Resolve"/> afterwards.
    /// </summary>
    public void RenameDevice(string from, string to)
    {
        foreach (PortSlotViewModel port in Ports)
            if (string.Equals(port.Name, from, StringComparison.OrdinalIgnoreCase)) port.Restore(null, to);
        WritePorts();
    }

    /// <summary>Puts a device on a port without telling anyone, for setting one up.</summary>
    public void Lay(int index, MidiDeviceInfo? device)
    {
        Ports[index].Restore(device, device is { } chosen ? DeviceName.KeyOf(chosen) : string.Empty);
        WritePorts();
        Resolve();
    }

    /// <summary>
    /// Chooses the output module again from what is stored, for after the list of modules
    /// has been rebuilt.
    /// </summary>
    /// <remarks>
    /// The combo box lets go of its choice while its list is emptied, and that comes back here
    /// as nothing chosen (<see cref="OnUseModuleChanged(string?)"/>), the way <see cref="Resolve"/>
    /// puts the ports back after the devices are listed again.
    /// </remarks>
    public void RestoreUseModule() => UseModule = Model.UseModule.Length > 0 ? Model.UseModule : null;

    partial void OnTitleChanged(string value) => Model.Title = value;

    /// <remarks>
    /// An empty answer is dropped rather than stored. A combo box clears its selection when
    /// its list stops containing the chosen item, as on loading a definition that does not
    /// list this module.
    /// </remarks>
    partial void OnUseModuleChanged(string? value)
    {
        if (value is { Length: > 0 }) Model.UseModule = value;
    }

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(Marker));

    private void OnPortsChanged()
    {
        WritePorts();
        Resolve();
        _changed(this);
    }

    /// <summary>Writes the assignment back, by name.</summary>
    private void WritePorts()
        => Model.Ports = Ports.Where(p => p.Name.Length > 0)
                              .ToDictionary(p => PortMap.PortKey(p.Index), p => p.Name);

    /// <summary>Writes back which ports a module reset goes to.</summary>
    private void WriteResetPorts()
        => Model.ResetPorts = [.. Ports.Where(p => p.Reset).Select(p => PortMap.PortKey(p.Index))];
}
