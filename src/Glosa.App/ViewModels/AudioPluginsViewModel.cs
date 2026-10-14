#if BRACK
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Brack;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Glosa.App.Services;
using Glosa.Midi;
using Glosa.Midi.Brack;

namespace Glosa.App.ViewModels;

/// <summary>
/// A few channels' levels, and whether any has gone past full scale since a meter was last
/// clicked.
/// </summary>
/// <param name="clearAll">Clears every meter's lamp, for a click on this one; without it, the click clears this one's.</param>
public sealed partial class Meter(Action? clearAll = null) : ObservableObject
{
    /// <summary>Each channel's level, the bottom 60 dB over 0 to 127.</summary>
    public ObservableCollection<int> Levels { get; } = [];

    /// <summary>Each channel's count of blocks past full scale as last read.</summary>
    private ulong[] _clips = [];

    /// <summary>Each channel's blocks past full scale since the meter was last cleared.</summary>
    private ulong[] _past = [];

    [ObservableProperty]
    public partial bool Clipped { get; set; }

    /// <summary>How often it went past full scale, for the tooltip; null while it has not.</summary>
    [ObservableProperty]
    public partial string? ClipTip { get; set; }

    /// <summary>Shows <paramref name="levels"/>; none (the audio stopped) leaves the bars at nothing.</summary>
    public void Show(IReadOnlyList<ChannelLevel> levels)
    {
        if (levels.Count == 0)
        {
            for (int i = 0; i < Levels.Count; i++)
                if (Levels[i] != 0) Levels[i] = 0;
            return;
        }

        while (Levels.Count > levels.Count) Levels.RemoveAt(Levels.Count - 1);
        for (int i = 0; i < levels.Count; i++)
        {
            int level = Level(levels[i].Peak);
            if (i == Levels.Count) Levels.Add(level);
            else if (Levels[i] != level) Levels[i] = level;
        }

        if (_clips.Length != levels.Count) Array.Resize(ref _clips, levels.Count);
        if (_past.Length != levels.Count) Array.Resize(ref _past, levels.Count);
        for (int i = 0; i < levels.Count; i++)
        {
            _past[i] += levels[i].Clips - _clips[i];
            _clips[i] = levels[i].Clips;
        }
        ulong most = _past.Max();
        Clipped = most > 0;
        ClipTip = most > 0 ? string.Format(Strings.AudioPluginsClipTip, most) : null;
    }

    [RelayCommand]
    private void Clear()
    {
        if (clearAll is not null) clearAll();
        else Reset();
    }

    /// <summary>Puts out its lamp; it lights again on the next block past full scale.</summary>
    public void Reset()
    {
        Array.Clear(_past);
        Clipped = false;
        ClipTip = null;
    }

    private static int Level(float peak)
        => peak <= 0 ? 0 : (int)Math.Round(Math.Clamp((20 * Math.Log10(peak) + 60) / 60, 0, 1) * 127);
}

/// <summary>What a row says of a plugin in the rack, an instrument or an effect.</summary>
internal static class PluginText
{
    public static bool Wrong(RackPlugin plugin) => plugin.State is RackPluginState.Failed or RackPluginState.Crashed;

    /// <summary><paramref name="ready"/> once it is loaded; otherwise that it is loading, failed or crashed.</summary>
    public static string Detail(RackPlugin plugin, string ready) => plugin.State switch
    {
        RackPluginState.Loading => Strings.AudioPluginLoading,
        RackPluginState.Failed => Strings.AudioPluginFailed,
        RackPluginState.Crashed => Strings.AudioPluginCrashed,
        _ => ready,
    };
}

/// <summary>An effect in a chain, as its row shows it.</summary>
public sealed record EffectRow(RackPlugin Plugin, AudioPluginsViewModel Owner)
{
    public string Name => Plugin.Name;

    public bool On => !Plugin.Bypass;

    /// <summary>Its format; or what is wrong.</summary>
    public string Detail => PluginText.Detail(Plugin, Plugin.Format);

    public bool Wrong => PluginText.Wrong(Plugin);

    public string? Tip => Wrong ? Plugin.Status : null;
}

/// <summary>An instrument in the rack, as its card shows it: what it is, how it is doing, and its effects.</summary>
public sealed partial class RackRow(RackPlugin plugin, Meter meter, AudioPluginsViewModel owner) : ObservableObject
{
    public RackPlugin Plugin { get; } = plugin;

    public AudioPluginsViewModel Owner { get; } = owner;

    public string Name => Plugin.Name;

    /// <summary>Its format, architecture and, when more than one, its MIDI port count; or what is wrong.</summary>
    public string Detail => PluginText.Detail(
        Plugin,
        Plugin.NotePorts > 1
            ? $"{Plugin.Format}  {Plugin.Architecture}  {string.Format(Strings.AudioPluginPorts, Plugin.NotePorts)}"
            : $"{Plugin.Format}  {Plugin.Architecture}");

    public bool Ready => Plugin.State == RackPluginState.Ready;

    public bool Wrong => PluginText.Wrong(Plugin);

    /// <summary>Brack's reason, for the tooltip.</summary>
    public string? Tip => Wrong ? Plugin.Status : null;

    public IReadOnlyList<EffectRow> Effects { get; } = [.. plugin.Effects.Select(effect => new EffectRow(effect, owner))];

    private double _volume = Decibels.FromGain(plugin.Gain);

    /// <summary>Its volume in dB, on its sound after its effects; applies at once.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (!SetProperty(ref _volume, value)) return;
            OnPropertyChanged(nameof(VolumeTip));
            Owner.SetGain(Plugin.Id, Decibels.ToGain(value));
        }
    }

    public string VolumeTip => string.Format(Strings.AudioPluginsVolumeTip, Decibels.Text(Volume));

    private bool _mute = plugin.Mute;

    /// <summary>Whether it is silenced, while it goes on taking MIDI and playing; applies at once.</summary>
    public bool Mute
    {
        get => _mute;
        set
        {
            if (SetProperty(ref _mute, value)) Owner.SetMute(Plugin.Id, value);
        }
    }

    /// <summary>Its main output, after its effects and its volume.</summary>
    public Meter Meter { get; } = meter;

    /// <summary>Whether MIDI came since the meters were last read.</summary>
    [ObservableProperty]
    public partial bool Receiving { get; set; }

    [ObservableProperty]
    public partial string? MidiTip { get; set; }

    /// <summary>The time it and its effects take to render, while the audio runs.</summary>
    [ObservableProperty]
    public partial string? Load { get; set; }
}

/// <summary>A plugin the scan found, as the list to add from shows it.</summary>
public sealed record FoundRow(PluginDescription Plugin)
{
    public string Name => Plugin.Name;

    public string Detail => $"{Plugin.Format.ToString().ToUpperInvariant()}  {Plugin.Architecture}  {Plugin.Vendor}";
}

/// <summary>
/// The audio plugins window: the instruments with their effects, the master's effects, and the
/// audio output, each with how it is doing. Changes apply at once.
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
        Output = new Meter(ClearClips);
        MasterInput = new Meter(ClearClips);
        MasterOutput = new Meter(ClearClips);
        _rack.Changed += OnRackChanged;
        ShowRack();

        _ = ShowSettingsAsync();
        _ = ScanAsync();

        _metersTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, (_, _) => ShowMeters());
        _metersTimer.Start();
    }

    /// <summary>Stops following the rack, when the window closes.</summary>
    public void Detach()
    {
        _rack.Changed -= OnRackChanged;
        _metersTimer.Stop();
    }

    public ObservableCollection<RackRow> Plugins { get; } = [];

    [ObservableProperty]
    public partial RackRow? SelectedPlugin { get; set; }

    public ObservableCollection<EffectRow> MasterEffects { get; } = [];

    /// <summary>Whether a rack change is under way; one at a time.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(EditorCommand), nameof(ReloadCommand),
                                nameof(BypassCommand))]
    [NotifyPropertyChangedFor(nameof(Idle))]
    public partial bool Busy { get; set; }

    public bool Idle => !Busy;

    /// <summary>What the rack is doing, or what went wrong.</summary>
    [ObservableProperty]
    public partial string? Status { get; set; }

    // ---- meters ----

    private readonly DispatcherTimer _metersTimer;

    /// <summary>Each instrument's meter, kept when the rack is shown again so its clip lamp stays.</summary>
    private readonly Dictionary<string, Meter> _meters = [];

    /// <summary>Each instrument's MIDI count when the meters were last read, and when it last went up.</summary>
    private readonly Dictionary<string, (ulong Count, long At)> _midi = [];

    /// <summary>How long the MIDI lamp stays lit after a message, so that a single one shows.</summary>
    private static readonly TimeSpan MidiLit = TimeSpan.FromMilliseconds(250);

    /// <summary>Every output channel, after the master gain.</summary>
    public Meter Output { get; }

    /// <summary>The mix, before the master's effects.</summary>
    public Meter MasterInput { get; }

    /// <summary>The mix, after the master's effects.</summary>
    public Meter MasterOutput { get; }

    /// <summary>Puts out every meter's clip lamp, for a click on any of them.</summary>
    private void ClearClips()
    {
        foreach (Meter meter in (Meter[])[Output, MasterInput, MasterOutput, .. _meters.Values]) meter.Reset();
    }

    /// <summary>Whether the audio is running.</summary>
    [ObservableProperty]
    public partial bool Running { get; set; }

    /// <summary>The device and the rates the audio runs at.</summary>
    [ObservableProperty]
    public partial string? Playing { get; set; }

    /// <summary>The time a period takes to render, everything in it.</summary>
    [ObservableProperty]
    public partial string? Load { get; set; }

    /// <summary>The time the master's effects take.</summary>
    [ObservableProperty]
    public partial string? MasterLoad { get; set; }

    /// <summary>How many periods took longer than they last; null while none have.</summary>
    [ObservableProperty]
    public partial string? Overruns { get; set; }

    private void ShowMeters()
    {
        RackMeters meters = _rack.Meters();
        Running = meters.Running;
        Playing = !meters.Running ? null
            : meters.PluginRate == meters.OutputRate || meters.PluginRate == 0
                ? string.Format(Strings.AudioPluginsPlaying, meters.Device, Rate(meters.OutputRate), meters.Channels)
                : string.Format(Strings.AudioPluginsPlayingConverted, meters.Device, Rate(meters.OutputRate), meters.Channels,
                                Rate(meters.PluginRate));
        Load = meters.Running ? LoadText(meters.Load) : null;
        MasterLoad = meters.Running && MasterEffects.Count > 0 ? LoadText(meters.MasterLoad) : null;
        Overruns = meters.Overruns > 0 ? string.Format(Strings.AudioPluginsOverruns, meters.Overruns) : null;
        Output.Show(meters.Output);
        MasterInput.Show(meters.MasterInput);
        MasterOutput.Show(meters.MasterOutput);

        long now = Environment.TickCount64;
        foreach (RackRow row in Plugins)
        {
            ChainMeters? chain = meters.Instruments.GetValueOrDefault(row.Plugin.Id);
            row.Meter.Show(chain?.Output ?? []);
            row.Load = chain is { Active: true } ? LoadText(chain.Load) : null;

            // Lit by the count going up, not by the first read once the audio runs.
            string id = row.Plugin.Id;
            if (chain is not null)
                _midi[id] = _midi.TryGetValue(id, out (ulong Count, long At) last)
                    ? (chain.MidiMessages, chain.MidiMessages != last.Count ? now : last.At)
                    : (chain.MidiMessages, now - (long)MidiLit.TotalMilliseconds);
            (ulong count, long at) = _midi.GetValueOrDefault(id);
            row.Receiving = chain is not null && now - at < MidiLit.TotalMilliseconds;
            row.MidiTip = string.Format(Strings.AudioPluginsMidiTip, count);
        }
    }

    private static string Rate(int hertz) => (hertz / 1000.0).ToString("0.###", CultureInfo.CurrentCulture);

    private static string LoadText(ProcessLoad load) => string.Format(Strings.AudioPluginsLoadValue, load.Average * 100, load.Peak * 100);

    // ---- settings ----

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

    // ---- the plugins to add ----

    /// <summary>What the scan found, instruments and effects, by name; replaced whole by a scan.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PluginDescription> Found { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    public partial bool Scanning { get; set; }

    /// <summary>What the scan is doing, or what went wrong.</summary>
    [ObservableProperty]
    public partial string? ScanStatus { get; set; }

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
            Found = [.. found.OrderBy(plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase)];
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

    /// <summary>The list to add an instrument from.</summary>
    public PluginPickerViewModel InstrumentPicker()
        => new(this, Strings.AudioPluginsAddInstrumentTitle, Strings.AudioPluginsFoundTip, plugin => plugin.Instrument);

    /// <summary>
    /// The list to add an effect from to an instrument's chain, or the master's (null): those built
    /// for the chain's CPU.
    /// </summary>
    public PluginPickerViewModel EffectPicker(RackRow? owner)
    {
        string architecture = owner?.Plugin.Architecture ?? BrackRack.MasterArchitecture;
        return new PluginPickerViewModel(
            this, string.Format(Strings.AudioPluginsAddEffectTitle, owner?.Name ?? Strings.AudioPluginsMaster),
            string.Format(Strings.AudioPluginsFoundEffectsTip, architecture),
            plugin => !plugin.Instrument && plugin.Architecture == architecture);
    }

    // ---- changes ----

    /// <summary>Loads an instrument, picked from the scan or by file (<paramref name="pluginId"/> null).</summary>
    public Task AddInstrument(string path, string? pluginId, string name)
        => Change(string.Format(Strings.AudioPluginsAdding, name), () => _rack.Add(path, pluginId));

    /// <summary>Loads an effect at the end of an instrument's chain, or the master's (null).</summary>
    public Task AddEffect(RackRow? owner, string path, string? pluginId, string name)
    {
        string? id = owner?.Plugin.Id;
        int at = owner?.Plugin.Effects.Count ?? MasterEffects.Count;
        return Change(string.Format(Strings.AudioPluginsAdding, name), () => _rack.AddEffect(id, at, path, pluginId));
    }

    /// <summary>Loads a plugin picked by file, which the scan may not list.</summary>
    public Task AddFile(string path) => AddInstrument(path, null, Path.GetFileName(path));

    private bool CanChange(RackPlugin? plugin) => plugin is not null && !Busy;

    [RelayCommand(CanExecute = nameof(CanChange))]
    private Task Remove(RackPlugin? plugin) => Change(null, () => _rack.Remove(plugin!.Id));

    /// <summary>Renames an instrument or an effect; the port maps follow an instrument.</summary>
    public async Task Rename(RackPlugin plugin, string name)
    {
        IReadOnlyList<(MidiDeviceInfo From, MidiDeviceInfo To)> renamed = [];
        await Change(null, () => renamed = _rack.Rename(plugin.Id, name));
        if (renamed.Count > 0) _renamed(renamed);
    }

    /// <summary>Moves an instrument into the gap at <paramref name="gap"/> for a drag; true if the order changed.</summary>
    /// <remarks>The row moves at once; the rack follows off the UI thread.</remarks>
    public bool Move(RackRow row, int gap)
    {
        int from = Plugins.IndexOf(row);
        if (Busy || from < 0) return false;

        int to = Gap(from, gap, Plugins.Count);
        if (to == from) return false;

        Plugins.Move(from, to);
        string id = row.Plugin.Id;
        _ = Change(null, () => _rack.Move(id, to));
        return true;
    }

    /// <summary>Moves an effect into the gap at <paramref name="gap"/> among its chain's; true if the order changed.</summary>
    /// <remarks>The rack is shown again once it has moved.</remarks>
    public bool MoveEffect(EffectRow row, IReadOnlyList<EffectRow> chain, int gap)
    {
        int from = chain.ToList().IndexOf(row);
        if (Busy || from < 0) return false;

        int to = Gap(from, gap, chain.Count);
        if (to == from) return false;

        string id = row.Plugin.Id;
        _ = Change(null, () => _rack.Move(id, to));
        return true;
    }

    /// <summary>Where a row dropped into a gap lands; the gap counts the row itself while it is still in the list.</summary>
    private static int Gap(int from, int gap, int count) => Math.Clamp(gap - (from < gap ? 1 : 0), 0, count - 1);

    private bool CanShowEditor(RackPlugin? plugin) => plugin is { State: RackPluginState.Ready, HasEditor: true } && !Busy;

    [RelayCommand(CanExecute = nameof(CanShowEditor))]
    private Task Editor(RackPlugin? plugin) => Change(null, () => _rack.ShowEditor(plugin!.Id));

    private bool CanReload(RackPlugin? plugin) => plugin?.State is RackPluginState.Failed or RackPluginState.Crashed && !Busy;

    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task Reload(RackPlugin? plugin)
        => Change(string.Format(Strings.AudioPluginsReloading, plugin!.Name), () => _rack.Reload(plugin.Id));

    /// <summary>An instrument's volume, which neither blocks nor waits for a change under way.</summary>
    internal void SetGain(string id, float gain) => Quick(() => _rack.SetGain(id, gain));

    internal void SetMute(string id, bool mute) => Quick(() => _rack.SetMute(id, mute));

    private void Quick(Action change)
    {
        try
        {
            change();
        }
        catch (BrackException ex)
        {
            Status = ex.Message;
        }
    }

    private bool CanBypass(RackPlugin? effect) => effect is { State: RackPluginState.Ready } && !Busy;

    /// <summary>Turns an effect off, letting the sound through, or on again.</summary>
    [RelayCommand(CanExecute = nameof(CanBypass))]
    private Task Bypass(RackPlugin? effect) => Change(null, () => _rack.SetBypass(effect!.Id, !effect.Bypass));

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
        foreach (RackPlugin plugin in _rack.Plugins)
        {
            if (!_meters.TryGetValue(plugin.Id, out Meter? meter)) _meters[plugin.Id] = meter = new Meter(ClearClips);
            Plugins.Add(new RackRow(plugin, meter, this));
        }
        SelectedPlugin = Plugins.FirstOrDefault(row => row.Plugin.Id == selected);

        // Those of instruments no longer in the rack go.
        string[] ids = [.. Plugins.Select(row => row.Plugin.Id)];
        foreach (string id in _meters.Keys.Except(ids).ToList()) _meters.Remove(id);
        foreach (string id in _midi.Keys.Except(ids).ToList()) _midi.Remove(id);

        MasterEffects.Clear();
        foreach (RackPlugin effect in _rack.MasterEffects) MasterEffects.Add(new EffectRow(effect, this));

        if (!Busy) Status = _changeFailed ?? _rack.Problem;
    }
}

/// <summary>
/// A list to add a plugin from: what the scan found that fits, narrowed by a few letters of its
/// name or vendor.
/// </summary>
public sealed partial class PluginPickerViewModel : ViewModelBase
{
    private readonly Func<PluginDescription, bool> _fits;

    public PluginPickerViewModel(AudioPluginsViewModel owner, string title, string tip, Func<PluginDescription, bool> fits)
    {
        Owner = owner;
        Title = title;
        Tip = tip;
        _fits = fits;
        Owner.PropertyChanged += OnOwnerChanged;
        Narrow();
    }

    /// <summary>Stops following the scan, when the window closes.</summary>
    public void Detach() => Owner.PropertyChanged -= OnOwnerChanged;

    private void OnOwnerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioPluginsViewModel.Found)) Narrow();
    }

    /// <summary>For the scan's state and to scan again.</summary>
    public AudioPluginsViewModel Owner { get; }

    public string Title { get; }

    /// <summary>What is listed.</summary>
    public string Tip { get; }

    public ObservableCollection<FoundRow> Shown { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PickCommand))]
    public partial FoundRow? Selected { get; set; }

    /// <summary>The plugin last chosen in the list, chosen again when the list is narrowed or scanned anew.</summary>
    private (string Path, string Id)? _chosen;

    private bool _narrowing;

    partial void OnSelectedChanged(FoundRow? value)
    {
        if (!_narrowing && value is not null) _chosen = (value.Plugin.Path, value.Plugin.Id);
    }

    [ObservableProperty]
    public partial string? Filter { get; set; }

    partial void OnFilterChanged(string? value) => Narrow();

    /// <summary>The plugin picked, once <see cref="PickCommand"/> has run.</summary>
    public PluginDescription? Picked { get; private set; }

    /// <summary>Raised when a plugin has been picked.</summary>
    public event Action? Done;

    private bool CanPick() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanPick))]
    private void Pick()
    {
        Picked = Selected!.Plugin;
        Done?.Invoke();
    }

    private void Narrow()
    {
        string[] words = (Filter ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _narrowing = true;
        try
        {
            Shown.Clear();
            foreach (PluginDescription plugin in Owner.Found.Where(_fits))
            {
                if (words.All(word => plugin.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase)
                                      || plugin.Vendor.Contains(word, StringComparison.CurrentCultureIgnoreCase)))
                    Shown.Add(new FoundRow(plugin));
            }
            // The first only while nothing was chosen: one chosen and scanned away is not stood in for.
            Selected = _chosen is { } chosen
                ? Shown.FirstOrDefault(row => (row.Plugin.Path, row.Plugin.Id) == chosen)
                : Shown.FirstOrDefault();
        }
        finally
        {
            _narrowing = false;
        }
    }
}
#endif
