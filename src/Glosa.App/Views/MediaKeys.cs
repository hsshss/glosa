using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Glosa.App.Services;
using Glosa.App.ViewModels;

namespace Glosa.App.Views;

/// <summary>
/// Connects the platform's media keys (<see cref="IMediaKeys"/>) to the player.
/// </summary>
internal static class MediaKeys
{
    /// <summary>
    /// Hands the media keys to <paramref name="model"/> for as long as
    /// <paramref name="window"/> is open and <see cref="MainViewModel.UseMediaKeys"/> is on,
    /// where the platform has them.
    /// </summary>
    public static void Attach(Window window, MainViewModel model)
    {
        IDisposable? connection = null;

        void Follow()
        {
            if (model.UseMediaKeys && connection is null) connection = Connect(window, model);
            else if (!model.UseMediaKeys && connection is not null)
            {
                connection.Dispose();
                connection = null;
            }
        }

        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.UseMediaKeys)) Follow();
        }

        model.PropertyChanged += OnChanged;
        Follow();

        window.Closed += (_, _) =>
        {
            model.PropertyChanged -= OnChanged;
            connection?.Dispose();
            connection = null;
        };
    }

    /// <summary>
    /// Takes the media keys, or null where the platform has none. Disposing the result lets
    /// them go.
    /// </summary>
    /// <remarks>
    /// Once the window is open: the keys can belong to the window's handle.
    /// </remarks>
    private static IDisposable? Connect(Window window, MainViewModel model)
    {
        if (Create(window) is not { } keys) return null;

        // Set as the keys are let go, so what was already posted on their way does nothing.
        bool released = false;

        // Pressed on a thread of the system's; the transport is driven from this one.
        keys.Pressed += key => Dispatcher.UIThread.Post(() =>
        {
            if (!released) Press(model, key);
        });

        // IsPerforming first: IsPaused is read off the clock and lags a stop by a tick.
        void Show()
        {
            MediaStatus status = !model.IsPerforming ? MediaStatus.Stopped
                               : model.IsPaused ? MediaStatus.Paused
                               : MediaStatus.Playing;
            keys.Show(status, status == MediaStatus.Stopped ? null : model.NowPlaying);
        }

        // Told once, on the next UI turn after these have all changed.
        bool pending = false;
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (nameof(MainViewModel.IsPerforming) or nameof(MainViewModel.IsPaused)
                or nameof(MainViewModel.NowPlaying)) || pending)
                return;
            pending = true;
            Dispatcher.UIThread.Post(() =>
            {
                pending = false;
                if (!released) Show();
            });
        }

        model.PropertyChanged += OnChanged;
        Show();

        return new Release(() =>
        {
            released = true;
            model.PropertyChanged -= OnChanged;
            keys.Dispose();
        });
    }

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    /// <summary>The platform's media keys for a window, or null where there are none.</summary>
    private static IMediaKeys? Create(Window window)
    {
#if WINDOWS
        if (window.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
            return WindowsMediaKeys.For(handle.Handle);
#endif
#if MACOS
        // The application's, not the window's: macOS has one set of media controls per app.
        if (OperatingSystem.IsMacOS()) return MacMediaKeys.Create();
#endif
#if LINUX
        // The application's too: the desktop sees players on the bus, not windows.
        if (OperatingSystem.IsLinux()) return MprisMediaKeys.Create();
#endif
        return null;
    }

    /// <summary>Play and pause each do only that, never toggle.</summary>
    private static void Press(MainViewModel model, MediaKey key)
    {
        switch (key)
        {
            case MediaKey.Play: model.PlayCommand.Execute(null); break;
            case MediaKey.Pause: model.PauseOnly(); break;
            case MediaKey.Stop: model.StopCommand.Execute(null); break;
            case MediaKey.Next: model.NextCommand.Execute(null); break;
            case MediaKey.Previous: model.PreviousCommand.Execute(null); break;
        }
    }
}
