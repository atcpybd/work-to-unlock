using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace WorkToUnlock.App.Controls;

/// <summary>A thick circular progress ring with rounded caps (0..1) and a subtle track.</summary>
public sealed class CircularProgress : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(CircularProgress), new PropertyMetadata(0d, (d, _) => ((CircularProgress)d).UpdateArc()));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(CircularProgress), new PropertyMetadata(14d, (d, _) => ((CircularProgress)d).UpdateArc()));

    public static readonly DependencyProperty ProgressBrushProperty = DependencyProperty.Register(
        nameof(ProgressBrush), typeof(Brush), typeof(CircularProgress), new PropertyMetadata(null, (d, e) => ((CircularProgress)d)._arc.Stroke = (Brush)e.NewValue));

    private readonly Ellipse _track = new();
    private readonly Path _arc = new() { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };

    public CircularProgress()
    {
        Children.Add(_track);
        Children.Add(_arc);
        _track.Stroke = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        _arc.Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        SizeChanged += (_, _) => UpdateArc();
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

        var value = double.IsNaN(Value) ? 0 : Math.Clamp(Value, 0, 1);
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
