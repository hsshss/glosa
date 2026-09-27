using Glosa.Core.Playback;

namespace Glosa.Tests;

/// <summary>What the project's YAML files leave out, and that it all comes back.</summary>
public class OmitUnspecifiedTests
{
    /// <summary>Starts somewhere other than the type defaults, as the settings do.</summary>
    public sealed class Shape
    {
        public bool On { get; set; } = true;

        public int Row { get; set; } = -1;

        public string Name { get; set; } = "既定";

        public string Note { get; set; } = string.Empty;

        public List<string> Items { get; set; } = [];

        public List<string> Filled { get; set; } = ["a"];

        public Inner Nested { get; set; } = new();
    }

    public sealed class Inner
    {
        public int Count { get; set; }
    }

    [Fact]
    public void WhatWasNeverSetIsNotWritten()
        => Assert.Equal("nested: {}", YamlFile.ToYaml(new Shape()).Trim());

    [Fact]
    public void AValueThatDiffersIsWrittenAndReadBack()
    {
        var shape = new Shape
        {
            On = false,
            Row = 0,
            Name = "別",
            Note = "x",
            Items = ["b"],
            Filled = [],
            Nested = new Inner { Count = 3 },
        };

        string yaml = YamlFile.ToYaml(shape);
        Shape read = YamlFile.Parse<Shape>(yaml);

        // The type defaults: left out, these would read back as true and -1.
        Assert.Contains("on: false", yaml);
        Assert.Contains("row: 0", yaml);
        Assert.False(read.On);
        Assert.Equal(0, read.Row);
        Assert.Equal("別", read.Name);
        Assert.Equal("x", read.Note);
        Assert.Equal(["b"], read.Items);
        // Emptied where it starts full: left out, it would read back full.
        Assert.Empty(read.Filled);
        Assert.Equal(3, read.Nested.Count);
    }

    [Fact]
    public void AListReadReplacesTheOneItStartsWith()
    {
        // Not added to it: a map's reset ports start as [0], and [1] means B alone.
        Shape read = YamlFile.Parse<Shape>("filled: [b]");

        Assert.Equal(["b"], read.Filled);
    }

    [Fact]
    public void AnUntouchedSongIsItsPathAndLength()
    {
        var list = new Playlist
        {
            Name = "リスト",
            Items = [new PlaylistItem { Path = @"D:\a.mid", DurationMs = 1000 }],
        };

        string yaml = PlaylistFile.ToYaml(list);
        Playlist read = PlaylistFile.Parse(yaml);

        Assert.DoesNotContain("title", yaml);
        Assert.DoesNotContain("module", yaml);
        Assert.Equal(@"D:\a.mid", read.Items[0].Path);
        Assert.Equal(1000, read.Items[0].DurationMs);
        Assert.Equal(string.Empty, read.Items[0].Module);
    }

    [Fact]
    public void AnOpenTabThatWasLeftAtTheTopIsItsPath()
    {
        string yaml = YamlFile.ToYaml(new OpenPlaylist { Path = @"C:\lists\a.yaml" });

        Assert.Equal(@"path: C:\lists\a.yaml", yaml.Trim());
        Assert.Equal(-1, YamlFile.Parse<OpenPlaylist>(yaml).LastPlayed);
    }
}
