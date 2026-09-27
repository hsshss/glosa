#if WINDOWS
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Glosa.App.Services;

/// <summary>
/// The media keys on Windows: the SystemMediaTransportControls of one window, reached from
/// a desktop window through <c>ISystemMediaTransportControlsInterop.GetForWindow</c>.
/// </summary>
/// <remarks>
/// The slot numbers and IIDs are those of the Windows SDK (10.0.26100.0):
/// <c>windows.media.idl</c> for the interfaces, <c>windows.media.h</c> for the event
/// handler's IID, <c>SystemMediaTransportControlsInterop.idl</c> for the interop. Every
/// interface here but the handler derives from IInspectable, whose six slots come first.
/// </remarks>
internal sealed unsafe class WindowsMediaKeys : IMediaKeys
{
    private static readonly Guid InteropIid = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");
    private static readonly Guid ControlsIid = new("99fa3ff4-1742-42a6-902e-087d41f965ec");

    // ISystemMediaTransportControlsInterop
    private const int GetForWindow = 6;

    // ISystemMediaTransportControls
    private const int PutPlaybackStatus = 7;
    private const int GetDisplayUpdater = 8;
    private const int PutIsEnabled = 11;
    private const int PutIsPlayEnabled = 13;
    private const int PutIsStopEnabled = 15;
    private const int PutIsPauseEnabled = 17;
    private const int PutIsPreviousEnabled = 25;
    private const int PutIsNextEnabled = 27;
    private const int AddButtonPressed = 32;
    private const int RemoveButtonPressed = 33;

    // ISystemMediaTransportControlsDisplayUpdater
    private const int PutType = 7;
    private const int GetMusicProperties = 12;
    private const int ClearAll = 16;
    private const int Update = 17;

    // IMusicDisplayProperties
    private const int PutTitle = 7;

    /// <summary><c>Windows.Media.MediaPlaybackType.Music</c>.</summary>
    private const int Music = 1;

    private IntPtr _controls;
    private long _token;
    private bool _listening;

    private MediaStatus? _status;
    private string? _title;

    public event Action<MediaKey>? Pressed;

    private WindowsMediaKeys(IntPtr controls) => _controls = controls;

    /// <summary>
    /// The media keys for <paramref name="window"/> (an HWND), or null when the system will
    /// not give them — which leaves the player without media keys and nothing more.
    /// </summary>
    public static WindowsMediaKeys? For(IntPtr window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return null;

        const string name = "Windows.Media.SystemMediaTransportControls";
        if (WindowsCreateString(name, name.Length, out IntPtr className) < 0) return null;

        int hr = RoGetActivationFactory(className, InteropIid, out IntPtr interop);
        WindowsDeleteString(className);
        if (hr < 0) return null;

        IntPtr controls;
        Guid iid = ControlsIid;
        hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Slots(interop)[GetForWindow])(
            interop, window, &iid, &controls);
        Release(interop);
        if (hr < 0) return null;

        var keys = new WindowsMediaKeys(controls);
        if (!keys.Enable() || !keys.Listen())
        {
            keys.Dispose();
            return null;
        }
        return keys;
    }

    private bool Enable()
    {
        foreach (int slot in (int[])[PutIsEnabled, PutIsPlayEnabled, PutIsPauseEnabled,
                                     PutIsStopEnabled, PutIsNextEnabled, PutIsPreviousEnabled])
            if (PutBool(_controls, slot, true) < 0) return false;
        return true;
    }

    private bool Listen()
    {
        IntPtr handler = ButtonHandler.Create(button =>
        {
            if (Translate(button) is { } key) Pressed?.Invoke(key);
        });

        long token;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slots(_controls)[AddButtonPressed])(
            _controls, handler, &token);
        // The controls hold their own reference to the handler from here on.
        Release(handler);
        if (hr < 0) return false;

        _token = token;
        _listening = true;
        return true;
    }

    /// <summary><c>Windows.Media.SystemMediaTransportControlsButton</c>, the ones asked for.</summary>
    private static MediaKey? Translate(int button) => button switch
    {
        0 => MediaKey.Play,
        1 => MediaKey.Pause,
        2 => MediaKey.Stop,
        6 => MediaKey.Next,
        7 => MediaKey.Previous,
        _ => null,
    };

    /// <summary><c>Windows.Media.MediaPlaybackStatus</c>: Stopped 2, Playing 3, Paused 4.</summary>
    private static int Translate(MediaStatus status) => status switch
    {
        MediaStatus.Playing => 3,
        MediaStatus.Paused => 4,
        _ => 2,
    };

    /// <remarks>Nothing is sent when nothing has changed.</remarks>
    public void Show(MediaStatus status, string? title)
    {
        if (_controls == IntPtr.Zero) return;

        if (status != _status)
        {
            _status = status;
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slots(_controls)[PutPlaybackStatus])(
                _controls, Translate(status));
        }

        if (title == _title) return;
        _title = title;

        IntPtr updater;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slots(_controls)[GetDisplayUpdater])(_controls, &updater) < 0)
            return;

        if (title is null) Call(updater, ClearAll);
        else ShowTitle(updater, title);

        Call(updater, Update);
        Release(updater);
    }

    private static void ShowTitle(IntPtr updater, string title)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slots(updater)[PutType])(updater, Music);

        IntPtr music;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slots(updater)[GetMusicProperties])(updater, &music) < 0)
            return;

        // The callee takes a copy; the string stays ours to delete.
        if (WindowsCreateString(title, title.Length, out IntPtr text) >= 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slots(music)[PutTitle])(music, text);
            WindowsDeleteString(text);
        }
        Release(music);
    }

    public void Dispose()
    {
        if (_controls == IntPtr.Zero) return;

        if (_listening)
            ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slots(_controls)[RemoveButtonPressed])(_controls, _token);

        Release(_controls);
        _controls = IntPtr.Zero;
    }

    private static void** Slots(IntPtr com) => *(void***)com;

    private static int PutBool(IntPtr com, int slot, bool value)
        => ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Slots(com)[slot])(com, value ? (byte)1 : (byte)0);

    private static int Call(IntPtr com, int slot)
        => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slots(com)[slot])(com);

    private static uint Release(IntPtr com)
        => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slots(com)[2])(com);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr text);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr text);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr className, in Guid iid, out IntPtr factory);

    /// <summary>
    /// The <c>TypedEventHandler&lt;SystemMediaTransportControls,
    /// SystemMediaTransportControlsButtonPressedEventArgs&gt;</c> the controls call: a COM
    /// object of our own, three IUnknown slots and Invoke.
    /// </summary>
    /// <remarks>
    /// Agile — it answers IAgileObject — because the controls call it from a thread of their
    /// own, and all it does there is hand the button on.
    /// </remarks>
    private static class ButtonHandler
    {
        /// <summary>From windows.media.h: the handler's parameterised IID.</summary>
        private static readonly Guid HandlerIid = new("0557e996-7b23-5bae-aa81-ea0d671143a4");
        private static readonly Guid UnknownIid = new("00000000-0000-0000-c000-000000000046");
        private static readonly Guid AgileIid = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

        private const int E_NOINTERFACE = unchecked((int)0x80004002);

        /// <summary>ISystemMediaTransportControlsButtonPressedEventArgs.Button.</summary>
        private const int GetButton = 6;

        private struct Instance
        {
            public void** Slots;
            public IntPtr Pressed;
            public int References;
        }

        private static readonly void** HandlerSlots = Build();

        private static void** Build()
        {
            var slots = (void**)NativeMemory.Alloc((nuint)(4 * sizeof(void*)));
            slots[0] = (delegate* unmanaged[Stdcall]<Instance*, Guid*, void**, int>)&QueryInterface;
            slots[1] = (delegate* unmanaged[Stdcall]<Instance*, uint>)&AddRef;
            slots[2] = (delegate* unmanaged[Stdcall]<Instance*, uint>)&Release;
            slots[3] = (delegate* unmanaged[Stdcall]<Instance*, IntPtr, IntPtr, int>)&Invoke;
            return slots;
        }

        /// <summary>A new handler, with one reference that is the caller's.</summary>
        public static IntPtr Create(Action<int> pressed)
        {
            var instance = (Instance*)NativeMemory.Alloc((nuint)sizeof(Instance));
            instance->Slots = HandlerSlots;
            instance->Pressed = GCHandle.ToIntPtr(GCHandle.Alloc(pressed));
            instance->References = 1;
            return (IntPtr)instance;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(Instance* self, Guid* iid, void** result)
        {
            if (*iid == HandlerIid || *iid == UnknownIid || *iid == AgileIid)
            {
                Interlocked.Increment(ref self->References);
                *result = self;
                return 0;
            }

            *result = null;
            return E_NOINTERFACE;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(Instance* self) => (uint)Interlocked.Increment(ref self->References);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(Instance* self)
        {
            int left = Interlocked.Decrement(ref self->References);
            if (left == 0)
            {
                GCHandle.FromIntPtr(self->Pressed).Free();
                NativeMemory.Free(self);
            }
            return (uint)left;
        }

        /// <remarks>Nothing may be thrown back into the system's code, so nothing is.</remarks>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Invoke(Instance* self, IntPtr sender, IntPtr args)
        {
            try
            {
                int button;
                if (((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slots(args)[GetButton])(args, &button) >= 0
                    && GCHandle.FromIntPtr(self->Pressed).Target is Action<int> pressed)
                    pressed(button);
            }
            catch
            {
                // A key that did nothing.
            }
            return 0;
        }
    }
}
#endif
