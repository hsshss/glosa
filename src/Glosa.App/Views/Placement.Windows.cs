#if WINDOWS
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Glosa.App.Views;

internal static partial class Placement
{
    /// <remarks>
    /// Windows should do this by itself, and Avalonia (12.1.3, Win32) stops it: every layout
    /// pass hands the window's size back to the platform, and at a scaling like 175% the
    /// maximised size comes back from device pixels a pixel short, reads as a change, and is
    /// written over the size the window is to be restored to.
    ///
    /// A size is taken as ordinary only when both Avalonia and Windows say the window is:
    /// each lags the other on one side. Maximising, Avalonia reports the maximised size while
    /// its state still says Normal; restoring, it reports the spoilt size while Windows
    /// already says the window is back to normal.
    ///
    /// Checked at run time as well: a build made on Windows for no runtime in particular
    /// carries this, and can be run elsewhere.
    /// </remarks>
    static partial void KeepNormalSize(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        Size normal = default;

        window.Resized += (_, e) =>
        {
            if (window.WindowState == WindowState.Normal && IsOrdinary(window)) normal = e.ClientSize;
        };

        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty) return;
            if (e.GetNewValue<WindowState>() != WindowState.Normal || normal == default) return;

            // Posted: the platform is still in the middle of restoring when this is raised,
            // and a size set now would be overwritten by the one it is about to report.
            Size wanted = normal;
            Dispatcher.UIThread.Post(() =>
            {
                if (window.WindowState != WindowState.Normal || window.ClientSize == wanted) return;
                window.Width = wanted.Width;
                window.Height = wanted.Height;
            });
        };
    }

    /// <summary>Whether the window is neither maximised nor minimised, as Windows sees it.</summary>
    private static bool IsOrdinary(Window window)
        => window.TryGetPlatformHandle() is { } handle
           && !IsZoomed(handle.Handle) && !IsIconic(handle.Handle);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
}
#endif
