using Glosa.App;
using Glosa.App.Services;
using Glosa.Core.Emulation;
using Glosa.Core.Playback;

namespace Glosa.Tests;

[Collection(ConfigFolder.Name)]
public class AppSettingsTests : ConfigFolder
{
    private void Write(string yaml) => File.WriteAllText(AppSettings.SettingsPath, yaml);

    [Fact]
    public void NoFileIsTheDefaults()
    {
        AppSettings read = AppSettings.Load(out string? problem);

        Assert.Null(problem);
        Assert.Empty(read.PortMaps);
        Assert.Equal(2, read.LoopRepeatCount);
        Assert.True(read.HardwareRendering);
    }

    [Fact]
    public void ReadsBackWhatItWrote()
    {
        var settings = new AppSettings
        {
            PortMaps = [new PortMap { Title = "Studio", Ports = { ["A"] = "SC-88", ["C"] = "MU80" }, SplitParts = true, TransferRate = 3125 }],
            PinnedPortMap = 0,
            Repeat = RepeatMode.All,
            OpenPlaylists = [new OpenPlaylist { Path = "a.yaml", LastPlayed = 3 }],
            Windows = { ["main"] = new WindowPlacement { X = 10, Width = 800, Height = 600 } },
            HardwareRendering = false,
        };
        settings.Save();

        AppSettings read = AppSettings.Load(out string? problem);

        Assert.Null(problem);
        Assert.Equal(AppSettings.FormatVersion, read.Version);
        Assert.Equal("Studio", read.PortMaps[0].Title);
        Assert.Equal("MU80", read.PortMaps[0].Ports["C"]);
        Assert.True(read.PortMaps[0].SplitParts);
        Assert.Equal(3125, read.PortMaps[0].TransferRate);
        Assert.Equal(0, read.PinnedPortMap);
        Assert.Equal(RepeatMode.All, read.Repeat);
        Assert.Equal(3, read.OpenPlaylists[0].LastPlayed);
        Assert.Equal(800, read.Windows["main"].Width);
        Assert.False(read.HardwareRendering);
    }

    [Fact]
    public void TheTransferRateOnceSetForAllMapsIsNotTakenOver()
    {
        Write("""
            version: 1
            transferRate: 3125
            portMaps:
            - title: Studio
            """);

        AppSettings read = AppSettings.Load(out string? problem);

        Assert.Null(problem);
        Assert.Equal(0, Assert.Single(read.PortMaps).TransferRate);
    }

    [Fact]
    public void KeysLeftWithoutAValueTakeTheirDefaults()
    {
        Write("""
            version: 1
            definitionPath:
            language:
            portMaps:
            -
            - title:
              ports:
                A: SC-88
                B:
              resetPorts:
              modules:
              useModule:
            detectionSources:
            openPlaylists:
            - path:
            windows:
              main:
            """);

        AppSettings read = AppSettings.Load(out string? problem);

        Assert.Null(problem);
        PortMap map = Assert.Single(read.PortMaps);
        Assert.Equal(new PortMap().Title, map.Title);
        Assert.Equal(["A"], map.Ports.Keys);
        Assert.Equal([PortMap.PortKey(0)], map.ResetPorts);
        Assert.Empty(map.Modules);
        Assert.Equal("THRU", map.UseModule);
        Assert.False(map.SplitParts);
        Assert.Equal(0, map.TransferRate);
        Assert.Equal(string.Empty, read.DefinitionPath);
        Assert.Equal(string.Empty, read.Language);
        Assert.NotEmpty(read.DetectionSources);
        Assert.Empty(read.OpenPlaylists);
        Assert.Empty(read.Windows);
    }

    [Fact]
    public void ANewerFormatIsNotReadAndSaysSo()
    {
        Write($"version: {AppSettings.FormatVersion + 1}\nloopRepeatCount: 9\n");

        AppSettings read = AppSettings.Load(out string? problem);

        Assert.Equal(string.Format(Strings.SettingsTooNew, AppSettings.FormatVersion + 1,
                                   AppSettings.FormatVersion), problem);
        Assert.Equal(2, read.LoopRepeatCount);
    }

    [Fact]
    public void AFileThatIsNotUtf8IsNotReadAndSaysSo()
    {
        // "version: 1" and a title in Shift_JIS.
        File.WriteAllBytes(AppSettings.SettingsPath,
                           [.. "version: 1\nportMaps:\n- title: "u8, 0x82, 0xA0, (byte)'\n']);

        AppSettings read = AppSettings.Load(out string? problem);

        Assert.NotNull(problem);
        Assert.Empty(read.PortMaps);
    }

    [Fact]
    public void BrokenYamlIsNotReadAndSaysSo()
    {
        Write("portMaps: [\n");

        AppSettings.Load(out string? problem);

        Assert.NotNull(problem);
    }
}

public class PortMapTests
{
    [Theory]
    [InlineData(0, "A")]
    [InlineData(5, "F")]
    public void APortGoesUnderItsLetter(int port, string key)
    {
        Assert.Equal(key, PortMap.PortKey(port));
        Assert.Equal(port, PortMap.PortOf(key));
    }

    [Theory]
    [InlineData("G")]
    [InlineData("a")]
    [InlineData("AB")]
    [InlineData("")]
    [InlineData(null)]
    public void AKeyThatNamesNoPortIsNone(string? key) => Assert.Null(PortMap.PortOf(key));
}

public class DetectionSourceSettingTests
{
    [Fact]
    public void TheDefaultsListEverySourceOnceWithTheSearchedOnesFirst()
    {
        List<DetectionSourceSetting> defaults = DetectionSourceSetting.Defaults();
        IReadOnlyList<DetectionSource> on = NameDetection.Default.Sources;

        Assert.Equal(Enum.GetValues<DetectionSource>().Length, defaults.Count);
        Assert.Equal(on, defaults.Take(on.Count).Select(s => s.Source));
        Assert.All(defaults.Take(on.Count), s => Assert.True(s.Enabled));
        Assert.All(defaults.Skip(on.Count), s => Assert.False(s.Enabled));
    }

    [Fact]
    public void TidyKeepsTheSavedOrderAndPutsTheMissingAfter()
    {
        DetectionSource[] all = Enum.GetValues<DetectionSource>();
        DetectionSource last = all[^1];

        List<DetectionSourceSetting> tidy = DetectionSourceSetting.Tidy(
        [
            new() { Source = last, Enabled = true },
            new() { Source = last, Enabled = false },
            new() { Source = (DetectionSource)999, Enabled = true },
        ]);

        Assert.Equal(all.Length, tidy.Count);
        Assert.Equal(last, tidy[0].Source);
        Assert.True(tidy[0].Enabled);
        Assert.Equal(all.Length, tidy.Select(s => s.Source).Distinct().Count());
        Assert.DoesNotContain(tidy, s => s.Source == (DetectionSource)999);
    }
}

public class ConfigArgumentTests
{
    [Fact]
    public void TheLastConfigWins()
        => Assert.Equal("b", App.App.ConfigArgument(["--config", "a", "song.mid", "--config", "b"]));

    [Fact]
    public void AConfigWithNoFolderAfterItIsNone()
        => Assert.Null(App.App.ConfigArgument(["song.mid", "--config"]));
}
