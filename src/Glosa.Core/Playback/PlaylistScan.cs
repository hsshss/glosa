using Glosa.Core.Smf;

namespace Glosa.Core.Playback;

/// <summary>
/// Fills in the length and title of playlist entries by reading the files.
/// </summary>
/// <remarks>
/// Only a summary of each song is read (<see cref="SmfReader.ReadSummary(string, int, int)"/>).
/// </remarks>
public static class PlaylistScan
{
    /// <summary>
    /// Reads whatever has no length yet. <paramref name="scanned"/> is called for each entry
    /// that changed, on the calling thread.
    /// </summary>
    /// <param name="items">
    /// The entries to read. A caller on a worker hands in a copy taken on the thread that edits
    /// the list: the list itself can change under a pass, and so can the copying of it.
    /// </param>
    /// <param name="moduleFromData">
    /// What module the song's own messages point to, or empty: asked of every song read, so
    /// that the file only has to be opened once for all it can tell.
    /// </param>
    /// <returns>How many entries were read.</returns>
    public static int Fill(IEnumerable<PlaylistItem> items,
                           Func<string, SongSummary>? load = null,
                           Action<PlaylistItem>? scanned = null,
                           CancellationToken cancellation = default,
                           Func<SongSummary, string>? moduleFromData = null)
    {
        Func<string, SongSummary> read = load ?? (path => SmfReader.ReadSummary(path, 0));
        int count = 0;

        foreach (PlaylistItem item in items)
        {
            if (cancellation.IsCancellationRequested) break;
            if (item.DurationMs > 0) continue;

            try
            {
                SongSummary song = read(item.Path);
                item.DurationMs = song.DurationUs / 1000;
                if (Text.TitleText.IsBlank(item.Title)) item.Title = song.Title;
                if (moduleFromData is not null) item.ModuleFromData = moduleFromData(song);
                count++;
                scanned?.Invoke(item);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                                          or UnauthorizedAccessException)
            {
                // A file that cannot be read keeps its unknown length; playback reports it.
            }
        }

        return count;
    }
}
