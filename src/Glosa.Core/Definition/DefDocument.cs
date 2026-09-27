using Glosa.Core.Text;

namespace Glosa.Core.Definition;

/// <summary>One <c>key=value</c> line, kept in file order.</summary>
public readonly record struct DefEntry(string Key, string Value);

/// <summary>How TMIDI turns a DEF value into a number.</summary>
public static class ProfileValue
{
    /// <summary>
    /// C's <c>atoi</c>: optional space, an optional sign, then digits, stopping at the first
    /// character that is not one. Everything TMIDI reads as a number goes through
    /// this, so unreadable text is zero rather than an error.
    /// </summary>
    public static int Atoi(string text)
    {
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

        int sign = 1;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
            sign = text[i++] == '-' ? -1 : 1;

        long value = 0;
        while (i < text.Length && text[i] is >= '0' and <= '9')
        {
            value = value * 10 + (text[i++] - '0');
            if (value > int.MaxValue) value = int.MaxValue;
        }
        return (int)(sign * value);
    }
}

/// <summary>
/// A <c>[section]</c> and its entries.
/// </summary>
/// <remarks>
/// Entry order is part of the format's meaning, so entries are a list rather than a
/// dictionary: <c>[keyword]</c> is scanned top to bottom and the first match wins, and
/// patch maps are searched in the order they are written. Duplicate keys are kept for the
/// same reason; lookups return the first, matching <c>GetPrivateProfileString</c>.
/// </remarks>
public sealed class DefSection(string name, IReadOnlyList<DefEntry> entries)
{
    public string Name { get; } = name;

    public IReadOnlyList<DefEntry> Entries { get; } = entries;

    /// <summary>First entry with this key, ignoring case. Null when absent.</summary>
    public string? Get(string key)
    {
        foreach (DefEntry e in Entries)
            if (string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                return e.Value;
        return null;
    }

    public string Get(string key, string fallback) => Get(key) ?? fallback;

    /// <summary>
    /// Reads a key as a number, falling back only when the key is absent.
    /// </summary>
    /// <remarks>
    /// TMIDI runs the value through <c>atoi</c>, so a key that is present but holds
    /// nothing readable reads as zero rather than keeping the fallback. That matters because
    /// these are read with the current setting as the fallback: <c>DisableExclusive=</c>
    /// turns the flag off, it does not leave it alone.
    /// </remarks>
    public int GetInt(string key, int fallback)
    {
        string? raw = Get(key);
        return raw is null ? fallback : ProfileValue.Atoi(raw);
    }

    public bool GetBool(string key, bool fallback) => GetInt(key, fallback ? 1 : 0) != 0;

    public override string ToString() => $"[{Name}] ({Entries.Count} entries)";
}

/// <summary>
/// A parsed TMIDI emulation definition file.
/// </summary>
/// <remarks>
/// Not built on a general INI library: those store sections in dictionaries, losing the
/// ordering the format relies on for priority.
/// </remarks>
public sealed class DefDocument
{
    private readonly List<DefSection> _sections;

    private DefDocument(List<DefSection> sections) => _sections = sections;

    public IReadOnlyList<DefSection> Sections => _sections;

    /// <summary>First section with this name, ignoring case. Null when absent.</summary>
    public DefSection? this[string name]
    {
        get
        {
            foreach (DefSection s in _sections)
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    return s;
            return null;
        }
    }

    public bool Contains(string name) => this[name] is not null;

    /// <summary>Reads a DEF file. Content is Shift-JIS.</summary>
    public static DefDocument Load(string path) => Parse(File.ReadAllBytes(path));

    public static DefDocument Parse(ReadOnlySpan<byte> bytes) => ParseText(Cp932.Decode(bytes));

    public static DefDocument ParseText(string text)
    {
        var sections = new List<DefSection>();
        List<DefEntry>? entries = null;
        string sectionName = string.Empty;

        void Flush()
        {
            if (entries is not null) sections.Add(new DefSection(sectionName, entries));
        }

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            // Win32 treats ';' as a comment only when it opens the line; elsewhere it is data.
            if (line[0] is ';') continue;

            if (line[0] == '[')
            {
                int close = line.IndexOf(']');
                if (close < 0) continue;
                Flush();
                sectionName = line[1..close].Trim();
                entries = [];
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq < 0 || entries is null) continue;

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (key.Length == 0) continue;
            entries.Add(new DefEntry(key, value));
        }

        Flush();
        return new DefDocument(sections);
    }
}
