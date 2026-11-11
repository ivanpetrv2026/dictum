using System.Windows;
using System.Windows.Media.Animation;

namespace Dictum.Views;

/// <summary>
/// Small always-on-top, click-through dot that pulses while the microphone is
/// recording. It never takes focus, so the text keeps going to the window the
/// user was typing in.
/// </summary>
public partial class RecordingIndicator : Window
{
    public RecordingIndicator()
    {
        InitializeComponent();
        Loaded += (_, _) => StartPulse();
    }

    public void PlaceNearBottom()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = SystemParameters.PrimaryScreenHeight - Height - 140;
    }

    private void StartPulse()
    {
        var haloScale = new DoubleAnimation(1.0, 1.7, TimeSpan.FromSeconds(1.15))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        var haloOpacity = new DoubleAnimation(0.55, 0.15, TimeSpan.FromSeconds(1.15))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        var dotScale = new DoubleAnimation(1.0, 1.12, TimeSpan.FromSeconds(0.6))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        HaloScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, haloScale);
        HaloScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, haloScale);
        Halo.BeginAnimation(OpacityProperty, haloOpacity);
        DotScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, dotScale);
        DotScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, dotScale);
    }
}
