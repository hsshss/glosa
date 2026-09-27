#if MACOS
using System.Runtime.InteropServices;

namespace Glosa.App.Services;

/// <summary>
/// The media keys on macOS: the MediaPlayer framework's <c>MPRemoteCommandCenter</c>, and
/// <c>MPNowPlayingInfoCenter</c> for what the system shows as playing.
/// </summary>
/// <remarks>
/// The commands call <see cref="Handle"/> on an object of <c>GlosaMediaKeyTarget</c>, a class
/// made here at run time. Everything is on the main thread: the commands are called there,
/// and the player tells the status there.
/// </remarks>
internal sealed unsafe class MacMediaKeys : IMediaKeys
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string MediaPlayer = "/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer";

    /// <summary><c>MPRemoteCommandHandlerStatusSuccess</c>.</summary>
    private const nint Success = 0;

    /// <summary><c>MPNowPlayingPlaybackState</c>: playing 1, paused 2, stopped 3.</summary>
    private static nuint Translate(MediaStatus status) => status switch
    {
        MediaStatus.Playing => 1,
        MediaStatus.Paused => 2,
        _ => 3,
    };

    private static readonly nint MsgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");

    /// <summary>The one instance the target's method reports to; there is one main window.</summary>
    private static MacMediaKeys? _current;

    private readonly nint _target;
    private readonly (nint Command, MediaKey? Key)[] _commands;
    private readonly nint _nowPlaying;
    private readonly nint _titleKey;

    private MediaStatus? _status;
    private string? _title;
    private bool _disposed;

    public event Action<MediaKey>? Pressed;

    private MacMediaKeys(nint target, (nint, MediaKey?)[] commands, nint nowPlaying, nint titleKey)
    {
        _target = target;
        _commands = commands;
        _nowPlaying = nowPlaying;
        _titleKey = titleKey;
    }

    /// <summary>
    /// The media keys, or null when the system will not give them — which leaves the player
    /// without media keys and nothing more.
    /// </summary>
    public static MacMediaKeys? Create()
    {
        if (!OperatingSystem.IsMacOS() || _current is not null) return null;

        try
        {
            nint framework = NativeLibrary.Load(MediaPlayer);
            nint titleKey = *(nint*)NativeLibrary.GetExport(framework, "MPMediaItemPropertyTitle");

            nint center = Send(Class("MPRemoteCommandCenter"), "sharedCommandCenter");
            nint nowPlaying = Send(Class("MPNowPlayingInfoCenter"), "defaultCenter");
            nint target = Send(Send(TargetClass(), "alloc"), "init");
            if (center == 0 || nowPlaying == 0 || target == 0) return null;

            // Toggle's key is null: which of play and pause it means depends on the status.
            (string Name, MediaKey? Key)[] names =
            [
                ("playCommand", MediaKey.Play),
                ("pauseCommand", MediaKey.Pause),
                ("togglePlayPauseCommand", null),
                ("stopCommand", MediaKey.Stop),
                ("nextTrackCommand", MediaKey.Next),
                ("previousTrackCommand", MediaKey.Previous),
            ];

            var commands = new (nint, MediaKey?)[names.Length];
            nint action = Selector("handle:");
            for (int i = 0; i < names.Length; i++)
            {
                nint command = Send(center, names[i].Name);
                ((delegate* unmanaged<nint, nint, byte, void>)MsgSend)(command, Selector("setEnabled:"), 1);
                ((delegate* unmanaged<nint, nint, nint, nint, void>)MsgSend)(
                    command, Selector("addTarget:action:"), target, action);
                commands[i] = (command, names[i].Key);
            }

            return _current = new MacMediaKeys(target, commands, nowPlaying, titleKey);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <remarks>Nothing is sent when nothing has changed.</remarks>
    public void Show(MediaStatus status, string? title)
    {
        if (_disposed) return;

        nint pool = objc_autoreleasePoolPush();
        try
        {
            // The title first: the system takes the state as the moment to look at it.
            if (title != _title || _status is null)
            {
                _title = title;
                nint info = 0;
                if (title is not null)
                {
                    nint text = CreateString(title);
                    info = ((delegate* unmanaged<nint, nint, nint, nint, nint>)MsgSend)(
                        Class("NSDictionary"), Selector("dictionaryWithObject:forKey:"), text, _titleKey);
                    CFRelease(text);
                }
                ((delegate* unmanaged<nint, nint, nint, void>)MsgSend)(
                    _nowPlaying, Selector("setNowPlayingInfo:"), info);
            }

            if (status != _status)
            {
                _status = status;
                ((delegate* unmanaged<nint, nint, nuint, void>)MsgSend)(
                    _nowPlaying, Selector("setPlaybackState:"), Translate(status));
            }
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach ((nint command, _) in _commands)
            ((delegate* unmanaged<nint, nint, nint, void>)MsgSend)(command, Selector("removeTarget:"), _target);

        ((delegate* unmanaged<nint, nint, nint, void>)MsgSend)(_nowPlaying, Selector("setNowPlayingInfo:"), 0);
        ((delegate* unmanaged<nint, nint, nuint, void>)MsgSend)(
            _nowPlaying, Selector("setPlaybackState:"), Translate(MediaStatus.Stopped));
        Send(_target, "release");

        if (_current == this) _current = null;
    }

    /// <summary>
    /// <c>- (MPRemoteCommandHandlerStatus)handle:(MPRemoteCommandEvent *)event</c>. Nothing may
    /// be thrown back into the system's code, so nothing is.
    /// </summary>
    [UnmanagedCallersOnly]
    private static nint Handle(nint self, nint selector, nint commandEvent)
    {
        try
        {
            if (_current is not { } keys) return Success;

            nint command = Send(commandEvent, "command");
            foreach ((nint candidate, MediaKey? key) in keys._commands)
            {
                if (candidate != command) continue;
                keys.Pressed?.Invoke(key ?? (keys._status == MediaStatus.Playing ? MediaKey.Pause : MediaKey.Play));
                break;
            }
        }
        catch
        {
            // A key that did nothing.
        }
        return Success;
    }

    /// <summary>The class of the commands' target, made the first time it is asked for.</summary>
    private static nint TargetClass()
    {
        const string name = "GlosaMediaKeyTarget";
        nint existing = objc_getClass(name);
        if (existing != 0) return existing;

        nint made = objc_allocateClassPair(Class("NSObject"), name, 0);
        // Returns NSInteger (q); takes self (@), the selector (:) and the event (@).
        class_addMethod(made, Selector("handle:"),
                        (nint)(delegate* unmanaged<nint, nint, nint, nint>)&Handle, "q@:@");
        objc_registerClassPair(made);
        return made;
    }

    private static nint Class(string name) => objc_getClass(name);

    private static nint Selector(string name) => sel_registerName(name);

    private static nint Send(nint receiver, string selector)
        => ((delegate* unmanaged<nint, nint, nint>)MsgSend)(receiver, Selector(selector));

    /// <summary>A new CFString — toll-free an NSString — which the caller releases.</summary>
    private static nint CreateString(string text)
    {
        fixed (char* chars = text) return CFStringCreateWithCharacters(0, chars, text.Length);
    }

    [DllImport(ObjC)]
    private static extern nint objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(ObjC)]
    private static extern nint objc_allocateClassPair(
        nint superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint extraBytes);

    [DllImport(ObjC)]
    private static extern void objc_registerClassPair(nint cls);

    [DllImport(ObjC)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool class_addMethod(
        nint cls, nint name, nint implementation, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport(ObjC)]
    private static extern nint objc_autoreleasePoolPush();

    [DllImport(ObjC)]
    private static extern void objc_autoreleasePoolPop(nint pool);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern nint CFStringCreateWithCharacters(nint allocator, char* chars, nint length);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(nint obj);
}
#endif
