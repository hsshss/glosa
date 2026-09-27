using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Glosa.App.Controls;

/// <summary>
/// A part's keyboard: one mark per note that is down, brighter the harder it was struck.
/// </summary>
/// <remarks>
/// Drawn rather than composed from 128 child controls, because a monitor shows one of these
/// per part and there can be 32 of them redrawing several times a second.
/// </remarks>
public sealed class NoteStrip : Control
{
    private const int FirstNote = 21;    // A0, where an 88-key keyboard starts
    private const int LastNote = 108;    // C8

    public static readonly StyledProperty<byte[]?> NotesProperty =
        AvaloniaProperty.Register<NoteStrip, byte[]?>(nameof(Notes));

    public static readonly StyledProperty<byte[]?> VelocityProperty =
        AvaloniaProperty.Register<NoteStrip, byte[]?>(nameof(Velocity));

    /// <summary>A key that is down, at full strength.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<NoteStrip, IBrush?>(nameof(Foreground));

    /// <summary>Bumped by the owner when the arrays have changed underneath.</summary>
    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<NoteStrip, int>(nameof(Revision));

    private static readonly SolidColorBrush WhiteKey = new(Color.FromRgb(0x2A, 0x2A, 0x30));
    private static readonly SolidColorBrush BlackKey = new(Color.FromRgb(0x20, 0x20, 0x24));

    static NoteStrip() => AffectsRender<NoteStrip>(NotesProperty, RevisionProperty, ForegroundProperty);

    public byte[]? Notes
    {
        get => GetValue(NotesProperty);
        set => SetValue(NotesProperty, value);
    }

    public byte[]? Velocity
    {
        get => GetValue(VelocityProperty);
        set => SetValue(VelocityProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        Rect area = new(Bounds.Size);
        if (area.Width <= 0 || area.Height <= 0) return;

        context.FillRectangle(WhiteKey, area);

        const int count = LastNote - FirstNote + 1;
        double step = area.Width / count;
        if (step <= 0) return;

        // The black keys are darker so the octaves stay readable with nothing playing.
        for (int note = FirstNote; note <= LastNote; note++)
        {
            if (!IsBlack(note)) continue;
            context.FillRectangle(
                BlackKey,
                new Rect((note - FirstNote) * step, 0, Math.Max(1, step), area.Height));
        }

        byte[]? notes = Notes;
        if (notes is null) return;
        byte[]? velocity = Velocity;
        IBrush lit = Foreground ?? Brushes.White;

        for (int note = FirstNote; note <= LastNote && note < notes.Length; note++)
        {
            // bit0 is the key itself; bit1 is the pedal holding it after release.
            byte state = notes[note];
            if ((state & 0x03) == 0) continue;

            byte hit = velocity is not null && note < velocity.Length ? velocity[note] : (byte)100;
            double level = 0.45 + Math.Clamp(hit / 127.0, 0, 1) * 0.55;

            // Held by the pedal rather than by a finger: same place, dimmer.
            if ((state & 0x01) == 0) level *= 0.45;

            // Faded into the key rather than darkened, so the colour stays the brush's own.
            using (context.PushOpacity(level))
                context.FillRectangle(
                    lit,
                    new Rect((note - FirstNote) * step, 0, Math.Max(1, step), area.Height));
        }
    }

    private static bool IsBlack(int note) => (note % 12) is 1 or 3 or 6 or 8 or 10;
}
