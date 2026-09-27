namespace Glosa.Core.Playback;

/// <summary>Reads and writes playlists. See <see cref="YamlFile"/> for what the format is.</summary>
public static class PlaylistFile
{
    /// <summary>
    /// The version of the format this player reads and writes. Raised only when a file
    /// written in it could no longer be read as it was meant (IMPLEMENTATION.md).
    /// </summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// Reads a playlist, with each song's path made whole from the file's own folder.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is in a newer format.</exception>
    public static Playlist Load(string path)
    {
        Playlist list = Parse(YamlFile.Load<Playlist>(path));
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        foreach (PlaylistItem item in list.Items)
            if (!Path.IsPathFullyQualified(item.Path)) item.Path = Path.GetFullPath(item.Path, folder);
        return list;
    }

    /// <exception cref="InvalidDataException">The text is in a newer format.</exception>
    public static Playlist Parse(string yaml) => Parse(YamlFile.Parse<Playlist>(yaml));

    private static Playlist Parse(Playlist list)
        => list.Version <= FormatVersion
            ? Tidy(list)
            : throw new InvalidDataException(
                string.Format(Strings.FormatTooNew, list.Version, FormatVersion));

    /// <summary>
    /// Puts back what a key left without a value took away: the reader makes it null. An
    /// item with no path is dropped, having nothing to play.
    /// </summary>
    private static Playlist Tidy(Playlist list)
    {
        list.Name ??= string.Empty;
        list.Items = [.. (list.Items ?? []).Where(item => item is { Path.Length: > 0 })];
        foreach (PlaylistItem item in list.Items)
        {
            item.Title ??= string.Empty;
            item.Module ??= string.Empty;
        }
        return list;
    }

    public static void Save(Playlist playlist, string path)
    {
        playlist.Version = FormatVersion;
        YamlFile.Save(playlist, path);
    }

    public static string ToYaml(Playlist playlist) => YamlFile.ToYaml(playlist);
}
