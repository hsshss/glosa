using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Glosa.App.Services;

namespace Glosa.App.Views;

/// <summary>
/// Puts a window back where it was last time, and remembers where it ends up.
/// </summary>
/// <remarks>
/// Used for every window, not only the main one.
/// </remarks>
internal static partial class Placement
{
    /// <summary>
    /// Watches a window, storing its placement under <paramref name="key"/>.
    /// </summary>
    /// <param name="settings">
    /// Asked for each time rather than held: the main window has no view model yet when this
    /// is attached, and a window that outlives one would otherwise write into the old one.
    /// </param>
    public static void Attach(Window window, string key, Func<AppSettings?> settings)
    {
        window.Opened += (_, _) =>
        {
            Restore(window, key, settings());
            RestoreState(window, key, settings());
        };

        // Closing, not Closed: by Closed the window has let go of its placement, and the
        // main window's own Closed is where everything gets written out.
        window.Closing += (_, _) => Record(window, key, settings());

        KeepNormalSize(window);
    }

    /// <summary>
    /// Brings a window back to its own size when it comes out of being maximised. Only a
    /// Windows build has one: see Placement.Windows.cs. Elsewhere the call compiles away.
    /// </summary>
    static partial void KeepNormalSize(Window window);

    /// <summary>
    /// Puts the window back, moved and shrunk as far as it takes to fit on the screen it was
    /// mostly on.
    /// </summary>
    /// <remarks>
    /// The screens may not be the ones it was saved on: a monitor unplugged, a laptop taken
    /// off its dock, the scaling turned up. A window that is on none of them now is left
    /// where the system puts it, at a size that fits the screen it opened on.
    ///
    /// The whole window is weighed, not its corner. On Windows the position is that of the
    /// frame, which reaches past the visible edge by the invisible borders, so a window
    /// snapped to the left edge of the screen has its corner off it. Those borders may hang
    /// over the edges here too.
    /// </remarks>
    private static void Restore(Window window, string key, AppSettings? settings)
    {
        if (settings?.Windows.GetValueOrDefault(key) is not { Width: > 0, Height: > 0 } saved)
            return;

        // What the frame adds to the client area: the title bar, and the borders. Known now
        // that the window is open.
        Size frame = window.FrameSize is { } outer ? outer - window.ClientSize : default;
        frame = new Size(Math.Max(0, frame.Width), Math.Max(0, frame.Height));

        var at = new PixelPoint(saved.X, saved.Y);
        Screen? home = window.Screens.All.MaxBy(screen => Overlap(screen, at, saved, frame));
        if (home is not null && Overlap(home, at, saved, frame) == 0) home = null;
        Screen? fitTo = home ?? window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;

        double width = Math.Max(window.MinWidth, saved.Width);
        double height = Math.Max(window.MinHeight, saved.Height);
        if (fitTo is not null)
        {
            double scaling = fitTo.Scaling;
            width = Math.Max(window.MinWidth, Math.Min(width, fitTo.WorkingArea.Width / scaling - frame.Width));
            height = Math.Max(window.MinHeight, Math.Min(height, fitTo.WorkingArea.Height / scaling - frame.Height));
        }
        window.Width = width;
        window.Height = height;

        if (home is null) return;

        PixelRect area = home.WorkingArea;
        int outerWidth = (int)Math.Ceiling((width + frame.Width) * home.Scaling);
        int outerHeight = (int)Math.Ceiling((height + frame.Height) * home.Scaling);
        // One side's border: the frame's width is the two of them. The bottom has one too;
        // the top is the title bar, which stays on the screen.
        int border = (int)(frame.Width / 2 * home.Scaling);

        int x = Math.Clamp(at.X, area.X - border, Math.Max(area.X - border, area.Right + border - outerWidth));
        int y = Math.Clamp(at.Y, area.Y, Math.Max(area.Y, area.Bottom + border - outerHeight));
        window.Position = new PixelPoint(x, y);
    }

    /// <summary>Maximises a window that was maximised, once it is where it was.</summary>
    private static void RestoreState(Window window, string key, AppSettings? settings)
    {
        if (settings?.Windows.GetValueOrDefault(key) is { Maximized: true } && window.CanMaximize)
            window.WindowState = WindowState.Maximized;
    }

    /// <summary>How many of a screen's pixels the saved window would cover.</summary>
    private static long Overlap(Screen screen, PixelPoint at, WindowPlacement saved, Size frame)
    {
        var rect = new PixelRect(at, PixelSize.FromSize(
            new Size(saved.Width + frame.Width, saved.Height + frame.Height), screen.Scaling));
        PixelRect common = screen.WorkingArea.Intersect(rect);
        return (long)common.Width * common.Height;
    }

    /// <summary>
    /// Writes down whether the window is one to open again next time.
    /// </summary>
    /// <remarks>
    /// Kept beside the placement, not in a table of its own that could disagree about which
    /// windows there are. Separate from <see cref="Record"/> because the answer depends on
    /// why the window is closing, which only the caller knows.
    /// </remarks>
    public static void SetOpen(string key, AppSettings? settings, bool open)
    {
        if (Entry(key, settings) is { } placement) placement.Open = open;
    }

    /// <summary>
    /// Writes down where the window is, and whether it is maximised.
    /// </summary>
    /// <remarks>
    /// The place and size only while it is an ordinary window: a maximised one reports the
    /// size of the screen, which restored as a normal window would cover everything. A
    /// minimised one is left as it was last seen.
    /// </remarks>
    private static void Record(Window window, string key, AppSettings? settings)
    {
        if (window.WindowState is not (WindowState.Normal or WindowState.Maximized)) return;
        if (Entry(key, settings) is not { } placement) return;

        placement.Maximized = window.WindowState == WindowState.Maximized;
        if (placement.Maximized) return;

        placement.X = window.Position.X;
        placement.Y = window.Position.Y;
        placement.Width = (int)window.ClientSize.Width;
        placement.Height = (int)window.ClientSize.Height;
    }

    /// <summary>
    /// The window's row in the table, made if it is not there yet.
    /// </summary>
    /// <remarks>
    /// Updated in place: the placement and whether to open again are written at different
    /// moments.
    /// </remarks>
    private static WindowPlacement? Entry(string key, AppSettings? settings)
    {
        if (settings is null) return null;

        if (!settings.Windows.TryGetValue(key, out WindowPlacement? placement))
            settings.Windows[key] = placement = new WindowPlacement();

        return placement;
    }
}
