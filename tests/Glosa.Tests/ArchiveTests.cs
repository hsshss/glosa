using System.IO.Compression;
using Glosa.Core.Archives;
using Glosa.Core.Playback;
using Glosa.Core.Smf;
using Glosa.Core.Text;

namespace Glosa.Tests;

public class LzhArchiveTests
{
    private static byte[] Song(string title) => new SmfBuilder(480)
        .Track(t => t.Meta(0, 0x03, Cp932.Encoding.GetBytes(title)).Short(0, 0x90, 60, 100)
                     .Short(480, 0x80, 60, 0).End(0))
        .Build();

    /// <summary>Long enough, and repetitive enough, that matches reach well back.</summary>
    private static byte[] Text()
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < 400; i++) text.Append($"line {i % 37}: GS reset, then the drums.\r\n");
        return System.Text.Encoding.ASCII.GetBytes(text.ToString());
    }

    private static (IReadOnlyList<LzhEntry> Entries, MemoryStream Stream) Open(byte[] archive)
    {
        var stream = new MemoryStream(archive);
        return (LzhArchive.List(stream), stream);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryHeaderLevelGivesBackTheFile(int level)
    {
        byte[] data = Text();
        (var entries, var stream) = Open(new LzhBuilder().Add("README.TXT", data, level).Build());

        LzhEntry entry = Assert.Single(entries);
        Assert.Equal("README.TXT", entry.Name);
        Assert.Equal(data, LzhArchive.Extract(stream, entry));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FoldersBecomeSlashes(int level)
    {
        (var entries, _) = Open(new LzhBuilder()
            .Add("SONG.MID", Song("x"), level, folder: "album/disc1").Build());

        Assert.Equal("album/disc1/SONG.MID", Assert.Single(entries).Name);
    }

    [Fact]
    public void StoredFilesComeOutAsTheyWent()
    {
        byte[] data = Song("stored");
        (var entries, var stream) = Open(new LzhBuilder().Add("A.MID", data, method: "-lh0-").Build());

        Assert.Equal(data, LzhArchive.Extract(stream, Assert.Single(entries)));
    }

    [Fact]
    public void SeveralFilesAreListedInOrder()
    {
        (var entries, var stream) = Open(new LzhBuilder()
            .Add("B.MID", Song("b"), level: 0)
            .Add("A.MID", Song("a"), level: 1)
            .Add("C.TXT", Text(), level: 2)
            .Build());

        Assert.Equal(["B.MID", "A.MID", "C.TXT"], entries.Select(e => e.Name));
        Assert.Equal("a", SmfReader.Read(LzhArchive.Extract(stream, entries[1])).Title);
    }

    [Fact]
    public void NamesAreShiftJis()
    {
        (var entries, _) = Open(new LzhBuilder().Add("ソナタ.MID", Song("x"), level: 1).Build());

        // The name's first character, katakana SO, is 0x83 0x5C in CP932: a backslash as
        // its second byte, which must not split the name.
        Assert.Equal("ソナタ.MID", Assert.Single(entries).Name);
    }

    [Fact]
    public void AUnixArchiveMayCarryUtf8()
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes("ソナタ.MID");
        (var entries, _) = Open(new LzhBuilder()
            .Add("", Song("x"), level: 2, os: 'U', rawName: utf8).Build());

        Assert.Equal("ソナタ.MID", Assert.Single(entries).Name);
    }

    [Fact]
    public void ASelfExtractingArchiveIsFoundPastItsProgram()
    {
        byte[] lzh = new LzhBuilder().Add("A.MID", Song("a")).Build();
        byte[] sfx = [.. "MZ"u8, .. new byte[1000], .. lzh];

        Assert.Equal("A.MID", Assert.Single(Open(sfx).Entries).Name);
    }

    [Fact]
    public void DamageIsCaughtByTheCrc()
    {
        byte[] lzh = new LzhBuilder().Add("A.TXT", Text(), method: "-lh0-").Build();
        lzh[^20] ^= 0xFF;
        (var entries, var stream) = Open(lzh);

        Assert.Throws<InvalidDataException>(() => LzhArchive.Extract(stream, Assert.Single(entries)));
    }

    [Fact]
    public void AHeaderTooShortForItsOwnFieldsIsRefused()
    {
        byte[] lzh = new LzhBuilder().Add("A.MID", Song("a"), level: 0).Build();
        lzh[0] = 1;

        Assert.Throws<InvalidDataException>(() => Open(lzh));
    }

    [Fact]
    public void ALevel1HeaderWithNoRoomForItsOsByteIsRefused()
    {
        byte[] lzh = new LzhBuilder().Add("A.MID", Song("a"), level: 1).Build();
        // Room for the name and the CRC, and no further.
        lzh[0] = (byte)(22 + "A.MID".Length);

        Assert.Throws<InvalidDataException>(() => Open(lzh));
    }

    [Fact]
    public void AnExtensionShorterThanItsOwnSizeFieldIsRefused()
    {
        byte[] lzh = new LzhBuilder().Add("A.MID", Song("a"), level: 2).Build();
        // The first extension's size, just after the base header.
        lzh[24] = 2;
        lzh[25] = 0;

        Assert.Throws<InvalidDataException>(() => Open(lzh));
    }

    [Fact]
    public void ACodeTableNamingASymbolPastItsEndIsRefused()
    {
        // A block of one event. The table that codes the other tables holds one symbol,
        // 31 of its 19, and the literal table then asks it for a length.
        byte[] packed = [0x00, 0x01, 0x07, 0xC0, 0x20];
        (var entries, var stream) = Open(new LzhBuilder().Add("A.MID", packed, method: "-lh6-").Build());

        Assert.Throws<InvalidDataException>(() => LzhArchive.Extract(stream, Assert.Single(entries)));
    }

    [Fact]
    public void AFileClaimingMoreThanAnySongIsRefusedBeforeItIsUnpacked()
    {
        byte[] lzh = new LzhBuilder().Add("A.MID", Song("a"), level: 2).Build();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            lzh.AsSpan(11), SongStore.MaxEntrySize + 1u);
        (var entries, var stream) = Open(lzh);

        var ex = Assert.Throws<InvalidDataException>(
            () => LzhArchive.Extract(stream, Assert.Single(entries)));
        Assert.Equal(string.Format(Core.Strings.ArchiveEntryTooLarge, "A.MID"), ex.Message);
    }

    [Fact]
    public void AMethodNotReadIsRefusedAsInvalidData()
    {
        (var entries, var stream) = Open(new LzhBuilder()
            .Add("A.MID", Song("a"), method: "-lh1-").Build());

        var ex = Assert.Throws<InvalidDataException>(
            () => LzhArchive.Extract(stream, Assert.Single(entries)));
        Assert.Contains("-lh1-", ex.Message);
    }
}

public class SongStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"glosa-archive-{Guid.NewGuid():N}");

    public SongStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] Song(string title) => new SmfBuilder(480)
        .Track(t => t.Meta(0, 0x03, Cp932.Encoding.GetBytes(title)).End(0))
        .Build();

    private string Lzh(string name, LzhBuilder builder)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private string Zip(string name, params (string Name, byte[] Data)[] entries)
    {
        string path = Path.Combine(_root, name);
        using (var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create,
                                        leaveOpen: false, entryNameEncoding: Cp932.Encoding))
        {
            foreach ((string entryName, byte[] data) in entries)
            {
                using Stream stream = zip.CreateEntry(entryName).Open();
                stream.Write(data);
            }
        }
        return path;
    }

    [Fact]
    public void ASongInsideAnLzhIsReadByItsPath()
    {
        string archive = Lzh("album.lzh", new LzhBuilder().Add("SONG.MID", Song("曲名")));

        Assert.Equal("曲名", SmfReader.Read(Path.Combine(archive, "SONG.MID")).Title);
    }

    [Fact]
    public void ASongInsideAZipIsReadByItsPath()
    {
        string archive = Zip("album.zip", ("曲/SONG.MID", Song("曲名")));

        Assert.Equal("曲名", SmfReader.Read(Path.Combine(archive, "曲", "SONG.MID")).Title);
    }

    [Fact]
    public void AZipFileClaimingMoreThanAnySongIsRefused()
    {
        string archive = Zip("album.zip", ("SONG.MID", Song("x")));
        byte[] zip = File.ReadAllBytes(archive);
        // The uncompressed size in the central directory, which is what the runtime reports.
        int central = zip.AsSpan().LastIndexOf("PK\x01\x02"u8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(central + 24), 0xFFFFFFF0);
        File.WriteAllBytes(archive, zip);

        Assert.Throws<InvalidDataException>(
            () => SongStore.ReadAllBytes(Path.Combine(archive, "SONG.MID")));
    }

    [Fact]
    public void ANameLeadingOutOfTheArchiveStaysInIt()
    {
        string archive = Zip("album.zip", ("../SECRET.MID", Song("inside")));
        File.WriteAllBytes(Path.Combine(_root, "SECRET.MID"), Song("outside"));
        File.WriteAllBytes(Path.Combine(_root, "OTHER.MID"), Song("outside"));

        Assert.Equal(["SECRET.MID"], SongStore.List(archive));
        Assert.Equal("inside", SmfReader.Read(Path.Combine(archive, "..", "SECRET.MID")).Title);
        Assert.False(SongStore.Exists(Path.Combine(archive, "..", "OTHER.MID")));
    }

    [Theory]
    [InlineData("/ROOTED.MID")]
    [InlineData("C:/DRIVE.MID")]
    [InlineData("./a/../HERE.MID")]
    public void ANameIsKeptToTheArchivesOwnFolders(string name)
    {
        string archive = Zip("album.zip", (name, Song("x")));

        string listed = Assert.Single(SongStore.List(archive));
        Assert.DoesNotContain("..", listed);
        Assert.False(listed.StartsWith('/') || listed.Contains(':'));
        Assert.True(SongStore.Exists(SongStore.Combine(archive, listed)));
    }

    [Fact]
    public void NamesInsideMatchWhateverTheCase()
    {
        string archive = Zip("album.zip", ("SONG.MID", Song("a")), ("SONG.TXT", "doc"u8.ToArray()));

        Assert.True(SongStore.Exists(Path.Combine(archive, "song.txt")));
        Assert.Equal("doc"u8.ToArray(), SongStore.ReadAllBytes(Path.Combine(archive, "Song.Txt")));
    }

    [Fact]
    public void WhatIsNotInsideDoesNotExist()
    {
        string archive = Lzh("album.lzh", new LzhBuilder().Add("SONG.MID", Song("a")));

        Assert.False(SongStore.Exists(Path.Combine(archive, "OTHER.MID")));
        Assert.Throws<FileNotFoundException>(
            () => SongStore.ReadAllBytes(Path.Combine(archive, "OTHER.MID")));
    }

    [Fact]
    public void AnArchiveExpandsIntoItsSongsSorted()
    {
        string archive = Lzh("album.lzh", new LzhBuilder()
            .Add("b.mid", Song("b"))
            .Add("README.TXT", "x"u8.ToArray())
            .Add("a.MID", Song("a"), folder: "sub"));

        Assert.Equal([Path.Combine(archive, "b.mid"), Path.Combine(archive, "sub", "a.MID")],
                     SongFiles.Expand([archive]));
    }

    [Fact]
    public void AFolderOpensTheArchivesInIt()
    {
        Lzh("b.lzh", new LzhBuilder().Add("IN_B.MID", Song("b")));
        Zip("c.zip", ("IN_C.MID", Song("c")));
        File.WriteAllBytes(Path.Combine(_root, "a.mid"), Song("a"));
        File.WriteAllBytes(Path.Combine(_root, "broken.lzh"), "not an archive"u8.ToArray());

        Assert.Equal(["a.mid", Path.Combine("b.lzh", "IN_B.MID"), Path.Combine("c.zip", "IN_C.MID")],
                     SongFiles.Expand([_root]).Select(p => Path.GetRelativePath(_root, p)));
    }

    [Fact]
    public void ASongNamedInsideAnArchiveIsTakenAsItIs()
    {
        string archive = Zip("album.zip", ("SONG.MID", Song("a")));
        string song = Path.Combine(archive, "SONG.MID");

        Assert.Equal([song], SongFiles.Expand([song, Path.Combine(archive, "NONE.MID")]));
    }

    [Fact]
    public void ANonSmfIsAnUnreadableFileLikeAnyOther()
    {
        string archive = Zip("album.zip", ("SONG.MID", "RIFF...."u8.ToArray()));

        Assert.ThrowsAny<IOException>(() => SmfReader.Read(Path.Combine(archive, "SONG.MID")));
    }
}
