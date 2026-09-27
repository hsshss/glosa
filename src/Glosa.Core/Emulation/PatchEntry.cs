using Glosa.Core.Definition;

namespace Glosa.Core.Emulation;

/// <summary>
/// One patch map rule, written in the DEF as <c>[DM:]lsb:msb:pc = lsb:msb:pc</c>.
/// </summary>
/// <remarks>
/// <c>*</c> becomes <see cref="Wildcard"/>: on the source side it matches anything, on the
/// destination side it means "leave unchanged". In a drum map the fields are reused —
/// <see cref="SrcPc"/> is the note number and <see cref="SrcMsb"/> the drum set.
/// </remarks>
public readonly record struct PatchEntry(
    byte SrcPc, byte SrcMsb, byte SrcLsb, bool SrcDrum,
    byte DstPc, byte DstMsb, byte DstLsb, bool DstDrum)
{
    public const byte Wildcard = 0xFF;

    /// <summary>
    /// Parses one entry. Always produces a rule.
    /// </summary>
    /// <remarks>
    /// As in TMIDI, nothing is validated: three colon-separated fields are taken off each
    /// side and everything that does not start with <c>*</c> goes through <c>atoi</c>, so a
    /// missing field or a word reads as zero. A stray <c>comment=...</c> line in a patch
    /// section registers as <c>0:0:0 = 0:0:0</c>, which only intercepts program 0 on bank
    /// 0:0 of a melodic part, since a rule without a wildcard is reached only by the
    /// exact-match shape.
    /// </remarks>
    /// <param name="readable">
    /// False when the line did not look like a rule. The entry is produced either way; this
    /// only feeds the warning that tells whoever wrote the DEF what happened.
    /// </param>
    public static PatchEntry Parse(string key, string value, out bool readable)
    {
        // TMIDI tests two letters and then skips three characters without looking at
        // the third, so "DM" followed by anything is a drum rule.
        string source = key.Trim();
        bool drum = source.Length >= 2 && source[0] is 'D' or 'd' && source[1] is 'M' or 'm';
        bool spelled = !drum || source.StartsWith("DM:", StringComparison.OrdinalIgnoreCase);
        if (drum) source = source.Length >= 3 ? source[3..] : string.Empty;

        bool ok = spelled;
        ReadTriple(source, out byte srcLsb, out byte srcMsb, out byte srcPc, ref ok);
        ReadTriple(value.Trim(), out byte dstLsb, out byte dstMsb, out byte dstPc, ref ok);

        readable = ok;
        return new PatchEntry(srcPc, srcMsb, srcLsb, drum, dstPc, dstMsb, dstLsb, drum);
    }

    /// <summary>Reads <c>lsb:msb:pc</c>. A field that is not there is an empty one.</summary>
    private static void ReadTriple(
        string text, out byte lsb, out byte msb, out byte pc, ref bool readable)
    {
        string[] parts = text.Split(':');
        if (parts.Length != 3) readable = false;

        lsb = ReadField(Field(parts, 0), ref readable);
        msb = ReadField(Field(parts, 1), ref readable);
        pc = ReadField(Field(parts, 2), ref readable);
    }

    private static string Field(string[] parts, int index)
        => index < parts.Length ? parts[index] : string.Empty;

    /// <summary>
    /// Reads one field, and reports whether it starts with something <c>atoi</c> can use.
    /// </summary>
    /// <remarks>
    /// Only the start is judged, because the shipped DEF writes a comment after the last
    /// field (<c>0:16:35 ; Dance</c>) and <c>atoi</c> simply stops there. What is worth a
    /// warning is a field that begins with neither <c>*</c> nor a digit, since that silently
    /// reads as zero — a padded <c>" *"</c> included, as the wildcard test only looks at the
    /// very first character.
    /// </remarks>
    private static byte ReadField(string field, ref bool readable)
    {
        bool wildcard = field.StartsWith('*');
        if (!wildcard && !char.IsAsciiDigit(field.TrimStart().FirstOrDefault()))
            readable = false;

        return wildcard ? Wildcard : (byte)ProfileValue.Atoi(field);
    }
}

/// <param name="Added">How many rules the section contributed.</param>
/// <param name="Unreadable">
/// Keys that did not look like a rule. They were registered anyway, as TMIDI does.
/// </param>
public readonly record struct PatchMapAddition(int Added, IReadOnlyList<string> Unreadable);

/// <summary>
/// The melodic and drum rule lists. Sections named by <c>PatchMap=</c> and
/// <c>PatchDrumMap=</c> are appended in the order they are written, because lookups take
/// the first match.
/// </summary>
public sealed class PatchMapSet
{
    /// <summary>TMIDI stores at most 2048 rules per map.</summary>
    public const int Capacity = 0x800;

    private readonly List<PatchEntry> _melodic = [];
    private readonly List<PatchEntry> _drum = [];

    public IReadOnlyList<PatchEntry> Melodic => _melodic;
    public IReadOnlyList<PatchEntry> Drum => _drum;

    /// <summary>Appends a section's rules.</summary>
    public PatchMapAddition AddMelodic(DefSection? section) => Add(_melodic, section);

    public PatchMapAddition AddDrum(DefSection? section) => Add(_drum, section);

    private static PatchMapAddition Add(List<PatchEntry> target, DefSection? section)
    {
        if (section is null) return new PatchMapAddition(0, []);

        int added = 0;
        List<string>? unreadable = null;
        foreach (DefEntry line in section.Entries)
        {
            if (target.Count >= Capacity) break;

            target.Add(PatchEntry.Parse(line.Key, line.Value, out bool readable));
            added++;
            if (!readable) (unreadable ??= []).Add(line.Key);
        }
        return new PatchMapAddition(added, unreadable ?? (IReadOnlyList<string>)[]);
    }
}
