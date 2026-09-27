using System.Text;
using Glosa.Core.Text;

namespace Glosa.Core.Playback;

/// <summary>
/// M3U playlists, the plain lists of paths other players read and write.
/// </summary>
/// <remarks>
/// Only the paths are read. The title and length on an <c>#EXTINF</c> line are what the
/// other player made of the song; Glosa reads its own from the song. A relative path is from
/// the playlist's folder, and a <c>file:</c> URL is a path too; other URLs are passed over.
/// The encoding is told as an attached document's is (<see cref="DocumentText"/>), so a
/// <c>.m3u</c> written in Shift-JIS still reads.
///
/// Written in UTF-8, with a byte order mark in a <c>.m3u</c>, whose readers otherwise take
/// it for the system's code page, and none in a <c>.m3u8</c>, which is UTF-8 by name.
/// </remarks>
public static class M3uFile
{
    public static readonly string[] Extensions = [".m3u", ".m3u8"];

    /// <summary>
    /// The largest M3U read. Far past any list of songs, ten thousand of which come to a
    /// megabyte or two; a file past it is not a playlist, and is not read into memory.
    /// </summary>
    public const int MaxBytes = 16 * 1024 * 1024;

    public static bool IsPlaylist(string path)
        => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The songs <paramref name="path"/> lists, each as a full path, in its order.</summary>
    /// <exception cref="InvalidDataException">The file is larger than <see cref="MaxBytes"/>.</exception>
    public static IReadOnlyList<string> Read(string path)
    {
        string text = DocumentText.Decode(ReadBytes(path));
        string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;

        var songs = new List<string>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (Local(line, folder) is { } song) songs.Add(song);
        }
        return songs;
    }

    /// <summary>
    /// Writes <paramref name="items"/> to <paramref name="path"/>, each under its title and
    /// length.
    /// </summary>
    /// <remarks>
    /// A song under the playlist's folder is written relative to it, so the two can be moved
    /// together; any other by its full path. A song inside an archive is written as Glosa
    /// names it, which other players do not follow.
    /// </remarks>
    public static void Write(string path, IEnumerable<PlaylistItem> items)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;

        var text = new StringBuilder("#EXTM3U\r\n");
        foreach (PlaylistItem item in items)
        {
            long seconds = item.DurationMs > 0 ? (item.DurationMs + 500) / 1000 : -1;
            string title = item.Title.Length > 0 ? item.Title : Path.GetFileNameWithoutExtension(item.Path);
            text.Append("#EXTINF:").Append(seconds).Append(',').Append(OneLine(title)).Append("\r\n");
            text.Append(Entry(item.Path, folder)).Append("\r\n");
        }

        bool mark = !string.Equals(Path.GetExtension(path), ".m3u8", StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: mark));
    }

    /// <remarks>
    /// Sized before it is opened: a FIFO or a device has no size, and opening one would wait
    /// for a writer or read without end.
    /// </remarks>
    private static byte[] ReadBytes(string path)
    {
        long length = new FileInfo(path).Length;
        if (length > MaxBytes) throw new InvalidDataException(Strings.M3uTooLarge);
        if (length == 0) return [];

        using FileStream stream = File.OpenRead(path);
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static string? Local(string entry, string folder)
    {
        if (entry.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(entry, UriKind.Absolute, out Uri? uri) && uri.IsFile ? uri.LocalPath : null;
        if (entry.Contains("://", StringComparison.Ordinal)) return null;

        // Written on Windows, a path is split by backslashes, which elsewhere are part of a name.
        if (Path.DirectorySeparatorChar == '/') entry = entry.Replace('\\', '/');
        try
        {
            return Path.GetFullPath(Path.Combine(folder, entry));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string Entry(string song, string folder)
    {
        string relative = Path.GetRelativePath(folder, song);
        return Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal) ? song : relative;
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");
}
