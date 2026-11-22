using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Utility.App.Controls;

/// <summary>
/// 270-degree ring gauge. Bind <see cref="Target"/>; the rendered value glides toward it
/// from whatever is on screen, so rapid telemetry updates never jump or stack.
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    private const double SweepDegrees = 270;
    private const double StartDegrees = 135;
    private static readonly CubicEase EaseOut = CreateEase();

    private Geometry? _track;
    private (Point Center, double Radius) _trackDimensions;
    private Pen? _trackPen;
    private Pen? _valuePen;

    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(double), typeof(RingGauge),
        new PropertyMetadata(double.NaN, OnTargetChanged));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsRender, InvalidatePens));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender, InvalidatePens));

    public static readonly DependencyProperty ValueBrushProperty = DependencyProperty.Register(
        nameof(ValueBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender, InvalidatePens));

    public double Target { get => (double)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush ValueBrush { get => (Brush)GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (RingGauge)d;
        var target = e.NewValue is double v && double.IsFinite(v) ? Math.Clamp(v, 0, Math.Max(0, gauge.Maximum)) : 0;
        var previous = gauge.Value;
        gauge.BeginAnimation(ValueProperty, null);
        gauge.Value = target;
        if (!gauge.IsLoaded || !gauge.IsVisible || !SystemParameters.ClientAreaAnimation || Math.Abs(target - previous) < 0.5)
        {
            return;
        }

        // Distance-scaled duration: small changes settle quickly, large ones glide.
        var fraction = Math.Clamp(Math.Abs(target - previous) / Math.Max(1, gauge.Maximum), 0, 1);
        var ms = 100 + (1 - Math.Pow(1 - fraction, 3)) * 180;
        var animation = new DoubleAnimation(previous, target, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(animation, 24);
        gauge.BeginAnimation(ValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= StrokeWidth * 2)
            return;

        var radius = (size - StrokeWidth) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        if (_track is null || _trackDimensions != (center, radius))
        {
            _track = Arc(center, radius, SweepDegrees);
            _trackDimensions = (center, radius);
        }
        dc.DrawGeometry(null, _trackPen ??= CreatePen(TrackBrush), _track);

        var fraction = Maximum > 0 ? Math.Clamp(Value / Maximum, 0, 1) : 0;
        if (fraction > 0.002)
        {
            dc.DrawGeometry(null, _valuePen ??= CreatePen(ValueBrush), Arc(center, radius, SweepDegrees * fraction));
        }
    }

    private Pen CreatePen(Brush brush) =>
        new(brush, StrokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

    private static void InvalidatePens(DependencyObject element, DependencyPropertyChangedEventArgs _)
    {
        var gauge = (RingGauge)element;
        gauge._trackPen = null;
        gauge._valuePen = null;
    }

    private static CubicEase CreateEase()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        easing.Freeze();
        return easing;
    }

    private static StreamGeometry Arc(Point c, double r, double sweep)
    {
        static Point At(Point c, double r, double deg)
        {
            var rad = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
        }

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(At(c, r, StartDegrees), false, false);
            ctx.ArcTo(At(c, r, StartDegrees + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise, true, false);
        }

        geo.Freeze();
        return geo;
    }
}

/// <summary>Lightweight filled sparkline on a fixed 0..Maximum scale.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Color), typeof(Sparkline),
        new FrameworkPropertyMetadata(Colors.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public Color Stroke { get => (Color)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var values = Values;
        if (values is null || values.Count == 0 || w <= 0 || h <= 2)
            return;

        var cap = Math.Max(2, Capacity);
        var step = w / (cap - 1);
        var offset = (cap - values.Count) * step;
        Point P(int i) => new(Math.Clamp(offset + i * step, 2, Math.Max(2, w - 2)),
            h - Math.Clamp(values[i] / Math.Max(1, Maximum), 0, 1) * (h - 4) - 2);
        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var curve = line.Open())
        using (var area = fill.Open())
        {
            var connected = false;
            var lastPoint = new Point();
            for (var i = Math.Max(0, values.Count - cap); i < values.Count; i++)
            {
                if (!double.IsFinite(values[i]))
                {
                    if (connected)
                        area.LineTo(new Point(lastPoint.X, h), false, false);
                    connected = false;
                    continue;
                }
                var point = P(i);
                if (!connected)
                {
                    curve.BeginFigure(point, false, false);
                    area.BeginFigure(new Point(point.X, h), true, true);
                }
                else
                {
                    curve.LineTo(point, true, true);
                }
                area.LineTo(point, false, false);
                lastPoint = point;
                connected = true;
            }
            if (connected)
                area.LineTo(new Point(lastPoint.X, h), false, false);
        }
        line.Freeze();
        fill.Freeze();

        var gradient = new LinearGradientBrush(
            Color.FromArgb(0x55, Stroke.R, Stroke.G, Stroke.B),
            Color.FromArgb(0x00, Stroke.R, Stroke.G, Stroke.B), 90);
        gradient.Freeze();
        dc.DrawGeometry(gradient, null, fill);
        var pen = new Pen(new SolidColorBrush(Stroke), 2.2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
        if (double.IsFinite(values[^1]))
            dc.DrawEllipse(pen.Brush, null, P(values.Count - 1), 2.5, 2.5);
    }
}
