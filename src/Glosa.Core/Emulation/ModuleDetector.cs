using Glosa.Core.Definition;
using Glosa.Core.Smf;
using Glosa.Core.Text;

namespace Glosa.Core.Emulation;

/// <summary>Where a detected module name came from.</summary>
public enum ModuleSource
{
    /// <summary>Nothing said which module, so the configured default stands.</summary>
    Default,
    /// <summary>
    /// A keyword of the DEF, or a pattern of <c>define.yaml</c>, matched in the words around
    /// the song: its folder, file name, title or document.
    /// </summary>
    Keyword,
    /// <summary>
    /// Nothing in the words: an exclusive message near the start of the song matched one of the
    /// data rules (<see cref="ModuleDefinition.ScanData(MidiSequence)"/>).
    /// </summary>
    Data,
}

/// <param name="Module">The name as written, before <c>[alias]</c> has had its say.</param>
/// <param name="Matched">
/// The keyword that matched, or the start of the message, for showing why.
/// </param>
public readonly record struct ModuleDetection(string Module, ModuleSource Source, string Matched)
{
    public override string ToString() => Source switch
    {
        ModuleSource.Keyword or ModuleSource.Data => $"{Module} ({Matched})",
        _ => Module,
    };
}

/// <summary>
/// Works out which module a song was written for, from its name and the words around it.
/// </summary>
/// <remarks>
/// The <c>[keyword]</c> search does not look inside the MIDI data, as TMIDI does not; the
/// DEF's <c>[GM InquiryRequest]</c> section is unused.
///
/// TMIDI's <c>&lt;song&gt;.TDF</c> is not read: the module set for a song belongs to the
/// playlist, where it is set and seen.
///
/// The scan runs over CP932 bytes rather than characters, as TMIDI's does: the delimiter
/// list mixes one- and two-byte characters, the length limit counts bytes, and the
/// case-insensitive compare is ASCII-only so it leaves the second byte of a double-byte
/// character alone.
///
/// The same search can be run with the patterns of <c>define.yaml</c> in place of the DEF's
/// <c>[keyword]</c> (<see cref="ModuleDefinition"/>). That one searches the words it is told
/// to, in the order it is told (<see cref="NameDetection"/>).
/// </remarks>
public static class ModuleDetector
{
    /// <summary>What TMIDI uses when <c>[keyword]</c> gives no length.</summary>
    public const int DefaultLength = 0x10000;

    private const string KeywordSection = "keyword";

    public static ModuleDetection Detect(
        DefDocument definition,
        string path,
        string title = "",
        string document = "",
        string fallback = "THRU")
        => Scan(definition, Join(Path.GetFileName(path), title, document))
           ?? Nothing(fallback);

    /// <summary>The same, with the patterns of <c>define.yaml</c> doing the search.</summary>
    /// <param name="sequence">
    /// The song, when it has been read: what the words leave undecided, its first messages
    /// are asked about. Without it the answer comes from the words alone.
    /// </param>
    /// <param name="how">
    /// Which words are searched, in which order, and which match decides; what
    /// <see cref="NameDetection.Default"/> says when not given.
    /// </param>
    public static ModuleDetection Detect(
        ModuleDefinition definition,
        string path,
        string title = "",
        string document = "",
        string fallback = "THRU",
        MidiSequence? sequence = null,
        NameDetection? how = null)
    {
        how ??= NameDetection.Default;
        string text = how.Compose(path, title, document, definition.DocumentLength);
        return definition.Scan(text, how.Position)
               ?? (sequence is null ? null : definition.ScanData(sequence))
               ?? Nothing(fallback);
    }

    private static ModuleDetection Nothing(string fallback)
        => new(fallback, ModuleSource.Default, "");

    /// <summary>
    /// Runs the keyword scan on its own, over text the caller has already put together.
    /// </summary>
    public static ModuleDetection? Scan(DefDocument definition, string text)
    {
        DefSection? keywords = definition[KeywordSection];
        if (keywords is null) return null;

        int length = keywords.GetInt("length", DefaultLength);
        byte[] delimiters = Cp932.Encoding.GetBytes(keywords.Get("delimiter", string.Empty));

        byte[] buffer = Cp932.Encoding.GetBytes(text);
        int limit = Math.Min(length, buffer.Length);
        Flatten(buffer, limit, delimiters);

        for (int at = 0; at < limit;)
        {
            while (at < limit && buffer[at] == ' ') at++;
            int start = at;
            while (at < limit && buffer[at] != ' ') at++;
            if (at == start) break;

            var token = buffer.AsSpan(start, at - start);
            foreach (DefEntry entry in keywords.Entries)
            {
                // The two settings live in the same section as the keywords themselves.
                if (entry.Key.Equals("length", StringComparison.OrdinalIgnoreCase) ||
                    entry.Key.Equals("delimiter", StringComparison.OrdinalIgnoreCase)) continue;

                if (!SameWord(token, Cp932.Encoding.GetBytes(entry.Key))) continue;

                // First match wins, left to right and in the order the DEF lists them, so
                // the order inside [keyword] is the priority.
                return new ModuleDetection(entry.Value, ModuleSource.Keyword, entry.Key);
            }
        }

        return null;
    }

    /// <summary>Builds the haystack: file name, then title, then the document text.</summary>
    private static string Join(params string[] parts)
        => string.Join(' ', parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Turns every delimiter into a space, so what is left is words.</summary>
    private static void Flatten(byte[] buffer, int limit, ReadOnlySpan<byte> delimiters)
    {
        for (int at = 0; at < limit;)
        {
            if (IsLeadByte(buffer[at]) && at + 1 < limit)
            {
                // The ideographic space counts as a delimiter whether or not it is listed.
                bool cut = (buffer[at] == 0x81 && buffer[at + 1] == 0x40) ||
                           ContainsPair(delimiters, buffer[at], buffer[at + 1]);
                if (cut) buffer[at] = buffer[at + 1] = (byte)' ';
                at += 2;
                continue;
            }

            byte b = buffer[at];
            if (b is 0x0D or 0x0A or 0x1A or 0x09 || ContainsSingle(delimiters, b))
                buffer[at] = (byte)' ';
            at++;
        }
    }

    private static bool ContainsSingle(ReadOnlySpan<byte> delimiters, byte value)
    {
        for (int i = 0; i < delimiters.Length;)
        {
            if (IsLeadByte(delimiters[i]) && i + 1 < delimiters.Length) { i += 2; continue; }
            if (delimiters[i] == value) return true;
            i++;
        }
        return false;
    }

    private static bool ContainsPair(ReadOnlySpan<byte> delimiters, byte lead, byte trail)
    {
        for (int i = 0; i < delimiters.Length;)
        {
            if (!IsLeadByte(delimiters[i]) || i + 1 >= delimiters.Length) { i++; continue; }
            if (delimiters[i] == lead && delimiters[i + 1] == trail) return true;
            i += 2;
        }
        return false;
    }

    /// <summary>
    /// Whole-word compare, ASCII case folded. A keyword only matches a token it covers
    /// completely, which is why <c>88</c> does not match <c>8850</c>.
    /// </summary>
    private static bool SameWord(ReadOnlySpan<byte> token, ReadOnlySpan<byte> keyword)
    {
        if (token.Length != keyword.Length) return false;
        for (int i = 0; i < token.Length; i++)
            if (Upper(token[i]) != Upper(keyword[i])) return false;
        return true;
    }

    /// <summary>ASCII only, so the trail byte of a double-byte character is left alone.</summary>
    private static byte Upper(byte b) => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b;

    private static bool IsLeadByte(byte b) => b is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC;
}
