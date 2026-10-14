using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glosa.App.Controls;

namespace Glosa.Tests;

public sealed class FineDragTests
{
    [Fact]
    public void WithShiftASliderMovesATenthAsFarAndNeverJumps()
    {
        var values = new List<double>();
        double perPixel = 0;
        Headless.Run(() =>
        {
            var slider = new Slider
            {
                Width = 120, Minimum = -60, Maximum = 12, Value = -24,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            FineDrag.SetEnabled(slider, true);
            var window = new Window { Width = 300, Height = 100, Content = slider };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Track track = slider.FindDescendantOfType<Track>()!;
            perPixel = (slider.Maximum - slider.Minimum) / (track.Bounds.Width - track.Thumb!.Bounds.Width);
            Point at = track.Thumb.TranslatePoint(new Point(track.Thumb.Bounds.Width / 2, track.Thumb.Bounds.Height / 2), window)!.Value;

            void Move(double dx, RawInputModifiers modifiers)
            {
                at += new Point(dx, 0);
                window.MouseMove(at, RawInputModifiers.LeftMouseButton | modifiers);
                Dispatcher.UIThread.RunJobs();
                values.Add(slider.Value);
            }

            window.MouseDown(at, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            values.Add(slider.Value);
            Move(20, RawInputModifiers.None);
            Move(0, RawInputModifiers.Shift);
            Move(20, RawInputModifiers.Shift);
            Move(0, RawInputModifiers.None);
            Move(20, RawInputModifiers.None);
            window.MouseUp(at, MouseButton.Left);
            window.Close();
        });

        Assert.Equal(-24, values[0], 0.5);
        Assert.Equal(20 * perPixel, values[1] - values[0], 0.5);
        Assert.Equal(values[1], values[2], 0.01);
        Assert.Equal(20 * perPixel * FineDrag.Fine, values[3] - values[2], 0.01);
        Assert.Equal(values[3], values[4], 0.01);
        Assert.Equal(20 * perPixel, values[5] - values[4], 0.01);
    }
}
