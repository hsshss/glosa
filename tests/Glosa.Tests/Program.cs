using System.Runtime.InteropServices;

namespace Glosa.Tests;

internal static class Program
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>
    /// Runs the tests. On macOS they run on another thread, and the main thread runs its run
    /// loop: Brack works on it there, and waits for it otherwise (IMPLEMENTATION.md, "Brack バックエンド").
    /// </summary>
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) return Run(args);

        int result = 0;
        var tests = new Thread(() =>
        {
            result = Run(args);
            CFRunLoopStop(CFRunLoopGetMain());
        });
        tests.Start();
        CFRunLoopRun();
        tests.Join();
        return result;
    }

    private static int Run(string[] args) => Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args).GetAwaiter().GetResult();

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopRun();

    [DllImport(CoreFoundation)]
    private static extern nint CFRunLoopGetMain();

    [DllImport(CoreFoundation)]
    private static extern void CFRunLoopStop(nint loop);
}
