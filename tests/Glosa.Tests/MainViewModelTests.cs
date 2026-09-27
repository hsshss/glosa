using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Glosa.App;
using Glosa.App.Services;
using Glosa.App.ViewModels;
using Glosa.Core.Playback;

namespace Glosa.Tests;

/// <summary>
/// The player's main view model, started and closed as the window does, over a settings
/// folder of the test's own.
/// </summary>
/// <remarks>
/// Nothing here plays: that would open the outputs. The settings each test starts from have
/// a port map with no outputs, so a first run does not put the machine's first device on it.
/// </remarks>
[Collection(ConfigFolder.Name)]
public class MainViewModelTests : ConfigFolder
{
    public MainViewModelTests()
        => File.WriteAllText(AppSettings.SettingsPath, "version: 1\nportMaps:\n- title: Default\n");

    /// <summary>Starts the player, hands it to <paramref name="test"/>, and closes it.</summary>
    private static void WithPlayer(Action<MainViewModel> test)
        => Headless.Run(() =>
        {
            using var model = new MainViewModel();
            test(model);
        });

    private static bool Said(MainViewModel model, string line)
        => model.Messages.Any(m => m.EndsWith(line, StringComparison.Ordinal));

    private string SavedList(string name, params string[] songs)
    {
        string path = Path.Combine(Folder, $"{name}.yaml");
        PlaylistFile.Save(new Playlist { Name = name, Items = [.. songs.Select(s => new PlaylistItem { Path = s })] },
                          path);
        return path;
    }

    private void OpenOnStart(params string[] lists)
        => File.WriteAllText(AppSettings.SettingsPath,
            "version: 1\nportMaps:\n- title: Default\nopenPlaylists:\n"
            + string.Concat(lists.Select(path => $"- path: '{path}'\n")));

    [Fact]
    public void ChangedSettingsAreWrittenOnCloseAndReadOnTheNextStart()
    {
        WithPlayer(model =>
        {
            model.Repeat = RepeatMode.All;
            model.LoopRepeatCount = 5;
            model.HardwareRendering = false;
        });

        WithPlayer(model =>
        {
            Assert.Equal(RepeatMode.All, model.Repeat);
            Assert.Equal(5, model.LoopRepeatCount);
            Assert.False(model.HardwareRendering);
        });
        Assert.False(AppSettings.Load(out _).HardwareRendering);
    }

    [Fact]
    public void SettingsThatHaveNotChangedAreNotWrittenAgain()
    {
        OpenOnStart(SavedList("Mix", "a.mid"));
        File.AppendAllText(AppSettings.SettingsPath, "# kept\n");
        WithPlayer(_ => { });

        Assert.EndsWith("# kept\n", File.ReadAllText(AppSettings.SettingsPath));
    }

    [Fact]
    public void AnUnreadableSettingsFileIsCopiedAsideAndSaidSo()
    {
        byte[] broken = [.. "version: 1\nportMaps:\n- title: "u8, 0x82, 0xA0, (byte)'\n'];
        File.WriteAllBytes(AppSettings.SettingsPath, broken);
        string? shown = null;

        WithPlayer(model =>
        {
            model.ShowError = (title, _) => shown = title;
            Dispatcher.UIThread.RunJobs();
        });

        Assert.Equal(Strings.CannotReadSettingsTitle, shown);
        string aside = Assert.Single(Directory.GetFiles(Folder, "settings.*.yaml"));
        Assert.Equal(broken, File.ReadAllBytes(aside));
        AppSettings.Load(out string? problem);
        Assert.Null(problem);
    }

    [Fact]
    public void ClosingTheLastTabOpensAnEmptyList()
    {
        OpenOnStart(SavedList("Mix", "a.mid"));

        WithPlayer(model =>
        {
            PlaylistTabViewModel only = Assert.Single(model.Playlists);

            model.ClosePlaylistCommand.Execute(null);

            PlaylistTabViewModel fresh = Assert.Single(model.Playlists);
            Assert.NotSame(only, fresh);
            Assert.Empty(fresh.Items);
            Assert.Same(fresh, model.SelectedPlaylist);
            Assert.NotEqual(only.FilePath, fresh.FilePath);
        });
    }

    [Fact]
    public void AListThatHasNotChangedIsNotWrittenOver()
    {
        string path = SavedList("Mix", "a.mid");
        OpenOnStart(path);

        WithPlayer(_ => File.AppendAllText(path, "# edited outside\n"));

        Assert.EndsWith("# edited outside\n", File.ReadAllText(path));
    }

    [Fact]
    public void AListThatHasChangedIsWrittenOnClose()
    {
        string path = SavedList("Mix", "a.mid");
        OpenOnStart(path);

        WithPlayer(model => model.Playlists[0].Name = "Renamed");

        Assert.Equal("Renamed", PlaylistFile.Load(path).Name);
    }

    [Fact]
    public void TheListsOpenOnTheNextStartAsTheyWereLeft()
    {
        OpenOnStart(SavedList("One", "a.mid", "b.mid"), SavedList("Two", "c.mid"));

        WithPlayer(model =>
        {
            model.Playlists[0].LastPlayed = 1;
            model.SelectedPlaylist = model.Playlists[1];
        });

        WithPlayer(model =>
        {
            Assert.Equal(["One", "Two"], model.Playlists.Select(t => t.Name));
            Assert.Equal(1, model.Playlists[0].LastPlayed);
            Assert.Same(model.Playlists[1], model.SelectedPlaylist);
        });
    }

    [Fact]
    public void NewListsAreNumberedPastTheNamesInUse()
    {
        WithPlayer(model =>
        {
            model.NewPlaylistCommand.Execute(null);
            model.NewPlaylistCommand.Execute(null);

            Assert.Equal([Strings.NewPlaylistName, $"{Strings.NewPlaylistName} (1)", $"{Strings.NewPlaylistName} (2)"],
                         model.Playlists.Select(t => t.Name));
            Assert.Equal(3, model.Playlists.Select(t => t.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });
    }

    [Fact]
    public void ACopyGoesBesideItsListWithTheNextNumberAndSongsOfItsOwn()
    {
        OpenOnStart(SavedList("Mix"), SavedList("Mix (1)", "a.mid"), SavedList("Other"));

        WithPlayer(model =>
        {
            PlaylistTabViewModel original = model.Playlists[1];

            model.DuplicatePlaylistCommand.Execute(original);

            PlaylistTabViewModel copy = model.Playlists[2];
            Assert.Equal("Mix (2)", copy.Name);
            Assert.Same(copy, model.SelectedPlaylist);
            Assert.Equal(["a.mid"], copy.List.Items.Select(i => Path.GetFileName(i.Path)));
            Assert.NotSame(original.List.Items[0], copy.List.Items[0]);
        });
    }

    [Fact]
    public void ClosingATabBehindLeavesTheOneInFront()
    {
        OpenOnStart(SavedList("One"), SavedList("Two"), SavedList("Three"));

        WithPlayer(model =>
        {
            PlaylistTabViewModel front = model.Playlists[2];
            model.SelectedPlaylist = front;

            model.ClosePlaylistCommand.Execute(model.Playlists[0]);

            Assert.Equal(["Two", "Three"], model.Playlists.Select(t => t.Name));
            Assert.Same(front, model.SelectedPlaylist);

            model.ClosePlaylistCommand.Execute(null);

            Assert.Equal("Two", Assert.Single(model.Playlists).Name);
        });
    }

    [Fact]
    public void OpeningAListThatIsOpenBringsItForward()
    {
        string path = SavedList("Mix", "a.mid");

        WithPlayer(model =>
        {
            model.LoadPlaylist(path);
            model.SelectedPlaylist = model.Playlists[0];

            model.LoadPlaylist(path);

            Assert.Equal(2, model.Playlists.Count);
            Assert.Equal(path, model.SelectedPlaylist?.FilePath);
        });
    }

    [Fact]
    public void AListThatCannotBeReadIsSaidSo()
    {
        string path = Path.Combine(Folder, "broken.yaml");
        File.WriteAllText(path, "items: [\n");
        string? shown = null;

        WithPlayer(model =>
        {
            model.ShowError = (title, _) => shown = title;

            model.LoadPlaylist(path);

            Assert.Single(model.Playlists);
        });

        Assert.Equal(Strings.CannotOpenPlaylistTitle, shown);
    }

    [Fact]
    public void ADefinitionAddsTheOutputModulesOnlyItKnowsAndIsLoadedAgainOnTheNextStart()
    {
        string def = Path.Combine(Folder, "test.def");
        File.WriteAllText(def, "[moduleindex]\nKITE-9=Kite\n");

        WithPlayer(model =>
        {
            Assert.DoesNotContain("KITE-9", model.UseModules);

            model.LoadDefinition(def);
            model.SelectUseModule("kite-9");

            Assert.Equal(def, model.DefinitionPath);
            Assert.Equal("KITE-9", model.UseModule);
            Assert.Equal("KITE-9", model.DefaultMap.Model.UseModule);
            Assert.False(Said(model, string.Format(Strings.NoteUnknownUseModule, "kite-9")));
        });

        WithPlayer(model =>
        {
            Assert.Equal(def, model.DefinitionPath);
            Assert.Equal("KITE-9", model.UseModule);

            model.CloseDefinition();

            Assert.Null(model.DefinitionPath);
            Assert.DoesNotContain("KITE-9", model.UseModules);
            Assert.Equal(model.UseModules[0], model.UseModule);
        });
    }

    [Fact]
    public void ADefinitionThatCannotBeReadLeavesTheOneInForceAndStillTakesTheModule()
    {
        string missing = Path.Combine(Folder, "missing.def");
        string? shown = null;

        WithPlayer(model =>
        {
            model.ShowError = (title, _) => shown = title;

            model.LoadDefinition(missing, use: "NOSUCH");
            Dispatcher.UIThread.RunJobs();

            Assert.Null(model.DefinitionPath);
            Assert.Equal("NOSUCH", model.UseModule);
            Assert.True(Said(model, string.Format(Strings.NoteUnknownUseModule, "NOSUCH")));
        });

        Assert.Equal(Strings.CannotLoadDefTitle, shown);
    }

    [Fact]
    public void AddedPortMapsCopyTheMapBeingEditedAndTheDefaultOneStays()
    {
        File.WriteAllText(AppSettings.SettingsPath, """
            version: 1
            portMaps:
            - title: Default
              ports:
                B: Gone
              useModule: MU80
            """);

        WithPlayer(model =>
        {
            model.EditPortMaps();
            model.AddPortMapCommand.Execute(null);

            PortMapViewModel added = model.PortMaps[1];
            Assert.Same(added, model.EditedMap);
            Assert.Equal("Gone", added.Model.Ports["B"]);
            Assert.Equal("MU80", added.Model.UseModule);

            model.PinnedMap = added;
            model.RemovePortMapCommand.Execute(null);

            Assert.Single(model.PortMaps);
            Assert.Same(model.DefaultMap, model.PinnedMap);

            model.RemovePortMapCommand.Execute(null);
            Assert.Single(model.PortMaps);
        });
    }

    [Fact]
    public void ThePortMapsKeepTheDefaultOneFirst()
    {
        WithPlayer(model =>
        {
            model.EditPortMaps();
            model.AddPortMapCommand.Execute(null);
            model.AddPortMapCommand.Execute(null);
            PortMapViewModel last = model.PortMaps[2];

            Assert.False(model.MovePortMap(model.DefaultMap, 3));
            Assert.True(model.MovePortMap(last, 0));

            Assert.Same(last, model.PortMaps[1]);
        });
    }

    [Fact]
    public void OnlyAMapOtherThanTheDefaultClaimsModules()
    {
        WithPlayer(model =>
        {
            model.EditPortMaps();
            Claim(model, "SC-88").Claimed = true;
            Assert.Empty(model.DefaultMap.Modules);

            model.AddPortMapCommand.Execute(null);
            Assert.False(Claim(model, "SC-88").Claimed);
            Claim(model, "SC-88").Claimed = true;

            Assert.Equal(["SC-88"], model.EditedMap!.Modules);
        });
    }

    [Fact]
    public void TheListOfTargetModulesFollowsTheMapBeingEdited()
    {
        WithPlayer(model =>
        {
            model.EditPortMaps();
            model.AddPortMapCommand.Execute(null);
            PortMapViewModel first = model.EditedMap!;
            Claim(model, "SC-88").Claimed = true;

            model.AddPortMapCommand.Execute(null);
            Assert.False(Claim(model, "SC-88").Claimed);

            model.EditedMap = first;
            Assert.True(Claim(model, "SC-88").Claimed);

            Claim(model, "SC-88").Claimed = false;
            Assert.Empty(first.Modules);
        });
    }

    [Fact]
    public void AModuleTheMapClaimsButTheDefinitionDoesNotListCanStillBeLetGo()
    {
        WithPlayer(model =>
        {
            model.EditPortMaps();
            model.AddPortMapCommand.Execute(null);
            PortMapViewModel map = model.EditedMap!;
            map.Modules.Add("NO-SUCH-1");
            model.EditedMap = model.DefaultMap;
            model.EditedMap = map;

            Assert.Equal("NO-SUCH-1", model.ModuleClaims[^1].Module);
            model.ModuleClaims[^1].Claimed = false;
            Assert.Empty(map.Modules);
        });
    }

    [Fact]
    public void AnM3uOpensAsANewListOfThePlayersOwnAndIsLeftAsItIs()
    {
        string m3u = Path.Combine(Folder, "From elsewhere.m3u8");
        File.WriteAllText(m3u, "#EXTM3U\na.mid\nc.mp3\nsub/b.mid\nnotes.txt\n");
        string written = File.ReadAllText(m3u);

        WithPlayer(model =>
        {
            model.LoadPlaylist(m3u);

            PlaylistTabViewModel tab = model.SelectedPlaylist!;
            Assert.Equal("From elsewhere", tab.Name);
            Assert.Equal([Path.Combine(Folder, "a.mid"), Path.Combine(Folder, "sub", "b.mid")],
                         tab.List.Items.Select(i => i.Path));
            Assert.Equal(AppSettings.PlaylistDirectory, Path.GetDirectoryName(tab.FilePath));
        });

        Assert.Equal(written, File.ReadAllText(m3u));
    }

    [Fact]
    public void AListIsExportedAsAnM3uWithoutMovingToIt()
    {
        string path = SavedList("Mix", Path.Combine(Folder, "a.mid"));
        OpenOnStart(path);
        string m3u = Path.Combine(Folder, "Mix.m3u8");

        WithPlayer(model =>
        {
            model.ExportM3u(model.Playlists[0], m3u);

            Assert.Equal(path, model.Playlists[0].FilePath);
        });

        Assert.Equal([Path.Combine(Folder, "a.mid")], Glosa.Core.Playback.M3uFile.Read(m3u));
    }

    [Fact]
    public void TheSettingsChoicesAreShownAsWordsAndPickedByValue()
    {
        WithPlayer(model =>
        {
            var window = new Glosa.App.Views.SettingsWindow { DataContext = model };
            window.Show();
            ComboBox[] boxes = [.. window.GetLogicalDescendants().OfType<ComboBox>()];
            ComboBox priority = boxes.Single(b => ReferenceEquals(b.ItemsSource, model.Priorities));
            ComboBox thru = boxes.First(b => ReferenceEquals(b.ItemsSource, model.DetectedDefaultChoices));

            // What UI Automation reads a closed combo box's choice as.
            Assert.Equal(Strings.PriorityHigh, priority.SelectedItem?.ToString());
            Assert.Equal(Strings.SettingsDetectedAsIs, thru.SelectedItem?.ToString());

            priority.SelectedItem = model.Priorities.Single(c => c.Value is PlaybackPriority.Low);
            thru.SelectedItem = model.DetectedDefaultChoices.Single(c => "SC-88".Equals(c.Value));

            Assert.Equal(PlaybackPriority.Low, model.Priority);
            Assert.Equal("SC-88", model.ThruPlaysAs);
            window.Close();
        });
    }

    private static ModuleClaimViewModel Claim(MainViewModel model, string module)
        => model.ModuleClaims.Single(line => line.Module == module);

    [Fact]
    public void ANewPortMapIsNamedPastTheNamesInUse()
    {
        WithPlayer(model =>
        {
            model.EditPortMaps();
            model.AddPortMapCommand.Execute(null);
            model.PortMaps[1].Title = string.Format(Strings.NewPortMapName, 3);

            model.AddPortMapCommand.Execute(null);

            Assert.Equal(string.Format(Strings.NewPortMapName, 2), model.PortMaps[2].Title);
        });
    }

    [Fact]
    public void AnUnderscoreInAMapsNameShowsInTheMenu()
    {
        WithPlayer(model =>
        {
            model.EditPortMaps();
            model.AddPortMapCommand.Execute(null);
            model.PortMaps[1].Title = "Studio_B";

            Assert.Contains(model.PortMapChoices.OfType<MenuChoice>(), choice => choice.Label == "Studio__B");
        });
    }

    [Fact]
    public void AListThatCouldNotBeReadAtStartIsKeptForTheNext()
    {
        string broken = Path.Combine(Folder, "broken.yaml");
        File.WriteAllText(broken, "items: [\n");
        string good = SavedList("Good");
        OpenOnStart(broken, good);

        WithPlayer(model =>
        {
            Assert.Equal(["Good"], model.Playlists.Select(t => t.Name));
            model.Repeat = RepeatMode.All;   // so the settings are written
        });

        AppSettings saved = AppSettings.Load(out _);
        Assert.Equal([broken, good], saved.OpenPlaylists.Select(o => o.Path));
        Assert.Equal(1, saved.ActivePlaylist);
    }

    [Fact]
    public void AListIsNotSavedOverAFileAnotherTabHasOpen()
    {
        string one = SavedList("One", "a.mid");
        string two = SavedList("Two", "b.mid");
        OpenOnStart(one, two);
        string? shown = null;

        WithPlayer(model =>
        {
            model.ShowError = (title, _) => shown = title;

            model.SavePlaylist(model.Playlists[0], two);

            Assert.Equal(one, model.Playlists[0].FilePath);
        });

        Assert.Equal(Strings.CannotSavePlaylistTitle, shown);
        Assert.Equal("Two", PlaylistFile.Load(two).Name);
    }
}

public class EnumLabelTests
{
    [Fact]
    public void ThePrioritiesAreShownInTheLanguagesWords()
    {
        Assert.Equal(Strings.PriorityLow, Glosa.App.Controls.EnumLabelConverter.Text(PlaybackPriority.Low));
        Assert.Equal(Strings.PriorityNormal, Glosa.App.Controls.EnumLabelConverter.Text(PlaybackPriority.Normal));
        Assert.Equal(Strings.PriorityHigh, Glosa.App.Controls.EnumLabelConverter.Text(PlaybackPriority.High));
    }
}
