using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace WorkToUnlock.App.Controls;

/// <summary>
/// A thick circular progress ring with rounded caps (0..1) and a subtle track. Value changes sweep smoothly, and the
/// ring fills up from empty when it first appears.
/// </summary>
public sealed class CircularProgress : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(CircularProgress), new PropertyMetadata(0d, (d, _) => ((CircularProgress)d).OnValueChanged()));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(CircularProgress), new PropertyMetadata(14d, (d, _) => ((CircularProgress)d).UpdateArc()));

    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
        nameof(ProgressBrush), typeof(Brush), typeof(CircularProgress), new PropertyMetadata(null, (d, e) => ((CircularProgress)d)._arc.Stroke = (Brush)e.NewValue));

    private static readonly TimeSpan ChangeDuration = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan FillDuration = TimeSpan.FromMilliseconds(1100);

    private readonly Ellipse _track = new();
    private readonly Path _arc = new() { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private double _shown;
    private double _from;
    private double _to;
    private long _started;
    private TimeSpan _duration;
    private bool _animating;

    public CircularProgress()
    {
        Children.Add(_track);
        Children.Add(_arc);
        _track.Stroke = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        _arc.Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        SizeChanged += (_, _) => UpdateArc();
        Loaded += (_, _) =>
        {
            var target = _shown;
            _shown = 0;
            AnimateTo(target, FillDuration);
        };
        Unloaded += (_, _) => StopAnimating();
        IsHitTestVisible = false;
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Brush? ProgressBrush
    {
        get => (Brush?)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    private void OnValueChanged() => AnimateTo(double.IsNaN(Value) ? 0 : Math.Clamp(Value, 0, 1), ChangeDuration);

    private void AnimateTo(double target, TimeSpan duration)
    {
        if (!IsLoaded || !Motion.Enabled || Math.Abs(target - _shown) < 0.0001)
        {
            StopAnimating();
            _shown = target;
            UpdateArc();
            return;
        }

        _from = _shown;
        _to = target;
        _duration = duration;
        _started = Stopwatch.GetTimestamp();
        if (!_animating)
        {
            _animating = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private void OnRendering(object? sender, object e)
    {
        var t = Stopwatch.GetElapsedTime(_started) / _duration;
        if (t >= 1)
        {
            _shown = _to;
            StopAnimating();
        }
        else
        {
            var eased = 1 - Math.Pow(1 - t, 3);
            _shown = _from + (_to - _from) * eased;
        }

        UpdateArc();
    }

    private void StopAnimating()
    {
        if (_animating)
        {
            _animating = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private void UpdateArc()
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var thickness = Math.Min(Thickness, size / 4);
        _track.StrokeThickness = thickness;
        _arc.StrokeThickness = thickness;
        _track.Margin = new Thickness(thickness / 2);

        var value = _shown;
        var radius = (size - thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        if (value <= 0.0005)
        {
            _arc.Data = null;
            return;
        }

        if (value >= 0.9995)
        {
            _arc.Data = new EllipseGeometry { Center = center, RadiusX = radius, RadiusY = radius };
            return;
        }

        var angle = (value * 360 - 90) * Math.PI / 180;
        var figure = new PathFigure { StartPoint = new Point(center.X, center.Y - radius), IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = value > 0.5,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _arc.Data = geometry;
    }
}
