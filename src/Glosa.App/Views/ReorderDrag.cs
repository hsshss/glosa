using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Glosa.App.Views;

/// <summary>
/// Drag-to-reorder for a list box: the playlist's rows, the port maps and the detection
/// sources.
/// </summary>
/// <remarks>
/// A line between two rows shows where the rows will land, and they are moved only when the
/// button comes up. Changing the list under the pointer mid-drag would make positions read
/// rows that have gone, and make the virtualizing panel lose its place while scrolling.
///
/// The list scrolls while a row is held near its top or bottom edge (<see cref="OnTick"/>).
/// </remarks>
internal sealed class ReorderDrag
{
    /// <summary>How far the pointer must travel before a press becomes a drag.</summary>
    private const double Threshold = 5;

    /// <summary>How deep the band along the top and bottom edge is that scrolls the list.</summary>
    private const double Edge = 24;

    /// <summary>
    /// How fast the list scrolls, in pixels a second, with the pointer on the edge of the
    /// list. Slower further in, faster beyond it, so how far the hand goes sets the pace.
    /// </summary>
    private const double EdgeSpeed = 300;

    /// <summary>The fastest the edge scrolls, however far out the pointer goes.</summary>
    private const double MaxSpeed = 3000;

    /// <summary>The drag each list has, so a second attach replaces the first.</summary>
    private static readonly ConditionalWeakTable<ListBox, ReorderDrag> Attached = new();

    private readonly ListBox _list;
    private readonly Func<PointerPressedEventArgs, bool> _press;
    private readonly Func<bool> _begin;
    private readonly Func<int, bool> _moveTo;
    private readonly Action _dropped;

    private Point _from;
    private bool _armed;
    private bool _moving;

    /// <summary>Where the pointer last was, in the list's coordinates.</summary>
    private Point _at;

    /// <summary>The gap the rows go into if the button comes up now.</summary>
    private int? _gap;

    private DropLine? _line;

    private readonly DispatcherTimer _timer;
    private readonly System.Diagnostics.Stopwatch _clock = new();

    private ReorderDrag(ListBox list, Func<PointerPressedEventArgs, bool> press,
                        Func<bool> begin, Func<int, bool> moveTo, Action dropped)
    {
        _list = list;
        _press = press;
        _begin = begin;
        _moveTo = moveTo;
        _dropped = dropped;
        // Not the constructor that takes the handler: that one starts the timer, and a list
        // that is not being dragged would scroll on its own.
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// Starts watching a list box for reorder drags.
    /// </summary>
    /// <param name="press">
    /// Called as the button goes down over a row, before the list box has had its say on the
    /// selection. False ignores the press.
    /// </param>
    /// <param name="begin">
    /// Called once the pointer has travelled far enough, to settle what travels. False
    /// cancels.
    /// </param>
    /// <param name="moveTo">
    /// Called as the button comes up, with the gap the rows should land in: 0 is above the
    /// first row, 1 between the first and the second, and the item count is below the last.
    /// True means the list changed.
    /// </param>
    /// <param name="dropped">Called after <paramref name="moveTo"/> has changed the list.</param>
    /// <remarks>
    /// The handlers tunnel: a row marks the press handled as it selects itself, so a
    /// bubbling handler would never see the press that arms a drag.
    /// </remarks>
    public static ReorderDrag Attach(ListBox list, Func<PointerPressedEventArgs, bool> press,
                                     Func<bool> begin, Func<int, bool> moveTo, Action dropped)
    {
        // A list can be loaded more than once, and two drags on one list would each scroll it
        // and each move the rows.
        if (Attached.TryGetValue(list, out ReorderDrag? old)) old.Detach();

        var drag = new ReorderDrag(list, press, begin, moveTo, dropped);
        Attached.AddOrUpdate(list, drag);

        list.AddHandler(InputElement.PointerPressedEvent, drag.OnPressed, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerMovedEvent, drag.OnMoved, RoutingStrategies.Tunnel);
        list.AddHandler(InputElement.PointerReleasedEvent, drag.OnReleased, RoutingStrategies.Tunnel);
        // Direct, as the event is declared: registered to tunnel, it would never fire.
        list.AddHandler(InputElement.PointerCaptureLostEvent, drag.OnCaptureLost, RoutingStrategies.Direct);

        return drag;
    }

    private void Detach()
    {
        Finish(null, drop: false);

        _list.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        _list.RemoveHandler(InputElement.PointerMovedEvent, OnMoved);
        _list.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
        _list.RemoveHandler(InputElement.PointerCaptureLostEvent, OnCaptureLost);
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) return;
        if (!_press(e)) return;

        _from = e.GetPosition(_list);
        _armed = true;
        _moving = false;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_armed) return;

        // The button came up somewhere the release was not delivered: that was the drop.
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed)
        {
            Finish(e.Pointer, drop: true);
            return;
        }

        _at = e.GetPosition(_list);
        if (!_moving)
        {
            if (Math.Abs(_at.Y - _from.Y) < Threshold) return;
            if (!_begin()) { _armed = false; return; }

            // Held for the rest of the gesture, so the moves and the release all arrive here
            // whatever is under the pointer.
            e.Pointer.Capture(_list);

            _line = DropLine.Over(_list);
            _list.LayoutUpdated += OnLayoutUpdated;
            _moving = true;
            _clock.Restart();
            _timer.Start();
        }

        Track();
    }

    /// <summary>
    /// Reads the gap under the pointer again when the rows have moved under it — the list
    /// scrolled — and puts the line back on it.
    /// </summary>
    /// <remarks>
    /// After layout rather than as the scroll is asked for: until then the rows are still
    /// where they were before it, and the pointer would be over the wrong one.
    /// </remarks>
    private void OnLayoutUpdated(object? sender, EventArgs e) => Track();

    private void Track()
    {
        if (!_moving) return;

        _gap = GapAt(Inside(_at)) ?? _gap;
        _line?.Show(_gap is { } gap ? LineAt(gap) : null);
    }

    /// <summary>
    /// Scrolls the list while the row is held near its top or bottom edge, or beyond it.
    /// </summary>
    /// <remarks>
    /// Paced by the clock rather than by the tick, so a late tick scrolls further rather
    /// than the list slowing down when the machine is busy.
    /// </remarks>
    private void OnTick(object? sender, EventArgs e)
    {
        if (!_moving) return;

        double seconds = _clock.Elapsed.TotalSeconds;
        _clock.Restart();

        if (Viewport() is not { } view) return;

        // Never more than a third of the list each, so a short list still has a middle
        // where it holds still.
        double edge = Math.Min(Edge, view.Height / 3);
        if (edge <= 0) return;

        double depth = _at.Y < view.Top + edge ? _at.Y - (view.Top + edge)
                     : _at.Y > view.Bottom - edge ? _at.Y - (view.Bottom - edge)
                     : 0;
        if (depth == 0) return;

        ScrollBy(Math.Clamp(depth / edge * EdgeSpeed, -MaxSpeed, MaxSpeed) * seconds);
    }

    private void ScrollBy(double dy)
    {
        if (_list.Scroll is not { } scroll) return;

        double bottom = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        double y = Math.Clamp(scroll.Offset.Y + dy, 0, bottom);
        if (y != scroll.Offset.Y) scroll.Offset = new Vector(scroll.Offset.X, y);
    }

    /// <summary>Where the rows are shown, in the list's coordinates.</summary>
    private Rect? Viewport()
    {
        if (_list.Scroll is not Visual scroller) return null;
        if (scroller.FindDescendantOfType<ScrollContentPresenter>() is not { } presenter) return null;
        if (presenter.TranslatePoint(new Point(0, 0), _list) is not { } top) return null;
        return new Rect(top, presenter.Bounds.Size);
    }

    /// <summary>
    /// The pointer brought inside the rows.
    /// </summary>
    /// <remarks>
    /// Above or below the list is how it is made to scroll, and the rows go next to the row
    /// on the edge the pointer is past; beside it, the row level with the pointer is still
    /// the one meant.
    /// </remarks>
    private Point Inside(Point at)
    {
        if (Viewport() is not { } view || view.Width < 2 || view.Height < 2) return at;
        return new Point(Math.Clamp(at.X, view.Left + 1, view.Right - 1),
                         Math.Clamp(at.Y, view.Top + 1, view.Bottom - 1));
    }

    /// <summary>
    /// Where the line for a gap goes: across the rows, on the top of the row below it, or
    /// on the bottom of the last row. Null when neither row is laid out.
    /// </summary>
    /// <remarks>
    /// Held inside the view: the edge a gap stands for can be just past the view's, on a row
    /// cut off by it, where a line would be clipped or not drawn at all.
    /// </remarks>
    private Rect? LineAt(int gap)
    {
        if (Viewport() is not { } view) return null;

        double? y = null;
        if (_list.ContainerFromIndex(gap) is Control below
            && below.TranslatePoint(new Point(0, 0), _list) is { } top)
            y = top.Y;
        else if (gap > 0 && _list.ContainerFromIndex(gap - 1) is Control above
                 && above.TranslatePoint(new Point(0, above.Bounds.Height), _list) is { } bottom)
            y = bottom.Y;

        if (y is not { } at) return null;

        double half = DropLine.Thickness / 2;
        double mid = Math.Clamp(at, view.Top + half, view.Bottom - half);
        return new Rect(view.Left, mid - half, view.Width, DropLine.Thickness);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e) => Finish(e.Pointer, drop: true);

    /// <summary>The drag was taken away — the window lost the pointer — and nothing moves.</summary>
    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => Finish(null, drop: false);

    private void Finish(IPointer? pointer, bool drop)
    {
        bool moving = _moving;
        int? gap = _gap;
        _armed = false;
        _moving = false;
        _gap = null;

        if (moving)
        {
            _timer.Stop();
            _list.LayoutUpdated -= OnLayoutUpdated;
            _line?.Remove();
            _line = null;
        }

        // Let go before the rows move: letting go raises capture-lost, which lands back here
        // and finds nothing left to do.
        if (pointer is not null && ReferenceEquals(pointer.Captured, _list)) pointer.Capture(null);

        if (moving && drop && gap is { } to && _moveTo(to)) _dropped();
    }

    /// <summary>
    /// Which gap between rows the pointer is in, or null when it is over no row.
    /// </summary>
    /// <remarks>
    /// The nearer edge of the row under the pointer: rows go <em>between</em> two rows, not
    /// <em>onto</em> one.
    ///
    /// Only a row of this list answers. Anything else (the scroll bar, the space below the
    /// last row, a row of a nested list) leaves the gap where it was, so a pointer straying
    /// sideways does not throw the rows to the end; the gap below the last row is reached
    /// by going below its middle.
    /// </remarks>
    private int? GapAt(Point at)
    {
        if (at.X < 0 || at.X > _list.Bounds.Width || at.Y < 0 || at.Y > _list.Bounds.Height)
            return null;

        if (_list.InputHitTest(at) is not Visual hit) return null;
        if (hit.FindAncestorOfType<ListBoxItem>() is not { } row) return null;

        int index = _list.IndexFromContainer(row);
        if (index < 0) return null;

        if (row.TranslatePoint(new Point(0, 0), _list) is not { } top) return null;
        return at.Y < top.Y + row.Bounds.Height / 2 ? index : index + 1;
    }
}
