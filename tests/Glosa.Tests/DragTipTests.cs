using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Glosa.App.Controls;

namespace Glosa.Tests;

public sealed class DragTipTests
{
    [Fact]
    public void TheTipStaysOpenWhileDraggedEvenOffTheControlAndClosesOnRelease()
    {
        var states = new List<(string When, bool Open)>();
        Headless.Run(() =>
        {
            var control = new Border
            {
                Width = 100, Height = 20, Background = Avalonia.Media.Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            ToolTip.SetTip(control, "0.0 dB");
            DragTip.SetShow(control, true);
            var window = new Window { Width = 300, Height = 200, Content = control };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            void Note(string when)
            {
                Dispatcher.UIThread.RunJobs();
                states.Add((when, ToolTip.GetIsOpen(control)));
            }

            window.MouseDown(new Point(50, 10), MouseButton.Left);
            Note("pressed");
            window.MouseMove(new Point(60, 10), RawInputModifiers.LeftMouseButton);
            Note("dragged");
            window.MouseMove(new Point(60, 100), RawInputModifiers.LeftMouseButton);
            Note("dragged off");
            window.MouseUp(new Point(60, 100), MouseButton.Left);
            Note("released");
            window.Close();
        });

        Assert.Equal([("pressed", true), ("dragged", true), ("dragged off", true), ("released", false)], states);
    }
}
