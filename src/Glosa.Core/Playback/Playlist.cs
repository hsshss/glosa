namespace Glosa.Core.Playback;

/// <summary>One song in a <see cref="Playlist"/>.</summary>
public sealed class PlaylistItem
{
    public string Path { get; set; } = string.Empty;

    /// <summary>Song title, from the SMF's first track name. Empty when the file has none.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Length in milliseconds, or 0 while it is still unknown.</summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// The module this song is written for, or empty (the normal state) to let the detector
    /// work it out.
    /// </summary>
    /// <remarks>
    /// Per song, as a list can hold a GS piece next to an XG one.
    /// </remarks>
    public string Module { get; set; } = string.Empty;

    /// <summary>
    /// The module the song's own messages point to, found when the file was last read; empty
    /// when they were looked at and point to none, and null when they have not been looked at.
    /// </summary>
    /// <remarks>
    /// Kept beside the length and the title for the same reason they are: they come from
    /// reading the whole file, which the list does not do again once it knows the answer.
    /// Detection falls back on it when the words around the song name nothing. It is what
    /// the data rules said at the time, so a change to those rules shows once the song is
    /// read again — when it plays.
    /// </remarks>
    public string? ModuleFromData { get; set; }

    /// <summary>What a list shows: the title when there is one, else the bare file name.</summary>
    /// <remarks>
    /// Kept out of the file: it is derived from the two fields above, and writing it would
    /// invite someone to edit a copy that has no effect.
    ///
    /// A title of nothing but spacing counts as no title. Track names are often padded out
    /// to a column width, and one that was never filled in arrives as a run of spaces: a
    /// name as far as the file is concerned, and a blank row as far as anyone reading is.
    /// </remarks>
    [YamlDotNet.Serialization.YamlIgnore]
    public string Display => Text.TitleText.IsBlank(Title)
        ? System.IO.Path.GetFileNameWithoutExtension(Path)
        : Title;
}

public sealed class Playlist
{
    /// <summary>
    /// The format the file is in (<see cref="PlaylistFile.FormatVersion"/>); 0 when it says
    /// none.
    /// </summary>
    public int Version { get; set; }

    /// <summary>The name its tab shows; empty for the file's own name.</summary>
    public string Name { get; set; } = string.Empty;

    public List<PlaylistItem> Items { get; set; } = [];

    // Where the list was left is kept with the player's open tabs (OpenPlaylist), not here.
}

/// <summary>
/// How names of files and folders are put in order, wherever the player sorts by them.
/// </summary>
/// <remarks>
/// The way a file manager lists them rather than by character code: by the language's
/// rules, so marks such as <c>_</c> come before digits and letters instead of after them,
/// and with runs of digits read as numbers, so <c>2.mid</c> comes before <c>10.mid</c>.
/// Made on each use, from the culture the player is running in at the time.
/// </remarks>
public static class NameOrder
{
    public static StringComparer Comparer => StringComparer.Create(
        System.Globalization.CultureInfo.CurrentCulture,
        System.Globalization.CompareOptions.IgnoreCase
        | System.Globalization.CompareOptions.NumericOrdering);
}
