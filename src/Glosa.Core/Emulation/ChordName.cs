namespace Glosa.Core.Emulation;

/// <summary>
/// Names the chord a set of sounding notes spells.
/// </summary>
/// <remarks>
/// It matches the pitch classes exactly against a table of shapes, so a set that
/// is not a chord gets no name rather than the nearest guess. The bass note breaks ties,
/// which is what makes an inversion read as the chord it is rather than as its relative.
/// </remarks>
public static class ChordName
{
    private static readonly string[] Roots =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>Shape by interval set, most specific first so a subset never hides a superset.</summary>
    private static readonly (string Suffix, int[] Intervals)[] Shapes =
    [
        ("9",     [0, 2, 4, 7, 10]),
        ("maj9",  [0, 2, 4, 7, 11]),
        ("m9",    [0, 2, 3, 7, 10]),
        ("6",     [0, 4, 7, 9]),
        ("m6",    [0, 3, 7, 9]),
        ("7",     [0, 4, 7, 10]),
        ("maj7",  [0, 4, 7, 11]),
        ("m7",    [0, 3, 7, 10]),
        ("mmaj7", [0, 3, 7, 11]),
        ("m7-5",  [0, 3, 6, 10]),
        ("dim7",  [0, 3, 6, 9]),
        ("7sus4", [0, 5, 7, 10]),
        ("add9",  [0, 2, 4, 7]),
        ("",      [0, 4, 7]),
        ("m",     [0, 3, 7]),
        ("dim",   [0, 3, 6]),
        ("aug",   [0, 4, 8]),
        ("sus4",  [0, 5, 7]),
        ("sus2",  [0, 2, 7]),
        ("5",     [0, 7]),
    ];

    /// <summary>
    /// Names what <paramref name="sounding"/> spells, or an empty string when it spells
    /// nothing. <paramref name="sounding"/> is indexed by note number and non-zero where a
    /// note is down.
    /// </summary>
    public static string Detect(ReadOnlySpan<byte> sounding)
    {
        int classes = 0;
        int bass = -1;
        for (int note = 0; note < sounding.Length; note++)
        {
            if (sounding[note] == 0) continue;
            classes |= 1 << (note % 12);
            if (bass < 0) bass = note % 12;
        }

        if (bass < 0) return string.Empty;
        if (System.Numerics.BitOperations.PopCount((uint)classes) == 1) return Roots[bass];

        string? fallback = null;
        foreach ((string suffix, int[] intervals) in Shapes)
        {
            for (int root = 0; root < 12; root++)
            {
                int mask = 0;
                foreach (int interval in intervals) mask |= 1 << ((root + interval) % 12);
                if (mask != classes) continue;

                // The shape table is ordered, so the first match on the bass is the answer.
                if (root == bass) return Roots[root] + suffix;
                fallback ??= $"{Roots[root]}{suffix}/{Roots[bass]}";
            }
        }

        return fallback ?? string.Empty;
    }
}
