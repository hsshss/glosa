using Avalonia;
using Avalonia.Controls;

namespace Glosa.App.Controls;

/// <summary>
/// A setting and the <see cref="InfoTip"/> that explains it, side by side: the first child,
/// then the second just after it.
/// </summary>
/// <remarks>
/// The setting is given the width that is left once the tip has its own, so a long label
/// wraps rather than pushing the tip out of a narrow window. A horizontal stack panel would
/// give it all the width it asks for.
/// </remarks>
public sealed class TipRow : Panel
{
    private const double Spacing = 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children is not [Control label, Control tip]) return base.MeasureOverride(availableSize);

        tip.Measure(Size.Infinity);
        double room = Math.Max(0, availableSize.Width - tip.DesiredSize.Width - Spacing);
        label.Measure(availableSize.WithWidth(room));

        return new Size(label.DesiredSize.Width + Spacing + tip.DesiredSize.Width,
                        Math.Max(label.DesiredSize.Height, tip.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children is not [Control label, Control tip]) return base.ArrangeOverride(finalSize);

        label.Arrange(new Rect(0, 0, label.DesiredSize.Width, finalSize.Height));
        tip.Arrange(new Rect(label.DesiredSize.Width + Spacing, 0, tip.DesiredSize.Width, finalSize.Height));
        return finalSize;
    }
}
