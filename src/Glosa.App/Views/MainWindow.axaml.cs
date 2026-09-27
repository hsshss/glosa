using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Glosa.App.Services;
using Glosa.App.ViewModels;

namespace Glosa.App.Views;

public partial class MainWindow : Window
{
    /// <summary>
    /// The panes that can be shown in a frame of their own, by name, with the size each
    /// opens at until it has been sized by hand.
    /// </summary>
    private static readonly (string Key, string Title, Size Size, Func<Control> Build)[] Panes =
    [
        ("monitor", Strings.PaneMonitor, new Size(705, 411), () => new MonitorView()),
        ("debug", Strings.PaneDebug, new Size(660, 440), () => new DebugView()),
    ];

    private readonly Dictionary<string, Window> _panes = [];
    private SettingsWindow? _settings;
    private PortMapWindow? _portMaps;

    /// <summary>
    /// True once the player is on its way out.
    /// </summary>
    /// <remarks>
    /// A pane closing because the player is closing should open again next time; one closed
    /// by hand should not. The event is the same either way.
    /// </remarks>
    private bool _leaving;

    public MainWindow()
    {
        InitializeComponent();

        Placement.Attach(this, "main", () => Model?.Settings);
        FileDrop.Attach(this, () => Model);
        AddHandler(KeyDownEvent, OnSpaceDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnSpaceUp, RoutingStrategies.Tunnel);
        // A key let go in another window never comes back here.
        Deactivated += (_, _) => _spaceHeld = false;

        // Opened, not the constructor: the view model is handed over after this runs.
        Opened += (_, _) =>
        {
            // The dialog needs a window to sit over, and this is the one that has it.
            if (Model is { } model)
            {
                model.ShowError = (title, body) => _ = MessageWindow.ShowAsync(this, title, body);
                MediaKeys.Attach(this, model);
                StayAwake.Attach(this, model);
            }

            ReopenPanes();
        };

        // Closing, not Closed: a pane can be shut by the platform on its owner's way out,
        // and that happens before this window's own Closed would have said we were leaving.
        Closing += (_, _) => _leaving = true;

        Closed += (_, _) =>
        {
            foreach (Window pane in _panes.Values.ToArray()) pane.Close();
            _settings?.Close();
            _portMaps?.Close();

            // Every window has written its placement down by now: the panes and the two
            // settings windows as they were closed just above, this one on its own Closing.
            (DataContext as MainViewModel)?.Dispose();
        };
    }

    /// <summary>
    /// Opens the panes that were open when the player was last closed.
    /// </summary>
    /// <remarks>
    /// The main window is brought back to the front afterwards: showing a pane puts it
    /// there, which is what opening one by hand should do and not what starting up should.
    /// Posted rather than called, because the panes are still being brought up around us
    /// and whichever of them lands last would otherwise take the front back.
    /// </remarks>
    private void ReopenPanes()
    {
        if (Model?.Settings is not { } settings) return;

        bool any = false;
        foreach ((string key, _, _, _) in Panes)
        {
            if (settings.Windows.GetValueOrDefault(key) is not { Open: true }) continue;
            OpenPane(key);
            any = true;
        }

        if (any) Dispatcher.UIThread.Post(Activate, DispatcherPriority.Background);
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    /// <summary>Whether the space bar is held, so its key repeat does not toggle again.</summary>
    private bool _spaceHeld;

    /// <summary>
    /// The space bar plays and pauses from anywhere in the window but a text box.
    /// </summary>
    /// <remarks>
    /// Tunnelling, so a focused button or list does not take the key first.
    /// </remarks>
    private void OnSpaceDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || e.Source is TextBox) return;

        if (!_spaceHeld) Model?.PlayOrPause();
        _spaceHeld = true;
        e.Handled = true;
    }

    private void OnSpaceUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_spaceHeld) return;

        _spaceHeld = false;
        e.Handled = true;
    }

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files =
            await Dialogs.OpenAsync(this, Strings.PickSongs, Dialogs.MidiFiles, multiple: true);
        // An archive picked here opens up into the songs inside it.
        Model?.AddFiles(Glosa.Core.Playback.SongFiles.Expand(files));
    }

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> folders = await Dialogs.OpenFoldersAsync(this, Strings.PickFolder);
        // Walked in full, archives and subfolders included, as a dropped folder is.
        Model?.AddFiles(Glosa.Core.Playback.SongFiles.Expand(folders));
    }

    /// <summary>
    /// Holds a seek back until the thumb is let go.
    /// </summary>
    /// <remarks>
    /// Seeking to every point a drag passes over would push that much of the song down the
    /// cable, so the drag moves only the reading and the seek happens once, on release.
    ///
    /// Told from the pointer rather than from the value, which the display tick writes too.
    /// Tunnelling, because the slider handles the press itself.
    /// </remarks>
    private void OnSeekBarLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Slider bar) return;

        // Removed first: the handler would otherwise be added again on every reload.
        bar.RemoveHandler(PointerPressedEvent, OnSeekPressed);
        bar.RemoveHandler(PointerReleasedEvent, OnSeekReleased);
        bar.RemoveHandler(PointerCaptureLostEvent, OnSeekCaptureLost);
        bar.RemoveHandler(KeyDownEvent, OnSeekKeyDown);
        bar.RemoveHandler(KeyUpEvent, OnSeekKeyUp);

        bar.AddHandler(PointerPressedEvent, OnSeekPressed, RoutingStrategies.Tunnel);
        bar.AddHandler(PointerReleasedEvent, OnSeekReleased, RoutingStrategies.Tunnel);
        bar.AddHandler(PointerCaptureLostEvent, OnSeekCaptureLost, RoutingStrategies.Tunnel);
        bar.AddHandler(KeyDownEvent, OnSeekKeyDown, RoutingStrategies.Tunnel);
        bar.AddHandler(KeyUpEvent, OnSeekKeyUp, RoutingStrategies.Tunnel);
    }

    private void OnSeekPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Model is { } model) model.Scrubbing = true;
    }

    private void OnSeekReleased(object? sender, PointerReleasedEventArgs e) => TakeSeek();

    /// <summary>The drag ending some other way than by letting go, which counts the same.</summary>
    private void OnSeekCaptureLost(object? sender, PointerCaptureLostEventArgs e) => TakeSeek();

    /// <summary>
    /// Moves the seek bar with the arrow keys, which also has to end in a seek.
    /// </summary>
    /// <remarks>
    /// The slider moves its own value on a key; without this the tick would simply put the
    /// thumb back where playback is, and the key would look broken.
    /// </remarks>
    private void OnSeekKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is { } model) model.Scrubbing = true;
    }

    private void OnSeekKeyUp(object? sender, KeyEventArgs e) => TakeSeek();

    private void TakeSeek()
    {
        if (Model is not { Scrubbing: true } model) return;

        model.Scrubbing = false;
        model.SeekToThumb();
    }

    private async void OnOpenPlaylist(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files =
            await Dialogs.OpenAsync(this, Strings.PickPlaylist, Dialogs.OpenablePlaylistFiles);
        if (files.Count > 0) Model?.LoadPlaylist(files[0]);
    }

    private async void OnOpenDefinition(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> files =
            await Dialogs.OpenAsync(this, Strings.PickDefinition, Dialogs.DefinitionFiles);
        if (files.Count > 0) Model?.LoadDefinition(files[0]);
    }

    private void OnCloseDefinition(object? sender, RoutedEventArgs e) => Model?.CloseDefinition();

    private void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        if (_settings is not null) { _settings.Activate(); return; }

        _settings = new SettingsWindow { DataContext = Model };
        Placement.Attach(_settings, "settings", () => Model?.Settings);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show(this);
    }

    /// <summary>
    /// Shows the folder the settings and playlists are kept in, in the system's file manager.
    /// </summary>
    /// <remarks>
    /// Made first if nothing has been saved yet, so there is a folder to show. The launcher is
    /// Avalonia's, which asks each platform's own shell.
    /// </remarks>
    private async void OnOpenConfigFolder(object? sender, RoutedEventArgs e)
    {
        string folder = AppSettings.ConfigDirectory;
        try
        {
            Directory.CreateDirectory(folder);
            if (await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder))) return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        Model?.Complain(string.Format(Strings.NoteCannotOpenConfigFolder, folder));
    }

    /// <summary>
    /// Opens the port mapper, showing the map in use.
    /// </summary>
    /// <remarks>
    /// It opens on whichever map the player is on rather than wherever it was left: the map
    /// in use is what someone coming here has a question about.
    /// </remarks>
    private void OnOpenPortMaps(object? sender, RoutedEventArgs e)
    {
        if (Model is null) return;

        Model.RefreshDevices();
        Model.EditPortMaps();
        if (_portMaps is not null) { _portMaps.Activate(); return; }

        _portMaps = new PortMapWindow { DataContext = Model };
        Placement.Attach(_portMaps, "portMaps", () => Model?.Settings);
        _portMaps.Closed += (_, _) => _portMaps = null;
        _portMaps.Show(this);
    }

    private async void OnOpenAbout(object? sender, RoutedEventArgs e) => await new AboutWindow().ShowDialog(this);

    private void OnOpenMonitor(object? sender, RoutedEventArgs e) => OpenPane("monitor");

    private void OnOpenDebug(object? sender, RoutedEventArgs e) => OpenPane("debug");

    /// <summary>
    /// Shows a pane in its own frame, or brings the one already open to the front.
    /// </summary>
    /// <remarks>
    /// The frame gets a fresh view over the same view model, which holds all the state a
    /// pane shows.
    /// </remarks>
    private void OpenPane(string key)
    {
        if (Model is null) return;

        if (_panes.TryGetValue(key, out Window? open)) { open.Activate(); return; }

        (_, string title, Size size, Func<Control> build) = Panes.First(pane => pane.Key == key);
        var window = new PaneWindow(title, build(), Model) { Width = size.Width, Height = size.Height };
        Placement.Attach(window, key, () => Model?.Settings);
        window.Closed += (_, _) =>
        {
            _panes.Remove(key);
            if (!_leaving) Placement.SetOpen(key, Model?.Settings, false);
        };
        _panes[key] = window;
        window.Show(this);
        Placement.SetOpen(key, Model.Settings, true);
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();
}
