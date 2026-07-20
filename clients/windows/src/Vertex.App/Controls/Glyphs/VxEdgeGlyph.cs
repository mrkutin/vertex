using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Vertex.App.Controls.Glyphs;

/// <summary>
/// Single ascending edge glyph for "Edge" rows. Direct port of macOS
/// <c>VxEdgeGlyph</c>: stroke (4,18) → (20,6) in a 24×24 box with a
/// filled destination dot at (20,6) radius 2.4.
/// </summary>
public sealed class VxEdgeGlyph : UserControl
{
    public static readonly DependencyProperty GlyphSizeProperty =
        DependencyProperty.Register(nameof(GlyphSize), typeof(double), typeof(VxEdgeGlyph),
            new PropertyMetadata(20.0, (d, _) => ((VxEdgeGlyph)d).Build()));

    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    public VxEdgeGlyph()
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

        canvas.Children.Add(new Line
        {
            X1 = 4 * s, Y1 = 18 * s, X2 = 20 * s, Y2 = 6 * s,
            Stroke = brush, StrokeThickness = 2 * s,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap   = PenLineCap.Round,
            StrokeLineJoin     = PenLineJoin.Round,
        });

        const double dotR = 2.4;
        var dot = new Ellipse { Width = 2 * dotR * s, Height = 2 * dotR * s, Fill = brush };
        Canvas.SetLeft(dot, (20 - dotR) * s);
        Canvas.SetTop(dot,  (6  - dotR) * s);
        canvas.Children.Add(dot);

        Content = canvas;
    }
}
