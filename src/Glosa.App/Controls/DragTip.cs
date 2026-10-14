using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Glosa.App.Controls;

/// <summary>
/// Keeps a control's tooltip open while it is dragged, so that a slider shows what it is set to
/// as it moves.
/// </summary>
/// <remarks>
/// The tooltip service closes a tip on a press, and when the pointer strays off its control; it
/// sees the input after the control does, so the tip is opened again once the input is handled.
/// </remarks>
public static class DragTip
{
    public static readonly AttachedProperty<bool> ShowProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Show", typeof(DragTip));

    private static readonly AttachedProperty<bool> DraggingProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Dragging", typeof(DragTip));

    static DragTip()
    {
        ShowProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not true) return;
            control.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            control.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            control.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        });
    }

    public static bool GetShow(Control control) => control.GetValue(ShowProperty);

    public static void SetShow(Control control, bool value) => control.SetValue(ShowProperty, value);

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) return;
        control.SetValue(DraggingProperty, true);
        Open(control);
    }

    private static void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control control || !control.GetValue(DraggingProperty)) return;
        // The button came up where the release was not delivered.
        if (!e.GetCurrentPoint(control).Properties.IsLeftButtonPressed) End(control);
        else Open(control);
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control control) End(control);
    }

    private static void Open(Control control)
        => Dispatcher.UIThread.Post(() =>
        {
            if (control.GetValue(DraggingProperty)) ToolTip.SetIsOpen(control, true);
        });

    private static void End(Control control)
    {
        if (!control.GetValue(DraggingProperty)) return;
        control.SetValue(DraggingProperty, false);
        ToolTip.SetIsOpen(control, false);
    }
}
