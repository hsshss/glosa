using CommunityToolkit.Mvvm.ComponentModel;
using Glosa.App.Services;
using Glosa.Midi;

namespace Glosa.App.ViewModels;

/// <summary>
/// One output port and the device it goes to. Ports are lettered the way the DEF names
/// them: A is the first, and a song's <c>FF 21</c> meta events pick between them.
/// </summary>
public sealed partial class PortSlotViewModel : ViewModelBase
{
    private readonly Action _changed;

    public PortSlotViewModel(int index, IReadOnlyList<MidiDeviceInfo> devices, Action changed)
    {
        Index = index;
        Devices = devices;
        _changed = changed;
    }

    public int Index { get; }

    public string Label => ((char)('A' + Index)).ToString();

    /// <summary>The same list every slot chooses from, plus the "unused" entry.</summary>
    public IReadOnlyList<MidiDeviceInfo> Devices { get; }

    /// <summary>The device this port opens, or null when the port is not used.</summary>
    [ObservableProperty]
    public partial MidiDeviceInfo? Device { get; set; }

    /// <summary>
    /// The machine this port is set to, by name, whether or not it is here now; empty when
    /// the port is unused.
    /// </summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// What the box says when there is no device on it, naming the machine that is missing.
    /// </summary>
    public string Placeholder => Name.Length > 0 ? string.Format(Strings.DeviceMissing, Name) : Strings.PortUnused;

    /// <summary>Whether a module reset goes to this port when there is no DEF.</summary>
    /// <remarks>See <see cref="Services.PortMap.ResetPorts"/>.</remarks>
    [ObservableProperty]
    public partial bool Reset { get; set; }

    /// <summary>Sets <see cref="Reset"/> without it counting as an edit, for loading.</summary>
    public void RestoreReset(bool reset)
    {
        _loading = true;
        try
        {
            Reset = reset;
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnResetChanged(bool value)
    {
        if (!_loading) _resetChanged?.Invoke();
    }

    /// <summary>Told when <see cref="Reset"/> is changed by hand.</summary>
    private Action? _resetChanged;

    /// <summary>Hands over what to tell when the reset choice is changed by hand.</summary>
    public void OnResetEdited(Action changed) => _resetChanged = changed;

    /// <summary>
    /// A mark for whether this port's device is open: ● open, × would not open, nothing
    /// when closed.
    /// </summary>
    [ObservableProperty]
    public partial string StateMark { get; set; } = string.Empty;

    /// <summary>What the mark means, for its tooltip.</summary>
    [ObservableProperty]
    public partial string? StateText { get; set; }

    /// <summary>Takes in where this port's device stands.</summary>
    public void ShowState((OutputState State, string? Why) state)
    {
        (StateMark, StateText) = state.State switch
        {
            OutputState.Open => ("●", Strings.PortOpen),
            OutputState.Failed => ("×", string.Format(Strings.PortCannotOpen, state.Why)),
            _ => (string.Empty, (string?)null),
        };
    }

    /// <summary>Sets the device without reopening, for loading a stored assignment.</summary>
    /// <remarks>
    /// The name is given separately because it outlives the device: a stored assignment
    /// that resolved to nothing still knows what it was looking for.
    /// </remarks>
    public void Restore(MidiDeviceInfo? device, string name)
    {
        _loading = true;
        try
        {
            Name = name;
            Device = device;
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(Placeholder));
    }

    private bool _loading;

    partial void OnDeviceChanged(MidiDeviceInfo? value)
    {
        if (_loading) return;

        // Chosen by hand, so the name follows, including a choice of nothing.
        Name = value?.Name ?? string.Empty;
        OnPropertyChanged(nameof(Placeholder));
        _changed();
    }
}
