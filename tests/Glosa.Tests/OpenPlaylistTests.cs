using Glosa.Core.Playback;

namespace Glosa.Tests;

public class OpenPlaylistTests
{
    /// <summary>The shape of the settings, as far as the open tabs go.</summary>
    private sealed class Settings
    {
        public List<OpenPlaylist> OpenPlaylists { get; set; } = [];

        public int ActivePlaylist { get; set; }
    }

    [Fact]
    public void WritesAndReadsWhereEachListWasLeft()
    {
        var settings = new Settings
        {
            OpenPlaylists =
            [
                new OpenPlaylist { Path = @"C:\lists\a.yaml", LastPlayed = 82, TopRow = 80 },
                new OpenPlaylist { Path = @"C:\lists\b.yaml" },
            ],
            ActivePlaylist = 1,
        };

        string yaml = YamlFile.ToYaml(settings);
        Settings read = YamlFile.Parse<Settings>(yaml);

        Assert.Contains("lastPlayed: 82", yaml);
        Assert.Equal(@"C:\lists\a.yaml", read.OpenPlaylists[0].Path);
        Assert.Equal(82, read.OpenPlaylists[0].LastPlayed);
        Assert.Equal(80, read.OpenPlaylists[0].TopRow);
        Assert.Equal(-1, read.OpenPlaylists[1].LastPlayed);
        Assert.Equal(1, read.ActivePlaylist);
    }
}
