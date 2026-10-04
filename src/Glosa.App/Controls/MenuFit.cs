using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Glosa.App.Controls;

/// <summary>
/// Keeps a menu that came out a pixel short of its items from showing scroll buttons.
/// </summary>
/// <remarks>
/// Avalonia sizes a popup's window by truncating to whole pixels (Win32 <c>WindowImpl.Resize</c>),
/// so at a scaling such as 175% a menu can lose a pixel to rounding error. Such a menu does not
/// scroll while it is open; one taller than the screen still does.
/// </remarks>
internal static class MenuFit
{
    public static void Install()
    {
        ScrollViewer.ExtentProperty.Changed.AddClassHandler<ScrollViewer>((viewer, _) => Fit(viewer));
        ScrollViewer.ViewportProperty.Changed.AddClassHandler<ScrollViewer>((viewer, _) => Fit(viewer));
    }

    private static void Fit(ScrollViewer viewer)
    {
        if (viewer.TemplatedParent is not (MenuItem or ContextMenu or MenuFlyoutPresenter)) return;
        double over = viewer.Extent.Height - viewer.Viewport.Height;
        double pixel = 1 / (TopLevel.GetTopLevel(viewer)?.RenderScaling ?? 1);
        // The lost pixel, with room for the layout's own rounding.
        if (over <= 0 || over >= 1.5 * pixel) return;

        viewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        viewer.DetachedFromVisualTree += Restore;

        void Restore(object? sender, VisualTreeAttachmentEventArgs e)
        {
            viewer.DetachedFromVisualTree -= Restore;
            viewer.ClearValue(ScrollViewer.VerticalScrollBarVisibilityProperty);
        }
    }
}
