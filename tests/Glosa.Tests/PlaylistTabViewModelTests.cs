using Glosa.App.ViewModels;
using Glosa.Core.Playback;

namespace Glosa.Tests;

public class PlaylistTabViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), $"glosa-tab-{Guid.NewGuid():N}");

    public PlaylistTabViewModelTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string ListPath => Path.Combine(_folder, "list.yaml");

    private PlaylistTabViewModel Tab(params string[] paths)
        => new(new Playlist { Name = "Mix", Items = [.. paths.Select(p => new PlaylistItem { Path = p })] },
               ListPath, item => item.Path);

    private static string[] Paths(PlaylistTabViewModel tab) => [.. tab.Items.Select(r => r.Path)];

    [Fact]
    public void ANewListHasChangesUntilSaved()
    {
        PlaylistTabViewModel tab = Tab("a.mid");
        Assert.True(tab.HasChanges);

        tab.Save();

        Assert.False(tab.HasChanges);
        Assert.True(File.Exists(ListPath));
    }

    [Fact]
    public void AListReadFromItsFileHasNoChangesUntilOneIsMade()
    {
        PlaylistFile.Save(new Playlist { Name = "Mix", Items = [new PlaylistItem { Path = "a.mid" }] }, ListPath);
        var tab = new PlaylistTabViewModel(PlaylistFile.Load(ListPath), ListPath, item => item.Path,
                                           fromFile: true);
        Assert.False(tab.HasChanges);

        tab.List.Items[0].DurationMs = 1000;
        Assert.True(tab.HasChanges);

        tab.List.Items[0].DurationMs = 0;
        Assert.False(tab.HasChanges);

        tab.Name = "Other";
        Assert.True(tab.HasChanges);
    }

    [Fact]
    public void SavingElsewhereFollowsTheList()
    {
        PlaylistTabViewModel tab = Tab("a.mid");
        string other = Path.Combine(_folder, "other.yaml");

        tab.Save(other);

        Assert.Equal(other, tab.FilePath);
        Assert.False(File.Exists(ListPath));
    }

    [Fact]
    public void ASaveThatFailsLeavesTheListUnsaved()
    {
        PlaylistTabViewModel tab = Tab("a.mid");
        string blocked = Path.Combine(_folder, "blocked");
        Directory.CreateDirectory(blocked);

        Exception? thrown = Record.Exception(() => tab.Save(blocked));

        Assert.True(thrown is IOException or UnauthorizedAccessException, thrown?.ToString());

        Assert.True(tab.HasChanges);
        Assert.Equal(ListPath, tab.FilePath);
    }

    [Theory]
    // Rows 1 and 3 into the gap above row 0.
    [InlineData(new[] { 1, 3 }, 0, "b d a c e")]
    // Into the gap below the last row.
    [InlineData(new[] { 0, 2 }, 5, "b d e a c")]
    // Into a gap between them: they land together where the gap was.
    [InlineData(new[] { 0, 4 }, 2, "b a e c d")]
    public void MovingRowsPutsThemIntoTheGapInTheirOwnOrder(int[] rows, int gap, string expected)
    {
        PlaylistTabViewModel tab = Tab("a", "b", "c", "d", "e");

        Assert.True(tab.Move([.. rows.Reverse().Select(i => tab.Items[i])], gap));

        Assert.Equal(expected.Split(' '), Paths(tab));
        Assert.Equal(expected.Split(' '), tab.List.Items.Select(i => i.Path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void RowsAlreadyAtTheGapDoNotMove(int gap)
    {
        PlaylistTabViewModel tab = Tab("a", "b", "c", "d");

        Assert.False(tab.Move([tab.Items[1], tab.Items[2]], gap));
        Assert.Equal(["a", "b", "c", "d"], Paths(tab));
    }

    [Fact]
    public void SortingByLengthPutsSongsWithNoneLastAndKeepsTiesInOrder()
    {
        PlaylistTabViewModel tab = Tab("a", "b", "c", "d");
        long[] lengths = [0, 2000, 1000, 2000];
        for (int i = 0; i < lengths.Length; i++) tab.List.Items[i].DurationMs = lengths[i];

        Assert.True(tab.Sort(SongSort.Length));
        Assert.Equal(["c", "b", "d", "a"], Paths(tab));

        Assert.True(tab.Sort(SongSort.LengthDescending));
        Assert.Equal(["b", "d", "c", "a"], Paths(tab));
        Assert.Equal(Paths(tab), tab.List.Items.Select(i => i.Path));
    }

    [Fact]
    public void SortingByPathKeepsEachFolderTogether()
    {
        PlaylistTabViewModel tab = Tab(@"Songs2\a.mid", @"Songs\b.mid", @"Songs\a.mid");

        tab.Sort(SongSort.Path);

        Assert.Equal([@"Songs\a.mid", @"Songs\b.mid", @"Songs2\a.mid"], Paths(tab));
    }

    [Fact]
    public void SortingWhatIsInOrderAlreadyMovesNothing()
        => Assert.False(Tab("a", "b").Sort(SongSort.FileName));

    [Fact]
    public void ThePlayingMarkFollowsTheSongItselfNotItsPath()
    {
        PlaylistTabViewModel tab = Tab("a", "a", "b");
        PlaylistItem second = tab.List.Items[1];

        tab.Played(second);

        Assert.Equal(1, tab.LastPlayed);
        Assert.True(tab.Items[1].IsLastPlayed);
        Assert.False(tab.Items[0].IsLastPlayed);
        Assert.True(tab.RevealsLastPlayed);

        tab.Move([tab.Items[2]], 0);
        Assert.Equal(2, tab.LastPlayed);
    }

    [Fact]
    public void ASongFromAnotherListLeavesTheMarkWhereItWas()
    {
        PlaylistTabViewModel tab = Tab("a", "b");
        tab.LastPlayed = 1;

        tab.Played(new PlaylistItem { Path = "a" });

        Assert.Equal(1, tab.LastPlayed);
    }

    [Fact]
    public void RemovingThePlayingRowTakesTheMarkWithIt()
    {
        PlaylistTabViewModel tab = Tab("a", "b");
        tab.LastPlayed = 0;

        tab.Remove(tab.Items[0]);

        Assert.Equal(-1, tab.LastPlayed);
        Assert.Equal(["b"], tab.List.Items.Select(i => i.Path));
    }

    [Fact]
    public void ALastPlayedPastTheEndIsNone()
    {
        PlaylistTabViewModel tab = Tab("a");
        tab.LastPlayed = 5;

        Assert.Equal(-1, tab.LastPlayed);
    }

    [Fact]
    public void TheHeaderMarksTheListPlaying()
    {
        PlaylistTabViewModel tab = Tab();

        tab.IsPlaying = true;

        Assert.Equal("▶ Mix", tab.Header);
    }
}

public class PlaylistItemViewModelTests
{
    private static PlaylistItemViewModel Row(string module = "")
        => new(new PlaylistItem { Path = "a.mid", Module = module }, item => item.Path);

    [Fact]
    public void ADetectedModuleIsShownInBrackets()
    {
        PlaylistItemViewModel row = Row();
        Assert.Equal(string.Empty, row.ModuleText);
        Assert.False(row.Detected);

        row.Detect("SC-88");

        Assert.Equal("(SC-88)", row.ModuleText);
        Assert.Equal("SC-88", row.SortModule);
        Assert.True(row.Detected);
    }

    [Fact]
    public void AModuleChosenByHandIsShownBareOverTheDetectedOne()
    {
        PlaylistItemViewModel row = Row("MU80");

        row.Detect("SC-88");

        Assert.Equal("MU80", row.ModuleText);
        Assert.Equal("MU80", row.SortModule);
    }

    [Fact]
    public void ForgettingTheDetectedModuleBlanksTheColumn()
    {
        PlaylistItemViewModel row = Row();
        row.Detect("SC-88");

        row.Forget();

        Assert.Equal(string.Empty, row.ModuleText);
        Assert.False(row.Detected);
    }

    [Theory]
    [InlineData(0, "--:--")]
    [InlineData(83_000, "01:23")]
    public void TheLengthIsMinutesAndSeconds(long ms, string shown)
    {
        PlaylistItemViewModel row = Row();
        row.Item.DurationMs = ms;

        Assert.Equal(shown, row.Length);
    }
}
