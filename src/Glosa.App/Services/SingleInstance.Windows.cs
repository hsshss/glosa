#if WINDOWS
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Glosa.App.Services;

internal sealed partial class SingleInstance
{
    /// <remarks>
    /// Only to the player's own process, which the pipe names. Checked at run time as well: a
    /// build made on Windows for no runtime in particular carries this, and can run elsewhere.
    /// </remarks>
    static partial void LetForward(NamedPipeClientStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint process))
            AllowSetForegroundWindow(process);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
#endif
