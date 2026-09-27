namespace Glosa.Core.Text;

/// <summary>
/// Tidies the titles that come out of MIDI files.
/// </summary>
/// <remarks>
/// Track names are laid out to look right in whatever wrote them, so they arrive padded and
/// spaced for a display that is not ours. This is a setting rather than done always,
/// because the spacing is sometimes the point.
/// </remarks>
public static class TitleText
{
    /// <summary>Ideographic space, which counts as padding just as much as the ASCII one.</summary>
    private const char IdeographicSpace = '　';

    /// <summary>
    /// Trims the ends and collapses every run of spacing to a single space.
    /// </summary>
    public static string Tidy(string text)
    {
        if (text.Length == 0) return text;

        var result = new System.Text.StringBuilder(text.Length);
        bool pendingSpace = false;

        foreach (char c in text)
        {
            if (IsSpacing(c))
            {
                // Held back rather than written, so a run at the end disappears on its own.
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace) result.Append(' ');
            pendingSpace = false;
            result.Append(c);
        }

        return result.ToString();
    }

    public static string Tidy(string text, bool enabled) => enabled ? Tidy(text) : text;

    /// <summary>
    /// True when there is nothing here but spacing.
    /// </summary>
    /// <remarks>
    /// A track name padded out to a column width and then left empty is a name in the file
    /// and no name to read, so the places that ask whether a song has a title ask this
    /// rather than whether the string is empty.
    /// </remarks>
    public static bool IsBlank(string text)
    {
        foreach (char c in text) if (!IsSpacing(c)) return false;
        return true;
    }

    private static bool IsSpacing(char c) => c == IdeographicSpace || char.IsWhiteSpace(c);
}
