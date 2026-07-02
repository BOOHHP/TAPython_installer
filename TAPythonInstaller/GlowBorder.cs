using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TAPythonInstaller;

/// <summary>
/// 附加行为：鼠标悬停在元素上时，沿其玻璃边缘叠加一条顺时针旋转的高光。
/// 通过 <c>local:GlowBorder.IsActive="True"</c> 在样式或元素上启用。
/// </summary>
public static class GlowBorder
{
    /// <summary>全局开关：关闭后悬停不再生成流光边框（用于“降低动态”设置）。</summary>
    public static bool MotionEnabled = true;

    private static readonly List<GlowBorderAdorner> ActiveAdorners = new();
    private static Color _accentColor = Color.FromArgb(0xFF, 0x6E, 0xE9, 0xFF);

    /// <summary>流光边框的强调色（跟随设置中的强调色）；赋值时实时刷新正在显示的流光。</summary>
    public static Color AccentColor
    {
        get => _accentColor;
        set
        {
            _accentColor = value;
            for (var i = ActiveAdorners.Count - 1; i >= 0; i--)
                ActiveAdorners[i].UpdateAccent(value);
        }
    }

    internal static void RegisterActive(GlowBorderAdorner adorner) => ActiveAdorners.Add(adorner);

    internal static void UnregisterActive(GlowBorderAdorner adorner) => ActiveAdorners.Remove(adorner);

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.RegisterAttached(
            "IsActive", typeof(bool), typeof(GlowBorder),
            new PropertyMetadata(false, OnIsActiveChanged));

    public static void SetIsActive(DependencyObject obj, bool value) => obj.SetValue(IsActiveProperty, value);

    public static bool GetIsActive(DependencyObject obj) => (bool)obj.GetValue(IsActiveProperty);

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        element.MouseEnter -= OnMouseEnter;
        element.MouseLeave -= OnMouseLeave;

        if (e.NewValue is true)
        {
            element.MouseEnter += OnMouseEnter;
            element.MouseLeave += OnMouseLeave;
        }
    }

    private static void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (!MotionEnabled) return;
        if (sender is not UIElement element) return;
        var layer = AdornerLayer.GetAdornerLayer(element);
        if (layer is null) return;

        var current = layer.GetAdorners(element);
        if (current is not null)
        {
            foreach (var adorner in current)
                if (adorner is GlowBorderAdorner) return;
        }

        layer.Add(new GlowBorderAdorner(element));
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not UIElement element) return;
        var layer = AdornerLayer.GetAdornerLayer(element);
        var current = layer?.GetAdorners(element);
        if (current is null) return;

        foreach (var adorner in current)
        {
            if (adorner is GlowBorderAdorner glow)
            {
                glow.StopAnimation();
                layer!.Remove(glow);
            }
        }
    }
}

internal sealed class GlowBorderAdorner : Adorner
{
    private readonly RotateTransform _rotate = new() { CenterX = 0.5, CenterY = 0.5 };
    private readonly Pen _pen;
    private readonly LinearGradientBrush _brush;
    private readonly CornerRadius _corner;

    public GlowBorderAdorner(UIElement adornedElement) : base(adornedElement)
    {
        IsHitTestVisible = false;
        _corner = (adornedElement as Border)?.CornerRadius ?? new CornerRadius(12d);

        var accent = GlowBorder.AccentColor;
        var accentClear = Color.FromArgb(0x00, accent.R, accent.G, accent.B);
        var accentBright = Color.FromArgb(0xE6, accent.R, accent.G, accent.B);
        _brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            RelativeTransform = _rotate,
            GradientStops =
            {
                new GradientStop(accentClear, 0.0),
                new GradientStop(accentClear, 0.34),
                new GradientStop(accentBright, 0.5),
                new GradientStop(accentClear, 0.66),
                new GradientStop(accentClear, 1.0),
            }
        };

        _pen = new Pen(_brush, 2.0);

        var animation = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(2.6)))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        _rotate.BeginAnimation(RotateTransform.AngleProperty, animation);
        GlowBorder.RegisterActive(this);
    }

    public void UpdateAccent(Color accent)
    {
        var clear = Color.FromArgb(0x00, accent.R, accent.G, accent.B);
        var bright = Color.FromArgb(0xE6, accent.R, accent.G, accent.B);
        _brush.GradientStops[0].Color = clear;
        _brush.GradientStops[1].Color = clear;
        _brush.GradientStops[2].Color = bright;
        _brush.GradientStops[3].Color = clear;
        _brush.GradientStops[4].Color = clear;
    }

    public void StopAnimation()
    {
        _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        GlowBorder.UnregisterActive(this);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = AdornedElement.RenderSize;
        if (size.Width <= 2 || size.Height <= 2) return;

        var half = _pen.Thickness / 2;
        var rect = new Rect(half, half, size.Width - _pen.Thickness, size.Height - _pen.Thickness);
        var inset = new CornerRadius(
            Math.Max(0, _corner.TopLeft - half),
            Math.Max(0, _corner.TopRight - half),
            Math.Max(0, _corner.BottomRight - half),
            Math.Max(0, _corner.BottomLeft - half));
        drawingContext.DrawGeometry(null, _pen, BuildRoundedRectGeometry(rect, inset));
    }

    private static Geometry BuildRoundedRectGeometry(Rect rect, CornerRadius radius)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(rect.Left + radius.TopLeft, rect.Top), false, true);
            ctx.LineTo(new Point(rect.Right - radius.TopRight, rect.Top), true, false);
            if (radius.TopRight > 0)
                ctx.ArcTo(new Point(rect.Right, rect.Top + radius.TopRight), new Size(radius.TopRight, radius.TopRight), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(rect.Right, rect.Bottom - radius.BottomRight), true, false);
            if (radius.BottomRight > 0)
                ctx.ArcTo(new Point(rect.Right - radius.BottomRight, rect.Bottom), new Size(radius.BottomRight, radius.BottomRight), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(rect.Left + radius.BottomLeft, rect.Bottom), true, false);
            if (radius.BottomLeft > 0)
                ctx.ArcTo(new Point(rect.Left, rect.Bottom - radius.BottomLeft), new Size(radius.BottomLeft, radius.BottomLeft), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(rect.Left, rect.Top + radius.TopLeft), true, false);
            if (radius.TopLeft > 0)
                ctx.ArcTo(new Point(rect.Left + radius.TopLeft, rect.Top), new Size(radius.TopLeft, radius.TopLeft), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
