using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace TAPythonInstaller;

public partial class AnimatedBackground : UserControl
{
    private Storyboard? _motion;
    private bool _motionEnabled = true;
    private bool _isRunning;
    private double _speedRatio = 1.0;

    public AnimatedBackground()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _motion ??= (Storyboard)FindResource("MotionStoryboard");
        if (_motionEnabled) StartMotion();
    }

    private RefractionEffect? _refraction;

    public void SetRefraction(bool enabled)
    {
        if (enabled)
        {
            _refraction ??= new RefractionEffect { Strength = 0.03 };
            blobLayer.Effect = _refraction;
            var animation = new DoubleAnimation(0, 200, new Duration(TimeSpan.FromSeconds(200)))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            _refraction.BeginAnimation(RefractionEffect.TimeProperty, animation);
        }
        else
        {
            _refraction?.BeginAnimation(RefractionEffect.TimeProperty, null);
            blobLayer.Effect = null;
        }
    }

    private void StartMotion()
    {
        if (_motion is null || _isRunning) return;
        _motion.Begin(this, true);
        _isRunning = true;
        _motion.SetSpeedRatio(this, _speedRatio);
    }

    public void SetMotionEnabled(bool enabled)
    {
        _motionEnabled = enabled;
        if (_motion is null) return;
        if (enabled)
        {
            // 幂等：已在运行则不重新 Begin，避免光球跳回初始位置重播。
            StartMotion();
        }
        else if (_isRunning)
        {
            _motion.Stop(this);
            _isRunning = false;
        }
    }

    public void SetSpeedRatio(double ratio)
    {
        _speedRatio = ratio;
        // SetSpeedRatio 保持当前播放进度，仅改变后续速度，光球从当前位置无缝衔接。
        if (_motion is not null && _isRunning) _motion.SetSpeedRatio(this, ratio);
    }

    public void SetBrightness(double opacity)
    {
        blobLayer.Opacity = opacity;
    }

    public void ApplyMotionPreferences()
    {
        if (!SystemParameters.ClientAreaAnimation) SetMotionEnabled(false);
    }
}