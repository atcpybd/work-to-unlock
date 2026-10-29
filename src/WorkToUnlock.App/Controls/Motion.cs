using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace WorkToUnlock.App.Controls;

/// <summary>
/// Small motion effects set from XAML (hover lift, pulse, breathe, entrance, grow-in, text change) or code (pop).
/// All of them are skipped when Windows' "Animation effects" setting is off.
/// </summary>
public static class Motion
{
    private static readonly UISettings UiSettings = new();
    private static readonly ConditionalWeakTable<FrameworkElement, Storyboard> Loops = new();
    private static readonly ConditionalWeakTable<FrameworkElement, object> LoopHooks = new();
    private static long _burstStart;
    private static int _burstIndex;

    public static bool Enabled => UiSettings.AnimationsEnabled;

    // ----- HoverLift: grows a few pixels under the pointer -----

    public static readonly DependencyProperty HoverLiftProperty = DependencyProperty.RegisterAttached(
        "HoverLift", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnHoverLiftChanged));

    public static bool GetHoverLift(DependencyObject element) => (bool)element.GetValue(HoverLiftProperty);

    public static void SetHoverLift(DependencyObject element, bool value) => element.SetValue(HoverLiftProperty, value);

    private static void OnHoverLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true)
        {
            return;
        }

        element.PointerEntered += (_, _) => Lift(element, true);
        element.PointerExited += (_, _) => Lift(element, false);
        element.PointerCanceled += (_, _) => Lift(element, false);
        element.PointerCaptureLost += (_, _) => Lift(element, false);
    }

    private static void Lift(FrameworkElement element, bool up)
    {
        if (up && (!Enabled || element.ActualWidth <= 0))
        {
            return;
        }

        // About 6 px of growth whatever the size, so wide cards don't balloon. Uses the render transform like every
        // other effect here: WinUI refuses a render transform on an element that uses the Scale/ScaleTransition facade.
        var scale = up ? Math.Min(1.03, 1 + 6 / element.ActualWidth) : 1;
        var transform = TransformOf(element);
        var storyboard = new Storyboard();
        Add(storyboard, transform, "ScaleX", null, scale, TimeSpan.FromMilliseconds(180), new CubicEase { EasingMode = EasingMode.EaseOut });
        Add(storyboard, transform, "ScaleY", null, scale, TimeSpan.FromMilliseconds(180), new CubicEase { EasingMode = EasingMode.EaseOut });
        storyboard.Begin();
    }

    // ----- Pulse (a live indicator) and Breathe (a call to action): repeat while true and on screen -----

    public static readonly DependencyProperty PulseProperty = DependencyProperty.RegisterAttached(
        "Pulse", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnLoopChanged));

    public static bool GetPulse(DependencyObject element) => (bool)element.GetValue(PulseProperty);

    public static void SetPulse(DependencyObject element, bool value) => element.SetValue(PulseProperty, value);

    public static readonly DependencyProperty BreatheProperty = DependencyProperty.RegisterAttached(
        "Breathe", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnLoopChanged));

    public static bool GetBreathe(DependencyObject element) => (bool)element.GetValue(BreatheProperty);

    public static void SetBreathe(DependencyObject element, bool value) => element.SetValue(BreatheProperty, value);

    private static void OnLoopChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if (!LoopHooks.TryGetValue(element, out _))
        {
            LoopHooks.Add(element, new object());
            element.Loaded += (_, _) => UpdateLoop(element);
            element.Unloaded += (_, _) => StopLoop(element);
        }

        UpdateLoop(element);
    }

    private static void UpdateLoop(FrameworkElement element)
    {
        var pulse = GetPulse(element);
        var breathe = GetBreathe(element);
        if (!(pulse || breathe) || !element.IsLoaded || !Enabled)
        {
            StopLoop(element);
            return;
        }

        if (Loops.TryGetValue(element, out _))
        {
            return;
        }

        var transform = TransformOf(element);
        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };
        var period = TimeSpan.FromMilliseconds(pulse ? 850 : 1300);
        var scale = pulse ? 1.35 : 1.035;
        Add(storyboard, transform, "ScaleX", 1, scale, period, new SineEase { EasingMode = EasingMode.EaseInOut });
        Add(storyboard, transform, "ScaleY", 1, scale, period, new SineEase { EasingMode = EasingMode.EaseInOut });
        if (pulse)
        {
            Add(storyboard, element, "Opacity", 1, 0.45, period, new SineEase { EasingMode = EasingMode.EaseInOut });
        }

        Loops.AddOrUpdate(element, storyboard);
        storyboard.Begin();
    }

    private static void StopLoop(FrameworkElement element)
    {
        if (Loops.TryGetValue(element, out var storyboard))
        {
            storyboard.Stop();
            Loops.Remove(element);
        }
    }

    // ----- Entrance: fades and rises in, staggered with siblings that load at the same moment -----

    public static readonly DependencyProperty EntranceProperty = DependencyProperty.RegisterAttached(
        "Entrance", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEntranceChanged));

    public static bool GetEntrance(DependencyObject element) => (bool)element.GetValue(EntranceProperty);

    public static void SetEntrance(DependencyObject element, bool value) => element.SetValue(EntranceProperty, value);

    private static void OnEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && e.NewValue is true)
        {
            element.Loaded += (_, _) => Enter(element);
        }
    }

    private static void Enter(FrameworkElement element)
    {
        if (!Enabled)
        {
            return;
        }

        // Fade to the element's own opacity (dimmed items stay dimmed).
        var opacity = element.Opacity;
        var delay = NextStagger();
        var transform = TransformOf(element);
        element.Opacity = 0;
        transform.TranslateY = 14;
        var storyboard = new Storyboard();
        Add(storyboard, element, "Opacity", 0, opacity, TimeSpan.FromMilliseconds(260), null, delay);
        Add(storyboard, transform, "TranslateY", 14, 0, TimeSpan.FromMilliseconds(380), new CubicEase { EasingMode = EasingMode.EaseOut }, delay);
        storyboard.Completed += (_, _) => element.Opacity = opacity;
        storyboard.Begin();
    }

    // ----- GrowIn: grows up from the bottom edge, staggered (chart bars) -----

    public static readonly DependencyProperty GrowInProperty = DependencyProperty.RegisterAttached(
        "GrowIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnGrowInChanged));

    public static bool GetGrowIn(DependencyObject element) => (bool)element.GetValue(GrowInProperty);

    public static void SetGrowIn(DependencyObject element, bool value) => element.SetValue(GrowInProperty, value);

    private static void OnGrowInChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement element && e.NewValue is true)
        {
            element.Loaded += (_, _) => GrowIn(element);
        }
    }

    private static void GrowIn(FrameworkElement element)
    {
        if (!Enabled)
        {
            return;
        }

        var transform = TransformOf(element, new Point(0.5, 1));
        transform.ScaleY = 0;
        var storyboard = new Storyboard();
        Add(storyboard, transform, "ScaleY", 0, 1, TimeSpan.FromMilliseconds(520), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }, NextStagger());
        storyboard.Begin();
    }

    // ----- AnimateTextChanges: new text slides up into place (headlines) -----

    public static readonly DependencyProperty AnimateTextChangesProperty = DependencyProperty.RegisterAttached(
        "AnimateTextChanges", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnAnimateTextChangesChanged));

    public static bool GetAnimateTextChanges(DependencyObject element) => (bool)element.GetValue(AnimateTextChangesProperty);

    public static void SetAnimateTextChanges(DependencyObject element, bool value) => element.SetValue(AnimateTextChangesProperty, value);

    private static void OnAnimateTextChangesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock text && e.NewValue is true)
        {
            text.RegisterPropertyChangedCallback(TextBlock.TextProperty, (sender, _) =>
            {
                if (sender is FrameworkElement { IsLoaded: true } element && Enabled)
                {
                    var transform = TransformOf(element);
                    var storyboard = new Storyboard();
                    Add(storyboard, element, "Opacity", 0, 1, TimeSpan.FromMilliseconds(260), null);
                    Add(storyboard, transform, "TranslateY", 10, 0, TimeSpan.FromMilliseconds(340), new CubicEase { EasingMode = EasingMode.EaseOut });
                    storyboard.Begin();
                }
            });
        }
    }

    // ----- Pop: a quick bounce to mark a moment (reward ready, break started) -----

    public static void Pop(FrameworkElement element, double scale = 1.07)
    {
        if (!Enabled)
        {
            return;
        }

        var transform = TransformOf(element);
        var storyboard = new Storyboard();
        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var frames = new DoubleAnimationUsingKeyFrames();
            frames.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 1 });
            frames.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(160), Value = scale, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            frames.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(520), Value = 1, EasingFunction = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 } });
            Storyboard.SetTarget(frames, transform);
            Storyboard.SetTargetProperty(frames, property);
            storyboard.Children.Add(frames);
        }

        storyboard.Begin();
    }

    // ----- helpers -----

    /// <summary>Elements loading within a short burst (a page or list appearing) enter one after another.</summary>
    private static TimeSpan NextStagger()
    {
        var now = Environment.TickCount64;
        if (now - _burstStart > 400)
        {
            _burstStart = now;
            _burstIndex = 0;
        }

        return TimeSpan.FromMilliseconds(Math.Min(_burstIndex++ * 35, 420));
    }

    private static CompositeTransform TransformOf(FrameworkElement element, Point? origin = null)
    {
        if (element.RenderTransform is not CompositeTransform transform)
        {
            transform = new CompositeTransform();
            element.RenderTransform = transform;
        }

        element.RenderTransformOrigin = origin ?? new Point(0.5, 0.5);
        return transform;
    }

    /// <param name="from">Null starts from the current (possibly animated) value.</param>
    private static void Add(Storyboard storyboard, DependencyObject target, string property, double? from, double to, TimeSpan duration, EasingFunctionBase? easing, TimeSpan? delay = null)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing,
            BeginTime = delay ?? TimeSpan.Zero,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
}
