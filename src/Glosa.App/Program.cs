using Avalonia;
using System;
using Glosa.App.Services;

namespace Glosa.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Before anything reads the settings, and before the claim: the folder names the player.
        // One that is no path at all is said so once there is somewhere to say it.
        if (App.ConfigArgument(args) is { } folder)
        {
            try
            {
                AppSettings.ConfigDirectory = Path.GetFullPath(folder);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException
                                          or System.Security.SecurityException)
            {
                App.ConfigProblem = string.Format(Strings.NoteBadConfig, folder, ex.Message);
            }
        }

        // A player already running on these settings takes the arguments, and this run ends.
        using SingleInstance? claim = SingleInstance.Claim(AppSettings.ConfigDirectory, args, out bool handedOver);
        if (handedOver) return;

        App.Claim = claim;
        BuildAvaloniaApp(AppSettings.Load(out _).HardwareRendering)
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(hardwareRendering: true);

    private static AppBuilder BuildAvaloniaApp(bool hardwareRendering)
    {
        AppBuilder builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

        return hardwareRendering
            ? builder
            : builder
                .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.Software] })
                .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
                .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] });
    }
}
