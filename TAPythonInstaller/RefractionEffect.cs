using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TAPythonInstaller;

/// <summary>
/// 液态玻璃折射 ShaderEffect：用程序化正弦位移扭曲输入，产生波动折射感。
/// </summary>
public sealed class RefractionEffect : ShaderEffect
{
    private static readonly PixelShader Shader = new()
    {
        UriSource = new Uri("pack://application:,,,/Shaders/Refraction.ps")
    };

    public RefractionEffect()
    {
        PixelShader = Shader;
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(TimeProperty);
        UpdateShaderValue(StrengthProperty);
    }

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(RefractionEffect), 0);

    public Brush Input
    {
        get => (Brush)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public static readonly DependencyProperty TimeProperty =
        DependencyProperty.Register("Time", typeof(double), typeof(RefractionEffect),
            new UIPropertyMetadata(0.0, PixelShaderConstantCallback(0)));

    public double Time
    {
        get => (double)GetValue(TimeProperty);
        set => SetValue(TimeProperty, value);
    }

    public static readonly DependencyProperty StrengthProperty =
        DependencyProperty.Register("Strength", typeof(double), typeof(RefractionEffect),
            new UIPropertyMetadata(0.015, PixelShaderConstantCallback(1)));

    public double Strength
    {
        get => (double)GetValue(StrengthProperty);
        set => SetValue(StrengthProperty, value);
    }
}
