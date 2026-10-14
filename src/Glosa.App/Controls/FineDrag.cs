using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Glosa.App.Controls;

/// <summary>
/// Lets a slider be dragged a tenth as far while Shift is held, to set it finely.
/// </summary>
/// <remarks>
/// A slider puts its value under the pointer. With Shift held, the value moves by the pointer's
/// travel instead, from where it is, so that Shift pressed or let go never makes it jump. The
/// move is taken at the window, before the slider would put the value under the pointer.
/// </remarks>
public static class FineDrag
{
    /// <summary>How much of the pointer's travel moves the value while Shift is held.</summary>
    public const double Fine = 0.1;

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<Slider, bool>("Enabled", typeof(FineDrag));

    private static readonly AttachedProperty<Drag?> DragProperty =
        AvaloniaProperty.RegisterAttached<Slider, Drag?>("Drag", typeof(FineDrag));

    /// <summary>A drag under way: once moved by travel, where the pointer and the value were last anchored.</summary>
    private sealed class Drag
    {
        public bool ByTravel;
        public bool Fine;
        public double X;
        public double Value;
    }

    static FineDrag()
    {
        EnabledProperty.Changed.AddClassHandler<Slider>((slider, e) =>
        {
            if (e.NewValue is not true) return;
            slider.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            slider.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            EventHandler<PointerEventArgs> moved = (_, args) => OnMoved(slider, args);
            slider.AttachedToVisualTree += (_, args) =>
                (args.RootVisual as InputElement)?.AddHandler(InputElement.PointerMovedEvent, moved, RoutingStrategies.Tunnel);
            slider.DetachedFromVisualTree += (_, args) =>
                (args.RootVisual as InputElement)?.RemoveHandler(InputElement.PointerMovedEvent, moved);
        });
    }

    public static bool GetEnabled(Slider slider) => slider.GetValue(EnabledProperty);

    public static void SetEnabled(Slider slider, bool value) => slider.SetValue(EnabledProperty, value);

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Slider slider && e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed)
            slider.SetValue(DragProperty, new Drag());
    }

    private static void OnMoved(Slider slider, PointerEventArgs e)
    {
        if (slider.GetValue(DragProperty) is not { } drag) return;
        if (!e.GetCurrentPoint(slider).Properties.IsLeftButtonPressed)
        {
            slider.ClearValue(DragProperty);
            return;
        }

        bool fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (!drag.ByTravel && !fine) return;
        if (slider.FindDescendantOfType<Track>() is not { } track) return;
        double length = track.Bounds.Width - (track.Thumb?.Bounds.Width ?? 0);
        if (length <= 0) return;

        double x = e.GetPosition(track).X;
        if (!drag.ByTravel || fine != drag.Fine)
        {
            drag.ByTravel = true;
            drag.Fine = fine;
            drag.X = x;
            drag.Value = slider.Value;
        }
        double perPixel = (slider.Maximum - slider.Minimum) / length * (fine ? Fine : 1);
        double value = drag.Value + (x - drag.X) * perPixel;
        double clamped = Math.Clamp(value, slider.Minimum, slider.Maximum);
        // Past an end, the travel back starts from it.
        if (clamped != value)
        {
            drag.X = x;
            drag.Value = clamped;
        }
        slider.SetCurrentValue(RangeBase.ValueProperty, clamped);
        e.Handled = true;
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider) slider.ClearValue(DragProperty);
    }
}
