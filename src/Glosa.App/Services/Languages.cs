using System.Globalization;

namespace Glosa.App.Services;

/// <summary>A language the player can be shown in, as the settings name it.</summary>
/// <param name="Code">The culture, or empty for the system's.</param>
/// <param name="Name">What the choice is called: each language in itself, so it can be found
/// by someone who cannot read the one in use.</param>
public sealed record LanguageChoice(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>The languages there are words for, and picking one of them at startup.</summary>
/// <remarks>
/// The words are read as the windows are built, so a language takes effect at the next start.
/// </remarks>
public static class Languages
{
    /// <summary>The languages there are words for, English first.</summary>
    private static readonly (string Code, string Name)[] Offered = [("en", "English"), ("ja", "日本語")];

    private static IReadOnlyList<LanguageChoice>? _all;

    /// <summary>The system's language, then each of <see cref="Offered"/>.</summary>
    /// <remarks>
    /// Made when first asked for, not with the class: the first choice's name is in the
    /// language in use, which <see cref="Apply"/> has to have set by then.
    /// </remarks>
    public static IReadOnlyList<LanguageChoice> All => _all ??=
        [new(string.Empty, Strings.LanguageSystem), .. Offered.Select(l => new LanguageChoice(l.Code, l.Name))];

    /// <summary>The choice a saved code stands for; the system's for one not offered.</summary>
    public static LanguageChoice Find(string? code)
        => All.FirstOrDefault(choice => choice.Code == code) ?? All[0];

    /// <summary>
    /// Shows the player in <paramref name="code"/>'s language from here on, on every thread.
    /// The system's (empty) leaves the UI culture as the system set it.
    /// </summary>
    /// <remarks>Before anything reads <see cref="Strings"/>.</remarks>
    public static void Apply(string? code)
    {
        if (code is null || !Offered.Any(language => language.Code == code)) return;
        var culture = CultureInfo.GetCultureInfo(code);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
