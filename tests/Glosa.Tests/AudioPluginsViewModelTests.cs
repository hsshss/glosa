#if BRACK
using Avalonia.Threading;
using Glosa.App.ViewModels;
using Glosa.Midi.Brack;

namespace Glosa.Tests;

public sealed class AudioPluginsViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"glosa-brack-{Guid.NewGuid():N}");

    public AudioPluginsViewModelTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void APluginThatWillNotLoadSaysWhyAfterTheRackIsShownAgain()
    {
        using BrackRack rack = BrackRack.TryCreate(Path.Combine(_folder, "brack-session.json"), out _)!;
        rack.WaitLoaded();
        string? status = null;

        Headless.Run(() =>
        {
            var model = new AudioPluginsViewModel(rack, _ => { });
            // A folder, as the macOS picker gives, that is no plugin bundle.
            Task adding = model.AddFile(_folder);
            // Held, as a UI thread is while Brack refuses it: the rack shown again, posted
            // during the change, is waiting alongside the change's own end.
            Thread.Sleep(500);
            while (!adding.IsCompleted) Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            status = model.Status;
            model.Detach();
        });

        Assert.NotNull(status);
        Assert.Contains(_folder, status);
    }
}
#endif
