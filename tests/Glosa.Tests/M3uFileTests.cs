using System.Text;
using Glosa.Core.Playback;
using Glosa.Core.Text;

namespace Glosa.Tests;

public class M3uFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"glosa-m3u-{Guid.NewGuid():N}");

    public M3uFileTests() => Directory.CreateDirectory(Path.Combine(_root, "songs"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private string Song(string name)
    {
        string path = At("songs", name);
        File.WriteAllBytes(path, [0x4D, 0x54, 0x68, 0x64]);
        return path;
    }

    [Fact]
    public void PathsAreReadFromTheListsFolderAndCommentsArePassedOver()
    {
        string elsewhere = At("elsewhere.mid");
        string list = At("list.m3u8");
        File.WriteAllText(list,
            "#EXTM3U\r\n#EXTINF:120,A song\r\nsongs/a.mid\r\n\r\n"
            + $"{elsewhere}\r\n{new Uri(At("songs", "b.mid")).AbsoluteUri}\r\nhttp://example.com/c.mid\r\n");

        Assert.Equal([At("songs", "a.mid"), elsewhere, At("songs", "b.mid")], M3uFile.Read(list));
    }

    [Fact]
    public void AListWrittenInShiftJisStillReads()
    {
        string list = At("古い.m3u");
        File.WriteAllBytes(list, Cp932.Encoding.GetBytes("songs\\曲.mid\r\n"));

        Assert.Equal([At("songs", "曲.mid")], M3uFile.Read(list));
    }

    [Fact]
    public void SongsUnderTheListsFolderAreWrittenRelativeToIt()
    {
        string outside = Path.Combine(Path.GetTempPath(), "glosa-m3u-outside.mid");
        string list = At("out.m3u8");
        M3uFile.Write(list,
        [
            new PlaylistItem { Path = At("songs", "a.mid"), Title = "A song", DurationMs = 61_400 },
            new PlaylistItem { Path = outside },
        ]);

        Assert.Equal(
            $"#EXTM3U\r\n#EXTINF:61,A song\r\n{Path.Combine("songs", "a.mid")}\r\n"
            + $"#EXTINF:-1,glosa-m3u-outside\r\n{outside}\r\n",
            File.ReadAllText(list));
        Assert.Equal([At("songs", "a.mid"), outside], M3uFile.Read(list));
    }

    [Fact]
    public void OnlyAnM3u8GoesWithoutAByteOrderMark()
    {
        PlaylistItem[] items = [new PlaylistItem { Path = At("songs", "曲.mid") }];
        M3uFile.Write(At("a.m3u8"), items);
        M3uFile.Write(At("a.m3u"), items);

        Assert.False(File.ReadAllBytes(At("a.m3u8")).AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.True(File.ReadAllBytes(At("a.m3u")).AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal([At("songs", "曲.mid")], M3uFile.Read(At("a.m3u")));
    }

    [Fact]
    public void AListHandedInBringsItsSongsButNotTheListsItNames()
    {
        string a = Song("a.mid"), b = Song("b.mid");
        File.WriteAllText(At("inner.m3u"), "songs/b.mid\n");
        File.WriteAllText(At("list.m3u"), "songs/a.mid\nsongs/missing.mid\ninner.m3u\nlist.m3u\n");

        Assert.Equal([a], SongFiles.Expand([At("list.m3u")]));
        Assert.Equal([b], SongFiles.Expand([At("inner.m3u")]));
    }

    [Fact]
    public void AFileTooLargeForAListIsNotRead()
    {
        string list = At("huge.m3u");
        using (FileStream stream = File.Create(list)) stream.SetLength(M3uFile.MaxBytes + 1);

        Assert.Throws<InvalidDataException>(() => M3uFile.Read(list));
        Assert.Empty(SongFiles.Expand([list]));
    }

    [Fact]
    public void OnlyTheShareTheListIsOnIsFollowed()
    {
        string[] songs = [At("songs", "a.mid"), @"\\nas\music\b.mid", @"\\other\music\c.mid", "//nas/music/d.mid"];

        Assert.Equal([At("songs", "a.mid")], SongFiles.OnReach(songs, At("list.m3u")));
        Assert.Equal([At("songs", "a.mid"), @"\\nas\music\b.mid", "//nas/music/d.mid"],
                     SongFiles.OnReach(songs, @"\\NAS\music\list.m3u"));
    }

    [Fact]
    public void AListInAFolderIsLeftAlone()
    {
        string a = Song("a.mid");
        File.WriteAllText(At("songs", "all.m3u"), "a.mid\na.mid\n");

        Assert.Equal([a], SongFiles.Expand([At("songs")]));
    }
}
