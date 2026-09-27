using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Glosa.App.Services;
using Glosa.App.ViewModels;
using Glosa.App.Views;

namespace Glosa.App;

public partial class App : Application
{
    /// <summary>
    /// This run's hold on its settings folder, from <see cref="Program.Main"/>; null in the
    /// designer, or when the player already running could not be reached.
    /// </summary>
    internal static SingleInstance? Claim { get; set; }

    /// <summary>Why <c>--config</c> was not taken, from <see cref="Program.Main"/>; null when it was.</summary>
    internal static string? ConfigProblem { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string[] args = desktop.Args ?? [];

            // Before anything is worded, and the settings are where the language is kept.
            Languages.Apply(AppSettings.Load(out _).Language);

            var model = new MainViewModel();

            // The last resort: what nothing else caught is reported, and the player carries on
            // rather than taking the settings and the lists down with it.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                e.Handled = true;
                model.ReportUnexpected(e.Exception);
            };
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                e.SetObserved();
                Dispatcher.UIThread.Post(() => model.ReportUnexpected(e.Exception));
            };

            if (ConfigProblem is { } problem) model.Complain(problem);
            ApplyArguments(model, args, Environment.CurrentDirectory);
            var window = new MainWindow { DataContext = model };
            desktop.MainWindow = window;

            // A later start's command line, taken as if it had been this run's own.
            Claim?.OnRequest(request => Dispatcher.UIThread.Post(() =>
            {
                ApplyArguments(model, request.Arguments, request.WorkingDirectory);
                BringForward(window);
            }));
            window.Closing += (_, _) => Claim?.Refuse();

            // A file opened with the player on macOS arrives as an event.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                activatable.Activated += (_, e) =>
                {
                    if (e is not FileActivatedEventArgs opened) return;
                    string[] paths = [.. opened.Files.Select(file => file.TryGetLocalPath()).OfType<string>()];
                    if (paths.Length == 0) return;
                    ApplyArguments(model, paths, Environment.CurrentDirectory);
                    BringForward(window);
                };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The folder <c>--config</c> names, or null. The last one wins, as elsewhere.</summary>
    internal static string? ConfigArgument(string[] args)
    {
        string? folder = null;
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--config") folder = args[++i];
        return folder;
    }

    /// <summary>
    /// <c>glosa [--config &lt;folder&gt;] [--def &lt;file&gt;] [--use &lt;module&gt;] [--target &lt;module&gt;] [song...]</c>.
    /// Bare paths join the playlist, which is also what a shell association will hand us,
    /// and <c>--target</c> sets the module on those songs.
    /// </summary>
    /// <param name="directory">
    /// Where relative paths start: this run's working folder, or that of the later start
    /// whose arguments these are.
    /// </param>
    private static void ApplyArguments(MainViewModel model, string[] args, string directory)
    {
        var songs = new List<string>();
        string? definition = null;
        string? use = null;
        string? target = null;
        // A path the system cannot make sense of stays as it came, and is complained about below.
        string Full(string path)
        {
            try { return Path.GetFullPath(path, directory); }
            catch (ArgumentException) { return path; }
        }

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                // Already taken, before there was a model to hand it to.
                case "--config" when i + 1 < args.Length: i++; break;
                case "--def" when i + 1 < args.Length: definition = Full(args[++i]); break;
                case "--use" when i + 1 < args.Length: use = args[++i]; break;
                case "--target" when i + 1 < args.Length: target = args[++i]; break;
                case "--device" when i + 1 < args.Length: model.SelectDevice(args[++i]); break;
                // Only paths that exist join the playlist; anything else is a mistyped option.
                // Folders and songs inside an archive are taken as a drop takes them.
                case var path when Directory.Exists(Full(path))
                                   || Glosa.Core.Archives.SongStore.Exists(Full(path)):
                    songs.Add(Full(path));
                    break;
                default: model.Complain(string.Format(Strings.NoteBadArgument, args[i])); break;
            }
        }

        if (definition is not null) model.LoadDefinition(definition, use);
        else if (use is not null) model.SelectUseModule(use);

        // Taken as a drop is. --target names the module for these songs only.
        if (songs.Count > 0) model.ReceiveFiles(songs, target ?? string.Empty);
    }

    /// <summary>Puts the window in front of the user, out of the taskbar if need be.</summary>
    private static void BringForward(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }
}