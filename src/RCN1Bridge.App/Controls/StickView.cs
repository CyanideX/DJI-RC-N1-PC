using System.Windows;
using System.Windows.Media;
using RCN1Bridge.Core.Input;

namespace RCN1Bridge.App.Controls;

// Redrawn up to 60 times a second, so pens are rebuilt only when the theme hands over new brushes
internal sealed class ThemePens
{
    private Brush? _faint, _strong, _accent;

    public Brush Faint { get; private set; } = Brushes.Gray;
    public Brush Strong { get; private set; } = Brushes.Gray;
    public Brush Accent { get; private set; } = Brushes.DodgerBlue;
    public Pen FaintLine { get; private set; } = null!;
    public Pen FaintDashed { get; private set; } = null!;
    public Pen StrongLine { get; private set; } = null!;
    public Pen StrongDotted { get; private set; } = null!;
    public Pen AccentLine { get; private set; } = null!;
    public Pen AccentThick { get; private set; } = null!;

    public void Refresh(FrameworkElement owner)
    {
        var faint = owner.TryFindResource("ControlStrokeColorDefaultBrush") as Brush ?? Brushes.Gray;
        var strong = owner.TryFindResource("ControlStrongStrokeColorDefaultBrush") as Brush ?? Brushes.Gray;
        var accent = owner.TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DodgerBlue;
        if (faint == _faint && strong == _strong && accent == _accent)
            return;
        (_faint, _strong, _accent) = (faint, strong, accent);
        (Faint, Strong, Accent) = (faint, strong, accent);
        FaintLine = Frozen(new Pen(faint, 1));
        FaintDashed = Frozen(new Pen(faint, 1) { DashStyle = new DashStyle([2, 4], 0) });
        StrongLine = Frozen(new Pen(strong, 1));
        StrongDotted = Frozen(new Pen(strong, 1) { DashStyle = DashStyles.Dot });
        AccentLine = Frozen(new Pen(accent, 1.5));
        AccentThick = Frozen(new Pen(accent, 2.5) { LineJoin = PenLineJoin.Round });
    }

    private static Pen Frozen(Pen pen)
    {
        if (pen.CanFreeze)
            pen.Freeze();
        return pen;
    }
}

public sealed class StickView : FrameworkElement
{
    private const double Inset = 12;
    private const double DotRadius = 11;

    private readonly ThemePens _pens = new();
    private double _x;
    private double _y;
    private double _deadzone = 0.03;

    public void Update(double x, double y, double deadzone)
    {
        if (x == _x && y == _y && deadzone == _deadzone)
            return;
        (_x, _y, _deadzone) = (x, y, deadzone);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double size = Math.Min(220, Math.Min(availableSize.Width, availableSize.Height));
        return double.IsInfinity(size) ? new Size(220, 220) : new Size(size, size);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Inset * 2)
            return;

        var origin = new Point((ActualWidth - size) / 2, (ActualHeight - size) / 2);
        var center = new Point(origin.X + size / 2, origin.Y + size / 2);
        double radius = size / 2 - Inset;

        _pens.Refresh(this);
        dc.DrawRectangle(null, _pens.FaintDashed, new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
        dc.DrawLine(_pens.FaintDashed, new Point(center.X, center.Y - radius), new Point(center.X, center.Y + radius));
        dc.DrawLine(_pens.FaintDashed, new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
        dc.DrawEllipse(null, _pens.StrongLine, center, radius, radius);
        if (_deadzone > 0)
            dc.DrawEllipse(null, _pens.FaintLine, center, radius * _deadzone + 1, radius * _deadzone + 1);

        double nx = _x, ny = -_y;
        double magnitude = Math.Sqrt(nx * nx + ny * ny);
        if (magnitude > 1)
        {
            // Hollow ghost shows where the square-gated stick really is when it's outside the circle
            var ghost = new Point(center.X + Math.Clamp(nx, -1, 1) * radius, center.Y + Math.Clamp(ny, -1, 1) * radius);
            dc.DrawEllipse(null, _pens.AccentLine, ghost, DotRadius - 2, DotRadius - 2);
            nx /= magnitude;
            ny /= magnitude;
        }
        dc.DrawEllipse(_pens.Accent, null, new Point(center.X + nx * radius, center.Y + ny * radius), DotRadius, DotRadius);
    }
}

public sealed class DialView : FrameworkElement
{
    private readonly ThemePens _pens = new();
    private double _value;
    private double _threshold;

    // threshold > 0 draws the marks where bound buttons press
    public void Update(double value, double threshold = 0)
    {
        if (value == _value && threshold == _threshold)
            return;
        (_value, _threshold) = (value, threshold);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, 22);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, mid = ActualHeight / 2;
        if (w < 20)
            return;

        _pens.Refresh(this);
        double inset = 4, span = w - inset * 2;
        double X(double v) => inset + (v + 1) / 2 * span;

        dc.DrawRoundedRectangle(_pens.Faint, null, new Rect(inset, mid - 3, span, 6), 3, 3);
        dc.DrawLine(_pens.StrongLine, new Point(X(0), mid - 6), new Point(X(0), mid + 6));
        if (_threshold > 0)
        {
            dc.DrawLine(_pens.StrongDotted, new Point(X(-_threshold), mid - 8), new Point(X(-_threshold), mid + 8));
            dc.DrawLine(_pens.StrongDotted, new Point(X(_threshold), mid - 8), new Point(X(_threshold), mid + 8));
        }
        dc.DrawRoundedRectangle(_pens.Accent, null, new Rect(X(Math.Clamp(_value, -1, 1)) - 2, mid - 9, 4, 18), 2, 2);
    }
}

// Input magnitude against output for one stick's shaping, with the live position on top
public sealed class CurveView : FrameworkElement
{
    private const int Steps = 64;
    private readonly ThemePens _pens = new();
    private StickShaping _shaping = StickShaping.Default;
    private double _input = -1, _output;
    private StreamGeometry? _curve;
    private Size _curveSize;

    public void SetShaping(StickShaping shaping)
    {
        if (shaping == _shaping)
            return;
        _shaping = shaping;
        _curve = null;
        InvalidateVisual();
    }

    // Negative input hides the dot
    public void SetPosition(double input, double output)
    {
        if (input == _input && output == _output)
            return;
        (_input, _output) = (input, output);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 260 : availableSize.Width, 180);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 40)
            return;

        _pens.Refresh(this);
        const double pad = 6;
        double side = Math.Min(w, h) - pad * 2;
        double left = (w - side) / 2, top = pad;
        Point P(double x, double y) => new(left + x * side, top + (1 - y) * side);

        dc.DrawRectangle(null, _pens.FaintLine, new Rect(P(0, 1), P(1, 0)));
        dc.DrawLine(_pens.FaintDashed, P(0.5, 0), P(0.5, 1));
        dc.DrawLine(_pens.FaintDashed, P(0, 0.5), P(1, 0.5));
        dc.DrawLine(_pens.StrongDotted, P(0, 0), P(1, 1));

        if (_curve is null || _curveSize != RenderSize)
        {
            _curveSize = RenderSize;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(P(0, 0), false, false);
                for (int i = 1; i <= Steps; i++)
                {
                    double x = (double)i / Steps;
                    ctx.LineTo(P(x, Output(x)), true, true);
                }
            }
            geometry.Freeze();
            _curve = geometry;
        }
        dc.DrawGeometry(null, _pens.AccentThick, _curve);

        if (_input >= 0)
        {
            var dot = P(Math.Clamp(_input, 0, 1), Math.Clamp(_output, 0, 1));
            dc.DrawEllipse(_pens.Accent, null, dot, 5, 5);
        }
    }

    private double Output(double input)
    {
        float dz = Math.Clamp(_shaping.Deadzone, 0f, 0.95f);
        float v = (float)input;
        float scaled = v <= dz ? 0f : (v - dz) / (1f - dz);
        return _shaping.Curve(scaled);
    }
}
