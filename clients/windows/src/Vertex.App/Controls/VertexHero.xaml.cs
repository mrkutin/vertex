using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Vertex.Shared;
using Windows.UI;

namespace Vertex.App.Controls;

/// <summary>
/// Animated marquee for ConnectScreen — port of macOS
/// <c>VertexHero.swift</c>. Renders the V-asterisk geometry (two
/// crossing tails through three nodes) with state-keyed glow, halo
/// breath, traffic-driven endpoint glow, edge shimmer, and connecting
/// pulse + sweep. 30 FPS via Win2D <c>CanvasAnimatedControl</c>.
///
/// <para>Reference: <c>clients/macos/Vertex/App/Views/Components/VertexHero.swift</c>.
/// Geometry constants and timing curves match line-for-line.</para>
/// </summary>
public sealed partial class VertexHero : UserControl
{
    public static readonly DependencyProperty StateProperty =
        DependencyProperty.Register(nameof(State), typeof(ConnectionState), typeof(VertexHero),
            new PropertyMetadata(ConnectionState.Disconnected, (d, _) => ((VertexHero)d).OnStateChanged()));

    public static readonly DependencyProperty UploadBytesPerSecProperty =
        DependencyProperty.Register(nameof(UploadBytesPerSec), typeof(double), typeof(VertexHero),
            new PropertyMetadata(0.0, (d, _) => ((VertexHero)d).OnRateChanged(true)));

    public static readonly DependencyProperty DownloadBytesPerSecProperty =
        DependencyProperty.Register(nameof(DownloadBytesPerSec), typeof(double), typeof(VertexHero),
            new PropertyMetadata(0.0, (d, _) => ((VertexHero)d).OnRateChanged(false)));

    public ConnectionState State              { get => (ConnectionState)GetValue(StateProperty);              set => SetValue(StateProperty, value); }
    public double          UploadBytesPerSec  { get => (double)GetValue(UploadBytesPerSecProperty);            set => SetValue(UploadBytesPerSecProperty, value); }
    public double          DownloadBytesPerSec { get => (double)GetValue(DownloadBytesPerSecProperty);         set => SetValue(DownloadBytesPerSecProperty, value); }

    /// <summary>
    /// Smoothed rate scalars in [0,1]. macOS uses SwiftUI animation on
    /// the @State property; Windows interpolates per-frame in the draw
    /// loop with the same 400ms ease-in-out window.
    /// </summary>
    private double _rUpDisplayed;
    private double _rDnDisplayed;
    private double _rUpTarget;
    private double _rDnTarget;
    private DateTime _rUpAnimStart;
    private DateTime _rDnAnimStart;
    private double _rUpAnimFrom, _rDnAnimFrom;
    private static readonly TimeSpan RateAnimDuration = TimeSpan.FromMilliseconds(400);

    /// <summary>Cached `Reduce Motion` setting from the OS.</summary>
    private readonly bool _reduceMotion;

    /// <summary>
    /// Runtime flag — set to false to fall back to the static XAML
    /// V-asterisk instead of the Win2D animated surface. Parallels VM
    /// on Apple Silicon hits an early Direct2D init failure that
    /// FailFasts the process on first paint; this lets the same binary
    /// run there for visual smoke tests of the rest of the UI.
    /// </summary>
    public static bool UseWin2D { get; set; } = false;

    public VertexHero()
    {
        InitializeComponent();
        _reduceMotion = !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        _rUpDisplayed = QuantizedIntensity(UploadBytesPerSec);
        _rDnDisplayed = QuantizedIntensity(DownloadBytesPerSec);
        _rUpTarget    = _rUpDisplayed;
        _rDnTarget    = _rDnDisplayed;

        if (UseWin2D)
        {
            Surface.Visibility  = Visibility.Visible;
            StaticGeo.Visibility = Visibility.Collapsed;
        }
        else
        {
            BuildStaticGeo();
        }
    }

    /// <summary>
    /// Static XAML-only V-asterisk for the Win2D fallback path.
    /// Re-renders on every state change; drops the breath / pulse /
    /// shimmer animation but reads the same geometry constants.
    /// </summary>
    private void BuildStaticGeo()
    {
        StaticGeo.Children.Clear();
        var color = State switch
        {
            ConnectionState.Connected     => Microsoft.UI.Colors.White,
            ConnectionState.Connecting    => FromResource("StateTransitioningBrush"),
            ConnectionState.Handshaking   => FromResource("StateTransitioningBrush"),
            ConnectionState.Reconnecting  => FromResource("StateTransitioningBrush"),
            _                             => FromResource("GlyphDimBrush"),
        };
        var stroke = State == ConnectionState.Disconnected ? 9.0 : 12.0;
        var brush = new SolidColorBrush(color);

        Line MakeLine(double x1, double y1, double x2, double y2) => new()
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = brush, StrokeThickness = stroke,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Ellipse MakeNode(double cx, double cy, double r)
        {
            var e = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = brush };
            Canvas.SetLeft(e, cx - r);
            Canvas.SetTop(e, cy - r);
            return e;
        }

        StaticGeo.Children.Add(MakeLine(51.8, 50.3, 122.5, 177.2));
        StaticGeo.Children.Add(MakeLine(168.2, 50.3, 97.5, 177.2));
        StaticGeo.Children.Add(MakeNode(60.2, 65.3, 10.3));
        StaticGeo.Children.Add(MakeNode(159.8, 65.3, 10.3));
        StaticGeo.Children.Add(MakeNode(110.0, 154.7, 15.5));
    }

    /// <summary>
    /// Win2D resources init. If Direct2D init fails (e.g. on a software
    /// renderer with no D3D feature-level 9_3) the canvas raises a
    /// CanvasCreateResourcesEventArgs Exception via the regular event;
    /// the alternative — letting the next Draw event crash inside
    /// native code — fast-fails the whole process. Surface the failure
    /// as a debug-broken animation rather than a process exit.
    /// </summary>
    private void OnCreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
    {
        // No-op: we don't pre-allocate brushes in CreateResources because
        // every Draw call rebuilds gradient stops from theme colors that
        // can change per-state. The handler exists purely so the runtime
        // doesn't FailFast on a missing event when the initial device
        // surface is software-only.
    }

    private void OnStateChanged()
    {
        if (State == ConnectionState.Disconnected)
        {
            // Snap rate scalars back to idle so the next Connect doesn't
            // start with stale glow.
            _rUpDisplayed = _rUpTarget = 0;
            _rDnDisplayed = _rDnTarget = 0;
        }
        if (!UseWin2D) BuildStaticGeo();
    }

    private void OnRateChanged(bool upload)
    {
        var target = QuantizedIntensity(upload ? UploadBytesPerSec : DownloadBytesPerSec);
        var now = DateTime.UtcNow;
        if (upload)
        {
            _rUpAnimFrom = _rUpDisplayed;
            _rUpTarget   = target;
            _rUpAnimStart = now;
        }
        else
        {
            _rDnAnimFrom = _rDnDisplayed;
            _rDnTarget   = target;
            _rDnAnimStart = now;
        }
    }

    /// <summary>
    /// Per-frame draw. The CanvasAnimatedControl ticks at 30 FPS via
    /// TargetElapsedTime; <c>args.Timing.TotalTime</c> is the seconds
    /// since first paint — same role as Swift's
    /// <c>ctx.date.timeIntervalSinceReferenceDate</c>.
    /// </summary>
    private void OnDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var size = sender.Size;
        var contentSize = (float)Math.Min(size.Width, size.Height);
        // Reserve 24pt halo padding on each side; geometry is the
        // remaining 220pt × ratio.
        const float haloPadding = 24f;
        var inner = Math.Max(1f, contentSize - 2 * haloPadding);
        var innerScale = inner / 220.0f;
        var offset = (contentSize - inner) / 2f;

        var t = args.Timing.TotalTime.TotalSeconds;
        AdvanceRateAnim(DateTime.UtcNow);

        var ds = args.DrawingSession;

        var geom = new Geom(innerScale, offset);
        var targets = StateTargets(t, _rUpDisplayed, _rDnDisplayed);

        // Layer 1 — ambient halo behind everything.
        if (targets.HaloIntensity > 0.001)
        {
            var center = new Vector2(contentSize / 2, contentSize / 2);
            var r = contentSize * 0.5f * (float)targets.HaloScale;
            using var brush = new CanvasRadialGradientBrush(ds, new[]
            {
                new CanvasGradientStop { Position = 0, Color = WithAlpha(targets.HaloColor, targets.HaloIntensity) },
                new CanvasGradientStop { Position = 1, Color = WithAlpha(targets.HaloColor, 0) },
            })
            {
                Center = center,
                RadiusX = r,
                RadiusY = r,
                OriginOffset = Vector2.Zero,
            };
            ds.FillCircle(center, r, brush);
        }

        // Layers 2 & 3 — endpoint glows.
        DrawNodeGlow(ds, geom.A, (float)targets.NodeAGlowRadius * innerScale,
            targets.NodeAColor, targets.NodeAGlowAlphaCap, targets.NodeAIntensity);
        DrawNodeGlow(ds, geom.B, (float)targets.NodeBGlowRadius * innerScale,
            targets.NodeBColor, targets.NodeBGlowAlphaCap, targets.NodeBIntensity);

        // Layer 4 — vertex glow.
        DrawNodeGlow(ds, geom.V, (float)targets.VertexGlowRadius * innerScale,
            targets.VertexColor, 0.65, targets.VertexIntensity);

        // Layers 5 & 6 — full extended edges.
        var edgeStroke = (float)targets.EdgeStroke * innerScale;
        DrawEdge(ds, geom.LineAStart, geom.LineAEnd,
            targets.EdgeColor, targets.EdgeOpacity, edgeStroke, targets.EdgeSweepA);
        DrawEdge(ds, geom.LineBStart, geom.LineBEnd,
            targets.EdgeColor, targets.EdgeOpacity, edgeStroke, targets.EdgeSweepB);

        // Layers 6a & 6b — active-segment halo bloom (endpoint→vertex segment).
        DrawSegmentHalo(ds, geom.SegAStart, geom.SegAEnd, _rUpDisplayed, edgeStroke, targets.EdgeColor);
        DrawSegmentHalo(ds, geom.SegBStart, geom.SegBEnd, _rDnDisplayed, edgeStroke, targets.EdgeColor);

        // Layers 7 & 8 — moving shimmer band (connected w/ traffic only).
        if (targets.ShimmerASpeed is double sa && !_reduceMotion)
            DrawShimmer(ds, geom.LineAStart, geom.LineAEnd, _rUpDisplayed, sa, t, edgeStroke, targets.EdgeColor);
        if (targets.ShimmerBSpeed is double sb && !_reduceMotion)
            DrawShimmer(ds, geom.LineBEnd, geom.LineBStart, _rDnDisplayed, sb, t, edgeStroke, targets.EdgeColor);

        // Layer 9 — endpoint cores.
        DrawCore(ds, geom.A, Geom.EndpointRadius * innerScale, targets.NodeAColor, targets.NodeAIntensity);
        DrawCore(ds, geom.B, Geom.EndpointRadius * innerScale, targets.NodeBColor, targets.NodeBIntensity);

        // Layer 10 — vertex core ON TOP.
        DrawCore(ds, geom.V, Geom.VertexRadius * innerScale, targets.VertexColor, targets.VertexIntensity);
    }

    // ---- rate animation ----

    private void AdvanceRateAnim(DateTime now)
    {
        _rUpDisplayed = AdvanceOne(_rUpAnimFrom, _rUpTarget, _rUpAnimStart, now, _rUpDisplayed);
        _rDnDisplayed = AdvanceOne(_rDnAnimFrom, _rDnTarget, _rDnAnimStart, now, _rDnDisplayed);
    }

    private double AdvanceOne(double from, double target, DateTime start, DateTime now, double current)
    {
        if (start == default) return target;
        var dur = _reduceMotion ? TimeSpan.FromMilliseconds(600) : RateAnimDuration;
        var t = Math.Clamp((now - start).TotalMilliseconds / dur.TotalMilliseconds, 0, 1);
        if (t >= 1) return target;
        // Ease-in-out — smooth-step.
        var eased = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
        return from + (target - from) * eased;
    }

    // ---- intensity mapping ----

    private double QuantizedIntensity(double bps)
    {
        if (_reduceMotion)
        {
            if (bps < 50_000)    return 0;
            if (bps < 5_000_000) return 0.5111;
            return 1.0;
        }
        return RateMap(bps);
    }

    private const double RateFloor    = 50_000;
    private const double RateCeil     = 50_000_000;
    private const double RateExponent = 0.85;

    private static double RateMap(double rawBps)
    {
        var bps = Math.Max(0, rawBps);
        if (bps <= RateFloor) return 0;
        var clamped = Math.Min(bps, RateCeil);
        var num = Math.Log10(1 + clamped / RateFloor);
        var den = Math.Log10(RateCeil / RateFloor);
        var normalized = num / den;
        return Math.Pow(Math.Clamp(normalized, 0, 1), RateExponent);
    }

    // ---- primitive drawing ----

    private static void DrawNodeGlow(CanvasDrawingSession ds, Vector2 center, float radius,
        Color color, double innerAlphaCap, double intensity)
    {
        if (intensity < 0.001 || radius < 0.001) return;
        using var brush = new CanvasRadialGradientBrush(ds, new[]
        {
            new CanvasGradientStop { Position = 0, Color = WithAlpha(color, intensity * innerAlphaCap) },
            new CanvasGradientStop { Position = 1, Color = WithAlpha(color, 0) },
        })
        {
            Center = center,
            RadiusX = radius,
            RadiusY = radius,
            OriginOffset = Vector2.Zero,
        };
        ds.FillCircle(center, radius, brush);
    }

    private static void DrawCore(CanvasDrawingSession ds, Vector2 center, float radius,
        Color color, double intensity)
    {
        if (intensity < 0.001 || radius < 0.001) return;
        ds.FillCircle(center, radius, WithAlpha(color, intensity));
    }

    private static void DrawEdge(CanvasDrawingSession ds, Vector2 p0, Vector2 p1,
        Color color, double opacity, float stroke, double? sweepProgress)
    {
        var brush = WithAlpha(color, opacity);
        var style = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
        ds.DrawLine(p0, p1, brush, stroke, style);

        if (sweepProgress is double t && t > 0)
        {
            var clamped = (float)Math.Clamp(t, 0, 1);
            var trimmed = p0 + (p1 - p0) * clamped;
            var sweepBrush = WithAlpha(Colors.White, 0.85 * opacity);
            ds.DrawLine(p0, trimmed, sweepBrush, stroke * 0.7f, style);
        }
    }

    private static void DrawSegmentHalo(CanvasDrawingSession ds, Vector2 p0, Vector2 p1,
        double r, float baseStroke, Color color)
    {
        if (r < 0.001) return;
        var width = baseStroke * (1.7f + 0.6f * (float)r);
        var opacity = 0.10 + 0.28 * r;
        var style = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        ds.DrawLine(p0, p1, WithAlpha(color, opacity), width, style);
    }

    private static void DrawShimmer(CanvasDrawingSession ds, Vector2 p0, Vector2 p1,
        double r, double speed, double time, float stroke, Color color)
    {
        if (r < 0.001) return;
        var u = (time * speed) - Math.Floor(time * speed); // fractional part
        const double half = 0.09;
        var lo = Math.Max(0, u - half);
        var hi = Math.Min(1, u + half);
        if (hi <= lo) return;
        var bandStart = p0 + (p1 - p0) * (float)lo;
        var bandEnd   = p0 + (p1 - p0) * (float)hi;
        var style = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
        ds.DrawLine(bandStart, bandEnd, WithAlpha(color, 0.20 + 0.35 * r), stroke * 0.55f, style);
    }

    private static Color WithAlpha(Color c, double a)
    {
        var clamped = (byte)Math.Clamp((int)Math.Round(a * 255), 0, 255);
        return Color.FromArgb(clamped, c.R, c.G, c.B);
    }

    // ---- geometry ----

    /// <summary>
    /// 220×220 reference geometry from the macOS source. Field names
    /// match Swift <c>Geom</c> for direct cross-reference.
    /// </summary>
    private readonly struct Geom
    {
        public readonly Vector2 A, B, V;
        public readonly Vector2 LineAStart, LineAEnd, LineBStart, LineBEnd;
        public readonly Vector2 SegAStart, SegAEnd, SegBStart, SegBEnd;

        public const float EndpointRadius = 10.3f;
        public const float VertexRadius   = 15.5f;

        public Geom(float scale, float offset)
        {
            Vector2 P(float x, float y) => new Vector2(x * scale + offset, y * scale + offset);
            A = P(60.2f, 65.3f);
            B = P(159.8f, 65.3f);
            V = P(110.0f, 154.7f);
            LineAStart = P(51.8f, 50.3f);
            LineAEnd   = P(122.5f, 177.2f);
            LineBStart = P(168.2f, 50.3f);
            LineBEnd   = P(97.5f, 177.2f);
            SegAStart  = A;
            SegAEnd    = V;
            SegBStart  = B;
            SegBEnd    = V;
        }
    }

    // ---- state targets ----

    private readonly record struct StateTargetValues(
        Color  NodeAColor, Color NodeBColor, Color VertexColor, Color EdgeColor,
        double NodeAIntensity, double NodeBIntensity, double VertexIntensity, double EdgeOpacity,
        double EdgeStroke,
        double NodeAGlowRadius, double NodeBGlowRadius,
        double NodeAGlowAlphaCap, double NodeBGlowAlphaCap,
        double VertexGlowRadius,
        double? EdgeSweepA, double? EdgeSweepB,
        double? ShimmerASpeed, double? ShimmerBSpeed,
        Color  HaloColor, double HaloIntensity, double HaloScale);

    private const double EdgeStrokeFull       = 12.0;
    private const double EdgeStrokeIdle       = 9.0;
    private const double EndpointGlowRadius   = 30.1;
    private const double VertexGlowRadiusBase = 51.6;
    private const double HeroBreathPeriod     = 2.4;
    private const double HeroPulsePeriod      = 0.9;
    private const double HeroReassertPeriod   = 1.4;

    private StateTargetValues StateTargets(double time, double rUp, double rDn)
    {
        const double twoPi = 2.0 * Math.PI;
        switch (State)
        {
            case ConnectionState.Connected:
            {
                var phase = Math.Sin(time * (twoPi / HeroBreathPeriod));
                var breathAlpha = _reduceMotion ? 0.55 : 0.55 + 0.15 * phase;
                var breathScale = _reduceMotion ? 1.0  : 1.0  + 0.04 * phase;

                var nodeAIntensity = 0.55 + 0.45 * rUp;
                var nodeBIntensity = 0.55 + 0.45 * rDn;
                var nodeAGlowRadius = EndpointGlowRadius + 14.9 * rUp;
                var nodeBGlowRadius = EndpointGlowRadius + 14.9 * rDn;
                var nodeAGlowAlphaCap = 0.45 + 0.30 * rUp;
                var nodeBGlowAlphaCap = 0.45 + 0.30 * rDn;
                const double vertexGlowR = 38.0;

                double? shimmerA = (rUp > 0.001 && !_reduceMotion) ? 0.6 + 1.4 * rUp : (double?)null;
                double? shimmerB = (rDn > 0.001 && !_reduceMotion) ? 0.6 + 1.4 * rDn : (double?)null;

                return new StateTargetValues(
                    Colors.White, Colors.White, Colors.White, Colors.White,
                    nodeAIntensity, nodeBIntensity, 1.0, 1.0,
                    EdgeStrokeFull,
                    nodeAGlowRadius, nodeBGlowRadius,
                    nodeAGlowAlphaCap, nodeBGlowAlphaCap,
                    vertexGlowR,
                    null, null,
                    shimmerA, shimmerB,
                    StateConnectedGlow(), 0.6 * breathAlpha, breathScale);
            }
            case ConnectionState.Connecting:
            case ConnectionState.Handshaking:
            {
                var cycle = HeroPulsePeriod * 3.0;
                var cyclePos = (time % cycle) / HeroPulsePeriod;
                var active = ((int)Math.Floor(cyclePos)) % 3;
                var pulse = 0.55 + 0.45 * Math.Sin(time * (twoPi / HeroPulsePeriod));
                var aInt = _reduceMotion ? 0.7 : (active == 0 ? pulse : 0.45);
                var bInt = _reduceMotion ? 0.7 : (active == 1 ? pulse : 0.45);
                var vInt = _reduceMotion ? 0.7 : (active == 2 ? pulse : 0.55);
                var sweepFrac = cyclePos - Math.Floor(cyclePos);
                double? sweepA = (active == 0 && !_reduceMotion) ? sweepFrac : (double?)null;
                double? sweepB = (active == 1 && !_reduceMotion) ? sweepFrac : (double?)null;
                var connectingBreath = _reduceMotion
                    ? 0.85
                    : 0.7 + 0.3 * Math.Sin(time * (twoPi / HeroPulsePeriod));

                return new StateTargetValues(
                    StateTransitioning(), StateTransitioning(), StateTransitioning(), StateTransitioning(),
                    aInt, bInt, vInt, 0.65,
                    EdgeStrokeFull,
                    EndpointGlowRadius, EndpointGlowRadius,
                    0.55, 0.55,
                    VertexGlowRadiusBase,
                    sweepA, sweepB,
                    null, null,
                    StateTransitioning(), 0.4 * connectingBreath, 1.0);
            }
            case ConnectionState.Reconnecting:
            {
                var cycle = HeroReassertPeriod * 3.0;
                var cyclePos = (time % cycle) / HeroReassertPeriod;
                var active = ((int)Math.Floor(cyclePos)) % 3;
                var pulse = 0.55 + 0.35 * Math.Sin(time * (twoPi / HeroReassertPeriod));
                var aInt = _reduceMotion ? 0.7 : (active == 0 ? pulse : 0.45);
                var bInt = _reduceMotion ? 0.7 : (active == 1 ? pulse : 0.45);
                var vInt = _reduceMotion ? 0.7 : (active == 2 ? pulse : 0.55);

                return new StateTargetValues(
                    StateTransitioning(), StateTransitioning(), StateTransitioning(), StateTransitioning(),
                    aInt, bInt, vInt, 0.6,
                    EdgeStrokeFull,
                    EndpointGlowRadius, EndpointGlowRadius,
                    0.55, 0.55,
                    VertexGlowRadiusBase,
                    null, null,
                    null, null,
                    StateTransitioning(), 0.3, 1.0);
            }
            case ConnectionState.Disconnected:
            default:
            {
                var dim = GlyphDim();
                return new StateTargetValues(
                    dim, dim, dim, dim,
                    1.0, 1.0, 1.0, 0.85,
                    EdgeStrokeIdle,
                    EndpointGlowRadius, EndpointGlowRadius,
                    0.55, 0.55,
                    VertexGlowRadiusBase,
                    null, null,
                    null, null,
                    Colors.Transparent, 0, 1.0);
            }
        }
    }

    // Theme colours pulled directly from Application.Resources so a future
    // palette change in Theme/Colors.xaml propagates here without code edits.
    private static Color FromResource(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;
    private static Color StateConnectedGlow()  => FromResource("AccentPrimaryBrush");
    private static Color StateTransitioning()  => FromResource("StateTransitioningBrush");
    private static Color GlyphDim()            => FromResource("GlyphDimBrush");
}
