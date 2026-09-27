using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Glosa.App.Services;
using Glosa.App.ViewModels;

namespace Glosa.App.Views;

/// <summary>
/// Holds off the system's idle sleep (<see cref="KeepAwake"/>) while a song is playing.
/// </summary>
/// <remarks>
/// Not while paused or stopped: a player left paused should not keep the machine up.
/// </remarks>
internal static class StayAwake
{
    public static void Attach(Window window, MainViewModel model)
    {
        IDisposable? hold = null;

        // IsPerforming first, as for the media keys: IsPaused lags a stop by a tick.
        void Follow()
        {
            bool playing = model.IsPerforming && !model.IsPaused;
            if (playing && hold is null) hold = KeepAwake.Take();
            else if (!playing && hold is not null)
            {
                hold.Dispose();
                hold = null;
            }
        }

        // Settled once, on the next UI turn after these have all changed.
        bool pending = false;
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (nameof(MainViewModel.IsPerforming) or nameof(MainViewModel.IsPaused))
                || pending)
                return;
            pending = true;
            Dispatcher.UIThread.Post(() =>
            {
                pending = false;
                Follow();
            });
        }

        model.PropertyChanged += OnChanged;
        Follow();

        window.Closed += (_, _) =>
        {
            model.PropertyChanged -= OnChanged;
            hold?.Dispose();
            hold = null;
        };
    }
}
