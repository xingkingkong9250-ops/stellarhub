using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls;

namespace QL.World.Views.Controls;

/// <summary>
/// M5-F: Progressive three-layer image with cross-fade.
/// <list type="bullet">
///   <item><b>PlaceholderSource</b> — thumb (256px), shown immediately.</item>
///   <item><b>PreviewSource</b> — preview (1024px), fades in over 200ms;
///     the placeholder layer is collapsed only after the fade completes
///     (never collapse-then-fade — that flickers).</item>
///   <item><b>FinalSource</b> — original resolution, same cross-fade over
///     the preview layer. M5-F wires the first two layers; M5-H adds the
///     original on demand for weak-network / 100% zoom.</item>
/// </list>
/// </summary>
public partial class ProgressiveImage : UserControl
{
    private const double FADE_DURATION_MS = 200;

    public static readonly DependencyProperty PlaceholderSourceProperty =
        DependencyProperty.Register(
            nameof(PlaceholderSource), typeof(ImageSource),
            typeof(ProgressiveImage),
            new PropertyMetadata(null, OnPlaceholderSourceChanged));

    public static readonly DependencyProperty PreviewSourceProperty =
        DependencyProperty.Register(
            nameof(PreviewSource), typeof(ImageSource),
            typeof(ProgressiveImage),
            new PropertyMetadata(null, OnPreviewSourceChanged));

    public static readonly DependencyProperty FinalSourceProperty =
        DependencyProperty.Register(
            nameof(FinalSource), typeof(ImageSource),
            typeof(ProgressiveImage),
            new PropertyMetadata(null, OnFinalSourceChanged));

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(
            nameof(Stretch), typeof(Stretch),
            typeof(ProgressiveImage),
            new PropertyMetadata(Stretch.UniformToFill));

    public ProgressiveImage()
    {
        InitializeComponent();
    }

    /// <summary>Bottom layer — thumb (256px). Shown immediately when set.</summary>
    public ImageSource? PlaceholderSource
    {
        get => (ImageSource?)GetValue(PlaceholderSourceProperty);
        set => SetValue(PlaceholderSourceProperty, value);
    }

    /// <summary>Middle layer — preview (1024px). Fades in over the placeholder.</summary>
    public ImageSource? PreviewSource
    {
        get => (ImageSource?)GetValue(PreviewSourceProperty);
        set => SetValue(PreviewSourceProperty, value);
    }

    /// <summary>Top layer — original resolution. Fades in over the preview.</summary>
    public ImageSource? FinalSource
    {
        get => (ImageSource?)GetValue(FinalSourceProperty);
        set => SetValue(FinalSourceProperty, value);
    }

    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    private static void OnPlaceholderSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ProgressiveImage)d;
        self.PlaceholderLayer.Source = e.NewValue as ImageSource;
        // If the placeholder arrives after the preview already faded in,
        // don't flash it back — keep the higher layer visible.
        if (e.NewValue is not null && self.PreviewLayer.Opacity < 1.0)
            self.PlaceholderLayer.Opacity = 1.0;
    }

    private static void OnPreviewSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ProgressiveImage)d;
        if (e.NewValue is null)
        {
            self.PreviewLayer.Source = null;
            return;
        }
        self.PreviewLayer.Source = e.NewValue as ImageSource;
        // Fade in the preview layer; collapse the placeholder only after
        // the fade completes to avoid flicker.
        self.PreviewLayer.Opacity = 0;
        self.PreviewLayer.Visibility = Visibility.Visible;
        self.FadeIn(self.PreviewLayer, () =>
        {
            // Anti-flicker: collapse placeholder only after preview is fully visible.
            self.PlaceholderLayer.Opacity = 0;
        });
    }

    private static void OnFinalSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (ProgressiveImage)d;
        if (e.NewValue is null)
        {
            self.FinalLayer.Source = null;
            return;
        }
        self.FinalLayer.Source = e.NewValue as ImageSource;
        self.FinalLayer.Opacity = 0;
        self.FinalLayer.Visibility = Visibility.Visible;
        self.FadeIn(self.FinalLayer, () =>
        {
            // Collapse the preview layer after the final fade completes.
            self.PreviewLayer.Opacity = 0;
        });
    }

    /// <summary>
    /// Animates <paramref name="target"/>.Opacity from its current value to
    /// 1.0 over <see cref="FADE_DURATION_MS"/> ms, then invokes
    /// <paramref name="onCompleted"/> on the UI thread.
    /// </summary>
    private void FadeIn(FrameworkElement target, Action? onCompleted)
    {
        var animation = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(FADE_DURATION_MS),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        if (onCompleted is not null)
            animation.Completed += (_, _) => onCompleted();
        target.BeginAnimation(OpacityProperty, animation);
    }
}
