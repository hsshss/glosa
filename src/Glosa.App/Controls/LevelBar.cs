using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Glosa.App.Controls;

/// <summary>
/// A 7-bit value as a bar: filled from the left for a level, or out from the middle for a
/// value that has one — a pan, where 64 is the centre.
/// </summary>
/// <remarks>
/// A reading, not a control: nothing here can be moved. Drawn rather than templated for the
/// same reason as <see cref="NoteStrip"/>: the monitor has two of these on every one of up
/// to 32 rows, redrawing several times a second.
/// </remarks>
public sealed class LevelBar : Control
{
    /// <summary>The middle of a centred value: pan 64 is neither left nor right.</summary>
    private const int Centre = 64;

    private const int Top = 127;

    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<LevelBar, int>(nameof(Value));

    /// <summary>Whether the value runs out either way from <see cref="Centre"/>.</summary>
    public static readonly StyledProperty<bool> CentredProperty =
        AvaloniaProperty.Register<LevelBar, bool>(nameof(Centred));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<LevelBar, IBrush?>(nameof(Track));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<LevelBar, IBrush?>(nameof(Fill));

    static LevelBar() =>
        AffectsRender<LevelBar>(ValueProperty, CentredProperty, TrackProperty, FillProperty);

    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool Centred
    {
        get => GetValue(CentredProperty);
        set => SetValue(CentredProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        Rect area = new(Bounds.Size);
        if (area.Width <= 0 || area.Height <= 0) return;

        if (Track is { } track) context.FillRectangle(track, area);
        if (Fill is not { } fill) return;

        int value = Math.Clamp(Value, 0, Top);

        if (!Centred)
        {
            context.FillRectangle(fill, new Rect(0, 0, area.Width * value / Top, area.Height));
            return;
        }

        // Each side runs to its own end: 0 is all the way left, 127 all the way right, and
        // the two halves are a step apart in size (64 steps left, 63 right) rather than the
        // middle being off centre.
        double middle = Math.Round(area.Width / 2);
        double reach = value < Centre
            ? -(middle * (Centre - value) / Centre)
            : (area.Width - middle) * (value - Centre) / (Top - Centre);

        if (reach != 0)
            context.FillRectangle(fill, new Rect(Math.Min(middle, middle + reach), 0,
                                                 Math.Abs(reach), area.Height));

        // The middle is always marked, so a pan dead centre reads as centre and not as nothing.
        context.FillRectangle(fill, new Rect(middle - 1, 0, 2, area.Height));
    }
}
