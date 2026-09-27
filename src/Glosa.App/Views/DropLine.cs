using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Glosa.App.Views;

/// <summary>
/// The line that shows where a drag will land, drawn over the control in its adorner layer
/// so the control's own layout is left alone.
/// </summary>
/// <remarks>
/// In the text colour rather than the accent: the rows or the tab on either side of the line
/// are often the selected ones, which the accent paints, and an accent line would vanish
/// against them.
/// </remarks>
internal sealed class DropLine : Control
{
    public const double Thickness = 3;

    private Rect? _at;
    private IBrush? _brush;
    private AdornerLayer? _layer;

    public static DropLine? Over(TemplatedControl control)
    {
        if (AdornerLayer.GetAdornerLayer(control) is not { } layer) return null;

        var line = new DropLine { IsHitTestVisible = false, _brush = control.Foreground, _layer = layer };
        AdornerLayer.SetAdornedElement(line, control);
        layer.Children.Add(line);
        return line;
    }

    /// <summary>Puts the line on a rectangle in the control's coordinates, or hides it.</summary>
    public void Show(Rect? at)
    {
        if (_at == at) return;
        _at = at;
        InvalidateVisual();
    }

    public void Remove() => _layer?.Children.Remove(this);

    public override void Render(DrawingContext context)
    {
        if (_at is not { } at || _brush is null) return;
        context.FillRectangle(_brush, at);
    }
}
