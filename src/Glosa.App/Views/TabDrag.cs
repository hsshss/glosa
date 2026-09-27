using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Glosa.App.Views;

/// <summary>
/// Drag-to-reorder for the tabs of a tab control.
/// </summary>
/// <remarks>
/// As <see cref="ReorderDrag"/> does for rows, sideways: a line between two tabs shows where
/// the tab will land, and it is moved only when the button comes up.
/// </remarks>
internal sealed class TabDrag
{
    /// <summary>How far the pointer must travel before a press becomes a drag.</summary>
    private const double Threshold = 5;

    private readonly TabControl _tabs;
    private readonly Func<object, int, bool> _moveTo;

    /// <summary>The tab the button went down on, while it is held.</summary>
    private TabItem? _pressed;

    private Point _from;
    private bool _moving;

    /// <summary>The gap the tab goes into if the button comes up now.</summary>
    private int? _gap;

    private DropLine? _line;

    private TabDrag(TabControl tabs, Func<object, int, bool> moveTo)
    {
        _tabs = tabs;
        _moveTo = moveTo;
    }

    /// <summary>
    /// Starts watching a tab control for reorder drags.
    /// </summary>
    /// <param name="moveTo">
    /// Called as the button comes up, with the tab's item and the gap it should land in: 0 is
    /// before the first tab, and the item count is after the last. True means the tabs
    /// changed.
    /// </param>
    /// <remarks>
    /// The handlers tunnel, so they see the press whatever the tab does with it.
    /// </remarks>
    public static void Attach(TabControl tabs, Func<object, int, bool> moveTo)
    {
        var drag = new TabDrag(tabs, moveTo);

        tabs.AddHandler(InputElement.PointerPressedEvent, drag.OnPressed, RoutingStrategies.Tunnel);
        tabs.AddHandler(InputElement.PointerMovedEvent, drag.OnMoved, RoutingStrategies.Tunnel);
        tabs.AddHandler(InputElement.PointerReleasedEvent, drag.OnReleased, RoutingStrategies.Tunnel);
        // Direct, as the event is declared: registered to tunnel, it would never fire.
        tabs.AddHandler(InputElement.PointerCaptureLostEvent, drag.OnCaptureLost, RoutingStrategies.Direct);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_tabs).Properties.IsLeftButtonPressed) return;

        // A tab of this control, not something in the page below the strip.
        if ((e.Source as Visual)?.FindAncestorOfType<TabItem>(includeSelf: true) is not { } tab) return;
        if (_tabs.IndexFromContainer(tab) < 0) return;

        _pressed = tab;
        _from = e.GetPosition(_tabs);
        _moving = false;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null) return;

        // The button came up somewhere the release was not delivered: that was the drop.
        if (!e.GetCurrentPoint(_tabs).Properties.IsLeftButtonPressed)
        {
            Finish(e.Pointer, drop: true);
            return;
        }

        Point at = e.GetPosition(_tabs);
        if (!_moving)
        {
            if (Math.Abs(at.X - _from.X) < Threshold) return;

            // Held for the rest of the gesture, so the moves and the release all arrive here
            // whatever is under the pointer.
            e.Pointer.Capture(_tabs);

            _line = DropLine.Over(_tabs);
            _moving = true;
        }

        _gap = GapAt(at.X);
        _line?.Show(LineAt(_gap.Value));
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e) => Finish(e.Pointer, drop: true);

    /// <summary>The drag was taken away — the window lost the pointer — and nothing moves.</summary>
    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => Finish(null, drop: false);

    private void Finish(IPointer? pointer, bool drop)
    {
        TabItem? pressed = _pressed;
        bool moving = _moving;
        int? gap = _gap;
        _pressed = null;
        _moving = false;
        _gap = null;

        _line?.Remove();
        _line = null;

        // Let go before the tab moves: letting go raises capture-lost, which lands back here
        // and finds nothing left to do.
        if (pointer is not null && ReferenceEquals(pointer.Captured, _tabs)) pointer.Capture(null);

        if (moving && drop && gap is { } to && pressed is not null
            && _tabs.ItemFromContainer(pressed) is { } item)
            _moveTo(item, to);
    }

    /// <summary>
    /// Which gap between tabs the pointer is level with: before the first tab whose middle
    /// it has not reached.
    /// </summary>
    /// <remarks>
    /// Only across, so a pointer straying above or below the strip still says where it is.
    /// </remarks>
    private int GapAt(double x)
    {
        int count = _tabs.ItemCount;
        for (int i = 0; i < count; i++)
            if (Bounds(i) is { } tab && x < tab.Center.X) return i;

        return count;
    }

    /// <summary>
    /// Where the line for a gap goes: on the left edge of the tab after it, or on the right
    /// edge of the last tab. Null when neither tab is laid out.
    /// </summary>
    /// <remarks>
    /// Held inside the control, where the first tab's edge would cut the line in half.
    /// </remarks>
    private Rect? LineAt(int gap)
    {
        Rect? tab = Bounds(gap);
        double? x = tab?.Left;
        if (tab is null && Bounds(gap - 1) is { } before)
        {
            tab = before;
            x = before.Right;
        }

        if (tab is not { } edge || x is not { } at) return null;

        double half = DropLine.Thickness / 2;
        double mid = Math.Clamp(at, half, Math.Max(half, _tabs.Bounds.Width - half));
        return new Rect(mid - half, edge.Top, DropLine.Thickness, edge.Height);
    }

    /// <summary>Where a tab is, in the control's coordinates. Null when it is not laid out.</summary>
    private Rect? Bounds(int index)
    {
        if (index < 0 || _tabs.ContainerFromIndex(index) is not Control tab) return null;
        if (tab.TranslatePoint(new Point(0, 0), _tabs) is not { } top) return null;
        return new Rect(top, tab.Bounds.Size);
    }
}
