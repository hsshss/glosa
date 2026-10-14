#if BRACK
using Avalonia.Threading;
using Glosa.App.ViewModels;
using Glosa.Midi.Brack;

namespace Glosa.Tests;

/// <remarks>In a settings folder of its own: the window's scan caches what it found there.</remarks>
[Collection(ConfigFolder.Name)]
public sealed class AudioPluginsViewModelTests : ConfigFolder
{
    [Fact]
    public void APluginThatWillNotLoadSaysWhyAfterTheRackIsShownAgain()
    {
        using BrackRack rack = BrackRack.TryCreate(Path.Combine(Folder, "brack-session.json"), out _)!;
        rack.WaitLoaded();
        string? status = null;

        Headless.Run(() =>
        {
            var model = new AudioPluginsViewModel(rack, _ => { });
            // A folder, as the macOS picker gives, that is no plugin bundle.
            Task adding = model.AddFile(Folder);
            // Held, as a UI thread is while Brack refuses it: the rack shown again, posted
            // during the change, is waiting alongside the change's own end.
            Thread.Sleep(500);
            while (!adding.IsCompleted) Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            status = model.Status;
            model.Detach();
            // Before the folder goes, which the scan writes its cache in.
            Assert.True(SpinWait.SpinUntil(() =>
            {
                Dispatcher.UIThread.RunJobs();
                return !model.Scanning;
            }, TimeSpan.FromMinutes(2)), "the scan did not end");
        });

        Assert.NotNull(status);
        Assert.Contains(Folder, status);
    }

    private static ChannelLevel[] Levels(params (float Peak, ulong Clips)[] channels)
        => [.. channels.Select(channel => new ChannelLevel(channel.Peak, channel.Clips))];

    [Fact]
    public void AMetersLampIsLitByClipsSinceItWasLastClickedAndOnlyThose()
    {
        var meter = new Meter();
        meter.Show(Levels((0.5f, 3), (0.5f, 0)));
        Assert.True(meter.Clipped);

        meter.ClearCommand.Execute(null);
        meter.Show(Levels((0.5f, 3), (0.5f, 0)));
        Assert.False(meter.Clipped);
        Assert.Null(meter.ClipTip);

        meter.Show(Levels((0.5f, 3), (1.0f, 2)));
        Assert.True(meter.Clipped);
        Assert.Contains("2", meter.ClipTip);
    }

    [Fact]
    public void AStoppedMeterKeepsItsBarsAtNothing()
    {
        var meter = new Meter();
        meter.Show(Levels((1.0f, 0), (0.1f, 0)));
        Assert.Equal([127, 85], meter.Levels);

        meter.Show([]);
        Assert.Equal([0, 0], meter.Levels);
    }
}
#endif
