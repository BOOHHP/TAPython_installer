using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TAPythonInstaller;

/// <summary>
/// 卡片真透底（Liquid Glass backdrop）：把背景光球层按卡片所在窗口区域映射进卡片背景底层，
/// 上叠原有玻璃 tint，形成"磨砂透视"。通过 <c>local:GlassBackdrop.IsEnabled="True"</c> 在少量主卡启用。
/// </summary>
public static class GlassBackdrop
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(GlassBackdrop), new PropertyMetadata(false, OnChanged));

    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border || e.NewValue is not true) return;
        if (border.IsLoaded) Attach(border);
        else border.Loaded += (_, _) => Attach(border);
    }

    private static void Attach(Border border)
    {
        if (Window.GetWindow(border) is not MainWindow window) return;
        var source = window.BackdropSource;
        if (source is null) return;

        var visualBrush = new VisualBrush(source)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };

        // 分层结构：透底层（可挂折射 shader）→ tint 层 → 自适应 scrim 层 → 原内容。
        var backdropRect = new Rectangle { Fill = visualBrush, Opacity = 0.55, IsHitTestVisible = false };
        var tintRect = new Rectangle { Fill = border.Background, IsHitTestVisible = false };
        var scrimRect = new Rectangle { Fill = new SolidColorBrush(Color.FromRgb(0x0C, 0x12, 0x22)), Opacity = 0, IsHitTestVisible = false };

        var content = border.Child;
        border.Child = null;
        var pad = border.Padding;
        border.Padding = new Thickness(0);
        border.Background = Brushes.Transparent;

        var host = new Grid();
        host.Children.Add(backdropRect);
        host.Children.Add(tintRect);
        host.Children.Add(scrimRect);
        if (content is not null)
            host.Children.Add(new Border { Padding = pad, Child = content });
        border.Child = host;

        var entry = new CardEntry(border, source, visualBrush, backdropRect, tintRect, scrimRect);
        Cards.Add(entry);
        EnsureAdaptiveTimer();
        if (RefractionEnabled) entry.SetRefraction(true);

        border.LayoutUpdated += (_, _) =>
        {
            if (!border.IsVisible || source.ActualWidth <= 0 || border.ActualWidth <= 0) return;
            try
            {
                var rect = border.TransformToVisual(source).TransformBounds(new Rect(border.RenderSize));
                if (rect != entry.LastRect)
                {
                    entry.LastRect = rect;
                    visualBrush.Viewbox = rect;
                }
                var radius = Math.Max(0, border.CornerRadius.TopLeft - 1);
                if (Math.Abs(backdropRect.RadiusX - radius) > 0.1)
                {
                    backdropRect.RadiusX = backdropRect.RadiusY = radius;
                    tintRect.RadiusX = tintRect.RadiusY = radius;
                    scrimRect.RadiusX = scrimRect.RadiusY = radius;
                }
            }
            catch
            {
                // 页面切换/布局重建期间的瞬时变换失败可安全忽略，下次布局会重算。
            }
        };
    }

    // ============ ★9 adaptive tint + ★10 卡缘透镜折射 ============

    /// <summary>光球运动中时为 true；关闭后 scrim 渐退到 0。由外观状态机设置。</summary>
    internal static bool AdaptiveEnabled = true;

    private static bool _refractionEnabled;

    /// <summary>卡缘透镜折射开关（GPU shader，仅作用于透底层）。</summary>
    internal static bool RefractionEnabled
    {
        get => _refractionEnabled;
        set
        {
            if (_refractionEnabled == value) return;
            _refractionEnabled = value;
            foreach (var card in Cards) card.SetRefraction(value);
        }
    }

    private static readonly List<CardEntry> Cards = new();
    private static DispatcherTimer? _adaptiveTimer;

    private static void EnsureAdaptiveTimer()
    {
        if (_adaptiveTimer is not null) return;
        _adaptiveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _adaptiveTimer.Tick += AdaptiveTick;
        _adaptiveTimer.Start();
    }

    private static void AdaptiveTick(object? sender, EventArgs e)
    {
        foreach (var card in Cards)
        {
            double target = 0;
            if (AdaptiveEnabled && card.Border.IsVisible && card.Source is AnimatedBackground bg && card.LastRect.Width > 0)
            {
                var center = new Point(card.LastRect.X + card.LastRect.Width / 2, card.LastRect.Y + card.LastRect.Height / 2);
                double sum = 0;
                foreach (var (blobCenter, radius) in bg.GetBlobSamples())
                {
                    var d = (center - blobCenter).Length;
                    var reach = radius * 0.9;
                    if (d < reach) sum += (1 - d / reach) * 0.5;
                }
                target = Math.Min(0.28, sum);
            }

            var current = card.Scrim.Opacity;
            var next = current + (target - current) * 0.25;
            if (Math.Abs(next - current) > 0.002) card.Scrim.Opacity = next;
        }
    }

    private sealed class CardEntry
    {
        public CardEntry(Border border, FrameworkElement source, VisualBrush brush, Rectangle backdrop, Rectangle tint, Rectangle scrim)
        {
            Border = border;
            Source = source;
            Brush = brush;
            Backdrop = backdrop;
            Tint = tint;
            Scrim = scrim;
        }

        public Border Border { get; }
        public FrameworkElement Source { get; }
        public VisualBrush Brush { get; }
        public Rectangle Backdrop { get; }
        public Rectangle Tint { get; }
        public Rectangle Scrim { get; }
        public Rect LastRect = Rect.Empty;
        private RefractionEffect? _refraction;

        public void SetRefraction(bool enabled)
        {
            if (enabled)
            {
                _refraction ??= new RefractionEffect { Strength = 0.025 };
                Backdrop.Effect = _refraction;
                var animation = new System.Windows.Media.Animation.DoubleAnimation(0, 200, new Duration(TimeSpan.FromSeconds(200)))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                _refraction.BeginAnimation(RefractionEffect.TimeProperty, animation);
            }
            else
            {
                _refraction?.BeginAnimation(RefractionEffect.TimeProperty, null);
                Backdrop.Effect = null;
            }
        }
    }
}
