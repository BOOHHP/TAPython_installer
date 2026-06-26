using System.Windows.Controls;
using System.Windows.Media;

namespace TAPythonInstaller;

public partial class AnimatedBackground : UserControl
{
    public AnimatedBackground()
    {
        InitializeComponent();
    }

    public void ApplyMotionPreferences()
    {
        flowBandOneTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        flowBandOneTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        flowBandTwoTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        flowBandTwoTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        gridFlowTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        gridFlowTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        scanLineTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        scanColumnTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        dataStreamOpacity.BeginAnimation(OpacityProperty, null);

        flowBandOneTranslate.X = 0;
        flowBandOneTranslate.Y = 0;
        flowBandTwoTranslate.X = 0;
        flowBandTwoTranslate.Y = 0;
        gridFlowTranslate.X = 0;
        gridFlowTranslate.Y = 0;
        scanLineTranslate.Y = 0;
        scanColumnTranslate.X = 0;
        dataStreamOpacity.Opacity = 0.16;
    }
}