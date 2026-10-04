using System.Windows;
using System.Windows.Media;

namespace RCN1Bridge.App.Controls;

public sealed class StickView : FrameworkElement
{
    private const double Inset = 12;
    private const double DotRadius = 11;

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

        var faint = Brush("ControlStrokeColorDefaultBrush", Brushes.Gray);
        var strong = Brush("ControlStrongStrokeColorDefaultBrush", Brushes.Gray);
        var accent = Brush("AccentFillColorDefaultBrush", Brushes.DodgerBlue);

        var dashed = new Pen(faint, 1) { DashStyle = new DashStyle([2, 4], 0) };
        dc.DrawRectangle(null, dashed, new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
        dc.DrawLine(dashed, new Point(center.X, center.Y - radius), new Point(center.X, center.Y + radius));
        dc.DrawLine(dashed, new Point(center.X - radius, center.Y), new Point(center.X + radius, center.Y));
        dc.DrawEllipse(null, new Pen(strong, 1), center, radius, radius);
        if (_deadzone > 0)
            dc.DrawEllipse(null, new Pen(faint, 1), center, radius * _deadzone + 1, radius * _deadzone + 1);

        double nx = _x, ny = -_y;
        double magnitude = Math.Sqrt(nx * nx + ny * ny);
        if (magnitude > 1)
        {
            // Hollow ghost shows where the square-gated stick really is when it's outside the circle
            var ghost = new Point(center.X + Math.Clamp(nx, -1, 1) * radius, center.Y + Math.Clamp(ny, -1, 1) * radius);
            dc.DrawEllipse(null, new Pen(accent, 1.5), ghost, DotRadius - 2, DotRadius - 2);
            nx /= magnitude;
            ny /= magnitude;
        }
        dc.DrawEllipse(accent, null, new Point(center.X + nx * radius, center.Y + ny * radius), DotRadius, DotRadius);
    }

    private Brush Brush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}

public sealed class DialView : FrameworkElement
{
    private double _value;

    public void Update(double value)
    {
        if (value == _value)
            return;
        _value = value;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, 22);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, mid = ActualHeight / 2;
        if (w < 20)
            return;

        var track = TryFindResource("ControlStrokeColorDefaultBrush") as Brush ?? Brushes.Gray;
        var strong = TryFindResource("ControlStrongStrokeColorDefaultBrush") as Brush ?? Brushes.Gray;
        var accent = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DodgerBlue;

        double inset = 4, span = w - inset * 2;
        double X(double v) => inset + (v + 1) / 2 * span;

        dc.DrawRoundedRectangle(track, null, new Rect(inset, mid - 3, span, 6), 3, 3);
        dc.DrawLine(new Pen(strong, 1), new Point(X(0), mid - 6), new Point(X(0), mid + 6));
        dc.DrawRoundedRectangle(accent, null, new Rect(X(Math.Clamp(_value, -1, 1)) - 2, mid - 9, 4, 18), 2, 2);
    }
}
