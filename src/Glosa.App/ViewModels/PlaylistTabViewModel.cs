using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Glosa.Core.Playback;

namespace Glosa.App.ViewModels;

/// <summary>
/// One playlist, as a tab.
/// </summary>
/// <remarks>
/// The tab on screen is not necessarily the one playing; <see cref="IsPlaying"/> marks the
/// list playback is working through.
/// </remarks>
public sealed partial class PlaylistTabViewModel : ViewModelBase
{
    private readonly Func<PlaylistItem, string> _label;

    /// <param name="label">What a row shows for a song (<see cref="PlaylistItemViewModel.Display"/>).</param>
    /// <param name="fromFile">
    /// The list was read from <paramref name="filePath"/>, so is saved only once it changes.
    /// A new one is saved at the first chance.
    /// </param>
    public PlaylistTabViewModel(Playlist list, string filePath, Func<PlaylistItem, string> label,
                                bool fromFile = false)
    {
        List = list;
        FilePath = filePath;
        _label = label;
        Name = list.Name;
        foreach (PlaylistItem item in list.Items) Items.Add(new PlaylistItemViewModel(item, label));
        if (fromFile) _saved = Snapshot.Of(list);
    }

    /// <summary>The list itself, which is what the player is handed.</summary>
    public Playlist List { get; }

    /// <summary>Where it is saved. Each playlist is its own file.</summary>
    public string FilePath { get; private set; }

    /// <summary>
    /// Whether the list differs from what its file was last read or written as.
    /// </summary>
    /// <remarks>
    /// Compared value by value rather than as the text it would be written as: this is asked
    /// every few seconds, and a long list is slow to turn into text.
    /// </remarks>
    public bool HasChanges => _saved is not { } saved || !saved.Matches(List);

    /// <summary>
    /// Writes the list to its file, or to <paramref name="path"/>, which becomes its file once
    /// the list is there.
    /// </summary>
    /// <exception cref="IOException">The file could not be written, and the list's file is as it was.</exception>
    /// <exception cref="UnauthorizedAccessException"><inheritdoc cref="IOException" path="/summary"/></exception>
    public void Save(string? path = null)
    {
        PlaylistFile.Save(List, path ?? FilePath);
        FilePath = path ?? FilePath;
        _saved = Snapshot.Of(List);
        SaveProblem = null;
    }

    /// <summary>
    /// Why the last save of the list failed, or null. So a save that keeps failing is told
    /// once rather than on every try.
    /// </summary>
    public string? SaveProblem { get; set; }

    /// <summary>The list as it was last read or written; null when it has been neither.</summary>
    private Snapshot? _saved;

    /// <summary>What is written of a list: its name, and each song's values in order.</summary>
    private sealed record Snapshot(int Version, string Name, Snapshot.Song[] Songs)
    {
        public readonly record struct Song(
            PlaylistItem Item, string Path, string Title, long DurationMs, string Module,
            string? ModuleFromData);

        public static Snapshot Of(Playlist list)
            => new(list.Version, list.Name,
                   [.. list.Items.Select(i => new Song(i, i.Path, i.Title, i.DurationMs, i.Module,
                                                       i.ModuleFromData))]);

        public bool Matches(Playlist list)
        {
            if (list.Version != Version || list.Name != Name || list.Items.Count != Songs.Length)
                return false;

            for (int i = 0; i < Songs.Length; i++)
            {
                PlaylistItem item = list.Items[i];
                if (Songs[i] != new Song(item, item.Path, item.Title, item.DurationMs, item.Module,
                                         item.ModuleFromData))
                    return false;
            }
            return true;
        }
    }

    public ObservableCollection<PlaylistItemViewModel> Items { get; } = [];

    /// <summary>
    /// The row with the playing mark, or null: the transport's cursor on the list being
    /// played (<see cref="StandsOn"/>), where the list was left on any other.
    /// </summary>
    /// <remarks>
    /// Kept per tab, since each list is left somewhere of its own. The row itself rather than
    /// its number, so it stays the same song when rows move above it, and is gone when it is
    /// removed.
    /// </remarks>
    public PlaylistItemViewModel? LastPlayedRow
    {
        get;
        private set
        {
            if (ReferenceEquals(field, value)) return;
            field?.IsLastPlayed = false;
            field = value;
            field?.IsLastPlayed = true;
        }
    }

    /// <summary>Where <see cref="LastPlayedRow"/> is in the list, or -1: what the settings keep.</summary>
    public int LastPlayed
    {
        get => LastPlayedRow is { } row ? Items.IndexOf(row) : -1;
        set => LastPlayedRow = (uint)value < (uint)Items.Count ? Items[value] : null;
    }

    /// <summary>The row at the top of the view, so a long list opens where it was left.</summary>
    public int TopRow { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial PlaylistItemViewModel? Selected { get; set; }

    /// <summary>True for the one list playback is working through.</summary>
    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    /// <summary>Marks the tab so it is obvious which list the transport is walking.</summary>
    public string Header => IsPlaying ? $"▶ {Name}" : Name;

    public void Add(PlaylistItem item)
    {
        List.Items.Add(item);
        Items.Add(new PlaylistItemViewModel(item, _label));
    }

    public void Remove(PlaylistItemViewModel row)
    {
        List.Items.Remove(row.Item);
        Items.Remove(row);
        if (ReferenceEquals(row, LastPlayedRow)) LastPlayedRow = null;
    }

    /// <summary>
    /// Moves rows into the gap at <paramref name="gap"/>, keeping their order among
    /// themselves. Answers whether anything actually moved.
    /// </summary>
    /// <remarks>
    /// A gap, not a row: 0 is above the first row and the count is below the last (see
    /// <c>ReorderDrag.GapAt</c>), so where the rows go does not depend on which way the hand
    /// was moving.
    ///
    /// The gap is counted with the moving rows still in the list, so the landing index is the
    /// gap less however many of them sit above it; the indices are read before anything is
    /// taken out. The two collections are kept in step by hand.
    /// </remarks>
    public bool Move(IReadOnlyList<PlaylistItemViewModel> rows, int gap)
    {
        PlaylistItemViewModel[] moving = [.. rows.OrderBy(Items.IndexOf)];
        if (moving.Length == 0) return false;

        int[] was = [.. moving.Select(Items.IndexOf)];
        int at = gap - was.Count(index => index < gap);

        // Already sitting there, as for most of the pointer moves of a drag.
        bool contiguous = was.Select((index, i) => index - i).Distinct().Count() == 1;
        if (contiguous && at == was[0]) return false;

        foreach (PlaylistItemViewModel row in moving)
        {
            List.Items.Remove(row.Item);
            Items.Remove(row);
        }

        at = Math.Clamp(at, 0, Items.Count);
        for (int i = 0; i < moving.Length; i++)
        {
            List.Items.Insert(at + i, moving[i].Item);
            Items.Insert(at + i, moving[i]);
        }

        return true;
    }

    /// <summary>
    /// Puts the whole list in order by <paramref name="by"/>, first to last. Answers whether
    /// anything moved.
    /// </summary>
    /// <remarks>
    /// Stable, so sorting by one thing and then another leaves each group in the first order.
    /// A song with nothing to sort by — no length yet, no module worked out — goes to the end.
    ///
    /// The rows are put back in one pass rather than moved one at a time, which would be
    /// slow on a long list.
    /// </remarks>
    public bool Sort(SongSort by)
    {
        PlaylistItemViewModel[] sorted = by switch
        {
            SongSort.Title => [.. Items.OrderBy(r => r.Item.Display, StringComparer.CurrentCultureIgnoreCase)],
            SongSort.FileName => [.. Items.OrderBy(r => System.IO.Path.GetFileName(r.Path),
                                                   NameOrder.Comparer)],
            SongSort.Path => [.. Items.OrderBy(r => r.Path, new PathOrder(NameOrder.Comparer))],
            SongSort.Module => [.. Items.OrderBy(r => r.SortModule.Length == 0)
                                        .ThenBy(r => r.SortModule, StringComparer.OrdinalIgnoreCase)],
            SongSort.Length => [.. Items.OrderBy(r => r.Item.DurationMs <= 0)
                                        .ThenBy(r => r.Item.DurationMs)],
            SongSort.LengthDescending => [.. Items.OrderBy(r => r.Item.DurationMs <= 0)
                                                  .ThenByDescending(r => r.Item.DurationMs)],
            _ => [.. Items],
        };
        if (sorted.SequenceEqual(Items)) return false;

        List.Items.Clear();
        List.Items.AddRange(sorted.Select(row => row.Item));
        Items.Clear();
        foreach (PlaylistItemViewModel row in sorted) Items.Add(row);
        return true;
    }

    /// <summary>
    /// Paths folder by folder: everything in one folder together, and before the folders
    /// whose names only start the same way.
    /// </summary>
    /// <remarks>
    /// Compared whole, <c>Songs2\a.mid</c> could come between <c>Songs\a.mid</c> and
    /// <c>Songs\b.mid</c>. Each name is compared by <see cref="NameOrder"/>.
    /// </remarks>
    private sealed class PathOrder(StringComparer names) : IComparer<string>
    {
        private static readonly char[] Separators = ['\\', '/'];

        public int Compare(string? x, string? y)
        {
            string[] a = (x ?? string.Empty).Split(Separators);
            string[] b = (y ?? string.Empty).Split(Separators);
            // No folders-first: an archive stands in a song's path as a folder, and belongs
            // among the files beside it.
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                int c = names.Compare(a[i], b[i]);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }
    }

    public void Refresh()
    {
        foreach (PlaylistItemViewModel row in Items) row.Refresh();
    }

    /// <summary>
    /// Puts the playing mark on the cursor's song, or takes it off when the list does not
    /// have it. By identity: the same file may sit in a list twice.
    /// </summary>
    public void StandsOn(PlaylistItem? item)
    {
        LastPlayedRow = Items.FirstOrDefault(row => ReferenceEquals(row.Item, item));
        RevealsLastPlayed = LastPlayedRow is not null;
    }

    /// <summary>
    /// True until the view has opened this list where it should open.
    /// </summary>
    /// <remarks>
    /// Only the first look after the player starts goes to the song this list was last
    /// playing. After that the list stays wherever it has been scrolled to.
    /// </remarks>
    public bool OpensOnLastPlayed { get; set; }

    /// <summary>
    /// True from a song starting in this list until the view has brought its row on screen.
    /// </summary>
    public bool RevealsLastPlayed { get; set; }

    /// <summary>Puts the cursor back on the row this list was last playing.</summary>
    public void SelectLastPlayed()
    {
        if (LastPlayedRow is not { } row || !Items.Contains(row)) return;

        Selected = row;
        OpensOnLastPlayed = true;
    }

    partial void OnNameChanged(string value)
    {
        List.Name = value;
        OnPropertyChanged(nameof(Header));
    }

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(Header));
}

/// <summary>What a list can be sorted by.</summary>
public enum SongSort
{
    Title,
    FileName,
    Path,
    Module,
    Length,
    LengthDescending,
}
