using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace WorkToUnlock.App.Controls;

/// <summary>A short burst of confetti over the page for moments worth celebrating. Never takes input.</summary>
public sealed class Confetti : Canvas
{
    private const int Pieces = 56;

    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 76, 194, 255),
        Color.FromArgb(255, 255, 200, 61),
        Color.FromArgb(255, 108, 203, 95),
        Color.FromArgb(255, 255, 138, 101),
        Color.FromArgb(255, 179, 136, 255),
        Color.FromArgb(255, 255, 92, 138),
    ];

    public Confetti()
    {
        IsHitTestVisible = false;
        Background = new SolidColorBrush(Colors.Transparent);
    }

    /// <param name="originY">Vertical start as a fraction of the height.</param>
    public void Burst(double originY = 0.3)
    {
        if (!Motion.Enabled || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var random = Random.Shared;
        var origin = new Point(ActualWidth / 2, ActualHeight * originY);
        var storyboard = new Storyboard();
        var added = new List<UIElement>(Pieces);
        for (var i = 0; i < Pieces; i++)
        {
            var piece = new Rectangle
            {
                Width = random.Next(6, 11),
                Height = random.Next(10, 17),
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Palette[i % Palette.Length]),
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var transform = new CompositeTransform { TranslateX = origin.X, TranslateY = origin.Y, Rotation = random.Next(360) };
            piece.RenderTransform = transform;
            Children.Add(piece);
            added.Add(piece);

            // Thrown upwards in a fan, then falls with gravity while spinning and fading out.
            var angle = (-160 + random.NextDouble() * 140) * Math.PI / 180;
            var speed = 220 + random.NextDouble() * 300;
            var peakX = origin.X + Math.Cos(angle) * speed;
            var peakY = origin.Y + Math.Sin(angle) * speed;
            var duration = TimeSpan.FromMilliseconds(1300 + random.Next(500));

            Animate(storyboard, transform, "TranslateX", (TimeSpan.Zero, origin.X, null), (duration, peakX + Math.Cos(angle) * speed * 0.35, new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            Animate(storyboard, transform, "TranslateY",
                (TimeSpan.Zero, origin.Y, null),
                (duration * 0.35, peakY, new QuadraticEase { EasingMode = EasingMode.EaseOut }),
                (duration, peakY + 260 + random.Next(160), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            Animate(storyboard, transform, "Rotation", (TimeSpan.Zero, transform.Rotation, null), (duration, transform.Rotation + random.Next(-720, 720), null));
            Animate(storyboard, piece, "Opacity", (TimeSpan.Zero, 1, null), (duration * 0.7, 1, null), (duration, 0, null));
        }

        storyboard.Completed += (_, _) =>
        {
            foreach (var piece in added)
            {
                Children.Remove(piece);
            }
        };
        storyboard.Begin();
    }

    private static void Animate(Storyboard storyboard, DependencyObject target, string property, params (TimeSpan Time, double Value, EasingFunctionBase? Easing)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        foreach (var (time, value, easing) in frames)
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = time, Value = value, EasingFunction = easing });
        }

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
}
