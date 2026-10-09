#if BRACK
using System.Collections.ObjectModel;
using Avalonia.Threading;
using Brack;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Glosa.App.Services;
using Glosa.Midi;
using Glosa.Midi.Brack;

namespace Glosa.App.ViewModels;

/// <summary>A plugin in the rack, as its row shows it.</summary>
public sealed record RackRow(RackPlugin Plugin)
{
    public string Name => Plugin.Name;

    /// <summary>
    /// Its format, architecture and, when more than one, its MIDI port count; or what is wrong.
    /// </summary>
    public string Detail => Plugin.State switch
    {
        RackPluginState.Loading => Strings.AudioPluginLoading,
        RackPluginState.Failed => Strings.AudioPluginFailed,
        RackPluginState.Crashed => Strings.AudioPluginCrashed,
        _ when Plugin.NotePorts > 1
            => $"{Plugin.Format}  {Plugin.Architecture}  {string.Format(Strings.AudioPluginPorts, Plugin.NotePorts)}",
        _ => $"{Plugin.Format}  {Plugin.Architecture}",
    };

    /// <summary>Brack's reason, for the tooltip.</summary>
    public string? Tip => Plugin.State is RackPluginState.Failed or RackPluginState.Crashed ? Plugin.Status : null;
}

/// <summary>An instrument the scan found, as its row shows it.</summary>
public sealed record FoundRow(PluginDescription Plugin)
{
    public string Name => Plugin.Name;

    public string Detail => $"{Plugin.Format.ToString().ToUpperInvariant()}  {Plugin.Architecture}  {Plugin.Vendor}";
}

/// <summary>
/// The audio plugins window: the rack, the instruments to add, and the audio output. Changes
/// apply at once.
/// </summary>
/// <remarks>
/// Blocking rack calls run off the UI thread, one at a time (<see cref="Busy"/>); the scan
/// runs beside them.
/// </remarks>
public sealed partial class AudioPluginsViewModel : ViewModelBase
{
    private readonly BrackRack _rack;
    private readonly Action<IReadOnlyList<(MidiDeviceInfo From, MidiDeviceInfo To)>> _renamed;

    /// <param name="renamed">Given the devices a rename changed, for the port maps to follow.</param>
    public AudioPluginsViewModel(BrackRack rack, Action<IReadOnlyList<(MidiDeviceInfo From, MidiDeviceInfo To)>> renamed)
    {
        _rack = rack;
        _renamed = renamed;
        _rack.Changed += OnRackChanged;
        ShowRack();

        _ = ShowSettingsAsync();
        _ = ScanAsync();

        _meters = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, (_, _) => ShowMeters());
        _meters.Start();
    }

    /// <summary>Stops following the rack, when the window closes.</summary>
    public void Detach()
    {
        _rack.Changed -= OnRackChanged;
        _meters.Stop();
    }

    public ObservableCollection<RackRow> Plugins { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(EditorCommand), nameof(ReloadCommand))]
    [NotifyPropertyChangedFor(nameof(CanRename))]
    public partial RackRow? SelectedPlugin { get; set; }

    public ObservableCollection<FoundRow> Found { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial FoundRow? SelectedFound { get; set; }

    /// <summary>Whether a rack change is under way; one at a time.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand), nameof(RemoveCommand), nameof(EditorCommand), nameof(ReloadCommand))]
    [NotifyPropertyChangedFor(nameof(CanRename))]
    public partial bool Busy { get; set; }

    /// <summary>What the rack is doing, or what went wrong.</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    public partial bool Scanning { get; set; }

    /// <summary>What the scan is doing, or what went wrong.</summary>
    [ObservableProperty]
    public partial string? ScanStatus { get; set; }


    private readonly DispatcherTimer _meters;

    /// <summary>How much of each audio period the plugins take, as a percentage; a dash while the audio is stopped.</summary>
    [ObservableProperty]
    public partial string Load { get; set; } = "–";

    /// <summary>Each output channel's level, the bottom 60 dB over 0 to 127.</summary>
    public ObservableCollection<int> Levels { get; } = [];

    private void ShowMeters()
    {
        RackMeters meters = _rack.Meters();
        Load = meters.Running ? $"{meters.Load * 100:0.0}%" : "–";

        int[] levels = [.. meters.Peaks.Select(peak => meters.Running ? Level(peak) : 0)];
        while (Levels.Count > levels.Length) Levels.RemoveAt(Levels.Count - 1);
        for (int i = 0; i < levels.Length; i++)
        {
            if (i == Levels.Count) Levels.Add(levels[i]);
            else if (Levels[i] != levels[i]) Levels[i] = levels[i];
        }
    }

    private static int Level(float peak)
        => peak <= 0 ? 0 : (int)Math.Round(Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1) * 127);

    /// <summary>Whether the rack's settings are shown, which waits for its session to load.</summary>
    [ObservableProperty]
    public partial bool SettingsShown { get; set; }

    /// <summary>The output devices, the system's default first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> AudioDevices { get; set; } = [];

    /// <summary>The device periods offered, in milliseconds.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Choice> BufferSizes { get; set; } = [];

    /// <summary>The rates the plugins may run at, the output's own first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Choice> SampleRates { get; set; } = [];

    private async Task ShowSettingsAsync()
    {
        await _rack.Loaded;

        string? device = _rack.AudioDevice;
        string[] devices = [Strings.AudioPluginsDefaultDevice, .. BrackRack.AudioDevices().Select(info => info.Name)];
        // A device that is gone stays chosen, so opening the window does not move the sound.
        if (device is { Length: > 0 } && !devices.Contains(device)) devices = [.. devices, device];
        AudioDevices = devices;
        _audioDevice = device is { Length: > 0 } ? device : devices[0];
        OnPropertyChanged(nameof(AudioDevice));

        int milliseconds = (int)Math.Round(_rack.Buffer.TotalMilliseconds);
        BufferSizes = Offered([10, 20, 40, 80], milliseconds, value => string.Format(Strings.AudioPluginsBufferMilliseconds, value));
        _bufferSize = BufferSizes.First(choice => (int)choice.Value == milliseconds);
        OnPropertyChanged(nameof(BufferSize));

        int rate = _rack.PluginSampleRate;
        SampleRates = Offered([0, 44100, 48000, 88200, 96000], rate,
                              value => value == 0 ? Strings.AudioPluginsSampleRateOutput : $"{value / 1000.0:0.#} kHz");
        _sampleRate = SampleRates.First(choice => (int)choice.Value == rate);
        OnPropertyChanged(nameof(SampleRate));

        _loadSerially = _rack.LoadPluginsSerially;
        OnPropertyChanged(nameof(LoadSerially));

        SettingsShown = true;
    }

    /// <summary>The values offered, and the rack's own where it is none of them, in order.</summary>
    private static Choice[] Offered(int[] values, int chosen, Func<int, string> label)
        => [.. values.Append(chosen).Distinct().Order().Select(value => new Choice(value, label(value)))];

    private string _audioDevice = string.Empty;

    public string AudioDevice
    {
        get => _audioDevice;
        set
        {
            if (value is null || value == _audioDevice) return;
            _audioDevice = value;
            OnPropertyChanged();
            string? device = value == AudioDevices[0] ? null : value;
            _ = Change(null, () => _rack.AudioDevice = device);
        }
    }

    private Choice? _bufferSize;

    public Choice? BufferSize
    {
        get => _bufferSize;
        set
        {
            if (value is null || value == _bufferSize) return;
            _bufferSize = value;
            OnPropertyChanged();
            var buffer = TimeSpan.FromMilliseconds((int)value.Value);
            _ = Change(null, () => _rack.Buffer = buffer);
        }
    }

    private bool _loadSerially;

    /// <summary>Whether the rack's plugins load one after another, from the next start.</summary>
    public bool LoadSerially
    {
        get => _loadSerially;
        set
        {
            if (value == _loadSerially) return;
            _loadSerially = value;
            OnPropertyChanged();
            _ = Change(null, () => _rack.LoadPluginsSerially = value);
        }
    }

    private Choice? _sampleRate;

    public Choice? SampleRate
    {
        get => _sampleRate;
        set
        {
            if (value is null || value == _sampleRate) return;
            _sampleRate = value;
            OnPropertyChanged();
            int rate = (int)value.Value;
            _ = Change(null, () => _rack.PluginSampleRate = rate);
        }
    }

    private bool CanAdd() => SelectedFound is not null && !Busy;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private Task Add()
    {
        PluginDescription plugin = SelectedFound!.Plugin;
        return Change(string.Format(Strings.AudioPluginsAdding, plugin.Name), () => _rack.Add(plugin.Path, plugin.Id));
    }

    /// <summary>Loads a plugin picked by file, which the scan may not list.</summary>
    public Task AddFile(string path)
        => Change(string.Format(Strings.AudioPluginsAdding, Path.GetFileName(path)), () => _rack.Add(path));

    private bool CanChangeSelected() => SelectedPlugin is not null && !Busy;

    [RelayCommand(CanExecute = nameof(CanChangeSelected))]
    private Task Remove()
    {
        string id = SelectedPlugin!.Plugin.Id;
        return Change(null, () => _rack.Remove(id));
    }

    /// <summary>Whether the selected plugin can be renamed.</summary>
    public bool CanRename => CanChangeSelected();

    /// <summary>Renames a plugin; the port maps follow.</summary>
    public async Task Rename(RackRow row, string name)
    {
        IReadOnlyList<(MidiDeviceInfo From, MidiDeviceInfo To)> renamed = [];
        await Change(null, () => renamed = _rack.Rename(row.Plugin.Id, name));
        if (renamed.Count > 0) _renamed(renamed);
    }

    /// <summary>Moves a plugin into the gap at <paramref name="gap"/> for a drag; true if the order changed.</summary>
    /// <remarks>The row moves at once; the rack follows off the UI thread.</remarks>
    public bool Move(RackRow row, int gap)
    {
        int from = Plugins.IndexOf(row);
        if (Busy || from < 0) return false;

        // The gap counts the row itself while it is still in the list.
        int to = Math.Clamp(gap - (from < gap ? 1 : 0), 0, Plugins.Count - 1);
        if (to == from) return false;

        Plugins.Move(from, to);
        string id = row.Plugin.Id;
        _ = Change(null, () => _rack.Move(id, to));
        return true;
    }

    private bool CanShowEditor() => SelectedPlugin?.Plugin is { State: RackPluginState.Ready, HasEditor: true } && !Busy;

    [RelayCommand(CanExecute = nameof(CanShowEditor))]
    private Task Editor()
    {
        string id = SelectedPlugin!.Plugin.Id;
        return Change(null, () => _rack.ShowEditor(id));
    }

    private bool CanReload() => SelectedPlugin?.Plugin.State is RackPluginState.Failed or RackPluginState.Crashed && !Busy;

    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task Reload()
    {
        RackPlugin plugin = SelectedPlugin!.Plugin;
        return Change(string.Format(Strings.AudioPluginsReloading, plugin.Name), () => _rack.Reload(plugin.Id));
    }

    private bool CanRescan() => !Scanning;

    /// <summary>Scans again; unchanged files are not reloaded.</summary>
    [RelayCommand(CanExecute = nameof(CanRescan))]
    private Task Rescan() => ScanAsync();

    private async Task ScanAsync()
    {
        Scanning = true;
        ScanStatus = Strings.AudioPluginsScanning;
        try
        {
            IReadOnlyList<PluginDescription> found = await Task.Run(() => BrackRack.Scan(Backends.PluginScanCachePath));
            Found.Clear();
            foreach (PluginDescription plugin in found.OrderBy(plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase))
                Found.Add(new FoundRow(plugin));
            ScanStatus = null;
        }
        catch (Exception ex) when (ex is BrackException or IOException or UnauthorizedAccessException)
        {
            ScanStatus = ex.Message;
        }
        finally
        {
            Scanning = false;
        }
    }

    /// <summary>Why the last change failed; null if it did not.</summary>
    /// <remarks>Kept apart from the rack's problem, which the refresh the change set off would show over it.</remarks>
    private string? _changeFailed;

    /// <summary>Makes one rack change off the UI thread, showing its status.</summary>
    private async Task Change(string? doing, Action change)
    {
        Busy = true;
        Status = doing;
        _changeFailed = null;
        try
        {
            await Task.Run(change);
        }
        catch (BrackException ex)
        {
            _changeFailed = ex.Message;
        }
        finally
        {
            Busy = false;
            Status = _changeFailed ?? _rack.Problem;
        }
    }

    private void OnRackChanged() => Dispatcher.UIThread.Post(ShowRack);

    /// <summary>Shows the rack, keeping the selection.</summary>
    private void ShowRack()
    {
        string? selected = SelectedPlugin?.Plugin.Id;
        Plugins.Clear();
        foreach (RackPlugin plugin in _rack.Plugins) Plugins.Add(new RackRow(plugin));
        SelectedPlugin = Plugins.FirstOrDefault(row => row.Plugin.Id == selected);
        if (!Busy) Status = _changeFailed ?? _rack.Problem;
    }
}
#endif
