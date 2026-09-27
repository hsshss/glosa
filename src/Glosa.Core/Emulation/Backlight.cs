using Glosa.Core.Definition;

namespace Glosa.Core.Emulation;

/// <summary>A backlight colour, in the sRGB channels a UI wants.</summary>
public readonly record struct BacklightColor(byte R, byte G, byte B)
{
    /// <summary>Reads a Win32 <c>COLORREF</c>, which packs the channels as 0x00BBGGRR.</summary>
    public static BacklightColor FromColorRef(uint colorRef)
        => new((byte)(colorRef & 0xFF), (byte)((colorRef >> 8) & 0xFF), (byte)((colorRef >> 16) & 0xFF));

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// The lamp behind the LCD. Which colour a module gets comes from the DEF's
/// <c>[LiquidBackLight]</c> section, which names a maker rather than a colour.
/// </summary>
/// <remarks>
/// The four names and their colours are built into TMIDI; anything else in the
/// section is read as a number and used as a <c>COLORREF</c> directly, so a DEF can pick a
/// colour the player has no name for.
/// </remarks>
public static class Backlight
{
    /// <summary>Roland's orange.</summary>
    public static readonly BacklightColor Roland = BacklightColor.FromColorRef(0x008CFF);

    /// <summary>Yamaha's yellow-green.</summary>
    public static readonly BacklightColor Yamaha = BacklightColor.FromColorRef(0x00FFC0);

    /// <summary>Korg's emerald green.</summary>
    public static readonly BacklightColor Korg = BacklightColor.FromColorRef(0x80FF00);

    /// <summary>The pale yellow everything else gets.</summary>
    public static readonly BacklightColor Other = BacklightColor.FromColorRef(0x80FFFF);

    /// <summary>Resolves the colour for <paramref name="module"/>, aliases included.</summary>
    public static BacklightColor Resolve(DefDocument definition, string module)
    {
        string name = EmulationResolver.Alias(definition["alias"], module);
        string? value = definition["LiquidBackLight"]?.Get(name);
        return Parse(value);
    }

    public static BacklightColor Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Other;

        if (value.Equals("ROLAND", StringComparison.OrdinalIgnoreCase)) return Roland;
        if (value.Equals("YAMAHA", StringComparison.OrdinalIgnoreCase)) return Yamaha;
        if (value.Equals("OTHER", StringComparison.OrdinalIgnoreCase)) return Other;
        if (value.Equals("KORG", StringComparison.OrdinalIgnoreCase)) return Korg;

        // Anything else is a number. TMIDI uses strtoul with base detection, so
        // "0x80FF00" and "8452352" both work; a string that is not a number reads as 0.
        return BacklightColor.FromColorRef(ParseColorRef(value));
    }

    private static uint ParseColorRef(string text)
    {
        string trimmed = text.Trim();
        bool hex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (hex) trimmed = trimmed[2..];

        return uint.TryParse(
            trimmed,
            hex ? System.Globalization.NumberStyles.HexNumber
                : System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out uint value)
            ? value
            : 0;
    }
}
