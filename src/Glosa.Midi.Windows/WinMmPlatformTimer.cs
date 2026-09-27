using System.Runtime.InteropServices;

namespace Glosa.Midi.Windows;

/// <summary>
/// Raises the Windows system timer resolution while playing. Without this,
/// <c>Thread.Sleep</c> can overshoot by more than 15 ms, which is audible.
/// </summary>
public sealed partial class WinMmPlatformTimer : Core.Playback.IPlatformTimer
{
    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint uPeriod);

    public IDisposable BeginHighResolution() => new Scope();

    private sealed class Scope : IDisposable
    {
        private const uint Period = 1;
        private bool _active;

        public Scope() => _active = timeBeginPeriod(Period) == 0;

        public void Dispose()
        {
            if (!_active) return;
            _active = false;
            timeEndPeriod(Period);
        }
    }
}
