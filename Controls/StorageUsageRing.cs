using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace StorageLens.Controls;

public sealed partial class StorageUsageRing : Grid
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(StorageUsageRing),
            new PropertyMetadata(0d, OnVisualPropertyChanged));

    public static readonly DependencyProperty ProgressBrushProperty =
        DependencyProperty.Register(
            nameof(ProgressBrush),
            typeof(Brush),
            typeof(StorageUsageRing),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(
            nameof(TrackBrush),
            typeof(Brush),
            typeof(StorageUsageRing),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(
            nameof(StrokeThickness),
            typeof(double),
            typeof(StorageUsageRing),
            new PropertyMetadata(12d, OnVisualPropertyChanged));

    private readonly XamlPath _trackPath;
    private readonly XamlPath _progressPath;

    public StorageUsageRing()
    {
        _trackPath = new XamlPath
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _progressPath = new XamlPath
        {
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        Children.Add(_trackPath);
        Children.Add(_progressPath);
        SizeChanged += (_, _) => UpdateRing();
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush? ProgressBrush
    {
        get => (Brush?)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    private static void OnVisualPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((StorageUsageRing)dependencyObject).UpdateRing();

    private void UpdateRing()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var thickness = double.IsFinite(StrokeThickness)
            ? Math.Max(0, StrokeThickness)
            : 0;
        _trackPath.Stroke = TrackBrush;
        _trackPath.StrokeThickness = thickness;
        _progressPath.Stroke = ProgressBrush;
        _progressPath.StrokeThickness = thickness;

        if (width <= thickness ||
            height <= thickness)
        {
            _trackPath.Data = null;
            _progressPath.Data = null;
            return;
        }

        var center = new Point(width / 2, height / 2);
        var radius = Math.Max(0, (Math.Min(width, height) - thickness) / 2);
        _trackPath.Data = new EllipseGeometry
        {
            Center = center,
            RadiusX = radius,
            RadiusY = radius,
        };

        var ratio = double.IsFinite(Value)
            ? Math.Clamp(Value / 100, 0, 1)
            : 0;
        if (ratio <= 0)
        {
            _progressPath.Data = null;
            return;
        }

        if (ratio >= 1)
        {
            _progressPath.Data = new EllipseGeometry
            {
                Center = center,
                RadiusX = radius,
                RadiusY = radius,
            };
            return;
        }

        var startAngle = -90d;
        var figure = new PathFigure
        {
            StartPoint = GetPoint(center, radius, startAngle),
            IsFilled = false,
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = GetPoint(center, radius, startAngle + 360 * ratio),
            Size = new Size(radius, radius),
            RotationAngle = 0,
            IsLargeArc = ratio > 0.5,
            SweepDirection = SweepDirection.Clockwise,
        });
        _progressPath.Data = new PathGeometry
        {
            Figures = { figure },
        };
    }

    private static Point GetPoint(
        Point center,
        double radius,
        double angle)
    {
        var radians = angle * Math.PI / 180;
        return new Point(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));
    }
}
