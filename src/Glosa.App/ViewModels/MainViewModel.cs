using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Glosa.App.Controls;
using Glosa.App.Services;
using Glosa.Core.Archives;
using Glosa.Core.Definition;
using Glosa.Core.Emulation;
using Glosa.Core.Playback;
using Glosa.Core.Smf;
using Glosa.Midi;

namespace Glosa.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// How often the display clocks are advanced. Fast enough that a 300ms scroll step looks
    /// even, cheap enough to leave the playback thread alone.
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(33);

    private readonly PlayerService _player = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _sinceLastTick = Stopwatch.StartNew();

    public MainViewModel()
    {
        foreach (MidiDeviceInfo device in PlayerService.Devices()) Devices.Add(device);

        _player.Controller.Loading += OnSongLoading;
        _player.Controller.CurrentChanged += item =>
            Dispatcher.UIThread.Post(() => OnCurrentChanged(item));
        _player.Controller.Started += item =>
            Dispatcher.UIThread.Post(() =>
            {
                foreach (PlaylistTabViewModel tab in Playlists) tab.Played(item);
                PlayingRowMoved?.Invoke();
            });
        _player.Controller.LoadFailed += (item, error) =>
            Dispatcher.UIThread.Post(() => Note(string.Format(Strings.NoteCannotLoad, item.Path, error.Message)));
        // Invoke, not Post: the transport waits for this, so the outputs are let go before
        // it looks at the next request — a play pressed straight after a stop opens them
        // afresh instead of having them closed under it.
        _player.Controller.Stopped += stop =>
            Dispatcher.UIThread.Invoke(() => OnTransportStopped(stop));
        _player.OutputSilenced += name =>
            Dispatcher.UIThread.Post(() => Note(string.Format(Strings.NoteOutputSilenced, name)));

        _timer = new DispatcherTimer { Interval = Tick };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        LoadModuleDefinition();
        AppSettings settings = LoadSettings(out bool read);
        Restore(settings);
        BuildMenus();

        // What the file already says is not written again; the first change is.
        if (read) _savedSettings = SettingsYaml();
        _saveTimer = new DispatcherTimer { Interval = SaveInterval };
        _saveTimer.Tick += (_, _) => SaveChanges(closing: false);
        _saveTimer.Start();
    }

    /// <summary>
    /// How long a change waits before it is written. Short enough that a crash loses little,
    /// long enough that a burst of changes is written once.
    /// </summary>
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(2);

    private readonly DispatcherTimer _saveTimer;

    /// <summary>
    /// The settings as last written or read, to tell whether they have changed since; null
    /// when the file has been neither.
    /// </summary>
    private string? _savedSettings;

    /// <summary>Why the settings could not be written the last time, so it is told once.</summary>
    private string? _settingsSaveProblem;

    /// <summary>
    /// The settings the last run left, or the defaults when they cannot be read.
    /// </summary>
    /// <remarks>
    /// A file that cannot be read is copied aside before the settings are written over it,
    /// and the listener is told where. Should even the copy fail, the file is left as it is.
    /// </remarks>
    /// <param name="read">Whether the settings came from the file.</param>
    private AppSettings LoadSettings(out bool read)
    {
        AppSettings settings = AppSettings.Load(out string? problem);
        read = problem is null && File.Exists(AppSettings.SettingsPath);
        if (problem is null) return settings;

        string path = AppSettings.SettingsPath;
        string aside = Path.Combine(AppSettings.ConfigDirectory,
                                    $"settings.{DateTime.Now:yyyyMMdd-HHmmss}.yaml");
        try
        {
            File.Copy(path, aside);
            HoldError(Strings.CannotReadSettingsTitle,
                      string.Format(Strings.CannotReadSettings, path, problem, aside));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _keepSettingsFile = true;
            HoldError(Strings.CannotReadSettingsTitle,
                      string.Format(Strings.CannotReadSettingsKept, path, problem));
        }
        return settings;
    }

    /// <summary>Whether the settings file is to be left as it is (<see cref="LoadSettings"/>).</summary>
    private bool _keepSettingsFile;

    /// <summary>Everything the last run left behind, before the command line has its say.</summary>
    private void Restore(AppSettings saved)
    {
        _settings = saved;

        LoopRepeatCount = saved.LoopRepeatCount;
        Priority = saved.Priority;
        TransferRate = saved.TransferRate;
        UseDefKeywords = saved.UseDefKeywords;
        DetectionSources.Clear();
        foreach (DetectionSourceSetting s in DetectionSourceSetting.Tidy(saved.DetectionSources))
            DetectionSources.Add(new DetectionSourceViewModel(s.Source, s.Enabled, DetectionChanged));
        // The song's data is asked only once the words have named nothing, so it goes last.
        DetectionSources.Add(new DetectionSourceViewModel(null, saved.DetectFromData, DetectionChanged));
        DetectionPosition = saved.DetectionPosition;
        ThruPlaysAs = saved.ThruPlaysAs;
        GsPlaysAs = saved.GsPlaysAs;
        XgPlaysAs = saved.XgPlaysAs;
        DetectionChanged();
        UseMidiOutReset = saved.UseMidiOutReset;
        SendAllNotesOffOnStop = saved.SendAllNotesOffOnStop;
        CapitalToneFallback = saved.CapitalToneFallback;
        SendModuleReset = saved.SendModuleReset;
        SwitchToneMap = saved.SwitchToneMap;
        UseMediaKeys = saved.UseMediaKeys;
        Language = Languages.Find(saved.Language);
        Order = saved.Order;
        Repeat = saved.Repeat;
        LcdEnlarged = saved.LcdEnlarged;
        HardwareRendering = saved.HardwareRendering;

        // Scanning is turned on last: it starts a pass, and there is nothing to scan yet.
        ScanLength = saved.ScanLength;
        TrimTitles = saved.TrimTitles;
        SongLabel = saved.SongLabel;
        AutoDropPlay = saved.AutoDropPlay;

        // The default map always exists and claims nothing, whatever a settings file edited
        // by hand may say.
        bool firstRun = saved.PortMaps.Count == 0;
        if (firstRun) saved.PortMaps.Add(new PortMap());
        saved.PortMaps[0].Modules.Clear();
        foreach (PortMap map in saved.PortMaps) Adopt(map);
        // Only the map chosen by hand; the map in use is settled as each song loads.
        PinnedMap = saved.PinnedPortMap is { } pinned
            ? PortMaps[Math.Clamp(pinned, 0, PortMaps.Count - 1)]
            : null;

        // A first run guesses the first device for port A. Only a first run: a saved map with
        // no ports is one whose ports were cleared, and stays so.
        if (firstRun && Devices.Count > 0)
            PortMaps[0].Lay(0, Devices[0]);
        // The map the run starts from has the say on the output module, so that a definition
        // that does not list it is corrected on that map rather than on none.
        if (saved.DefinitionPath.Length > 0 && File.Exists(saved.DefinitionPath))
            LoadDefinition(saved.DefinitionPath, StartingMap.UseModule);

        RestorePlaylists(saved);
    }

    /// <summary>
    /// Writes the settings and the open playlists that have changed since they were last
    /// written or read.
    /// </summary>
    /// <remarks>
    /// Every <see cref="SaveInterval"/>, and once more as the player closes. A file that
    /// cannot be written is tried again next time, and said so in the debug window the first
    /// time — while closing there is nobody to tell.
    ///
    /// A playlist nothing has changed is never written, so what was done to its file from
    /// outside survives. One that has changed is written over whatever the file now holds:
    /// the player is not expecting its files to be edited while it runs.
    /// </remarks>
    private void SaveChanges(bool closing)
    {
        foreach (PlaylistTabViewModel tab in Playlists)
        {
            if (!tab.HasChanges) continue;
            string? problem = TrySave(tab);
            if (problem is not null && problem != tab.SaveProblem && !closing)
                Note(string.Format(Strings.NoteCannotSavePlaylist, tab.FilePath, problem));
            tab.SaveProblem = problem;
        }

        if (_keepSettingsFile) return;
        string yaml = SettingsYaml();
        if (yaml == _savedSettings) return;
        try
        {
            YamlFile.Write(yaml, AppSettings.SettingsPath);
            _savedSettings = yaml;
            _settingsSaveProblem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (ex.Message != _settingsSaveProblem && !closing)
                Note(string.Format(Strings.NoteCannotSaveSettings, AppSettings.SettingsPath, ex.Message));
            _settingsSaveProblem = ex.Message;
        }
    }

    /// <summary>The settings as they are now, as the file would have them.</summary>
    private string SettingsYaml()
    {
        // Each map writes its changes through as they are made; only their order is left.
        _settings.PortMaps = [.. PortMaps.Select(map => map.Model)];
        _settings.PinnedPortMap = PinnedMap is null ? null : PortMaps.IndexOf(PinnedMap);
        _settings.DefinitionPath = DefinitionPath ?? string.Empty;
        _settings.UseDefKeywords = UseDefKeywords;
        _settings.DetectionSources = [.. DetectionSources.Where(s => s.Source is not null)
                                                      .Select(s => new DetectionSourceSetting
        {
            Source = s.Source!.Value,
            Enabled = s.Enabled,
        })];
        _settings.DetectionPosition = DetectionPosition;
        _settings.ThruPlaysAs = ThruPlaysAs;
        _settings.GsPlaysAs = GsPlaysAs;
        _settings.XgPlaysAs = XgPlaysAs;
        _settings.LoopRepeatCount = LoopRepeatCount;
        _settings.Priority = Priority;
        _settings.TransferRate = TransferRate;
        _settings.UseMidiOutReset = UseMidiOutReset;
        _settings.SendAllNotesOffOnStop = SendAllNotesOffOnStop;
        _settings.CapitalToneFallback = CapitalToneFallback;
        _settings.SendModuleReset = SendModuleReset;
        _settings.SwitchToneMap = SwitchToneMap;
        _settings.DetectFromData = DetectionSources.Any(s => s.Source is null && s.Enabled);
        _settings.UseMediaKeys = UseMediaKeys;
        _settings.Language = Language.Code;
        _settings.ScanLength = ScanLength;
        _settings.TrimTitles = TrimTitles;
        _settings.SongLabel = SongLabel;
        _settings.AutoDropPlay = AutoDropPlay;
        _settings.Order = Order;
        _settings.Repeat = Repeat;
        _settings.LcdEnlarged = LcdEnlarged;
        _settings.HardwareRendering = HardwareRendering;

        OpenPlaylist[] tabs = [.. Playlists.Select(t => new OpenPlaylist
        {
            Path = t.FilePath,
            LastPlayed = t.LastPlayed,
            TopRow = t.TopRow,
        })];
        List<OpenPlaylist> open = [.. tabs];
        foreach ((int at, OpenPlaylist unread) in _unreadPlaylists)
            if (!tabs.Any(tab => string.Equals(tab.Path, unread.Path, StringComparison.OrdinalIgnoreCase)))
                open.Insert(Math.Min(at, open.Count), unread);
        _settings.OpenPlaylists = open;
        _settings.ActivePlaylist = SelectedPlaylist is null ? 0 : open.IndexOf(tabs[Playlists.IndexOf(SelectedPlaylist)]);

        return _settings.ToYaml();
    }

    /// <summary>
    /// Reopens the tabs the last run had, and makes sure there is always at least one.
    /// </summary>
    /// <remarks>
    /// A list whose file has gone is dropped rather than recreated empty. One whose file is
    /// there but cannot be read is kept for next time (<see cref="_unreadPlaylists"/>).
    /// </remarks>
    private void RestorePlaylists(AppSettings saved)
    {
        for (int at = 0; at < saved.OpenPlaylists.Count; at++)
        {
            OpenPlaylist open = saved.OpenPlaylists[at];
            string path = open.Path;
            if (!File.Exists(path)) continue;
            try
            {
                Playlist list = PlaylistFile.Load(path);
                if (list.Name.Length == 0) list.Name = Path.GetFileNameWithoutExtension(path);
                AddTab(new PlaylistTabViewModel(list, path, SongText, fromFile: true)
                {
                    LastPlayed = open.LastPlayed,
                    TopRow = open.TopRow,
                }, select: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or InvalidDataException or YamlDotNet.Core.YamlException)
            {
                Note(string.Format(Strings.NoteCannotLoadPlaylist, path, ex.Message));
                _unreadPlaylists.Add((at, open));
            }
        }

        if (Playlists.Count == 0)
            AddTab(new PlaylistTabViewModel(new Playlist { Name = NewListName }, UnusedFilePath(),
                                            SongText), select: false);

        // Each list opens on the row it was last playing. Before the tab is chosen, so
        // that the list box's own scroll-to-selection happens while nobody is looking.
        foreach (PlaylistTabViewModel tab in Playlists) tab.SelectLastPlayed();

        SelectedPlaylist = Playlists[Math.Clamp(saved.ActivePlaylist, 0, Playlists.Count - 1)];
        StartScan();
    }

    /// <summary>
    /// Lists the last run had open that are there but could not be read this time, and where
    /// they were among the tabs.
    /// </summary>
    /// <remarks>
    /// Kept in the settings as they were, so a list that could not be read for a moment — a
    /// drive not yet mounted, a file being written — opens again next time.
    /// </remarks>
    private readonly List<(int At, OpenPlaylist Entry)> _unreadPlaylists = [];

    private void AddTab(PlaylistTabViewModel tab, bool select, int? at = null)
    {
        if (at is { } index) Playlists.Insert(index, tab);
        else Playlists.Add(tab);

        if (select) SelectedPlaylist = tab;
        // The rows arrive with no module column until detection has looked at them.
        StartDetect();
    }

    /// <summary>
    /// Writes a tab's list to its file, or to <paramref name="path"/> for a save as. Answers
    /// why it could not, or null.
    /// </summary>
    private static string? TrySave(PlaylistTabViewModel tab, string? path = null)
    {
        try
        {
            tab.Save(path);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>What an untitled list is called before a number is needed.</summary>
    private static string NewListName => Strings.NewPlaylistName;

    /// <summary>
    /// <paramref name="wanted"/>, or the first <c>(n)</c> after it that no open tab has.
    /// </summary>
    /// <remarks>
    /// Only for the names the player invents; names given by hand need not be unique.
    ///
    /// A number already on the end is replaced rather than added to, so a copy of
    /// <c>Mix (1)</c> is <c>Mix (2)</c> and not <c>Mix (1) (1)</c>.
    /// </remarks>
    private string UnusedName(string wanted)
    {
        string stem = WithoutNumber(wanted);
        if (!Playlists.Any(t => t.Name == stem)) return stem;

        for (int n = 1; ; n++)
        {
            string candidate = $"{stem} ({n})";
            if (!Playlists.Any(t => t.Name == candidate)) return candidate;
        }
    }

    /// <summary>The name without a trailing <c>(n)</c>, if it has one.</summary>
    private static string WithoutNumber(string name)
    {
        int open = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (open < 0 || !name.EndsWith(')')) return name;

        string inside = name[(open + 2)..^1];
        return inside.Length > 0 && inside.All(char.IsAsciiDigit) ? name[..open] : name;
    }

    /// <summary>
    /// A file name nothing else claims. Names are metadata inside the file, so renaming a
    /// playlist never has to move it.
    /// </summary>
    private string UnusedFilePath()
    {
        for (int n = 1; ; n++)
        {
            string candidate = Path.Combine(AppSettings.PlaylistDirectory, $"list-{n}.yaml");
            if (!File.Exists(candidate) &&
                !Playlists.Any(t => string.Equals(t.FilePath, candidate,
                                                  StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    /// <summary>Hands a tab's list to the player and marks it as the one being walked.</summary>
    private void StartPlaying(PlaylistTabViewModel tab)
    {
        foreach (PlaylistTabViewModel other in Playlists) other.IsPlaying = other == tab;
        if (!ReferenceEquals(_player.Controller.Playlist, tab.List))
            _player.Controller.SetPlaylist(tab.List);
    }

    public AppSettings Settings => _settings;

    private AppSettings _settings = new();

    /// <summary>The open playlists, one per tab.</summary>
    public ObservableCollection<PlaylistTabViewModel> Playlists { get; } = [];

    /// <summary>
    /// A song has started and the lists have moved their playing marks
    /// (<see cref="PlaylistTabViewModel.RevealsLastPlayed"/>). On the UI thread.
    /// </summary>
    public event Action? PlayingRowMoved;

    /// <summary>The performance monitor's rows, one per part on every port a song reaches.</summary>
    public ObservableCollection<PartRowViewModel> Parts { get; } = [];

    /// <summary>
    /// How many parts there are, which is also how many meters the panel draws.
    /// </summary>
    /// <remarks>
    /// Told rather than counted from <see cref="Parts"/>: that collection exists for the
    /// monitor, and the panel should not stop metering because nobody has the monitor open.
    /// </remarks>
    [ObservableProperty]
    public partial int PartCount { get; set; } = 16;

    /// <summary>What the debug window shows.</summary>
    public ObservableCollection<string> Messages { get; } = [];

    /// <summary>The MIDI outputs there are, as last asked (<see cref="RefreshDevices"/>).</summary>
    public ObservableCollection<MidiDeviceInfo> Devices { get; } = [];

    public IReadOnlyList<RepeatMode> RepeatModes { get; } = Enum.GetValues<RepeatMode>();

    public IReadOnlyList<PlayOrder> PlayOrders { get; } = Enum.GetValues<PlayOrder>();

    public IReadOnlyList<Choice> Priorities { get; } = Choices(Enum.GetValues<PlaybackPriority>());

    public IReadOnlyList<Choice> MatchPositions { get; } = Choices(Enum.GetValues<MatchPosition>());

    public IReadOnlyList<LanguageChoice> LanguageChoices => Languages.All;

    /// <summary>Unlimited, or the rate a MIDI cable actually carries.</summary>
    public IReadOnlyList<Choice> TransferRates { get; } = Choices([0, 3125]);

    private static Choice[] Choices<T>(IEnumerable<T> values) where T : notnull
        => [.. values.Select(v => new Choice(v, EnumLabelConverter.Text(v)))];

    public ObservableCollection<string> UseModules { get; } = [];

    /// <summary>
    /// What a song may be set to, as <c>define.yaml</c> lists them.
    /// </summary>
    /// <remarks>
    /// The playlist's right-click menu is built from this, and so is the port map window's
    /// list of modules a map may claim.
    /// </remarks>
    public ObservableCollection<string> TargetModules { get; } = [];

    /// <summary>
    /// What a song detected as THRU, GS or XG may be played as: empty for as detected, then
    /// the target modules.
    /// </summary>
    public ObservableCollection<Choice> DetectedDefaultChoices { get; } = [];

    /// <summary>What the menu calls a song that has no module of its own.</summary>
    /// <remarks>
    /// Auto-detect is not a setting but the absence of one, so it is only ever a label.
    /// </remarks>
    public static string AutoDetect => Strings.AutoDetect;

    /// <summary>Where detection lands when nothing says which module a song is for.</summary>
    public const string Thru = "THRU";

    public PanelState Panel => _player.Panel;

    /// <summary>
    /// The named port assignments, the default one first.
    /// </summary>
    /// <remarks>
    /// Each holds the devices its ports open, the output module and the target modules it
    /// claims. Never empty — see <see cref="DefaultMap"/>.
    /// </remarks>
    public ObservableCollection<PortMapViewModel> PortMaps { get; } = [];

    /// <summary>
    /// The map the player falls back to, which is the first one. Never removed, and claims
    /// no modules.
    /// </summary>
    public PortMapViewModel DefaultMap => PortMaps[0];

    /// <summary>
    /// The map the song on the transport plays through, or null while nothing is playing.
    /// </summary>
    /// <remarks>
    /// Settled as each song loads (<see cref="MapFor"/>) and let go when playback comes to
    /// rest.
    /// </remarks>
    [ObservableProperty]
    public partial PortMapViewModel? ActiveMap { get; set; }

    /// <summary>
    /// The map chosen by hand from the Settings > Port Map menu, or null for Auto. Takes
    /// effect with the next song.
    /// </summary>
    [ObservableProperty]
    public partial PortMapViewModel? PinnedMap { get; set; }

    /// <summary>The map this run starts from: the one chosen by hand, else the default.</summary>
    /// <remarks>
    /// Where <c>--device</c> and <c>--use</c> land, and where the port map window opens while
    /// nothing is playing.
    /// </remarks>
    private PortMapViewModel StartingMap => PinnedMap ?? DefaultMap;

    /// <summary>
    /// The map the port map window is showing, which is not necessarily the one in use.
    /// </summary>
    [ObservableProperty]
    public partial PortMapViewModel? EditedMap { get; set; }

    /// <summary>Whether the map being edited is the default one, which is held to different rules.</summary>
    public bool EditedIsDefault => PortMaps.Count > 0 && ReferenceEquals(EditedMap, PortMaps[0]);

    /// <summary>
    /// Whether the map follows the song's target module: nothing is chosen by hand. The Auto
    /// line of the port map menu.
    /// </summary>
    public bool AutoPortMap => PinnedMap is null;

    partial void OnPinnedMapChanged(PortMapViewModel? value)
    {
        OnPropertyChanged(nameof(AutoPortMap));
        MarkMenus();
        ShowEmulation();
        if (_player.Controller.State != TransportState.Stopped)
            Note(string.Format(Strings.NotePortMapNextSong, value?.Title ?? Strings.PortMapAuto));
    }

    /// <summary>
    /// The port map menu: Auto, a <see cref="Separator"/>, then the maps. Objects rather than
    /// choices because of the separator.
    /// </summary>
    /// <remarks>
    /// The menus are rebuilt only when a list behind them changes, and otherwise just
    /// re-ticked, so choosing a line does not pull the collection out from under the menu
    /// that is closing over it.
    /// </remarks>
    public ObservableCollection<object> PortMapChoices { get; } = [];

    public ObservableCollection<MenuChoice> OrderChoices { get; } = [];

    public ObservableCollection<MenuChoice> RepeatChoices { get; } = [];

    /// <summary>The tab on screen, which is not necessarily the one playing.</summary>
    [ObservableProperty]
    public partial PlaylistTabViewModel? SelectedPlaylist { get; set; }

    [ObservableProperty]
    public partial int PanelRevision { get; set; }

    /// <summary>
    /// Whether the display gets room of its own. Off means it takes the height of the
    /// transport beside it, which is how it starts; the display's own context menu turns
    /// it on.
    /// </summary>
    [ObservableProperty]
    public partial bool LcdEnlarged { get; set; }

    [ObservableProperty]
    public partial string NowPlaying { get; set; } = Strings.Stopped;

    /// <summary>What the melodic parts are spelling between them.</summary>
    [ObservableProperty]
    public partial string Chord { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SoundingNotes { get; set; }

    [ObservableProperty]
    public partial string PositionText { get; set; } = "00:00 / 00:00";

    /// <summary>
    /// What the song says about itself, under the title.
    /// </summary>
    /// <remarks>
    /// The copyright notice, or the first text event when there is none, shown unlabelled:
    /// SMF has no field for a composer.
    ///
    /// Never empty. A song that says nothing gets <see cref="NothingSaid"/>, so the line
    /// does not read as a gap where something failed.
    /// </remarks>
    [ObservableProperty]
    public partial string SongInfo { get; set; } = NothingSaid;

    /// <summary>What stands in the song's place when it says nothing about itself.</summary>
    public const string NothingSaid = "-";

    /// <summary>Where the seek bar's thumb is, in milliseconds.</summary>
    /// <remarks>
    /// Two-way with the bar and the only source of the clock reading, so the time shown
    /// follows the thumb while it is dragged. The display tick writes it otherwise.
    /// </remarks>
    [ObservableProperty]
    public partial double PositionMs { get; set; }

    /// <summary>How long the song is, which is the span the seek bar covers.</summary>
    [ObservableProperty]
    public partial double DurationMs { get; set; }

    /// <summary>The seek bar's own maximum, which is never zero.</summary>
    /// <remarks>
    /// A slider whose minimum and maximum are equal draws itself full. A millisecond of span
    /// keeps the thumb at the left while nothing is loaded.
    /// </remarks>
    public double SeekMax => Math.Max(1, DurationMs);

    /// <summary>True while a hand is on the seek bar.</summary>
    /// <remarks>
    /// The drag only moves the reading; seeking to every point it passes would push that
    /// much of the song down the cable. The view calls <see cref="SeekToThumb"/> when it
    /// lets go.
    ///
    /// Not observable: only the view writes it, and only the view model reads it.
    /// </remarks>
    public bool Scrubbing { get; set; }

    /// <summary>Moves playback to where the seek bar was left.</summary>
    public void SeekToThumb()
        => _player.Controller.Seek(TimeSpan.FromMilliseconds(PositionMs));

    partial void OnPositionMsChanged(double value) => ShowPosition();

    partial void OnDurationMsChanged(double value)
    {
        OnPropertyChanged(nameof(SeekMax));
        ShowPosition();
    }

    private void ShowPosition()
        => PositionText = $"{TimeSpan.FromMilliseconds(PositionMs):mm\\:ss}"
                        + $" / {TimeSpan.FromMilliseconds(DurationMs):mm\\:ss}";

    /// <summary>The transport's state, for the buttons that are coloured by it.</summary>
    /// <remarks>
    /// Two flags rather than the enum, because what the buttons ask is a yes or no each and
    /// a style selector tests a class, not a value. Kept in step by the display tick.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    /// <inheritdoc cref="IsPlaying"/>
    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    /// <summary>Whether the seek bar does anything: there has to be a song on the transport.</summary>
    /// <remarks>
    /// Play from stopped reads the song again and starts it at the beginning, so a position
    /// set while stopped would be thrown away.
    /// </remarks>
    public bool CanSeek => IsPlaying || IsPaused;

    /// <summary>
    /// Whether a performance is going on: from the moment something plays until the transport
    /// comes to rest, through pauses and the gaps between songs.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="IsPlaying"/>, it goes false only once, when the transport stops.
    /// What <c>MediaKeys</c> tells the system.
    /// </remarks>
    [ObservableProperty]
    public partial bool IsPerforming { get; set; }

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSeek));
        if (value) IsPerforming = true;
    }

    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(CanSeek));

    /// <summary>The status bar, which says what is in force and nothing else.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? DefinitionPath { get; set; }

    [ObservableProperty]
    public partial string UseModule { get; set; } = "THRU";

    [ObservableProperty]
    public partial RepeatMode Repeat { get; set; } = RepeatMode.None;

    [ObservableProperty]
    public partial PlayOrder Order { get; set; } = PlayOrder.Registered;

    [ObservableProperty]
    public partial int LoopRepeatCount { get; set; } = 2;

    [ObservableProperty]
    public partial PlaybackPriority Priority { get; set; } = PlaybackPriority.High;

    [ObservableProperty]
    public partial int TransferRate { get; set; }

    [ObservableProperty]
    public partial bool UseMidiOutReset { get; set; } = true;

    [ObservableProperty]
    public partial bool SendAllNotesOffOnStop { get; set; } = true;

    /// <summary>
    /// Stands a tone the machine has in for a variation tone it does not, on a Sound Canvas
    /// from the SC-55mkII on. Taken up by the next song, as the output module is.
    /// </summary>
    [ObservableProperty]
    public partial bool CapitalToneFallback { get; set; }

    /// <summary>
    /// With no DEF, resets the output module before each song, as define.yaml's
    /// initializeType says. Taken up by the next song.
    /// </summary>
    [ObservableProperty]
    public partial bool SendModuleReset { get; set; } = true;

    /// <summary>
    /// With no DEF, plays a song for an earlier model on that model's tone map, where the
    /// output module carries it (<see cref="ToneMap"/>). Taken up by the next song.
    /// </summary>
    /// <remarks>
    /// Apart from the reset: the map is chosen by messages of its own, which mean the same
    /// with or without a reset in front of them.
    /// </remarks>
    [ObservableProperty]
    public partial bool SwitchToneMap { get; set; } = true;

    /// <summary>Whether the platform's media keys drive the player (<c>Views.MediaKeys</c>).</summary>
    [ObservableProperty]
    public partial bool UseMediaKeys { get; set; } = true;

    /// <summary>
    /// The language to show the player in, taken up at the next start: the windows are
    /// worded as they are built.
    /// </summary>
    [ObservableProperty]
    public partial LanguageChoice Language { get; set; } = Languages.All[0];

    /// <summary>Whether the graphics hardware draws the windows, taken up at the next start.</summary>
    [ObservableProperty]
    public partial bool HardwareRendering { get; set; } = true;

    /// <summary>Reads every file in the list up front.</summary>
    [ObservableProperty]
    public partial bool ScanLength { get; set; } = true;

    /// <summary>Tidies the spacing in titles before showing them.</summary>
    [ObservableProperty]
    public partial bool TrimTitles { get; set; }

    /// <summary>Starts playing what was dropped, rather than only listing it.</summary>
    [ObservableProperty]
    public partial bool AutoDropPlay { get; set; } = true;

    /// <summary>
    /// The transport's play button: carries on where playback is.
    /// </summary>
    /// <remarks>
    /// A paused song resumes. A stopped player starts the tab in front from the top of the
    /// song it last played, or on its first song — or, when the transport is on that tab's
    /// list, on the song the cursor is on, which next and previous move while stopped. Not
    /// on the selected row: selecting is for looking and for the row menus
    /// (<see cref="PlaySelectedCommand"/> plays that).
    /// </remarks>
    [RelayCommand]
    private void Play()
    {
        if (_player.Controller.State != TransportState.Stopped)
        {
            _player.Controller.Play();
            return;
        }

        if (SelectedPlaylist is not { } tab) return;
        bool onCursor = ReferenceEquals(_player.Controller.Playlist, tab.List)
                        && _player.Controller.Current is not null;
        StartPlaying(tab);
        if (onCursor)
        {
            _player.Controller.Play();
            return;
        }

        int last = tab.LastPlayed;
        if (last >= 0) _player.Controller.Play(last);
        else _player.Controller.Play();
    }

    /// <summary>
    /// Starts the song the list has selected, whatever is playing at the time.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="PlayCommand"/>: double-clicking a row says "play this one",
    /// not the transport button's "carry on".
    /// </remarks>
    [RelayCommand]
    private void PlaySelected()
    {
        if (SelectedPlaylist is not { Selected: { } row } tab) { Play(); return; }

        StartPlaying(tab);
        _player.Controller.Play(tab.Items.IndexOf(row));
    }

    [RelayCommand]
    private void Pause() => _player.Controller.TogglePause();

    /// <summary>Pauses a song that is playing, and does nothing otherwise.</summary>
    /// <remarks>
    /// For a media key's pause, which must not toggle.
    /// </remarks>
    public void PauseOnly() => _player.Controller.Pause();

    /// <summary>Starts at rest, and otherwise pauses or resumes, as the play and pause buttons do.</summary>
    /// <remarks>For the space bar, one key for both.</remarks>
    public void PlayOrPause()
    {
        if (_player.Controller.State == TransportState.Stopped) Play();
        else Pause();
    }

    /// <remarks>
    /// Only asks. The display is cleared and the outputs let go when the transport has come
    /// to rest (<see cref="OnTransportStopped"/>).
    /// </remarks>
    [RelayCommand]
    private void Stop() => _player.Controller.Stop();

    /// <summary>
    /// Clears what belonged to the song that was playing.
    /// </summary>
    /// <remarks>
    /// Not hung off the sequencer's own stop, which also fires between songs and would show
    /// "(stopped)" while the next song loads.
    /// </remarks>
    private void ShowStopped()
    {
        NowPlaying = Strings.Stopped;
        SongInfo = NothingSaid;
        IsPerforming = false;
    }

    [RelayCommand]
    private void Next() => _player.Controller.Next();

    [RelayCommand]
    private void Previous() => _player.Controller.Previous();

    /// <summary>
    /// Moves songs into a gap in the list in front, for a drag. True when the list changed.
    /// </summary>
    /// <remarks>
    /// The walk order is not rebuilt here but in <see cref="ReorderFinished"/>, which the
    /// drag calls once the list has changed.
    /// </remarks>
    public bool MoveItems(IReadOnlyList<PlaylistItemViewModel> rows, int gap)
        => SelectedPlaylist is { } tab && tab.Move(rows, gap);

    /// <summary>Puts the list in front in order.</summary>
    /// <remarks>
    /// A list being played goes on from the song it is on, in the new order, the way it does
    /// after a drag.
    /// </remarks>
    public void SortItems(SongSort by)
    {
        if (SelectedPlaylist is not { } tab || !tab.Sort(by)) return;
        if (tab.IsPlaying) _player.Controller.Rearranged();
    }

    /// <summary>Tells the player the list it is walking has been rearranged.</summary>
    public void ReorderFinished()
    {
        if (SelectedPlaylist is { IsPlaying: true }) _player.Controller.Rearranged();
    }

    /// <summary>
    /// Puts the rows into another list, taking them out of this one when moving.
    /// </summary>
    /// <remarks>
    /// The songs are copied one by one, as a duplicated list's are, so a module set on one
    /// copy afterwards is not set on the other.
    ///
    /// A move copies first and removes after, so whatever might go wrong goes wrong while
    /// the rows are still where they were.
    /// </remarks>
    public void CopyItems(IEnumerable<PlaylistItemViewModel> rows,
                          PlaylistTabViewModel to, bool move)
    {
        if (SelectedPlaylist is not { } from || ReferenceEquals(from, to)) return;

        PlaylistItemViewModel[] taken = [.. rows];
        if (taken.Length == 0) return;

        foreach (PlaylistItemViewModel row in taken)
            to.Add(new PlaylistItem
            {
                Path = row.Item.Path,
                Title = row.Item.Title,
                DurationMs = row.Item.DurationMs,
                ModuleFromData = row.Item.ModuleFromData,
                Module = row.Item.Module,
            });

        if (move)
        {
            foreach (PlaylistItemViewModel row in taken) from.Remove(row);
            if (from.IsPlaying) _player.Controller.Rearranged();
        }
        if (to.IsPlaying) _player.Controller.Rearranged();

        StartDetect();
        Note(move
            ? string.Format(Strings.NoteSongsMoved, taken.Length, to.Name)
            : string.Format(Strings.NoteSongsCopied, taken.Length, to.Name));
    }

    /// <summary>Takes songs out of the list in front.</summary>
    /// <remarks>
    /// Over a copy of what is handed in: the rows come straight from the list box's own
    /// selection, which shrinks as the rows go.
    /// </remarks>
    public void RemoveItems(IEnumerable<PlaylistItemViewModel> rows)
    {
        if (SelectedPlaylist is not { } tab) return;

        PlaylistItemViewModel[] taken = [.. rows];
        if (taken.Length == 0) return;

        foreach (PlaylistItemViewModel row in taken) tab.Remove(row);
        // Rearranged, not handed over again: that would put the cursor back at the top.
        if (tab.IsPlaying) _player.Controller.Rearranged();
        Note(string.Format(Strings.NoteSongsRemoved, taken.Length, tab.Name));
    }

    /// <summary>Opens an empty list in a new tab, saved in the player's own folder.</summary>
    [RelayCommand]
    private void NewPlaylist()
    {
        var list = new Playlist { Name = UnusedName(NewListName) };
        AddTab(new PlaylistTabViewModel(list, UnusedFilePath(), SongText), select: true);
    }

    /// <summary>
    /// Opens a second copy of a list, in its own tab and its own file.
    /// </summary>
    /// <remarks>
    /// The songs are copied one by one, so changes to the copy do not reach the original.
    /// The new tab goes in beside what it came from and comes to the front.
    /// </remarks>
    [RelayCommand]
    private void DuplicatePlaylist(PlaylistTabViewModel? which)
    {
        if ((which ?? SelectedPlaylist) is not { } tab) return;
        if (!Playlists.Contains(tab)) return;

        var list = new Playlist
        {
            Name = UnusedName(tab.Name),
            Items = [.. tab.List.Items.Select(item => new PlaylistItem
            {
                Path = item.Path,
                Title = item.Title,
                DurationMs = item.DurationMs,
                ModuleFromData = item.ModuleFromData,
                Module = item.Module,
            })],
        };

        AddTab(new PlaylistTabViewModel(list, UnusedFilePath(), SongText),
               select: true, at: Playlists.IndexOf(tab) + 1);
        Note(string.Format(Strings.NotePlaylistDuplicated, tab.Name));
    }

    /// <summary>
    /// Moves a tab into a gap in the strip, for a drag: 0 is before the first tab. True when
    /// the tabs changed.
    /// </summary>
    /// <remarks>
    /// The tab in front is put back afterwards: the tab control may drop its selection as the
    /// tab leaves its place.
    /// </remarks>
    public bool MovePlaylist(PlaylistTabViewModel tab, int gap)
    {
        int from = Playlists.IndexOf(tab);
        if (from < 0) return false;

        int to = Math.Clamp(gap > from ? gap - 1 : gap, 0, Playlists.Count - 1);
        if (to == from) return false;

        PlaylistTabViewModel? front = SelectedPlaylist;
        Playlists.Move(from, to);
        SelectedPlaylist = front;
        return true;
    }

    /// <summary>
    /// Closes the tab in front. The file stays where it is; only the tab goes.
    /// </summary>
    /// <remarks>
    /// There is always a tab: closing the last opens an empty list in its place. A list that
    /// is playing is stopped first. A list whose changes cannot be written stays open, so
    /// they are not lost with the tab.
    /// </remarks>
    [RelayCommand]
    private void ClosePlaylist(PlaylistTabViewModel? which)
    {
        if ((which ?? SelectedPlaylist) is not { } tab) return;
        if (!Playlists.Contains(tab)) return;

        if (tab.HasChanges && TrySave(tab) is { } problem)
        {
            Note(string.Format(Strings.NoteCannotSavePlaylist, tab.FilePath, problem));
            ShowError?.Invoke(Strings.CannotSavePlaylistTitle,
                              string.Format(Strings.CannotCloseUnsaved, tab.FilePath, problem));
            return;
        }

        // The transport lets go of the list too, so next and previous do not walk the songs
        // of a tab that is no longer there.
        if (tab.IsPlaying) _player.Controller.Stop();
        if (ReferenceEquals(_player.Controller.Playlist, tab.List))
            _player.Controller.SetPlaylist(new Playlist());

        int at = Playlists.IndexOf(tab);
        Playlists.Remove(tab);

        if (Playlists.Count == 0)
        {
            NewPlaylist();
            return;
        }

        // Closing a tab that was not in front must not move the one that is.
        if (SelectedPlaylist is null || !Playlists.Contains(SelectedPlaylist))
            SelectedPlaylist = Playlists[Math.Min(at, Playlists.Count - 1)];
    }

    /// <summary>
    /// Takes songs handed in from outside the list — dropped on it, or named on a command
    /// line, which is also how a file manager opens them: songs join the list, folders bring
    /// what is inside them.
    /// </summary>
    /// <param name="module">A module for the new songs, or empty to leave it to detection.</param>
    /// <remarks>
    /// Playing starts on the first of the new songs when <see cref="AutoDropPlay"/> is on and
    /// the transport is stopped. Playing or paused, they are only added.
    /// </remarks>
    public void ReceiveFiles(IEnumerable<string> paths, string module = "")
    {
        if (SelectedPlaylist is not { } tab) return;

        int from = tab.List.Items.Count;
        string[] songs = [.. SongFiles.Expand(paths)];
        if (songs.Length == 0)
        {
            Note(Strings.NoteNothingPlayable);
            return;
        }

        bool busy = _player.Controller.State != TransportState.Stopped;
        AddFiles(songs, module);
        Note(busy
            ? string.Format(Strings.NoteSongsAddedPlaying, songs.Length, tab.Name)
            : string.Format(Strings.NoteSongsAdded, songs.Length, tab.Name));

        if (AutoDropPlay && !busy) PlayFrom(from);
    }

    /// <summary>
    /// Adds songs to the tab in front, and reports where in it they landed.
    /// </summary>
    /// <param name="module">A module for the new songs, or empty to leave it to detection.</param>
    /// <returns>The index of the first song added, or -1 when nothing was.</returns>
    public int AddFiles(IEnumerable<string> paths, string module = "")
    {
        if (SelectedPlaylist is not { } tab) return -1;

        int from = tab.List.Items.Count;
        foreach (string path in paths)
            tab.Add(new PlaylistItem { Path = path, Module = module });
        if (tab.List.Items.Count == from) return -1;

        // Rearranged, not SetPlaylist: the cursor stays on the song playing, where handing
        // the list over again would put it back at the top.
        if (tab.IsPlaying) _player.Controller.Rearranged();
        StartScan();
        StartDetect();
        return from;
    }

    /// <summary>
    /// Starts the tab in front at <paramref name="index"/>, or at its first song.
    /// </summary>
    private void PlayFrom(int index)
    {
        if (SelectedPlaylist is not { } tab) return;

        StartPlaying(tab);

        if (index >= 0) _player.Controller.Play(index);
        else _player.Controller.Play();
    }

    /// <summary>Opens a playlist file in a tab of its own, or brings it forward if already open.</summary>
    /// <remarks>An M3U is taken in rather than opened: see <see cref="ImportM3u"/>.</remarks>
    public void LoadPlaylist(string path)
    {
        if (M3uFile.IsPlaylist(path))
        {
            ImportM3u(path);
            return;
        }

        foreach (PlaylistTabViewModel open in Playlists)
        {
            if (!string.Equals(open.FilePath, path, StringComparison.OrdinalIgnoreCase)) continue;
            SelectedPlaylist = open;
            return;
        }

        Playlist list;
        try
        {
            list = PlaylistFile.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidDataException or YamlDotNet.Core.YamlException)
        {
            Note(string.Format(Strings.NoteCannotLoadPlaylist, path, ex.Message));
            ShowError?.Invoke(Strings.CannotOpenPlaylistTitle,
                              string.Format(Strings.CannotOpenPlaylist, path, ex.Message));
            return;
        }

        if (list.Name.Length == 0) list.Name = Path.GetFileNameWithoutExtension(path);
        AddTab(new PlaylistTabViewModel(list, path, SongText, fromFile: true), select: true);
        Note(string.Format(Strings.NotePlaylistLoaded, path));
        StartScan();
    }

    /// <summary>
    /// Opens the songs an M3U lists in a new tab, saved in the player's own folder.
    /// </summary>
    /// <remarks>
    /// The M3U is left as it is: it has no room for what a list of the player's keeps, such
    /// as the target module set by hand, so the tab does not write back to it.
    ///
    /// Only what the player can play is taken: a list made by another player is likely to
    /// name MP3s, and anything else is not a song. A song that is not there stays, as in a
    /// list of the player's own; nothing is asked of it until it is played.
    /// </remarks>
    private void ImportM3u(string path)
    {
        string[] songs;
        try
        {
            songs = [.. M3uFile.Read(path).Where(SongFiles.IsSong)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Note(string.Format(Strings.NoteCannotLoadPlaylist, path, ex.Message));
            ShowError?.Invoke(Strings.CannotOpenPlaylistTitle,
                              string.Format(Strings.CannotOpenPlaylist, path, ex.Message));
            return;
        }

        var list = new Playlist
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Items = [.. songs.Select(song => new PlaylistItem { Path = song })],
        };
        AddTab(new PlaylistTabViewModel(list, UnusedFilePath(), SongText), select: true);
        Note(string.Format(Strings.NotePlaylistLoaded, path));
        StartScan();
    }

    /// <summary>Writes a tab's songs to an M3U, for other players. The tab keeps its own file.</summary>
    public void ExportM3u(PlaylistTabViewModel tab, string path)
    {
        try
        {
            M3uFile.Write(path, tab.List.Items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Note(string.Format(Strings.NoteCannotSavePlaylist, path, ex.Message));
            ShowError?.Invoke(Strings.CannotSavePlaylistTitle,
                              string.Format(Strings.CannotSavePlaylist, path, ex.Message));
            return;
        }
        Note(string.Format(Strings.NotePlaylistExported, path));
    }

    /// <summary>Writes a tab to a file of the caller's choosing, and follows it there.</summary>
    /// <remarks>
    /// Which tab is the caller's to say: the menu this comes from belongs to a tab, which is
    /// not always the one in front.
    ///
    /// A list that cannot be written there stays with the file it had, as does one sent to a
    /// file another tab has open.
    /// </remarks>
    public void SavePlaylist(PlaylistTabViewModel tab, string path)
    {
        // Two tabs on one file would each write it, the later over the earlier.
        PlaylistTabViewModel? other = Playlists.FirstOrDefault(open => !ReferenceEquals(open, tab)
            && string.Equals(open.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if ((other is null ? TrySave(tab, path) : string.Format(Strings.PlaylistOpenInTab, other.Name))
            is { } problem)
        {
            Note(string.Format(Strings.NoteCannotSavePlaylist, path, problem));
            ShowError?.Invoke(Strings.CannotSavePlaylistTitle,
                              string.Format(Strings.CannotSavePlaylist, path, problem));
            return;
        }
        Note(string.Format(Strings.NotePlaylistSaved, path));
    }

    /// <summary>Puts a line in the debug window from outside the view model.</summary>
    public void Complain(string line) => Note(line);

    /// <summary>
    /// Says that something failed that nothing was ready for, from the handlers of last
    /// resort (<see cref="App"/>). The player carries on.
    /// </summary>
    public void ReportUnexpected(Exception ex)
    {
        Note(string.Format(Strings.NoteUnexpected, ex));
        HoldError(Strings.UnexpectedErrorTitle, string.Format(Strings.UnexpectedError, ex.Message));
    }

    /// <summary>
    /// Puts an output on port A, by id or by part of its name, for the command line.
    /// </summary>
    /// <remarks>
    /// It lands on the map the run starts from (<see cref="StartingMap"/>).
    /// </remarks>
    public void SelectDevice(string idOrName)
    {
        if (Find(idOrName) is { } device) StartingMap.Ports[0].Device = device;
        else Note(string.Format(Strings.NoteOutputNotFound, idOrName));
    }

    /// <summary>
    /// Makes a module the output module, for <c>--use</c> given without a DEF.
    /// </summary>
    /// <remarks>
    /// A name the list does not have is taken all the same, as <see cref="LoadDefinition"/>
    /// takes it, and said to be unknown.
    /// </remarks>
    public void SelectUseModule(string module)
    {
        string? listed = UseModules.FirstOrDefault(m => string.Equals(m, module, StringComparison.OrdinalIgnoreCase));
        if (listed is null) Note(string.Format(Strings.NoteUnknownUseModule, module));
        UseModule = listed ?? module;
    }

    /// <summary>
    /// Puts a DEF in force, and <paramref name="use"/> as the output module when given.
    /// </summary>
    /// <remarks>
    /// A file that cannot be read leaves whatever was in force, and says so. A module named
    /// with it is still taken, as if no DEF had been named.
    /// </remarks>
    public void LoadDefinition(string path, string? use = null)
    {
        DefDocument definition;
        try
        {
            definition = DefDocument.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            Note(string.Format(Strings.NoteCannotLoadDef, path, ex.Message));
            HoldError(Strings.CannotLoadDefTitle, string.Format(Strings.CannotLoadDef, path, ex.Message));
            if (use is not null) SelectUseModule(use);
            return;
        }

        DefinitionPath = path;
        _player.LoadDefinition(definition);

        // The target modules stay define.yaml's. The DEF adds the output modules only it
        // knows (SYG20, MSGS and the like), which only it can emulate for.
        FillUseModules(definition);

        // A name given on the command line wins, even one the index does not list: the
        // resolver falls back to the group walk for those.
        if (use is not null) UseModule = use;
        else if (!UseModules.Contains(UseModule)) UseModule = UseModules.FirstOrDefault() ?? "THRU";

        BuildMenus();
        ApplyEmulation();
        // ApplyEmulation says nothing until a song has named a target, and the bar should
        // not be left claiming no definition is loaded.
        ShowEmulation();
        // What the definition knows has changed, so every answer taken from it is stale.
        if (UseDefKeywords) Redetect();
    }

    /// <summary>
    /// Plays on without a definition, as if none had been loaded.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="LoadDefinition"/>. The song playing keeps its emulation.
    /// </remarks>
    public void CloseDefinition()
    {
        if (DefinitionPath is null) return;

        DefinitionPath = null;
        _player.CloseDefinition();
        FillUseModules(null);
        if (!UseModules.Contains(UseModule)) UseModule = UseModules.FirstOrDefault() ?? "THRU";

        BuildMenus();
        ApplyEmulation();
        ShowEmulation();
        // Told apart by the DEF's [keyword], a song cannot be told apart at all now.
        if (UseDefKeywords) Redetect();
    }

    /// <summary>
    /// Reads the target modules and the patterns that detect them: <c>define.override.yaml</c>
    /// in the settings folder when it is there, otherwise <c>define.yaml</c> from beside the
    /// executable.
    /// </summary>
    /// <remarks>
    /// Once, at start-up, after copying the shipped file into the settings folder as the
    /// sample. An override that cannot be read, or is of another format version, gives way
    /// to the shipped file with a message; other problems go to the debug window. Whichever
    /// is read gets the modules detection answers with
    /// (<see cref="ModuleDefinition.WithOwnAnswers"/>).
    /// </remarks>
    private void LoadModuleDefinition()
    {
        string shipped = Path.Combine(AppContext.BaseDirectory, ModuleDefinition.FileName);
        string folder = AppSettings.ConfigDirectory;

        if (File.Exists(shipped))
        {
            try
            {
                ModuleDefinition.WriteSample(shipped, folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Note(string.Format(Strings.NoteCannotWriteDefineSample,
                    Path.Combine(folder, ModuleDefinition.SampleFileName), ex.Message));
            }
        }

        string? custom = ModuleDefinition.FindOverride(folder);
        string? reason = null;
        ModuleDefinition? read = null;
        bool overridden = custom is not null && TryLoadModuleDefinition(custom, out read, out reason);
        if (overridden && read!.Version != ModuleDefinition.FormatVersion)
        {
            reason = string.Format(Strings.OverrideWrongVersion,
                                   read.Version, ModuleDefinition.FormatVersion);
            Note(string.Format(Strings.NoteCannotLoadDefine, custom, reason));
            overridden = false;
        }
        // One of another version is not put in force even when the shipped file cannot be read.
        if (overridden) _define = read!;
        else if (TryLoadModuleDefinition(shipped, out ModuleDefinition? builtIn, out _)) _define = builtIn!;

        _define = _define.WithOwnAnswers();

        if (overridden)
        {
            Note(string.Format(Strings.NoteDefineOverride, custom));
        }
        else if (custom is not null)
        {
            HoldError(Strings.CannotReadOverrideTitle,
                      string.Format(Strings.CannotReadOverride, custom, reason));
        }

        foreach (string problem in _define.Problems) Note(string.Format(Strings.NoteDefineProblem, problem));

        TargetModules.Clear();
        foreach (string module in _define.Modules) TargetModules.Add(module);
        DetectedDefaultChoices.Clear();
        DetectedDefaultChoices.Add(new Choice(string.Empty, Strings.SettingsDetectedAsIs));
        foreach (string module in _define.Modules) DetectedDefaultChoices.Add(new Choice(module, module));
        FillUseModules(null);
        FillModuleClaims();
    }

    /// <summary>
    /// Reads one definition file. False, and said why in the debug window and in
    /// <paramref name="reason"/>, when it cannot be read.
    /// </summary>
    private bool TryLoadModuleDefinition(string path, out ModuleDefinition? read, out string? reason)
    {
        read = null;
        try
        {
            read = ModuleDefinition.Load(path);
            reason = null;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Note(string.Format(Strings.NoteNoDefine, path));
            reason = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidDataException or YamlDotNet.Core.YamlException)
        {
            Note(string.Format(Strings.NoteCannotLoadDefine, path, ex.Message));
            reason = ex.Message;
        }

        return false;
    }

    /// <summary>The output modules: define.yaml's, then any only the DEF names.</summary>
    /// <remarks>
    /// The maps choose theirs again afterwards: a map the port map window is showing loses
    /// its choice while the list is rebuilt (<see cref="PortMapViewModel.RestoreUseModule"/>).
    /// </remarks>
    private void FillUseModules(DefDocument? definition)
    {
        UseModules.Clear();
        foreach (string module in _define.Modules) UseModules.Add(module);
        foreach (DefEntry e in definition?["moduleindex"]?.Entries ?? [])
            if (!UseModules.Contains(e.Key, StringComparer.OrdinalIgnoreCase)) UseModules.Add(e.Key);

        foreach (PortMapViewModel map in PortMaps) map.RestoreUseModule();
    }

    /// <summary>What <c>define.yaml</c> says, or nothing when it could not be loaded.</summary>
    private ModuleDefinition _define = ModuleDefinition.Empty;

    /// <summary>
    /// Whether songs are told apart by the DEF's <c>[keyword]</c> instead of by the
    /// patterns of <c>define.yaml</c>.
    /// </summary>
    /// <remarks>
    /// The list of modules is define.yaml's either way; only the search changes.
    /// </remarks>
    [ObservableProperty]
    public partial bool UseDefKeywords { get; set; }

    partial void OnUseDefKeywordsChanged(bool value) => Redetect();

    /// <summary>
    /// The words define.yaml's patterns are run over, in the order they are searched, then
    /// the song's data, each on or off.
    /// </summary>
    public ObservableCollection<DetectionSourceViewModel> DetectionSources { get; } = [];

    /// <summary>Whether the model named first in those words decides, or the one named last.</summary>
    [ObservableProperty]
    public partial MatchPosition DetectionPosition { get; set; }

    partial void OnDetectionPositionChanged(MatchPosition value) => DetectionChanged();

    /// <summary>The target module a song detected as THRU is played as; empty for THRU.</summary>
    [ObservableProperty]
    public partial string ThruPlaysAs { get; set; } = string.Empty;

    /// <summary>The same for a song detected as GS.</summary>
    [ObservableProperty]
    public partial string GsPlaysAs { get; set; } = string.Empty;

    /// <summary>The same for a song detected as XG.</summary>
    [ObservableProperty]
    public partial string XgPlaysAs { get; set; } = string.Empty;

    // Null while the combo box has nothing picked: taken as leaving it as detected.
    partial void OnThruPlaysAsChanged(string value) => DetectedDefaultsChanged();

    partial void OnGsPlaysAsChanged(string value) => DetectedDefaultsChanged();

    partial void OnXgPlaysAsChanged(string value) => DetectedDefaultsChanged();

    private void DetectedDefaultsChanged()
    {
        _detectedDefaults = new(ThruPlaysAs ?? string.Empty, GsPlaysAs ?? string.Empty,
                                XgPlaysAs ?? string.Empty);
        Redetect();
    }

    /// <summary>
    /// <see cref="ThruPlaysAs"/>, <see cref="GsPlaysAs"/> and <see cref="XgPlaysAs"/> as one
    /// value, for the workers that detect.
    /// </summary>
    private DetectionDefaults _detectedDefaults = DetectionDefaults.None;

    /// <summary>
    /// Puts a source into the gap it was dropped in, the way <see cref="MovePortMap"/> does.
    /// True when the order changed.
    /// </summary>
    public bool MoveDetectionSource(DetectionSourceViewModel source, int gap)
    {
        int from = DetectionSources.IndexOf(source);
        if (from < 0 || source.IsFixed) return false;

        // Never below the lines that stay where they are.
        int last = DetectionSources.Count(s => !s.IsFixed) - 1;
        int to = Math.Clamp(gap - (from < gap ? 1 : 0), 0, last);
        if (to == from) return false;

        DetectionSources.Move(from, to);
        // The order is the priority, so every answer may have changed.
        DetectionChanged();
        return true;
    }

    /// <summary>
    /// Takes what <see cref="DetectionSources"/> and <see cref="DetectionPosition"/> now say, and
    /// works every song out again by it.
    /// </summary>
    private void DetectionChanged()
    {
        _nameDetection = new(
            [.. DetectionSources.Where(s => s.Enabled && s.Source is not null).Select(s => s.Source!.Value)],
            DetectionPosition);
        _detectFromData = DetectionSources.Any(s => s.Source is null && s.Enabled);
        Redetect();
    }

    /// <summary>
    /// <see cref="DetectionSources"/> and <see cref="DetectionPosition"/> as one value that does not
    /// change, for the transport's thread to read.
    /// </summary>
    /// <remarks>
    /// The list behind the settings window belongs to the UI thread and can throw when read
    /// while a line is dragged. A new value is put in whole each time the settings change,
    /// so a reader never sees half of each.
    /// </remarks>
    private NameDetection _nameDetection = NameDetection.Default;

    /// <summary>Whether the song's data is asked, taken with <see cref="_nameDetection"/>.</summary>
    private bool _detectFromData = true;

    /// <summary>
    /// The detection in force, ready to be handed to a worker. Null when there is none to
    /// run: the DEF's keywords were asked for and no DEF is loaded.
    /// </summary>
    /// <remarks>
    /// Taken on the calling thread, so a worker keeps the settings and DEF it started with.
    ///
    /// The attached document is read only when it is searched: always by the DEF's keywords,
    /// by define.yaml's patterns when the settings say so.
    ///
    /// The song's data is looked at only by define.yaml's detection, while the settings say
    /// so. The list's column is worked out without it and takes
    /// <see cref="PlaylistItem.ModuleFromData"/> instead.
    /// </remarks>
    private Func<string, string, MidiSequence?, ModuleDetection>? Detector()
    {
        ModuleDefinition define = _define;
        if (!UseDefKeywords)
        {
            NameDetection how = _nameDetection;
            bool readDocument = how.Reads(DetectionSource.Document);
            bool useData = _detectFromData;
            return (path, title, sequence) => ModuleDetector.Detect(
                define, path, title, readDocument ? ReadDocument(path) : string.Empty,
                fallback: Thru, sequence: useData ? sequence : null, how: how);
        }

        if (_player.Definition is not { } definition) return null;
        return (path, title, _)
            => ModuleDetector.Detect(definition, path, title, ReadDocument(path), fallback: Thru);
    }

    /// <summary>
    /// Settles which module a song is played as, just before it starts.
    /// </summary>
    /// <remarks>
    /// The song's own setting if it has one, otherwise the detector. Runs on the transport's
    /// thread; the emulation is rebuilt on the UI thread, since that moves properties the
    /// window is bound to. Invoke, not Post: the song must not start before its module is
    /// settled. When it cannot be — an output will not open, the setup will not go out — a
    /// stop is asked for, and the transport does not start the song.
    ///
    /// Only here is the song's own title to hand, so the answer is written back to the row.
    /// </remarks>
    private void OnSongLoading(PlaylistItem item, MidiSequence sequence)
    {
        // Read afresh, like the length the controller has just written back.
        item.ModuleFromData = _define.ScanData(sequence)?.Module ?? string.Empty;

        ModuleDetection? found = item.Module.Length > 0 || Detector() is not { } detect
            ? null
            : detect(item.Path, sequence.Title, sequence);

        // Empty only when the song leaves it to detection and there is none to run.
        string module = found is { } answer ? _detectedDefaults.Resolve(answer.Module) : item.Module;

        // It arrives padded and over several lines; there is room for one.
        string said = Glosa.Core.Text.TitleText.Tidy(
            sequence.Copyright.Length > 0 ? sequence.Copyright : sequence.Comment);

        bool routed = Dispatcher.UIThread.Invoke(() =>
        {
            SongInfo = said.Length > 0 ? said : NothingSaid;

            // The outputs first, and whatever is known about the song: it goes somewhere
            // even when nothing says what it was written for.
            if (!RouteThrough(MapFor(module))) return false;

            // Nothing to go on — the DEF's keywords were asked for and there is no DEF — is
            // the same as nothing found: THRU, and the machine still reset for it.
            if (module.Length == 0) module = Thru;

            _moduleDetected = found is not null;
            ApplyEmulation(module, twoPorts: sequence.MaxPort >= 1);
            if (found is { } detection)
            {
                RowFor(item)?.Detect(module);
                Note(module == detection.Module
                    ? string.Format(Strings.NoteDetected, detection)
                    : string.Format(Strings.NoteDetectedPlaysAs, detection, module));
            }
            else
            {
                Note(string.Format(Strings.NoteTargetModule, module));
            }
            return true;
        });

        // The setup goes out on the transport's thread, outside the Invoke: it has waits in it.
        if (routed && _player.SendInit() is { } problem) ReportInitProblem(problem);
    }

    /// <summary>The row showing a song, in whichever open list holds it.</summary>
    private PlaylistItemViewModel? RowFor(PlaylistItem item)
        => Playlists.SelectMany(tab => tab.Items)
                    .FirstOrDefault(row => ReferenceEquals(row.Item, item));

    /// <summary>
    /// The attached document: the text file that sits beside the song, if there is one
    /// — inside the same archive, for a song that is in one.
    /// </summary>
    private static string ReadDocument(string songPath)
    {
        try
        {
            string path = Path.ChangeExtension(songPath, ".txt");
            return SongStore.Exists(path)
                ? Glosa.Core.Text.DocumentText.Decode(SongStore.ReadAllBytes(path))
                : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                      or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Sets, or clears, the module of every row handed in.
    /// </summary>
    /// <remarks>
    /// <paramref name="module"/> empty puts a row back to being worked out from the song.
    /// The rows come from the list box's selection, so one call covers a whole block.
    /// </remarks>
    public void SetModule(IEnumerable<PlaylistItemViewModel> rows, string module)
    {
        PlaylistItemViewModel[] taken = [.. rows];
        if (taken.Length == 0) return;

        foreach (PlaylistItemViewModel row in taken) row.Module = module;

        Note(module.Length > 0
            ? string.Format(Strings.NoteSongsTargetSet, taken.Length, module)
            : string.Format(Strings.NoteSongsTargetCleared, taken.Length));
    }

    /// <summary>
    /// Works out what each row would be played as, for the list to show, on a worker.
    /// </summary>
    /// <remarks>
    /// Detection may read the document beside each song (<see cref="Detector"/>). One pass
    /// at a time; a request that arrives during one is run after it.
    ///
    /// Only songs whose file has been read, by the length scan or by playing, are worked
    /// out. Until then the title inside the file and what its data says are unknown, and
    /// the row is left blank rather than showing an answer the song may contradict.
    /// </remarks>
    private void StartDetect()
    {
        if (Detector() is not { } detect) return;
        if (_detecting) { _detectAgain = true; return; }

        (PlaylistItemViewModel Row, string Path, string Title)[] rows =
            [.. Playlists.SelectMany(tab => tab.Items)
                         .Where(row => !row.Detected && row.Item.DurationMs > 0)
                         .Select(row => (row, row.Item.Path, row.Item.Title))];

        // What a song's data said only stands in for define.yaml's own detection; the DEF's
        // keywords never look at the data.
        bool useData = !UseDefKeywords && _detectFromData;
        DetectionDefaults defaults = _detectedDefaults;

        if (rows.Length == 0) return;
        _detecting = true;
        int generation = _detectGeneration;

        Task.Run(() =>
        {
            // A pass that throws is given up, so it cannot leave detection switched off for
            // the rest of the run.
            (PlaylistItemViewModel Row, ModuleDetection Words)[] found = [];
            Exception? failed = null;
            try
            {
                found = [.. rows.Select(r => (r.Row, detect(r.Path, r.Title, null)))];
            }
            catch (Exception ex)
            {
                failed = ex;
            }

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (failed is not null) Note(string.Format(Strings.NoteDetectFailed, failed.Message));

                    // Answers worked out before everything was thrown away are not put back:
                    // they would mark their rows done, and the pass that follows would pass
                    // them by.
                    if (generation == _detectGeneration)
                    {
                        // What the song's data said is read here rather than when the pass
                        // started: the song may have been scanned or played while it was
                        // running.
                        foreach ((PlaylistItemViewModel row, ModuleDetection words) in found)
                            row.Detect(defaults.Resolve(
                                words.Source == ModuleSource.Default && useData
                                && row.Item.ModuleFromData is { Length: > 0 } data
                                    ? data : words.Module));
                    }
                }
                finally
                {
                    _detecting = false;
                }

                if (!_detectAgain) return;
                _detectAgain = false;
                StartDetect();
            });
        });
    }

    private bool _detecting;

    private bool _detectAgain;

    /// <summary>
    /// Moved on by <see cref="Redetect"/>, so a pass already running can tell it is out of date.
    /// </summary>
    private int _detectGeneration;

    /// <summary>The target the emulation was last built for, so it can be rebuilt as it is.</summary>
    private string _moduleInForce = string.Empty;

    /// <summary>The output module it was built for, which is not always the one pending.</summary>
    /// <remarks>
    /// A change made while a song is playing waits for the next one, so the two can differ.
    /// The status bar shows this one.
    /// </remarks>
    private string _useInForce = string.Empty;

    /// <summary>Whether <see cref="_moduleInForce"/> was worked out rather than set.</summary>
    private bool _moduleDetected;

    /// <summary>
    /// The tone map the module was put on for the song, by its name in the manuals; empty
    /// when none was chosen — a DEF loaded, a module with no earlier maps, or at rest, when
    /// the machine has been put back on its own.
    /// </summary>
    private string _toneMapInForce = string.Empty;

    /// <summary>
    /// Writes the line the status bar carries.
    /// </summary>
    /// <remarks>
    /// What is in force: the map the outputs belong to, then the module the song was written
    /// for and the machine on the end of the outputs, in the direction the song travels, and
    /// what the emulation made of that pair.
    ///
    /// **A value in brackets was worked out; a bare one was set by hand**, as in the
    /// playlist's module column. The map is bracketed under Auto, the target when it came
    /// from detection.
    ///
    /// The output module is the one the engine was built with, not the one pending for the
    /// next song.
    ///
    /// With no DEF loaded the line ends after the target, or after the tone map the module
    /// was put on, in the place the emulation would take.
    /// </remarks>
    private void ShowEmulation()
    {
        string map = ActiveMap is { } current ? Bracket(current.Title, AutoPortMap) : NothingSaid;
        string use = _useInForce.Length > 0 ? _useInForce : UseModule;
        string target = _moduleInForce.Length > 0
            ? Bracket(_moduleInForce, _moduleDetected) : NothingSaid;

        string emulation = DefinitionPath is not null && _moduleInForce.Length > 0
            ? _player.EmulationLabel
            : DefinitionPath is null && _toneMapInForce.Length > 0
                ? string.Format(Strings.ToneMapInForce, _toneMapInForce) : string.Empty;

        Status = emulation.Length > 0
            ? string.Format(Strings.StatusWithEmulation, map, target, use, emulation)
            : string.Format(Strings.Status, map, target, use);

        static string Bracket(string value, bool workedOut) => workedOut ? $"({value})" : value;
    }

    /// <summary>Throws away every detected answer and works them out again.</summary>
    private void Redetect()
    {
        _detectGeneration++;
        foreach (PlaylistItemViewModel row in Playlists.SelectMany(tab => tab.Items)) row.Forget();
        StartDetect();
    }

    /// <summary>
    /// Builds the emulation for a target module.
    /// </summary>
    /// <remarks>
    /// No argument means the definition or the output module has changed. Nothing is built
    /// then: every song builds its own emulation as it loads, so the next one picks the
    /// change up.
    ///
    /// A named target always builds, whatever the transport is doing: that call is a song's
    /// own setup as it loads. <paramref name="twoPorts"/> says the song plays on port B as
    /// well, so has both part groups of a 32-part module to set up.
    /// </remarks>
    public void ApplyEmulation(string? target = null, bool twoPorts = false)
    {
        if (target is null)
        {
            if (_player.Controller.State != TransportState.Stopped)
                Note(string.Format(Strings.NoteUseModuleNextSong, UseModule));
            return;
        }

        string module = target;
        if (module.Length == 0) return;

        _moduleInForce = module;
        _useInForce = UseModule;

        // The fallback is the machine's, whatever the song is and whether or not a DEF has
        // anything to say about it: it only acts on what would otherwise not sound.
        SoundCanvasTones? tones = CapitalToneFallback ? SoundCanvasTones.Of(UseModule) : null;
        _player.UseFallback(tones);
        if (tones is not null) Note(string.Format(Strings.NoteFallbackTables, tones.Model));

        // With no DEF: the reset define.yaml's initializeType names, and the tone map of the
        // model the song was written for where the module carries it (ToneMap).
        if (DefinitionPath is null)
        {
            // The type is looked up whether or not the reset goes out: it also says which of an
            // MU's two personalities is in use, which decides whether it has maps to switch.
            string? type = _define.InitializeTypeOf(UseModule);
            ToneMap? maps = SwitchToneMap ? ToneMap.Of(UseModule, type) : null;
            byte wanted = maps?.For(module) ?? 0;
            // A Roland's map goes with the reset, once per machine — reaching the second part
            // group from there too for a song on two ports — and rides on the song's own
            // program changes from there. The MU's is a setting of the whole machine and only
            // reaches it by being sent, to every port it may be on.
            int[] ports = maps is { Lasting: true } ? PortsInUse() : PortsToReset();
            ToneMapChoice? map = maps is not null && maps.Needs(wanted)
                                 && (!maps.Lasting || ports.Length > 0)
                ? new ToneMapChoice(maps, wanted, ports, BothGroups: twoPorts)
                : null;
            string? reset = SendModuleReset ? type : null;
            _player.UseInit(reset is null ? [] : InitializeType.InitFor(reset, PortsToReset()), map);
            if (reset is not null) Note(string.Format(Strings.NoteInitialize, UseModule, reset));
            _toneMapInForce = map is { } chosen ? chosen.Maps.NameOf(chosen.Map) : string.Empty;
            if (_toneMapInForce.Length > 0) Note(string.Format(Strings.ToneMapInForce, _toneMapInForce));
            ShowEmulation();
            OnPropertyChanged(nameof(Panel));
            // The emulation layer is a new one, and the panel with it.
            Parts.Clear();
            BuildParts();
            return;
        }

        _player.ApplyEmulation(UseModule, module);
        _toneMapInForce = string.Empty;
        ShowEmulation();
        Note(string.Format(Strings.NoteEmulation, UseModule, module, _player.EmulationLabel));
        if (_player.InitCut) Note(Strings.NoteInitCut);
        OnPropertyChanged(nameof(Panel));
        // The panel is a new object, so the monitor's rows are pointing at the old one.
        Parts.Clear();
        BuildParts();
    }

    /// <summary>
    /// Stops the song when its setup did not go out.
    /// </summary>
    /// <remarks>
    /// The port opened but will not take anything now: unplugged since, or its driver has
    /// given up. Without its setup the song would sound wrong, if at all.
    ///
    /// On the transport's thread, where the setup is sent, so the transport sees the stop
    /// before it would start the song.
    /// </remarks>
    private void ReportInitProblem(string problem)
    {
        Dispatcher.UIThread.Post(() => Note(string.Format(Strings.NoteCannotSendInit, problem)));
        StopAndShow(Strings.CannotSendTitle, Strings.CannotSendInit
            + Environment.NewLine + Environment.NewLine + problem);
    }

    /// <summary>
    /// The ports a reset goes to: the ones the map in use is set to reset
    /// (<see cref="PortMap.ResetPorts"/>), that have a device on them.
    /// </summary>
    private int[] PortsToReset()
    {
        if (ActiveMap is not { } map) return [];

        DeviceName?[] names = map.OpenList();
        return [.. map.Model.ResetPorts.Select(PortMap.PortOf).OfType<int>().Distinct().Order()
                      .Where(port => port < names.Length && names[port] is not null)];
    }

    /// <summary>
    /// Every port of the map in use that has a device on it.
    /// </summary>
    /// <remarks>
    /// Where an MU's Voice Map goes (<see cref="ToneMap.Lasting"/>). A Roland's map goes where
    /// its reset does.
    /// </remarks>
    private int[] PortsInUse()
    {
        if (ActiveMap is not { } map) return [];

        DeviceName?[] names = map.OpenList();
        return [.. Enumerable.Range(0, names.Length).Where(port => names[port] is not null)];
    }

    /// <summary>
    /// Whether the ports a reset goes to can be chosen: only with no DEF loaded, since a
    /// DEF's resets go where the DEF says.
    /// </summary>
    public bool ResetPortsEditable => DefinitionPath is null;

    partial void OnDefinitionPathChanged(string? value) => OnPropertyChanged(nameof(ResetPortsEditable));

    partial void OnLoopRepeatCountChanged(int value)
        => _player.Options.InfiniteLoopRepeatCount = Math.Max(0, value);

    partial void OnPriorityChanged(PlaybackPriority value) => _player.Options.Priority = value;

    partial void OnTransferRateChanged(int value)
        => _player.Options.TransferRateBytesPerSecond = value;

    partial void OnUseMidiOutResetChanged(bool value) => _player.Options.UseMidiOutReset = value;

    partial void OnSendAllNotesOffOnStopChanged(bool value)
        => _player.Options.SendAllNotesOffOnStop = value;

    partial void OnTrimTitlesChanged(bool value)
    {
        foreach (PlaylistTabViewModel tab in Playlists) tab.Refresh();
        NowPlaying = Title(_player.Controller.Current);
    }

    /// <summary>How a song's name reads on screen, spacing setting applied.</summary>
    private string Title(PlaylistItem? item)
        => item is null ? Strings.Stopped : Glosa.Core.Text.TitleText.Tidy(item.Display, TrimTitles);

    /// <summary>What the playlists show for each song, chosen from their right-click menu.</summary>
    /// <remarks>
    /// One choice for every list, like the spacing: it is how the person likes to read a
    /// list, not a fact about one of them.
    /// </remarks>
    [ObservableProperty]
    public partial SongLabel SongLabel { get; set; }

    partial void OnSongLabelChanged(SongLabel value)
    {
        foreach (PlaylistTabViewModel tab in Playlists) tab.Refresh();
    }

    /// <summary>A list row's text for a song, as <see cref="SongLabel"/> says.</summary>
    /// <remarks>
    /// Only the title is tidied: a file name or a path is what is on disk, and spacing taken
    /// out of it would name a file that is not there.
    /// </remarks>
    private string SongText(PlaylistItem item) => SongLabel switch
    {
        SongLabel.FileName => Path.GetFileName(item.Path),
        SongLabel.Path => item.Path,
        _ => Title(item),
    };

    partial void OnScanLengthChanged(bool value)
    {
        if (value) StartScan();
    }

    /// <summary>
    /// Reads the lengths of whatever the list does not know yet, on a worker.
    /// </summary>
    /// <remarks>
    /// One scan at a time. A request that arrives during a pass is run after it, since the
    /// pass under way knows nothing of what has just been added.
    /// </remarks>
    private void StartScan()
    {
        if (!ScanLength) return;
        if (_scanning) { _scanAgain = true; return; }
        _scanning = true;

        // Every open tab. Copied here, on the thread that edits the lists, and not on the
        // worker: a list copied while a song is added to it can come out short or throw.
        PlaylistItem[] items = [.. Playlists.SelectMany(t => t.List.Items)];
        // Each song read for its length is asked what its data says too, while it is open.
        ModuleDefinition define = _define;
        Task.Run(() =>
        {
            // A pass that throws is given up, so it cannot leave the scan switched off for
            // the rest of the run. What it filled in before then stays.
            int done = 0;
            Exception? failed = null;
            try
            {
                // A song on a share, or that is no ordinary file, waits to be played.
                done = PlaylistScan.Fill(
                    items.Where(item => item.DurationMs <= 0 && SongFiles.IsLocalFile(item.Path)),
                    path => _player.ReadSummary(path, define.DataMessages),
                    moduleFromData: song => define.ScanData(song)?.Module ?? string.Empty);
            }
            catch (Exception ex)
            {
                failed = ex;
            }

            Dispatcher.UIThread.Post(() =>
            {
                _scanning = false;
                if (failed is not null) Note(string.Format(Strings.NoteScanFailed, failed.Message));
                foreach (PlaylistTabViewModel open in Playlists) open.Refresh();
                // The scan fills in titles, which is one of the things detection reads.
                if (done > 0) { Note(string.Format(Strings.NoteLengthsScanned, done)); Redetect(); }
                else if (failed is not null) StartDetect();

                if (!_scanAgain) return;
                _scanAgain = false;
                StartScan();
            });
        });
    }

    private bool _scanning;

    private bool _scanAgain;

    partial void OnRepeatChanged(RepeatMode value)
    {
        _player.Controller.Repeat = value;
        MarkMenus();
    }

    partial void OnOrderChanged(PlayOrder value)
    {
        _player.Controller.Order = value;
        MarkMenus();
    }

    /// <summary>
    /// The output module in force follows the map in force, and is written back to it.
    /// </summary>
    /// <remarks>
    /// The write-back lets a definition that does not list the name correct it on the map
    /// too (see <see cref="LoadDefinition"/>), so it does not come back on the next switch.
    /// </remarks>
    partial void OnUseModuleChanged(string value)
    {
        PortMapViewModel map = ActiveMap ?? StartingMap;
        if (map.UseModule != value) map.UseModule = value;
        ApplyEmulation();
        // ApplyEmulation says nothing when the change is waiting for the next song, and the
        // bar has a line to keep up to date either way.
        ShowEmulation();
    }

    /// <summary>Rebuilds the menus, for when one of the lists behind them has changed.</summary>
    private void BuildMenus()
    {
        BuildPortMapMenu();

        Fill(OrderChoices, PlayOrders,
             order => Controls.EnumLabelConverter.Text(order),
             order => order == Order, order => Order = order);

        Fill(RepeatChoices, RepeatModes,
             mode => Controls.EnumLabelConverter.Text(mode),
             mode => mode == Repeat, mode => Repeat = mode);
    }

    /// <summary>
    /// Builds the port map menu: Auto, then the maps to pin to, as one radio group.
    /// </summary>
    private void BuildPortMapMenu()
    {
        PortMapChoices.Clear();
        PortMapChoices.Add(new MenuChoice(Strings.PortMapAuto, () => AutoPortMap, () => PinnedMap = null));
        if (PortMaps.Count > 0) PortMapChoices.Add(new Separator());

        foreach (PortMapViewModel map in PortMaps)
        {
            PortMapViewModel chosen = map;
            // A menu item reads an underscore as the mark for an access key, and a map is
            // named by whoever made it. Doubling one shows one.
            PortMapChoices.Add(new MenuChoice(
                chosen.Title.Replace("_", "__"),
                () => ReferenceEquals(chosen, PinnedMap),
                () => PinnedMap = chosen));
        }
    }

    /// <summary>Re-ticks the menus, for when only the choice in force has changed.</summary>
    private void MarkMenus()
    {
        foreach (object entry in PortMapChoices)
            if (entry is MenuChoice choice) choice.Refresh();

        foreach (ObservableCollection<MenuChoice> menu in
                 new[] { OrderChoices, RepeatChoices })
            foreach (MenuChoice choice in menu) choice.Refresh();
    }

    private static void Fill<T>(ObservableCollection<MenuChoice> menu, IEnumerable<T> values,
                                Func<T, string> label, Func<T, bool> isSelected, Action<T> select)
    {
        menu.Clear();
        foreach (T value in values)
        {
            T chosen = value;
            menu.Add(new MenuChoice(label(chosen), () => isSelected(chosen),
                                    () => select(chosen)));
        }
    }

    /// <summary>The assignment the ports were last routed for, or null while none are.</summary>
    private string? _routed;

    /// <summary>
    /// Sends the ports through a map's devices, so a song's FF 21 meta events land where
    /// they say. The devices not open yet are opened; the rest are kept.
    /// </summary>
    /// <returns>False when a device would not open, and the song is being stopped.</returns>
    private bool RouteThrough(PortMapViewModel map)
    {
        ActiveMap = map;

        DeviceName?[] names = map.OpenList();
        string signature = string.Join('\u001F', names.Select(name => name is { } n ? $"{n.Name}\u001E{n.Nth}" : ""));
        if (signature == _routed) return _lastRouteWasWhole;

        _player.Route(names);
        _routed = signature;
        RefreshPortStates();

        foreach (string problem in _player.OpenProblems) Note(string.Format(Strings.NoteCannotOpenOutput, problem));
        string listed = map.Describe();
        Note(string.Format(Strings.NoteOutputs, listed.Length > 0 ? listed : Strings.None));

        _lastRouteWasWhole = _player.OpenProblems.Count == 0;
        if (_lastRouteWasWhole) return true;

        StopAndShow(Strings.CannotOpenTitle, Strings.CannotOpenOutputs
            + Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, _player.OpenProblems));
        return false;
    }

    /// <summary>
    /// Closes every output, now that playback has come to rest.
    /// </summary>
    /// <remarks>
    /// Only from <see cref="OnTransportStopped"/>, which the transport waits for: nothing
    /// can be starting the next song while this runs.
    /// </remarks>
    private void ReleaseOutputs()
    {
        _player.ReleaseDevices();
        if (_player.LastCloseProblem is { } closing) Note(string.Format(Strings.NoteClosingOutput, closing));

        _routed = null;
        _lastRouteWasWhole = true;
        // The machines have just been put back on their own maps.
        _toneMapInForce = string.Empty;
        ActiveMap = null;
        RefreshPortStates();
    }

    /// <summary>Shows on every port whether its device is open.</summary>
    private void RefreshPortStates()
    {
        foreach (PortMapViewModel map in PortMaps)
            for (int port = 0; port < map.Ports.Count; port++)
                map.Ports[port].ShowState(map.NameOf(port) is { } name
                    ? _player.StateOf(name)
                    : (OutputState.Closed, null));
    }

    /// <summary>
    /// Stops playback and puts up a message saying why.
    /// </summary>
    /// <remarks>
    /// Reached from the song being set up. The stop is asked for straight away, so the
    /// transport does not start the song. The message is posted: a dialog is modal, and the
    /// transport is waiting for the setup to return.
    /// </remarks>
    private void StopAndShow(string title, string body)
    {
        _player.Controller.Stop();
        Dispatcher.UIThread.Post(() => ShowError?.Invoke(title, body));
    }

    /// <summary>Whether every port the map named opened, the last time they were routed.</summary>
    private bool _lastRouteWasWhole = true;

    /// <summary>
    /// Puts a message in front of the listener. Set by the window, which owns the dialog.
    /// </summary>
    /// <remarks>
    /// Messages held from before there was a window (<see cref="HoldError"/>) are shown as
    /// soon as it is set.
    /// </remarks>
    public Action<string, string>? ShowError
    {
        get => _showError;
        set
        {
            _showError = value;
            if (value is null) return;

            foreach ((string title, string body) in _heldErrors)
                Dispatcher.UIThread.Post(() => value(title, body));
            _heldErrors.Clear();
        }
    }

    private Action<string, string>? _showError;

    /// <summary>
    /// Puts a message in front of the listener, or keeps it until there is a window to show it
    /// over.
    /// </summary>
    private void HoldError(string title, string body)
    {
        if (_showError is { } show) Dispatcher.UIThread.Post(() => show(title, body));
        else _heldErrors.Add((title, body));
    }

    private readonly List<(string Title, string Body)> _heldErrors = [];

    /// <summary>
    /// Switches to the port map that claims <paramref name="module"/>, if one does.
    /// </summary>
    /// <remarks>
    /// The first map that claims the module is taken; a song no map claims goes to
    /// <see cref="DefaultMap"/>.
    ///
    /// A map claims modules from define.yaml's list, but the DEF's <c>[keyword]</c> can
    /// answer with a name the list folds into another (SC-8820 for SC-8850). The name is
    /// put into the list's terms here and nowhere else: what the song is shown as and what
    /// the emulation is built for stay as the DEF said.
    /// </remarks>
    private PortMapViewModel MapFor(string module)
    {
        if (PinnedMap is { } pinned) return pinned;
        if (module.Length == 0) return DefaultMap;

        string listed = _define.ListName(module);
        PortMapViewModel target = PortMaps.FirstOrDefault(map => map.Claims(listed)) ?? DefaultMap;
        if (!ReferenceEquals(target, ActiveMap))
            Note(listed == module
                ? string.Format(Strings.NotePortMap, target.Title, module)
                : string.Format(Strings.NotePortMapVia, target.Title, module, listed));
        return target;
    }

    /// <summary>Takes a stored map into the window, and starts watching it for renames.</summary>
    private PortMapViewModel Adopt(PortMap model)
    {
        var map = new PortMapViewModel(model, Devices, OnMapPortsChanged);
        map.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PortMapViewModel.Title))
            {
                BuildMenus();
                if (ReferenceEquals(map, ActiveMap)) ShowEmulation();
            }

            // Editing the map in force takes effect where it is edited, the way its ports
            // do. Waiting for a switch that may not come would look like nothing happened.
            else if (e.PropertyName == nameof(PortMapViewModel.UseModule)
                     && ReferenceEquals(map, ActiveMap)
                     && map.UseModule is { Length: > 0 } module) UseModule = module;
        };
        PortMaps.Add(map);
        return map;
    }

    /// <summary>
    /// A map's outputs were edited. The song playing keeps the outputs it started with; the
    /// next song routes through the map as it now is.
    /// </summary>
    private void OnMapPortsChanged(PortMapViewModel map)
    {
        RefreshPortStates();
        if (ReferenceEquals(map, ActiveMap) && _player.Controller.State != TransportState.Stopped)
            Note(Strings.NoteOutputsNextSong);
    }

    /// <summary>
    /// Puts the port map window on the map in use, or on the one the next song would start
    /// from while nothing is playing.
    /// </summary>
    public void EditPortMaps() => EditedMap = ActiveMap ?? StartingMap;

    /// <summary>
    /// Asks the system again which MIDI outputs there are, as the port map window opens.
    /// </summary>
    /// <remarks>
    /// Every port lets go of its device while the list changes and takes it back by name
    /// after: a combo box whose chosen item leaves its list reports that as a choice of
    /// nothing, and the port would forget its machine.
    /// </remarks>
    public void RefreshDevices()
    {
        IReadOnlyList<MidiDeviceInfo> found = PlayerService.Devices();
        if (found.SequenceEqual(Devices)) return;

        PortSlotViewModel[] slots = [.. PortMaps.SelectMany(map => map.Ports)];
        foreach (PortSlotViewModel slot in slots) slot.Restore(null, slot.Name);

        Devices.Clear();
        foreach (MidiDeviceInfo device in found) Devices.Add(device);

        foreach (PortMapViewModel map in PortMaps) map.Resolve();
        RefreshPortStates();
    }

    /// <summary>
    /// Moves a map into the gap at <paramref name="gap"/>, for a drag in the list.
    /// True when the order changed.
    /// </summary>
    /// <remarks>
    /// The auto-switch takes the first map that claims the song's module, so the order
    /// settles two maps naming the same one. The default map keeps the top.
    /// </remarks>
    public bool MovePortMap(PortMapViewModel map, int gap)
    {
        int from = PortMaps.IndexOf(map);
        if (from <= 0) return false;

        // The gap counts the map itself while it is still in the list, so the landing index
        // is one less once it is above the gap. Never index 0: that is the default map's.
        int to = Math.Clamp(gap - (from < gap ? 1 : 0), 1, PortMaps.Count - 1);
        if (to == from) return false;

        PortMaps.Move(from, to);
        BuildMenus();
        return true;
    }

    /// <summary>
    /// Adds a copy of the map being edited, and shows it without using it.
    /// </summary>
    [RelayCommand]
    private void AddPortMap()
    {
        var model = new PortMap
        {
            Title = UnusedMapTitle(),
            Ports = new(EditedMap?.Model.Ports ?? []),
            ResetPorts = [.. EditedMap?.Model.ResetPorts ?? [PortMap.PortKey(0)]],
            UseModule = EditedMap?.Model.UseModule ?? Thru,
        };

        EditedMap = Adopt(model);
        BuildMenus();
    }

    /// <summary>
    /// The first "Map n" no map is called, the default map counting as the first, as
    /// <see cref="UnusedName"/> does for playlists.
    /// </summary>
    private string UnusedMapTitle()
    {
        for (int n = 2; ; n++)
        {
            string title = string.Format(Strings.NewPortMapName, n);
            if (!PortMaps.Any(map => map.Title == title)) return title;
        }
    }

    /// <summary>Drops the map being edited. The default one stays.</summary>
    /// <remarks>
    /// Dropping the map chosen by hand hands the choice to the default one. A song playing
    /// through it plays on through the outputs it started with.
    /// </remarks>
    [RelayCommand]
    private void RemovePortMap()
    {
        if (EditedMap is not { } map || EditedIsDefault) return;

        int index = PortMaps.IndexOf(map);
        bool wasPinned = ReferenceEquals(map, PinnedMap);

        PortMaps.Remove(map);
        EditedMap = PortMaps[Math.Min(index, PortMaps.Count - 1)];
        if (wasPinned) PinnedMap = DefaultMap;
        BuildMenus();
    }

    /// <summary>Whether <see cref="RemovePortMapCommand"/> has anything to drop.</summary>
    public bool CanRemovePortMap => !EditedIsDefault;

    /// <summary>Whether the map being edited is one that names the songs it wants.</summary>
    public bool CanClaimModules => !EditedIsDefault;

    /// <summary>
    /// Every target module, each ticked when the map being edited claims it.
    /// </summary>
    /// <remarks>
    /// A module the map claims that <c>define.yaml</c> no longer lists comes after the rest,
    /// so it can still be let go.
    /// </remarks>
    public ObservableCollection<ModuleClaimViewModel> ModuleClaims { get; } = [];

    private void FillModuleClaims()
    {
        ModuleClaims.Clear();
        PortMapViewModel? map = EditedIsDefault ? null : EditedMap;
        IEnumerable<string> modules = TargetModules.Concat(
            map?.Modules.Where(m => !TargetModules.Contains(m, StringComparer.OrdinalIgnoreCase)) ?? []);
        foreach (string module in modules)
            ModuleClaims.Add(new ModuleClaimViewModel(module, map?.Claims(module) ?? false, OnClaimEdited));
    }

    private void OnClaimEdited(ModuleClaimViewModel line)
    {
        if (EditedMap is not { } map || EditedIsDefault) return;

        if (line.Claimed)
        {
            if (!map.Claims(line.Module)) map.Modules.Add(line.Module);
            return;
        }

        for (int i = map.Modules.Count - 1; i >= 0; i--)
            if (map.Modules[i].Equals(line.Module, StringComparison.OrdinalIgnoreCase)) map.Modules.RemoveAt(i);
    }

    /// <summary>Clears an output, which a combo box on its own cannot do.</summary>
    [RelayCommand]
    private static void ClearPort(PortSlotViewModel? slot)
    {
        if (slot is not null) slot.Device = null;
    }

    partial void OnEditedMapChanged(PortMapViewModel? value)
    {
        FillModuleClaims();
        OnPropertyChanged(nameof(EditedIsDefault));
        OnPropertyChanged(nameof(CanRemovePortMap));
        OnPropertyChanged(nameof(CanClaimModules));
    }

    partial void OnActiveMapChanged(PortMapViewModel? oldValue, PortMapViewModel? newValue)
    {
        foreach (PortMapViewModel map in PortMaps) map.IsActive = ReferenceEquals(map, newValue);
        if (newValue?.UseModule is { Length: > 0 } module) UseModule = module;
        ShowEmulation();
    }

    /// <summary>
    /// Finds a device the way the command line names one: by id, or by part of the name.
    /// </summary>
    /// <remarks>
    /// The id first, so <c>1</c> is device 1 and not the first device with a 1 in its name.
    /// Stored assignments go through <see cref="DeviceName"/> instead.
    /// </remarks>
    private MidiDeviceInfo? Find(string idOrName)
    {
        foreach (MidiDeviceInfo device in Devices)
            if (device.Id == idOrName) return device;
        foreach (MidiDeviceInfo device in Devices)
            if (device.Name.Contains(idOrName, StringComparison.OrdinalIgnoreCase)) return device;
        return null;
    }

    private void OnTick()
    {
        int elapsed = (int)_sinceLastTick.ElapsedMilliseconds;
        _sinceLastTick.Restart();

        // The display moves on its own, whether or not anything is playing.
        Panel.Advance(elapsed);
        if (PanelRevision != Panel.Revision) PanelRevision = Panel.Revision;

        // The seek bar is the clock: the tick moves the thumb, and the reading follows it.
        // Except while a hand has the thumb, when the hand is the one saying where it is.
        // Stopped, both ends read zero — there is no performance for them to be about.
        bool stopped = _player.Sequencer.State == PlaybackState.Stopped;
        DurationMs = SongDurationMs();
        if (!Scrubbing) PositionMs = stopped ? 0 : _player.Sequencer.PositionUs / 1000.0;

        // Read off the clock rather than pushed from the sequencer: the state can change
        // without anything here asking for it (a song ends, a protection stop fires), and the
        // tick is already running. Setting the same value again raises nothing.
        PlaybackState state = _player.Sequencer.State;
        IsPlaying = state == PlaybackState.Playing;
        IsPaused = state == PlaybackState.Paused;

        // The monitor carries far more rows than the display, and a meter does not need
        // every frame, so it runs at a third of the rate.
        if (++_tick % 3 != 0) return;
        Chord = ChordName.Detect(Panel.MelodicNotes);
        SoundingNotes = Panel.SoundingNotes;
        foreach (PartRowViewModel row in Parts) row.Refresh();
    }

    private int _tick;

    /// <summary>
    /// Gives the monitor a row per part on every port the loaded song reaches.
    /// </summary>
    /// <remarks>
    /// Never past the last port there is. Port B counts when a song on port A alone plays B
    /// parts through it (<see cref="PanelState.PortsPlayed"/>).
    /// </remarks>
    private void BuildParts()
    {
        int ports = _player.Sequencer.Sequence is { } song ? PanelState.PortsPlayed(song) : 1;
        PartCount = ports * 16;
        if (Parts.Count == ports * 16) return;

        Parts.Clear();
        for (int port = 0; port < ports; port++)
            for (int channel = 0; channel < 16; channel++)
                Parts.Add(new PartRowViewModel(port, channel, Panel.Part(port, channel)));
    }

    /// <summary>
    /// How long the loaded song is, for the seek bar and the clock beside it; zero when
    /// stopped.
    /// </summary>
    private double SongDurationMs()
        => _player.Sequencer.State != PlaybackState.Stopped
           && _player.Sequencer.Sequence is { } song
            ? song.DurationUs / 1000.0
            : 0;

    /// <summary>
    /// The transport has come to rest: says why when it was not asked to, clears what
    /// belonged to the song, and lets the outputs go.
    /// </summary>
    /// <remarks>
    /// An output that stopped taking messages is let go rather than reopened: reopening
    /// while it may still be going away only fails again. The next song opens what it needs
    /// afresh.
    /// </remarks>
    private void OnTransportStopped(PlaybackStop stop)
    {
        switch (stop.Cause)
        {
            case StopCause.Faulted:
                Note(string.Format(Strings.NoteFaulted, stop.Fault));
                break;
            case StopCause.DeviceLost:
                Note(string.Format(Strings.NoteDeviceLost, stop.Problem));
                break;
            case StopCause.Failed:
                Note(string.Format(Strings.NoteSwitchFailed, stop.Problem));
                break;
        }

        ShowStopped();
        ReleaseOutputs();
    }

    private void OnCurrentChanged(PlaylistItem? item)
    {
        NowPlaying = Title(item);
        foreach (PlaylistTabViewModel tab in Playlists) tab.Refresh();
        BuildParts();
    }

    /// <summary>
    /// Says something, in the debug window.
    /// </summary>
    private void Note(string line)
    {
        Messages.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (Messages.Count > 500) Messages.RemoveAt(0);
    }

    public void Dispose()
    {
        _timer.Stop();
        _saveTimer.Stop();
        SaveChanges(closing: true);
        _player.Dispose();
    }
}

public sealed partial class PlaylistItemViewModel(PlaylistItem item, Func<PlaylistItem, string> label)
    : ViewModelBase
{
    public PlaylistItem Item { get; } = item;

    /// <summary>What the row shows for the song: its title, file name or path, as the list is set.</summary>
    public string Display => label(Item);

    public string Path => Item.Path;

    public string Length => Item.DurationMs > 0
        ? TimeSpan.FromMilliseconds(Item.DurationMs).ToString(@"mm\:ss")
        : "--:--";

    /// <summary>The song's own module, or empty when it is left to the detector.</summary>
    public string Module
    {
        get => Item.Module;
        set
        {
            if (Item.Module == value) return;
            Item.Module = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModuleText));
        }
    }

    /// <summary>
    /// What the list shows in the module column.
    /// </summary>
    /// <remarks>
    /// In brackets when worked out, bare when chosen by hand. Blank until detection has had
    /// its turn.
    /// </remarks>
    public string ModuleText => Item.Module.Length > 0
        ? Item.Module
        : _detected is { } found ? $"({found})" : string.Empty;

    /// <summary>
    /// The module the column names, without the brackets, for sorting by; empty while
    /// detection has not had its turn.
    /// </summary>
    public string SortModule => Item.Module.Length > 0 ? Item.Module : _detected ?? string.Empty;

    /// <summary>Whether detection has run for this row since the definition last changed.</summary>
    public bool Detected => _detected is not null;

    public void Detect(string module)
    {
        if (_detected == module) return;
        _detected = module;
        OnPropertyChanged(nameof(Detected));
        OnPropertyChanged(nameof(ModuleText));
    }

    /// <summary>Drops the detected answer, for when what it was worked out from has changed.</summary>
    public void Forget()
    {
        if (_detected is null) return;
        _detected = null;
        OnPropertyChanged(nameof(Detected));
        OnPropertyChanged(nameof(ModuleText));
    }

    private string? _detected;

    /// <summary>
    /// True for the row its list is playing or last played
    /// (<see cref="PlaylistTabViewModel.LastPlayedRow"/>).
    /// </summary>
    [ObservableProperty]
    public partial bool IsLastPlayed { get; set; }

    /// <summary>
    /// The playing mark, as text rather than a shown-and-hidden glyph.
    /// </summary>
    /// <remarks>
    /// The column keeps its width either way, so the titles beside it stay where they are as
    /// the mark moves down the list.
    /// </remarks>
    public string Marker => IsLastPlayed ? "▶" : string.Empty;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(Length));
    }

    partial void OnIsLastPlayedChanged(bool value) => OnPropertyChanged(nameof(Marker));
}
