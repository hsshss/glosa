using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Glosa.App.Controls;

/// <summary>
/// The (i) beside a label, whose tooltip explains what the label is for.
/// </summary>
/// <remarks>
/// An explanation printed under every control is read once and then only takes up room, so
/// it waits here until the pointer asks for it.
///
/// The tooltip is a wrapping text block of its own width rather than the bare string. The
/// window-wide tooltip width is set for file paths, which want one long line; a sentence of
/// advice at that width reads as a banner.
/// </remarks>
public sealed class InfoTip : PathIcon
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<InfoTip, string?>(nameof(Text));

    static InfoTip()
    {
        TextProperty.Changed.AddClassHandler<InfoTip>((tip, _) => tip.UpdateTip());
    }

    public InfoTip()
    {
        // The look is PathIcon's, set in Dark.axaml under this class.
        Classes.Add("info");
        Background = Brushes.Transparent;
    }

    /// <summary>What the tooltip says.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(PathIcon);

    private void UpdateTip()
        => ToolTip.SetTip(this, Text is { Length: > 0 } text
            ? new TextBlock { Text = text, MaxWidth = 320, TextWrapping = TextWrapping.Wrap }
            : null);
}
