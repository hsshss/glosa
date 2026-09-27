#if WINDOWS
using System.Runtime.InteropServices;
#endif
#if MACOS
using System.Diagnostics;
#endif
#if LINUX
using Tmds.DBus.Protocol;
#endif

namespace Glosa.App.Services;

/// <summary>
/// Shows a file in the system's file manager, in its folder and selected.
/// </summary>
/// <remarks>
/// Each system is asked in its own way, and each check is also made at run time: a build made
/// on one system for no runtime in particular carries that system's code, and can run
/// elsewhere. False when the system has no way, or it did not work, so the caller can fall
/// back to opening the folder.
/// </remarks>
internal static class FileManager
{
    public static async Task<bool> ShowAsync(string file)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()) return ShowOnWindows(file);
#endif
#if MACOS
        if (OperatingSystem.IsMacOS()) return ShowOnMac(file);
#endif
#if LINUX
        if (OperatingSystem.IsLinux()) return await ShowOnLinuxAsync(file);
#endif
        await Task.CompletedTask;
        return false;
    }

#if WINDOWS
    /// <remarks>
    /// The shell's own call, which reuses a window already showing the folder, where
    /// <c>explorer /select</c> opens another each time.
    /// </remarks>
    private static bool ShowOnWindows(string file)
    {
        nint item = ILCreateFromPathW(file);
        if (item == 0) return false;
        try
        {
            return SHOpenFolderAndSelectItems(item, 0, 0, 0) == 0;
        }
        finally
        {
            ILFree(item);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ILCreateFromPathW(string path);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, nint items, uint flags);

    [DllImport("shell32.dll")]
    private static extern void ILFree(nint item);
#endif

#if MACOS
    /// <summary>Finder, through <c>open -R</c>.</summary>
    private static bool ShowOnMac(string file)
    {
        try
        {
            using Process? open = Process.Start(new ProcessStartInfo("open", ["-R", file])
            {
                UseShellExecute = false,
            });
            return open is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }
#endif

#if LINUX
    /// <summary>
    /// Whichever file manager answers the freedesktop.org <c>FileManager1</c> interface on
    /// the session bus.
    /// </summary>
    private static async Task<bool> ShowOnLinuxAsync(string file)
    {
        if (DBusAddress.Session is not { } address) return false;
        try
        {
            using var connection = new DBusConnection(address);
            await connection.ConnectAsync();
            await connection.CallMethodAsync(ShowItems(connection, file));
            return true;
        }
        catch (Exception ex) when (ex is DBusExceptionBase or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static MessageBuffer ShowItems(DBusConnection connection, string file)
    {
        using MessageWriter writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader("org.freedesktop.FileManager1", "/org/freedesktop/FileManager1",
                                     "org.freedesktop.FileManager1", "ShowItems", "ass");
        writer.WriteArray(new[] { new Uri(file).AbsoluteUri });
        writer.WriteString(string.Empty);
        return writer.CreateMessage();
    }
#endif
}
