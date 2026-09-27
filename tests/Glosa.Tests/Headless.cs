using Avalonia;
using Avalonia.Headless;
using Glosa.App.Services;

namespace Glosa.Tests;

/// <summary>
/// Runs code on a UI thread, as the player's view models expect, with no window on screen.
/// </summary>
/// <remarks>
/// One application for every test: Avalonia allows one per process.
/// </remarks>
internal static class Headless
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() =>
        HeadlessUnitTestSession.StartNew(typeof(TestApp), AvaloniaTestIsolationLevel.PerAssembly));

    /// <summary>Runs <paramref name="action"/> on the UI thread and waits for it.</summary>
    public static void Run(Action action)
        => Session.Value.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    public sealed class TestApp : Application
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}

/// <summary>
/// Points the player's settings folder at a folder of the test's own, and puts it back after.
/// </summary>
/// <remarks>
/// The folder is a static, so every test that moves it is in <see cref="Name"/>, which runs
/// one test at a time.
/// </remarks>
public abstract class ConfigFolder : IDisposable
{
    public const string Name = "Settings folder";

    private readonly string _was = AppSettings.ConfigDirectory;

    protected ConfigFolder()
    {
        Directory.CreateDirectory(Folder);
        AppSettings.ConfigDirectory = Folder;
    }

    protected string Folder { get; } = Path.Combine(Path.GetTempPath(), $"glosa-config-{Guid.NewGuid():N}");

    public void Dispose()
    {
        AppSettings.ConfigDirectory = _was;
        Directory.Delete(Folder, recursive: true);
    }
}
