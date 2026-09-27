using System.IO.Compression;
using Glosa.Core.Text;

namespace Glosa.Core.Archives;

/// <summary>
/// Reads a song, or the files around it, whether it is on disk or inside an archive.
/// </summary>
/// <remarks>
/// A file inside an archive is named as though the archive were a folder
/// (<c>D:\MIDI\album.lzh\SONG.MID</c>), so everything that works on names works unchanged,
/// including the lookup of the document beside a song.
///
/// ZIP comes from the runtime and LZH from <see cref="LzhArchive"/>. Names in either are
/// Shift-JIS unless the archive says otherwise; ZIP marks UTF-8 names with a flag, and the
/// runtime honours it.
///
/// A name inside an archive is kept inside it: <c>..</c>, a leading <c>/</c> and a drive are
/// dropped (<see cref="Normalise"/>), and a path that runs through an archive is looked up in
/// the archive before on disk, so <c>album.zip\..\..\SECRET.MID</c> is not a file beside it.
///
/// Inside an archive, names match without regard to case. The archives come from MS-DOS,
/// where <c>SONG.TXT</c> and <c>song.txt</c> were one file, and the document lookup asks
/// for <c>.txt</c>.
/// </remarks>
public static class SongStore
{
    public static readonly string[] ArchiveExtensions = [".lzh", ".lha", ".zip"];

    /// <summary>
    /// The most a file inside an archive may unpack to. Far past any song or its document;
    /// the size is what the archive says, and is checked before anything is allocated.
    /// </summary>
    public const int MaxEntrySize = 64 * 1024 * 1024;

    public static bool IsArchive(string path)
        => ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The path a file inside <paramref name="archive"/> goes by.</summary>
    public static string Combine(string archive, string entry)
        => archive + Path.DirectorySeparatorChar + entry.Replace('/', Path.DirectorySeparatorChar);

    /// <summary>What an archive holds, as paths inside it with <c>/</c> between folders.</summary>
    public static IReadOnlyList<string> List(string archive)
    {
        using FileStream stream = File.OpenRead(archive);
        if (!IsZip(archive)) return [.. LzhArchive.List(stream).Select(e => Normalise(e.Name))];

        using ZipArchive zip = OpenZip(stream);
        return [.. zip.Entries.Where(e => !IsFolder(e)).Select(e => Normalise(e.FullName))];
    }

    public static byte[] ReadAllBytes(string path)
    {
        if (!Split(path, out string archive, out string entry)) return File.ReadAllBytes(path);

        using FileStream stream = File.OpenRead(archive);
        if (IsZip(archive))
        {
            using ZipArchive zip = OpenZip(stream);
            ZipArchiveEntry found = Find(zip.Entries.Where(e => !IsFolder(e)),
                                         e => Normalise(e.FullName), entry)
                                    ?? throw Missing(path);
            if (found.Length > MaxEntrySize)
                throw new InvalidDataException(string.Format(Strings.ArchiveEntryTooLarge, entry));

            using Stream data = found.Open();
            var bytes = new byte[found.Length];
            data.ReadExactly(bytes);
            return bytes;
        }

        LzhEntry lzh = Find(LzhArchive.List(stream), e => Normalise(e.Name), entry) ?? throw Missing(path);
        return LzhArchive.Extract(stream, lzh);
    }

    /// <summary>
    /// Whether <paramref name="path"/> names a file, on disk or in an archive. An archive
    /// that cannot be read holds nothing.
    /// </summary>
    public static bool Exists(string path)
    {
        if (!Split(path, out string archive, out string entry)) return File.Exists(path);

        try
        {
            return Find(List(archive), name => name, entry) is not null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                      or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The file on disk that is opened to read <paramref name="path"/>: the archive it is in,
    /// or the path itself.
    /// </summary>
    public static string FileOf(string path) => Split(path, out string archive, out _) ? archive : path;

    /// <summary>
    /// Finds the archive a path runs through, walking up from the file until a parent is an
    /// archive on disk.
    /// </summary>
    private static bool Split(string path, out string archive, out string entry)
    {
        for (string? parent = Path.GetDirectoryName(path);
             !string.IsNullOrEmpty(parent);
             parent = Path.GetDirectoryName(parent))
        {
            if (IsArchive(parent) && File.Exists(parent))
            {
                archive = parent;
                entry = Normalise(path[(parent.Length + 1)..]);
                return true;
            }
        }

        archive = entry = string.Empty;
        return false;
    }

    private static T? Find<T>(IEnumerable<T> entries, Func<T, string> name, string wanted)
        where T : class
    {
        T[] all = [.. entries];
        return all.FirstOrDefault(e => name(e) == wanted)
               ?? all.FirstOrDefault(e => string.Equals(name(e), wanted,
                                                        StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsZip(string path)
        => string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    private static ZipArchive OpenZip(Stream stream)
        => new(stream, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: Cp932.Encoding);

    private static bool IsFolder(ZipArchiveEntry entry)
        => entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    /// <summary>
    /// Forward slashes throughout, and nothing that would lead out of the archive: no
    /// <c>.</c> or <c>..</c>, no leading <c>/</c>, no drive.
    /// </summary>
    /// <remarks>
    /// MS-DOS zippers wrote backslashes, and a path handed in on Windows has them too.
    /// </remarks>
    private static string Normalise(string name)
        => string.Join('/', name.Replace('\\', '/').Split('/')
                                .Where((part, i) => part is not ("" or "." or "..")
                                                    && !(i == 0 && part.EndsWith(':'))));

    private static FileNotFoundException Missing(string path)
        => new(string.Format(Strings.ArchiveEntryMissing, path), path);
}
