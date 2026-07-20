using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Vertex.App.Controls.Glyphs;

/// <summary>
/// Mini V-asterisk glyph for "Vertex" rows. Direct port of macOS
/// <c>VxAsteriskGlyph</c>: two strokes from (6,8) → (16.5,22) and
/// (18,8) → (7.5,22) in a 24×24 box, stroke width 2 with round caps;
/// three filled circles mark endpoints + the meeting vertex (12,16).
/// </summary>
public sealed class VxAsteriskGlyph : UserControl
{
    public static readonly DependencyProperty GlyphSizeProperty =
        DependencyProperty.Register(nameof(GlyphSize), typeof(double), typeof(VxAsteriskGlyph),
            new PropertyMetadata(20.0, (d, _) => ((VxAsteriskGlyph)d).Build()));

    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    public VxAsteriskGlyph()
    {
        IsTabStop = false;
        Build();
    }

    private void Build()
    {
        Width  = GlyphSize;
        Height = GlyphSize;

        double s = GlyphSize / 24.0;
        var brush = (Brush)Application.Current.Resources["AccentPrimaryBrush"];

        var canvas = new Canvas { Width = GlyphSize, Height = GlyphSize };

        canvas.Children.Add(MakeStroke(6 * s, 8 * s, 16.5 * s, 22 * s, 2 * s, brush));
        canvas.Children.Add(MakeStroke(18 * s, 8 * s, 7.5 * s, 22 * s, 2 * s, brush));

        AddDot(canvas, 6 * s,  8 * s,  1.6 * s, brush);
        AddDot(canvas, 18 * s, 8 * s,  1.6 * s, brush);
        AddDot(canvas, 12 * s, 16 * s, 2.0 * s, brush);

        Content = canvas;
    }

    private static Line MakeStroke(double x1, double y1, double x2, double y2, double width, Brush brush) => new()
    {
        X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
        Stroke = brush, StrokeThickness = width,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap   = PenLineCap.Round,
        StrokeLineJoin     = PenLineJoin.Round,
    };

    private static void AddDot(Canvas parent, double cx, double cy, double r, Brush fill)
    {
        var dot = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = fill };
        Canvas.SetLeft(dot, cx - r);
        Canvas.SetTop(dot,  cy - r);
        parent.Children.Add(dot);
    }
}
