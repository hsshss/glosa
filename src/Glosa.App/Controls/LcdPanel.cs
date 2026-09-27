using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Glosa.Core.Emulation;

namespace Glosa.App.Controls;

/// <summary>
/// Draws the emulated module display: a text line with the 16x16 dot matrix under it, both
/// the same width.
/// </summary>
/// <remarks>
/// <para>
/// The three colours are the theme's, not the module's, and the panel is inverted: a lit
/// dot is the bright one.
/// </para>
/// <para>
/// The text is dots as well: characters come out of <see cref="LcdFont"/>, and the cells
/// they sit in show their unlit dots. Nothing here interprets MIDI; it reads
/// <see cref="Panel"/> as it stands and redraws when <see cref="Revision"/> changes.
/// </para>
/// </remarks>
public sealed class LcdPanel : Control
{
    private const int Rows = 16;
    private const int Columns = 16;

    /// <summary>
    /// How much wider a dot is than it is tall.
    /// </summary>
    /// <remarks>
    /// The cells on the real panel are not square, and a square matrix looks cramped. The
    /// matrix is 16 by 16 dots, so this is also the aspect of the whole matrix.
    /// </remarks>
    private const double DotAspect = 3.0;

    /// <summary>
    /// The most parts the meters will show, which is two rows of the matrix's width.
    /// </summary>
    /// <remarks>
    /// Two ports' worth. A player with more ports than that has more parts than a display
    /// sixteen dots across can say anything useful about.
    /// </remarks>
    private const int MaxMeters = Columns * 2;

    /// <summary>Character cells across the text display.</summary>
    private const int TextColumns = 16;

    /// <summary>Character cells down the text display.</summary>
    private const int TextRows = 2;

    /// <summary>
    /// Height of the text display, in matrix dot rows.
    /// </summary>
    /// <remarks>
    /// The text is as wide as the matrix, which fixes the character pitch: sixteen cells of
    /// <see cref="LcdFont.CellColumns"/> dots share <c>Columns * DotAspect</c> matrix dots
    /// of width. Square character dots then make each row <see cref="LcdFont.CellRows"/> of
    /// those tall. Both rows are always there, so nothing on the panel moves when a message
    /// arrives or expires.
    /// </remarks>
    private const double BandRows =
        (double)TextRows * LcdFont.CellRows * Columns * DotAspect
        / (TextColumns * LcdFont.CellColumns);

    /// <summary>Gap between the text display and the matrix, in matrix dot rows.</summary>
    private const double GapRows = 1.0;

    /// <summary>Height to lay out for when nothing has constrained one.</summary>
    private const double DefaultHeight = 112.0;

    /// <summary>Panel left showing around the dots at <see cref="DefaultHeight"/>, in pixels.</summary>
    private const double ReferenceInset = 8.0;

    /// <summary>
    /// That same margin in matrix dots, so it scales with the display.
    /// </summary>
    /// <remarks>
    /// Solving
    /// <c>dot = (height - 2 * k * dot) / rows</c> for the dot gives
    /// <c>dot = height / (rows + 2k)</c>, so the whole layout stays a division of the
    /// height it is given.
    /// </remarks>
    private const double InsetDots =
        ReferenceInset * (Rows + GapRows + BandRows) / (DefaultHeight - ReferenceInset * 2);

    public static readonly StyledProperty<PanelState?> PanelProperty =
        AvaloniaProperty.Register<LcdPanel, PanelState?>(nameof(Panel));

    /// <summary>The unlit panel behind the dots.</summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<LcdPanel, IBrush?>(nameof(Background));

    /// <summary>A dot that is on.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<LcdPanel, IBrush?>(nameof(Foreground));

    /// <summary>A dot that is off: visible, so the cell grid reads as a panel.</summary>
    public static readonly StyledProperty<IBrush?> UnlitBrushProperty =
        AvaloniaProperty.Register<LcdPanel, IBrush?>(nameof(UnlitBrush));

    /// <summary>Bumped by the owner when the panel state has moved on.</summary>
    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<LcdPanel, int>(nameof(Revision));

    /// <summary>How many parts the meters are for; see <see cref="Parts"/>.</summary>
    public static readonly StyledProperty<int> PartsProperty =
        AvaloniaProperty.Register<LcdPanel, int>(nameof(Parts), Columns);

    /// <summary>
    /// Whether the display asks for room of its own, rather than taking the height of
    /// whatever it is placed beside.
    /// </summary>
    public static readonly StyledProperty<bool> EnlargedProperty =
        AvaloniaProperty.Register<LcdPanel, bool>(nameof(Enlarged));

    static LcdPanel()
    {
        AffectsRender<LcdPanel>(PanelProperty, RevisionProperty, BackgroundProperty,
                                ForegroundProperty, UnlitBrushProperty, PartsProperty);
        AffectsMeasure<LcdPanel>(EnlargedProperty);
    }

    public PanelState? Panel
    {
        get => GetValue(PanelProperty);
        set => SetValue(PanelProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? UnlitBrush
    {
        get => GetValue(UnlitBrushProperty);
        set => SetValue(UnlitBrushProperty, value);
    }

    public int Revision
    {
        get => GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    /// <summary>
    /// How many parts there are to meter: the ports the song uses, times sixteen.
    /// </summary>
    /// <remarks>
    /// Asked for rather than counted here, because which ports a song uses is the player's
    /// answer and the panel would otherwise keep showing the last song's.
    /// </remarks>
    public int Parts
    {
        get => GetValue(PartsProperty);
        set => SetValue(PartsProperty, value);
    }

    public bool Enlarged
    {
        get => GetValue(EnlargedProperty);
        set => SetValue(EnlargedProperty, value);
    }

    /// <summary>
    /// Asks for the width a display of the height it will get needs, and for height only
    /// when <see cref="Enlarged"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The display's shape is fixed by <see cref="DotAspect"/>, so the control works its own
    /// width out from its height and can sit in an auto-sized column.
    /// </para>
    /// <para>
    /// Asking for no height makes it take its neighbour's. Measuring cannot yet know that
    /// height, so the width is worked out from the last height the panel was given, and
    /// <see cref="ArrangeOverride"/> asks for another pass when that has changed.
    /// </para>
    /// </remarks>
    protected override Size MeasureOverride(Size availableSize)
        => new(DotSize(new Size(double.PositiveInfinity,
                                Enlarged ? DefaultHeight : _givenHeight))
                   * (Columns * DotAspect + InsetDots * 2),
               Enlarged ? DefaultHeight : 0);

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!Enlarged && Math.Abs(finalSize.Height - _givenHeight) > 0.5)
        {
            _givenHeight = finalSize.Height;
            InvalidateMeasure();
        }

        return base.ArrangeOverride(finalSize);
    }

    /// <summary>The height the last arrangement gave, which the next measure sizes to.</summary>
    private double _givenHeight = DefaultHeight;

    public override void Render(DrawingContext context)
    {
        Rect area = new(Bounds.Size);
        if (area.Width <= 0 || area.Height <= 0) return;

        if (Background is { } ground) context.FillRectangle(ground, area);

        PanelState? panel = Panel;
        if (panel is null) return;

        IBrush lit = Foreground ?? Brushes.White;
        IBrush unlit = UnlitBrush ?? Brushes.Transparent;

        double dot = DotSize(area.Size);
        double dotWidth = dot * DotAspect;
        double width = dotWidth * Columns;
        double band = BandRows * dot;
        double height = band + GapRows * dot + Rows * dot;

        // Centred, because the room given is not always the room asked for: the panel keeps
        // its shape and leaves the slack as lit panel, which is what a bezel looks like.
        double left = (area.Width - width) / 2;
        double top = (area.Height - height) / 2;

        DrawText(context, panel, new Rect(left, top, width, band), unlit, lit);

        // The matrix area shows the meters whenever the module has put nothing in it.
        var origin = new Point(left, top + band + GapRows * dot);
        if (panel.BitmapVisible) DrawMatrix(context, panel, origin, dotWidth, dot, unlit, lit);
        else DrawMeters(context, panel, origin, dotWidth, dot, unlit, lit);
    }

    /// <summary>
    /// Height of one matrix dot in a panel of <paramref name="area"/>, margin included: the
    /// panel is <c>Rows + GapRows + BandRows</c> dots of drawing and <see cref="InsetDots"/>
    /// of margin at each edge, so the dot is simply what divides into both.
    /// </summary>
    private static double DotSize(Size area)
        => Math.Max(1, Math.Min(
            area.Height / (Rows + GapRows + BandRows + InsetDots * 2),
            area.Width / (Columns * DotAspect + InsetDots * 2)));

    private static void DrawMatrix(DrawingContext context, PanelState panel, Point origin,
                                   double dotWidth, double dotHeight, IBrush unlit, IBrush lit)
        => DrawDots(context, origin, dotWidth, dotHeight, unlit, lit,
                    (row, column) => panel.Bitmap[row * Columns + column] != 0);

    /// <summary>
    /// Draws one bar per part, rising from the foot of its tier, with a peak mark above it.
    /// </summary>
    /// <remarks>
    /// One dot column each, so thirty-two parts need two tiers. The first sixteen take the
    /// lower tier and the rest the one above, reading up from the foot of the display.
    ///
    /// The bottom row of each tier is always lit, so a silent part shows where its bar
    /// starts; the level is everything above that line. The line also separates the two
    /// tiers, so no row is spent on a gap.
    /// </remarks>
    private void DrawMeters(DrawingContext context, PanelState panel, Point origin,
                            double dotWidth, double dotHeight, IBrush unlit, IBrush lit)
    {
        int parts = Math.Clamp(Parts, 0, MaxMeters);
        int tiers = parts > Columns ? 2 : 1;
        int tierRows = Rows / tiers;

        // The tier, less the line the bar stands on.
        int barRows = tierRows - 1;

        // Read once: every dot of a bar asks the same question, and the levels are moving
        // underneath on the playback thread.
        int[] bars = new int[parts];
        int[] peaks = new int[parts];
        for (int part = 0; part < parts; part++)
        {
            DisplayPart it = panel.Part(part / Columns, part % Columns);
            bars[part] = Dots(panel.LevelOf(it), barRows);
            peaks[part] = Dots(it.Peak, barRows);
        }

        DrawDots(context, origin, dotWidth, dotHeight, unlit, lit, (row, column) =>
        {
            int tier = row / tierRows;
            int part = (tiers - 1 - tier) * Columns + column;
            if (part >= bars.Length) return false;

            // Zero is the line the bar stands on, so the level starts at one.
            int fromFoot = tierRows - 1 - row % tierRows;
            return fromFoot == 0 || fromFoot <= bars[part] || fromFoot == peaks[part];
        });
    }

    /// <summary>
    /// How many dots above the line a level of 0 to 127 comes to.
    /// </summary>
    /// <remarks>
    /// Rounded up, so a part playing quietly never looks like one playing nothing.
    /// </remarks>
    private static int Dots(int level, int rows)
        => level <= 0 ? 0 : Math.Min(rows, (level * rows + 126) / 127);

    /// <summary>
    /// Fills the matrix, asking <paramref name="lightUp"/> for each dot.
    /// </summary>
    /// <remarks>
    /// Every cell is drawn, lit or not, so the area reads as a panel rather than a blank.
    /// </remarks>
    private static void DrawDots(DrawingContext context, Point origin, double dotWidth,
                                 double dotHeight, IBrush unlit, IBrush lit,
                                 Func<int, int, bool> lightUp)
    {
        double width = dotWidth * 0.82;
        double height = dotHeight * 0.82;

        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                context.FillRectangle(
                    lightUp(row, column) ? lit : unlit,
                    new Rect(origin.X + column * dotWidth, origin.Y + row * dotHeight,
                             width, height));
            }
        }
    }

    /// <summary>
    /// Draws the 16 by 2 character display: characters built out of dots, on a grid of
    /// cells whose unlit dots stay visible.
    /// </summary>
    /// <remarks>
    /// The dots here are square, unlike the matrix's: stretched to <see cref="DotAspect"/>,
    /// sixteen characters would be twice the matrix's width.
    /// </remarks>
    private static void DrawText(DrawingContext context, PanelState panel, Rect box,
                                 IBrush unlit, IBrush lit)
    {
        if (box.Width <= 0 || box.Height <= 0) return;

        double dot = box.Width / (TextColumns * LcdFont.CellColumns);
        if (dot <= 0) return;

        double size = dot * 0.82;

        for (int line = 0; line < TextRows; line++)
        {
            double top = box.Y + line * LcdFont.CellRows * dot;
            for (int cell = 0; cell < TextColumns; cell++)
            {
                double left = box.X + cell * LcdFont.CellColumns * dot;
                char c = CharacterAt(panel, line, cell);
                for (int column = 0; column < LcdFont.GlyphColumns; column++)
                {
                    for (int row = 0; row < LcdFont.GlyphRows; row++)
                    {
                        context.FillRectangle(
                            LcdFont.Dot(c, column, row) ? lit : unlit,
                            new Rect(left + column * dot, top + row * dot, size, size));
                    }
                }
            }
        }
    }

    /// <summary>What is in one cell of the character display.</summary>
    /// <remarks>
    /// <see cref="PanelState.XgLcdVisible"/> says which line is on show: an XG message holds
    /// the display until <c>PanelState.Advance</c> runs the hold out.
    /// </remarks>
    private static char CharacterAt(PanelState panel, int line, int cell)
    {
        char[] text = panel.XgLcdVisible ? panel.XgLcdText : panel.LcdText;
        int index = line * TextColumns + cell;

        return index < text.Length ? text[index] : ' ';
    }
}
