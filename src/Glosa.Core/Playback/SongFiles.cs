using Glosa.Core.Archives;

namespace Glosa.Core.Playback;

/// <summary>
/// Turns what a drop or a command line hands over into a list of songs.
/// </summary>
/// <remarks>
/// Folders are walked in full, and an archive, including one met on the way, counts as a
/// folder. What is inside an archive is sorted like a folder's contents, since the order it
/// was packed in rarely means anything.
/// </remarks>
public static class SongFiles
{
    /// <summary>
    /// What the reader can open: SMF, its RIFF wrapper RMI, and Recomposer's four (RCP and
    /// R36 from 2.5F back, G18 and G36 after).
    /// </summary>
    public static readonly string[] Extensions =
        [".mid", ".midi", ".rmi", ".rcp", ".r36", ".g18", ".g36"];

    /// <summary>
    /// Expands folders and keeps only playable files, in the order given; a folder's own
    /// contents are sorted so a drop is reproducible.
    /// </summary>
    /// <remarks>
    /// An M3U playlist given here brings the songs it lists, in its order. One met inside a
    /// folder, or listed by another, is left alone: a folder of songs often carries one of
    /// the same songs, and a list can name itself. What it lists on another machine's share
    /// is left out (<see cref="OnReach"/>).
    /// </remarks>
    public static IEnumerable<string> Expand(IEnumerable<string> paths) => Expand(paths, playlists: true);

    private static IEnumerable<string> Expand(IEnumerable<string> paths, bool playlists)
    {
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (string found in InFolder(path)) yield return found;
            }
            else if (File.Exists(path) && SongStore.IsArchive(path))
            {
                foreach (string found in InArchive(path)) yield return found;
            }
            else if (playlists && M3uFile.IsPlaylist(path) && File.Exists(path))
            {
                foreach (string found in Expand(InPlaylist(path), playlists: false)) yield return found;
            }
            else if (IsSong(path) && SongStore.Exists(path))
            {
                yield return path;
            }
        }
    }

    public static bool IsSong(string path)
        => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a song can be read without being asked to: a song file on this machine that
    /// is an ordinary file, or inside an archive that is.
    /// </summary>
    /// <remarks>
    /// For what reads a list's songs before they are played. Opening a path on a share
    /// connects to the machine it names, and on Linux and macOS opening a FIFO waits for a
    /// writer and a device reads without end; those are read when they are played. Only an
    /// ordinary file has a size, which is what tells it apart without opening it.
    /// </remarks>
    public static bool IsLocalFile(string path)
    {
        if (!IsSong(path) || IsShare(path)) return false;
        try
        {
            return new FileInfo(SongStore.FileOf(path)) is { Exists: true, Length: > 0 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>A path that starts with two separators: a share, or a device on Windows.</summary>
    private static bool IsShare(string path) => path is ['\\' or '/', '\\' or '/', ..];

    private static IEnumerable<string> InFolder(string folder)
    {
        string[] found;
        try
        {
            // A subfolder we are not allowed into is passed over rather than failing the
            // walk: a drive's root always has one. Hidden and system folders are walked too.
            found = [.. Directory.EnumerateFiles(folder, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            })];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder we are not allowed to walk contributes nothing.
            yield break;
        }

        Array.Sort(found, StringComparer.OrdinalIgnoreCase);
        foreach (string file in found)
        {
            if (IsSong(file)) yield return file;
            else if (SongStore.IsArchive(file))
                foreach (string inside in InArchive(file)) yield return inside;
        }
    }

    private static IEnumerable<string> InPlaylist(string playlist)
    {
        try
        {
            return OnReach(M3uFile.Read(playlist), playlist);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A list we cannot read contributes nothing, as a folder we cannot walk does.
            return [];
        }
    }

    /// <summary>
    /// The songs a list names, less those on a share of a machine other than the one the list
    /// itself is on.
    /// </summary>
    /// <remarks>
    /// Whether a song is there is asked of each, and asking about a share connects to the
    /// machine it names, which on Windows hands it the user's credentials. The list decides
    /// what is named, not the user, and it may have come from anyone. A list on a share was
    /// opened from there by the user, so that machine is already being talked to.
    /// </remarks>
    internal static IEnumerable<string> OnReach(IEnumerable<string> songs, string playlist)
    {
        // The path as given first: elsewhere than Windows a share path is only a name, and
        // making it full would put it under the working folder.
        string? home = ServerOf(playlist) ?? ServerOf(Path.GetFullPath(playlist));
        return songs.Where(song => ServerOf(song) is not { } server
                                   || string.Equals(server, home, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The machine a share path names, or null for a path on this machine.</summary>
    private static string? ServerOf(string path)
        => IsShare(path) ? path[2..].Split('\\', '/')[0] : null;

    private static string[] InArchive(string archive)
    {
        string[] names;
        try
        {
            names = [.. SongStore.List(archive).Where(IsSong)];
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                      or UnauthorizedAccessException)
        {
            // An archive that will not open contributes nothing, like a folder we cannot walk.
            return [];
        }

        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        return [.. names.Select(name => SongStore.Combine(archive, name))];
    }
}
