using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Harbor.UI;

/// <summary>
/// A miniature of the user's dock for the settings window. It uses the same magnification maths as the
/// real dock, so hovering it shows exactly what the sliders do.
/// </summary>
internal sealed class MiniDock : FrameworkElement
{
    readonly DispatcherTimer _anim = new() { Interval = TimeSpan.FromMilliseconds(16) };
    double _hover, _hoverTarget, _mouseX = -1e4;

    /// <summary>Stands in for the Start button, which the dock paints itself.</summary>
    public static readonly ImageSource StartMarker = new DrawingImage();

    public IReadOnlyList<ImageSource?> Icons { get; private set; } = Array.Empty<ImageSource?>();
    public double IconSize { get; set; } = 40;
    public double Magnification { get; set; } = 0.55;
    public double Roundness { get; set; } = 0.34;
    public bool Dark { get; set; } = true;
    public double TintOpacity { get; set; } = 0.55;

    public MiniDock()
    {
        Cursor = Cursors.Hand;
        _anim.Tick += (_, _) =>
        {
            _hover += (_hoverTarget - _hover) * 0.28;
            if (Math.Abs(_hover - _hoverTarget) < 0.002) { _hover = _hoverTarget; _anim.Stop(); }
            InvalidateVisual();
        };
    }

    public void SetIcons(IEnumerable<ImageSource?> icons)
    {
        Icons = icons.ToList();
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    double W => IconSize;
    double Gap => Math.Round(W * 0.14);
    double PadX => Math.Round(W * 0.2);
    double BarH => W + Math.Round(W * 0.15) + Math.Round(W * 0.21);
    double R => W * 2.9;

    protected override Size MeasureOverride(Size available)
    {
        double barW = 2 * PadX + Icons.Count * W + Math.Max(0, Icons.Count - 1) * Gap;
        return new Size(barW + W * 1.6, BarH + W * Magnification + 12);
    }

    double Disp(double x)
    {
        double t = Math.Min(Math.Abs(x - _mouseX) / R, 1);
        return Magnification * _hover * R * (t * 0.5 + Math.Sin(t * Math.PI) / (2 * Math.PI)) * (x < _mouseX ? -1 : 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        int n = Icons.Count;
        if (n == 0) return;
        double total = 2 * PadX + n * W + (n - 1) * Gap;
        double left = (ActualWidth - total) / 2, right = left + total;
        double barTop = ActualHeight - BarH - 4;
        double bottomY = barTop + Math.Round(W * 0.15) + W;

        double l = left + Disp(left), r = right + Disp(right);
        double radius = Math.Min(BarH / 2, BarH * Roundness);
        var barRect = new Rect(l, barTop, r - l, BarH);

        byte a = (byte)(Math.Clamp(TintOpacity, 0.05, 1) * 255);
        var tint = new LinearGradientBrush(
            Dark ? Color.FromArgb((byte)(a * 0.85), 44, 44, 50) : Color.FromArgb((byte)(a * 0.9), 255, 255, 255),
            Dark ? Color.FromArgb(a, 24, 24, 28) : Color.FromArgb(a, 236, 236, 240), 90);
        var edge = new Pen(new SolidColorBrush(Dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0)), 1);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), null, new Rect(barRect.X, barRect.Y + 3, barRect.Width, barRect.Height), radius, radius);
        dc.DrawRoundedRectangle(tint, edge, barRect, radius, radius);

        double x = left + PadX;
        for (int i = 0; i < n; i++)
        {
            double lo = x, hi = x + W;
            double cx = (lo + Disp(lo) + hi + Disp(hi)) / 2;
            double size = W + Disp(hi) - Disp(lo);
            var icon = Icons[i];
            var rect = new Rect(cx - size / 2, bottomY - size, size, size);
            if (ReferenceEquals(icon, StartMarker)) DrawStartTile(dc, rect);
            else if (icon != null)
            {
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
                double inset = size * 0.04;
                dc.DrawImage(icon, new Rect(rect.X + inset, rect.Y + inset, rect.Width - inset * 2, rect.Height - inset * 2));
            }
            else
            {
                // Icon still loading: a quiet placeholder tile.
                double inset = size * 0.1;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), null,
                    new Rect(rect.X + inset, rect.Y + inset, rect.Width - inset * 2, rect.Height - inset * 2), size * 0.22, size * 0.22);
            }
            x += W + Gap;
        }
    }

    static void DrawStartTile(DrawingContext dc, Rect r)
    {
        double inset = r.Width * 0.08;
        var tile = new Rect(r.X + inset, r.Y + inset, r.Width - inset * 2, r.Height - inset * 2);
        var g = new LinearGradientBrush(Color.FromRgb(118, 140, 255), Color.FromRgb(58, 48, 190), 45);
        dc.DrawRoundedRectangle(g, null, tile, tile.Width * 0.25, tile.Width * 0.25);
        double s = r.Width * 0.135, gap = r.Width * 0.05, cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
        var white = Brushes.White;
        for (int i = 0; i < 4; i++)
        {
            double xx = i % 2 == 0 ? cx - gap / 2 - s : cx + gap / 2;
            double yy = i < 2 ? cy - gap / 2 - s : cy + gap / 2;
            dc.DrawRoundedRectangle(white, null, new Rect(xx, yy, s, s), s * 0.28, s * 0.28);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _mouseX = e.GetPosition(this).X;
        if (_hoverTarget != 1) { _hoverTarget = 1; _anim.Start(); }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverTarget = 0;
        _anim.Start();
    }
}
