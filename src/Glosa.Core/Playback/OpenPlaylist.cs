namespace Glosa.Core.Playback;

/// <summary>
/// A playlist open as a tab: its file, and where it was left.
/// </summary>
/// <remarks>
/// Where a list was left belongs to the player that left it there, not to the list. The
/// file is the songs and nothing else, so it reads the same whoever opens it and does not
/// change on disk because someone scrolled.
/// </remarks>
public sealed class OpenPlaylist
{
    /// <summary>The playlist file. A path rather than a name, so a list from anywhere stays open.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The row the list last played, or -1 for none. It opens on this row, so coming back
    /// to a list puts the cursor where the listening was.
    /// </summary>
    public int LastPlayed { get; set; } = -1;

    /// <summary>
    /// The row at the top of the view. A row number and not a pixel offset: offsets mean
    /// nothing once the row height changes, and a list is the same list at any size.
    /// </summary>
    public int TopRow { get; set; }
}

