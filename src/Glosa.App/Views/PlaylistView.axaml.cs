using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Glosa.App.Services;
using Glosa.App.ViewModels;
using Glosa.Core.Archives;

namespace Glosa.App.Views;

public partial class PlaylistView : UserControl
{
    public PlaylistView()
    {
        AvaloniaXamlLoader.Load(this);
        _lists = this.FindControl<Panel>("Lists")!;
        _lists.AddHandler(KeyDownEvent, OnListsEnter, RoutingStrategies.Tunnel);
        FileDrop.Attach(this, () => Model);

        var tabs = this.FindControl<TabControl>("Tabs")!;
        TabDrag.Attach(tabs,
            (item, gap) => item is PlaylistTabViewModel tab && Model?.MovePlaylist(tab, gap) == true);
        tabs.TemplateApplied += (_, e) =>
            e.NameScope.Find<ScrollViewer>("TabScroll")?.AddHandler(
                PointerWheelChangedEvent, OnTabsWheel, RoutingStrategies.Tunnel);
        tabs.SelectionChanged += (_, _) => RevealSelectedTab(tabs);
    }

    /// <summary>Turns the wheel into sideways scrolling of the tab strip, which has no bar.</summary>
    private static void OnTabsWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer strip) return;

        double delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
        strip.Offset = strip.Offset.WithX(strip.Offset.X - delta * 50);
        e.Handled = true;
    }

    /// <summary>
    /// Scrolls the tab strip to the tab in front, a new one included.
    /// </summary>
    /// <remarks>Posted, so a tab just added has been laid out.</remarks>
    private static void RevealSelectedTab(TabControl tabs)
        => Avalonia.Threading.Dispatcher.UIThread.Post(
            () => { if (tabs.SelectedItem is { } tab) tabs.ContainerFromItem(tab)?.BringIntoView(); },
            Avalonia.Threading.DispatcherPriority.Background);

    private MainViewModel? Model => DataContext as MainViewModel;

    private MainViewModel? _watched;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_watched is { } old)
        {
            old.PlayingRowMoved -= OnPlayingRowMoved;
            old.Playlists.CollectionChanged -= OnPlaylistsChanged;
            old.PropertyChanged -= OnModelChanged;
        }

        _watched = Model;
        if (_watched is { } model)
        {
            model.PlayingRowMoved += OnPlayingRowMoved;
            model.Playlists.CollectionChanged += OnPlaylistsChanged;
            model.PropertyChanged += OnModelChanged;
        }

        SyncLists();
    }

    private void OnPlaylistsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncLists();

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedPlaylist)) ShowSelected();
    }

    private readonly Panel _lists;

    private readonly Dictionary<PlaylistTabViewModel, ListBox> _listOf = [];

    /// <summary>Lists not yet put back where their tab was left (<see cref="Shown"/>).</summary>
    private readonly HashSet<ListBox> _unplaced = [];

    /// <summary>
    /// Gives each open tab a list box of its own, and shows the selected tab's.
    /// </summary>
    /// <remarks>
    /// One per tab, so a list keeps its scroll and selection while another tab is in front.
    /// Kept by tab rather than bound to the tabs: a bound list rebuilds a tab's list box when
    /// the tab is dragged along the strip.
    /// </remarks>
    private void SyncLists()
    {
        IList<PlaylistTabViewModel> tabs = Model?.Playlists ?? [];

        foreach (PlaylistTabViewModel gone in _listOf.Keys.Except(tabs).ToList())
        {
            _lists.Children.Remove(_listOf[gone]);
            _unplaced.Remove(_listOf[gone]);
            _listOf.Remove(gone);
        }

        foreach (PlaylistTabViewModel tab in tabs)
            if (!_listOf.ContainsKey(tab)) _lists.Children.Add(_listOf[tab] = CreateList(tab));

        ShowSelected();
    }

    private ListBox CreateList(PlaylistTabViewModel tab)
    {
        var list = (ListBox)((IDataTemplate)Resources["PlaylistList"]!).Build(tab)!;
        list.DataContext = tab;
        list.IsVisible = false;
        _unplaced.Add(list);

        list.AddHandler(ScrollViewer.ScrollChangedEvent, OnListScrolled);
        list.Loaded += OnListLoaded;
        ReorderDrag.Attach(list,
            press => Press(list, press),
            () => Begin(list),
            target => MoveTo(list, target),
            () => Model?.ReorderFinished());

        return list;
    }

    private void ShowSelected()
    {
        foreach ((PlaylistTabViewModel tab, ListBox list) in _listOf)
        {
            bool shown = tab == Model?.SelectedPlaylist;
            if (list.IsVisible == shown) continue;

            list.IsVisible = shown;
            if (shown && list.IsLoaded) Shown(list);
        }
    }

    private void OnListLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is ListBox { IsVisible: true } list) Shown(list);
    }

    /// <summary>
    /// Brings a list that has come on screen up to date.
    /// </summary>
    /// <remarks>
    /// The first time, it is put where its tab was left (<see cref="RestoreScroll"/>): a hidden
    /// list is not laid out, so it cannot be scrolled before. After that it keeps its own
    /// scroll, and only a song that started while it was out of sight is brought on screen.
    /// </remarks>
    private void Shown(ListBox list)
    {
        if (_unplaced.Remove(list)) RestoreScroll(list);
        else RevealLastPlayed(list);
    }

    private void OnPlayingRowMoved()
    {
        foreach (ListBox list in _listOf.Values)
            if (list.IsVisible && !_unplaced.Contains(list)) RevealLastPlayed(list);
    }

    /// <summary>
    /// Scrolls a list just far enough to show the row with the playing mark, when that is off
    /// screen.
    /// </summary>
    /// <remarks>
    /// Only for the list on screen. A list in another tab keeps the request until it is shown
    /// (<see cref="Shown"/>). Posted so the rows are laid out first.
    /// </remarks>
    private static void RevealLastPlayed(ListBox list)
    {
        if (list.DataContext is not PlaylistTabViewModel { RevealsLastPlayed: true } tab) return;
        tab.RevealsLastPlayed = false;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (tab.LastPlayedRow is { } row && tab.Items.Contains(row)) list.ScrollIntoView(row);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Plays the rows double-clicked on.</summary>
    /// <remarks>
    /// Only on a row: the list takes the double-click wherever it lands, on its scroll bar and
    /// the space below the last row too.
    /// </remarks>
    private void OnPlaySelected(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null) return;
        Model?.PlaySelectedCommand.Execute(null);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (!Shortcuts.IsDelete(e.Key) || sender is not ListBox list) return;

        Remove(list);
        e.Handled = true;
    }

    /// <summary>Enter plays the selected song, as a double-click does.</summary>
    /// <remarks>
    /// Tunnelling: Enter comes back from inside the list already handled, so a handler on the
    /// list never hears it, as it does Delete.
    /// </remarks>
    private void OnListsEnter(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBox>(includeSelf: true) is not { SelectedItem: not null })
            return;

        Model?.PlaySelectedCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// The list a context menu was last opened over.
    /// </summary>
    /// <remarks>
    /// Remembered rather than looked up when the item is clicked: a menu item's data context
    /// is the tab, not the list, and the menu lives in a popup of its own, so nothing in the
    /// tree leads from the item back to the list.
    /// </remarks>
    private ListBox? _menuList;

    private void OnListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not ListBox list) return;
        _menuList = list;
        ListSelection.ForMenu(list, e);

        PlaylistItemViewModel[] rows = Selected(list);
        if (Item(list, "Modules") is { } modules) FillModules(modules, rows);
        if (Item(list, "CopyTo") is { } copy) FillTabs(copy, rows, move: false);
        if (Item(list, "MoveTo") is { } move) FillTabs(move, rows, move: true);
        if (Item(list, "CopyPath") is { } copyPath) copyPath.IsEnabled = rows.Length > 0;
        if (Item(list, "Show") is { } show) MarkShown(show);
    }

    /// <summary>
    /// Puts the selected songs' paths on the clipboard, one per line, in list order.
    /// </summary>
    /// <remarks>
    /// A song inside an archive has the path the list knows it by, the archive's own path
    /// with the entry after it: there is no file of its own to point at.
    /// </remarks>
    private void OnCopyPaths(object? sender, RoutedEventArgs e)
    {
        if (_menuList is not { } list || list.DataContext is not PlaylistTabViewModel tab) return;

        string text = string.Join(Environment.NewLine,
            Selected(list).OrderBy(tab.Items.IndexOf).Select(row => row.Path));
        if (text.Length == 0 || TopLevel.GetTopLevel(list)?.Clipboard is not { } clipboard) return;

        _ = clipboard.SetTextAsync(text);
    }

    /// <summary>
    /// Shows the first selected song in the file manager, or the archive it is in.
    /// </summary>
    /// <remarks>
    /// One song, not each of them: a window per song would bury the player. A file that is
    /// gone still has its folder opened, if that is there.
    /// </remarks>
    private async void OnShowInFileManager(object? sender, RoutedEventArgs e)
    {
        if (_menuList is not { } list || list.DataContext is not PlaylistTabViewModel tab) return;
        if (Selected(list).OrderBy(tab.Items.IndexOf).FirstOrDefault() is not { } row) return;

        string file = SongStore.FileOf(row.Path);
        try
        {
            if (File.Exists(file) && await FileManager.ShowAsync(file)) return;
            if (Path.GetDirectoryName(file) is { } folder && Directory.Exists(folder)
                && TopLevel.GetTopLevel(list) is { } top
                && await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)))
                return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        Model?.Complain(string.Format(Strings.NoteCannotShowFile, file));
    }

    private void OnSort(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is SongSort by) Model?.SortItems(by);
    }

    /// <summary>Marks the line of the Show submenu for what the lists show now.</summary>
    private void MarkShown(MenuItem parent)
    {
        foreach (MenuItem item in parent.Items.OfType<MenuItem>())
            item.IsChecked = item.Tag is SongLabel label && label == Model?.SongLabel;
    }

    private void OnShow(object? sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is SongLabel label && Model is { } model) model.SongLabel = label;
    }

    private static MenuItem? Item(ListBox list, string name)
        => list.ContextMenu?.Items.OfType<MenuItem>().FirstOrDefault(i => i.Name == name);

    /// <summary>
    /// Puts the other open lists under the copy-to and move-to items.
    /// </summary>
    /// <remarks>
    /// Built as the menu opens: tabs come and go, and the rows' own tab is left out. With
    /// nowhere to send them the item is greyed.
    /// </remarks>
    private void FillTabs(MenuItem parent, PlaylistItemViewModel[] rows, bool move)
    {
        if (Model is not { } model) { parent.IsEnabled = false; return; }

        PlaylistTabViewModel[] others =
            [.. model.Playlists.Where(tab => tab != model.SelectedPlaylist)];

        parent.ItemsSource = others.Select(tab => TabItem(tab, rows, move)).ToList();
        parent.IsEnabled = rows.Length > 0 && others.Length > 0;
    }

    private MenuItem TabItem(PlaylistTabViewModel tab, PlaylistItemViewModel[] rows, bool move)
    {
        // A menu item reads an underscore as the mark for an access key, and a playlist
        // is named by whoever made it. Doubling one shows one.
        var item = new MenuItem
        {
            Header = tab.Name.Replace("_", "__"),
            Icon = this.FindResource("IconPlaylist"),
        };

        item.Click += (_, _) => Model?.CopyItems(rows, tab, move);
        return item;
    }

    /// <summary>
    /// Puts the definition's modules under the Target Module item, ticking what the rows are
    /// set to.
    /// </summary>
    /// <remarks>
    /// Built each time the menu opens rather than bound: the definition can be swapped while
    /// the player runs, and the mark depends on the rows. A mark means every selected row
    /// says so; a mixed selection shows none.
    /// </remarks>
    private void FillModules(MenuItem parent, PlaylistItemViewModel[] rows)
    {
        if (Model is not { } model) return;

        var items = new List<object> { ModuleItem(MainViewModel.AutoDetect, string.Empty, rows) };
        if (model.TargetModules.Count > 0) items.Add(new Separator());
        items.AddRange(model.TargetModules.Select(name => ModuleItem(name, name, rows)));

        parent.ItemsSource = items;
        parent.IsEnabled = rows.Length > 0;
    }

    private MenuItem ModuleItem(string label, string module, PlaylistItemViewModel[] rows)
    {
        var item = new MenuItem
        {
            Header = label,
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = rows.Length > 0 && rows.All(row => row.Module == module),
        };

        item.Click += (_, _) => Model?.SetModule(rows, module);
        return item;
    }

    private void OnRemoveSelected(object? sender, RoutedEventArgs e)
    {
        if (_menuList is { } list) Remove(list);
    }

    /// <summary>
    /// Writes the top row into the tab it belongs to, as it moves.
    /// </summary>
    /// <remarks>
    /// Written as it happens rather than on the way out, when the list box may be gone. Not
    /// before the list is put back: its first layout starts at the top.
    /// </remarks>
    private void OnListScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ListBox list || _unplaced.Contains(list)) return;
        if (list.DataContext is not PlaylistTabViewModel tab) return;
        if (RowHeight(list) is not { } height) return;

        tab.TopRow = (int)Math.Round(list.Scroll?.Offset.Y / height ?? 0);
    }

    /// <summary>
    /// Puts a list back where it was left, or on the song it was last playing.
    /// </summary>
    /// <remarks>
    /// The last-played song wins the first time only
    /// (<see cref="PlaylistTabViewModel.OpensOnLastPlayed"/>).
    ///
    /// Posted: the rows are not laid out yet when this runs, and the selected row pulls the
    /// view to itself on the way past, so the scroll has to be put back afterwards. A song
    /// that started while the list was out of sight is then brought on screen.
    /// </remarks>
    private static void RestoreScroll(ListBox list)
    {
        if (list.DataContext is not PlaylistTabViewModel tab) return;

        int row = tab.OpensOnLastPlayed ? tab.LastPlayed : tab.TopRow;
        tab.OpensOnLastPlayed = false;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (row > 0 && RowHeight(list) is { } height && list.Scroll is { } scroll)
                scroll.Offset = new Vector(scroll.Offset.X, row * height);

            RevealLastPlayed(list);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// How tall one row is, from whichever row happens to be on screen.
    /// </summary>
    /// <remarks>
    /// Every row is the same height. Null while none is laid out.
    /// </remarks>
    private static double? RowHeight(ListBox list)
    {
        foreach (Control row in list.GetRealizedContainers())
            if (row.Bounds.Height > 0) return row.Bounds.Height;

        return null;
    }

    /// <summary>The rows a list box has selected, as the view model wants them.</summary>
    private static PlaylistItemViewModel[] Selected(ListBox list)
        => [.. list.SelectedItems?.OfType<PlaylistItemViewModel>() ?? []];

    /// <summary>The selection as it stood when the button went down, and the row it hit.</summary>
    /// <remarks>
    /// Taken at the press because a plain click on one of several selected rows collapses
    /// the selection to that row a moment later, and dragging a block needs the block.
    /// </remarks>
    private PlaylistItemViewModel[] _pressedRows = [];

    private PlaylistItemViewModel? _pressedRow;

    private PlaylistItemViewModel[]? _dragRows;

    private bool Press(ListBox list, PointerPressedEventArgs e)
    {
        // Which rows travel is settled when the drag starts, not here: the list box is still
        // working out what this press does to the selection.
        if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>() is not { } item) return false;

        _pressedRows = [.. list.SelectedItems?.OfType<PlaylistItemViewModel>() ?? []];
        _pressedRow = item.DataContext as PlaylistItemViewModel;
        _dragRows = null;
        return true;
    }

    private bool Begin(ListBox list)
    {
        // Pressing inside a block drags the block; pressing outside one drags the row the
        // press landed on, which is all that is selected by now anyway.
        _dragRows = _pressedRow is not null && _pressedRows.Contains(_pressedRow)
            ? _pressedRows
            : [.. list.SelectedItems?.OfType<PlaylistItemViewModel>() ?? []];

        return _dragRows.Length > 0;
    }

    /// <summary>
    /// Puts the rows into the gap they were dropped in.
    /// </summary>
    /// <remarks>
    /// Moving a row takes it out of the collection, which drops it from the selection, so
    /// the selection is put back — the rows are still the ones being worked on.
    /// </remarks>
    private bool MoveTo(ListBox list, int target)
    {
        if (_dragRows is null || Model is null) return false;
        if (!Model.MoveItems(_dragRows, target)) return false;

        list.SelectedItems?.Clear();
        foreach (PlaylistItemViewModel row in _dragRows) list.SelectedItems?.Add(row);
        return true;
    }

    /// <summary>
    /// Hands the list box's own selection to the view model.
    /// </summary>
    /// <remarks>
    /// Read here rather than bound: the tab tracks only the one row a double-click plays.
    /// </remarks>
    private void Remove(ListBox list) => Model?.RemoveItems(Selected(list));

    private void OnNewPlaylist(object? sender, RoutedEventArgs e)
        => Model?.NewPlaylistCommand.Execute(null);

    /// <summary>
    /// The tab a menu item belongs to: the one that was right-clicked, not the one in front.
    /// </summary>
    /// <remarks>
    /// The menu is declared inside the tab's own template, so the item inherits the tab as
    /// its data context. That is the only thread back: the menu is drawn in a popup of its
    /// own, and its placement target does not lead to the tab.
    /// </remarks>
    private static PlaylistTabViewModel? TabOf(object? sender)
        => (sender as Control)?.DataContext as PlaylistTabViewModel;

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) Model?.ClosePlaylistCommand.Execute(tab);
    }

    private void OnDuplicatePlaylist(object? sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is { } tab) Model?.DuplicatePlaylistCommand.Execute(tab);
    }

    private async void OnSavePlaylist(object? sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is not { } tab) return;

        string? path = await Dialogs.SaveAsync(
            this, Strings.SavePlaylist, $"{tab.Name}.yaml", Dialogs.PlaylistFiles);

        if (path is not null) Model?.SavePlaylist(tab, path);
    }

    private async void OnExportM3u(object? sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is not { } tab) return;

        string? path = await Dialogs.SaveAsync(
            this, Strings.ExportM3u, $"{tab.Name}.m3u8", Dialogs.M3uFiles);

        if (path is not null) Model?.ExportM3u(tab, path);
    }

    private async void OnRenamePlaylist(object? sender, RoutedEventArgs e)
    {
        if (TabOf(sender) is not { } tab) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        string? name = await PromptWindow.AskAsync(
            owner, Strings.RenamePlaylist, Strings.RenamePlaylistPrompt, tab.Name);

        if (name is not null) tab.Name = name;
    }
}
