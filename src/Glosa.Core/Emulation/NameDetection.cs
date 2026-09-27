namespace Glosa.Core.Emulation;

/// <summary>Where the words that <c>define.yaml</c>'s patterns are run over come from.</summary>
public enum DetectionSource
{
    /// <summary>
    /// The folder the song is in (<see cref="NameDetection.Folder"/>). A song inside an
    /// archive is in the archive, as though it were a folder.
    /// </summary>
    FolderPath,

    /// <summary>The song's file name, extension and all.</summary>
    FileName,

    /// <summary>The title inside the song.</summary>
    Title,

    /// <summary>
    /// The attached document: the text file beside the song, as far as
    /// <see cref="ModuleDefinition.DocumentLength"/> characters into it.
    /// </summary>
    Document,
}

/// <summary>Which of the models named in the text decides.</summary>
public enum MatchPosition
{
    /// <summary>The one that appears first.</summary>
    First,

    /// <summary>The one that appears last.</summary>
    Last,
}

/// <summary>
/// Which words a song is detected from, in which order, and which match among them decides.
/// </summary>
/// <remarks>
/// The words are put together into one text, a line each, in the order
/// <see cref="Sources"/> lists them (<see cref="Compose(string, string, string, int)"/>).
/// </remarks>
public sealed record NameDetection(IReadOnlyList<DetectionSource> Sources, MatchPosition Position)
{
    /// <summary>
    /// The title, then the file name, then the folder; the document is left out, and the
    /// model named first wins.
    /// </summary>
    public static NameDetection Default { get; } = new(
        [DetectionSource.Title, DetectionSource.FileName, DetectionSource.FolderPath],
        MatchPosition.First);

    /// <summary>Whether the words include <paramref name="source"/>.</summary>
    /// <remarks>For a caller that would have to read the document from disk to hand it in.</remarks>
    public bool Reads(DetectionSource source) => Sources.Contains(source);

    /// <summary>The text the patterns are run over, the sources a line each in their order.</summary>
    /// <param name="documentLength">How much of the document is read.</param>
    public string Compose(string path, string title, string document, int documentLength)
        => Compose(path, title, document, documentLength,
                   Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>The same, with the home folder given.</summary>
    public string Compose(string path, string title, string document, int documentLength, string home)
        => string.Join('\n', Sources.Distinct().Select(source => source switch
        {
            DetectionSource.FolderPath => Folder(path, home),
            DetectionSource.FileName => Path.GetFileName(path),
            DetectionSource.Title => title,
            DetectionSource.Document => Cut(document, documentLength),
            _ => string.Empty,
        }).Where(text => text.Length > 0));

    /// <summary>
    /// The folder a song is in, with the home folder taken off the front when it is under it.
    /// </summary>
    public static string Folder(string path, string home)
    {
        string folder = Path.GetDirectoryName(path) ?? string.Empty;
        string root = home.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 0) return folder;

        // Names that differ only in case are the same folder where the file system says so.
        StringComparison compare = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (folder.Equals(root, compare)) return string.Empty;
        return folder.Length > root.Length + 1
               && folder.StartsWith(root, compare)
               && folder[root.Length] is var separator
               && (separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar)
            ? folder[(root.Length + 1)..]
            : folder;
    }

    /// <summary>The document as far as <paramref name="length"/>, never halfway through a character.</summary>
    private static string Cut(string document, int length)
    {
        if (document.Length <= length) return document;
        if (length <= 0) return string.Empty;

        // A pair of UTF-16 units is one character, and half of one is not text at all.
        if (char.IsHighSurrogate(document[length - 1])) length--;
        return document[..length];
    }
}
