#if WINDOWS || MACOS
using System.Runtime.InteropServices;
#endif
#if LINUX
using Tmds.DBus.Protocol;
#endif

namespace Glosa.App.Services;

/// <summary>
/// Keeps the system from going to sleep on its own while music is playing.
/// </summary>
/// <remarks>
/// Only idle sleep is held off: the display may still turn off, and sleep asked for by hand
/// or by closing a lid still happens. Nothing keeps a system awake for MIDI output the way
/// it does for sound played through the system, so without this a long song or a repeat
/// can be cut off.
///
/// Each check is also made at run time: a build made on one system for no runtime in
/// particular carries that system's code, and can run elsewhere.
/// </remarks>
internal static class KeepAwake
{
    private const string Reason = "Playing music";

    /// <summary>
    /// Holds off sleep until the result is disposed; null where the system has no way.
    /// </summary>
    /// <remarks>
    /// On Windows the hold belongs to the calling thread, so it is taken and let go on the
    /// UI thread.
    /// </remarks>
    public static IDisposable? Take()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()) return TakeOnWindows();
#endif
#if MACOS
        if (OperatingSystem.IsMacOS()) return TakeOnMac();
#endif
#if LINUX
        if (OperatingSystem.IsLinux()) return PortalInhibit.Start();
#endif
        return null;
    }

    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

#if WINDOWS
    private static IDisposable? TakeOnWindows()
    {
        if (SetThreadExecutionState(EsContinuous | EsSystemRequired) == 0) return null;
        return new Release(() => SetThreadExecutionState(EsContinuous));
    }

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
#endif

#if MACOS
    /// <summary>An IOKit power assertion that idle sleep waits for.</summary>
    private static IDisposable? TakeOnMac()
    {
        nint type = CFStringCreateWithCString(0, "PreventUserIdleSystemSleep", CFStringEncodingUtf8);
        nint reason = CFStringCreateWithCString(0, Reason, CFStringEncodingUtf8);
        try
        {
            if (IOPMAssertionCreateWithName(type, AssertionLevelOn, reason, out uint id) != 0) return null;
            return new Release(() => IOPMAssertionRelease(id));
        }
        finally
        {
            CFRelease(reason);
            CFRelease(type);
        }
    }

    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint CFStringEncodingUtf8 = 0x08000100;
    private const uint AssertionLevelOn = 255;

    [DllImport(IOKit)]
    private static extern int IOPMAssertionCreateWithName(nint type, uint level, nint name, out uint id);

    [DllImport(IOKit)]
    private static extern int IOPMAssertionRelease(uint id);

    [DllImport(CoreFoundation, CharSet = CharSet.Ansi)]
    private static extern nint CFStringCreateWithCString(nint allocator, string text, uint encoding);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint value);
#endif

#if LINUX
    /// <summary>
    /// The desktop portal's <c>Inhibit</c>, for suspend: GNOME, KDE Plasma and the other
    /// desktops that carry the portal answer it.
    /// </summary>
    /// <remarks>
    /// The hold lasts while the request stands and the connection that made it is open, so
    /// the connection is kept until the hold is let go. Asking and letting go finish on
    /// their own; a desktop without the portal simply does not hold.
    /// </remarks>
    private sealed class PortalInhibit : IDisposable
    {
        private const string Portal = "org.freedesktop.portal.Desktop";
        private const uint Suspend = 4;

        private readonly DBusConnection _connection;
        private readonly Task<string?> _request;

        private PortalInhibit(DBusConnection connection)
        {
            _connection = connection;
            _request = AskAsync();
        }

        public static PortalInhibit? Start()
            => DBusAddress.Session is { } address ? new PortalInhibit(new DBusConnection(address)) : null;

        private async Task<string?> AskAsync()
        {
            try
            {
                await _connection.ConnectAsync();
                return await _connection.CallMethodAsync(Inhibit(),
                    (message, _) => message.GetBodyReader().ReadObjectPathAsString());
            }
            catch (Exception ex) when (ex is DBusExceptionBase or IOException or ObjectDisposedException)
            {
                return null;
            }
        }

        private MessageBuffer Inhibit()
        {
            using MessageWriter writer = _connection.GetMessageWriter();
            writer.WriteMethodCallHeader(Portal, "/org/freedesktop/portal/desktop",
                                         "org.freedesktop.portal.Inhibit", "Inhibit", "sua{sv}");
            writer.WriteString(string.Empty);
            writer.WriteUInt32(Suspend);
            writer.WriteDictionary(new KeyValuePair<string, VariantValue>[] { new("reason", VariantValue.String(Reason)) });
            return writer.CreateMessage();
        }

        public void Dispose() => _ = LetGoAsync();

        private async Task LetGoAsync()
        {
            try
            {
                if (await _request is { } request) await _connection.CallMethodAsync(Close(request));
            }
            catch (Exception ex) when (ex is DBusExceptionBase or IOException or ObjectDisposedException)
            {
            }
            finally
            {
                _connection.Dispose();
            }
        }

        private MessageBuffer Close(string request)
        {
            using MessageWriter writer = _connection.GetMessageWriter();
            writer.WriteMethodCallHeader(Portal, request, "org.freedesktop.portal.Request", "Close");
            return writer.CreateMessage();
        }
    }
#endif
}
