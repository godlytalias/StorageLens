using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Foundation;

namespace StorageLens.Controls;

public sealed partial class AcrylicCardGrid : Grid
{
    private readonly SystemBackdropElement _backdrop;
    private readonly Border _surface;
    private readonly Grid _shadowHost;
    private SpriteVisual? _shadowVisual;
    private DropShadow? _shadow;
    private CompositionColorBrush? _shadowFill;

    public AcrylicCardGrid()
    {
        _shadowHost = new Grid { IsHitTestVisible = false };
        _backdrop = new SystemBackdropElement
        {
            IsHitTestVisible = false,
            CornerRadius = CornerRadius,
            Opacity = 0.6,
        };
        _surface = new Border
        {
            IsHitTestVisible = false,
            CornerRadius = CornerRadius,
            BorderBrush = BorderBrush,
            BorderThickness = BorderThickness,
            BackgroundSizing = BackgroundSizing.InnerBorderEdge,
        };
        Children.Add(_shadowHost);
        Children.Add(_backdrop);
        Children.Add(_surface);
        RegisterPropertyChangedCallback(
            CornerRadiusProperty,
            (_, _) =>
            {
                _backdrop.CornerRadius = CornerRadius;
                _surface.CornerRadius = CornerRadius;
            });
        RegisterPropertyChangedCallback(
            BorderBrushProperty,
            (_, _) => _surface.BorderBrush = BorderBrush);
        RegisterPropertyChangedCallback(
            BorderThicknessProperty,
            (_, _) =>
            {
                _surface.BorderThickness = BorderThickness;
                UpdateShadow();
            });
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => UpdateShadow();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (GetSystemMetrics(0x1000) != 0)
        {
            _backdrop.Visibility = Visibility.Collapsed;
            _backdrop.SystemBackdrop = null;
        }
        else
        {
            _backdrop.Visibility = Visibility.Visible;
            _backdrop.SystemBackdrop ??= new DesktopAcrylicBackdrop();
        }
        if (_shadowVisual is not null)
        {
            return;
        }

        try
        {
            // Avoid the desktop receiver-vector marshalling used by ThemeShadow.
            var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _shadow = compositor.CreateDropShadow();
            _shadow.BlurRadius = 16;
            _shadow.Offset = new Vector3(0, 4, 0);
            _shadow.SourcePolicy = CompositionDropShadowSourcePolicy.InheritFromVisualContent;
            _shadowVisual = compositor.CreateSpriteVisual();
            _shadowFill = compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
            _shadowVisual.Brush = _shadowFill;
            _shadowVisual.Shadow = _shadow;
            _shadowVisual.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
            ElementCompositionPreview.SetElementChildVisual(_shadowHost, _shadowVisual);
            UpdateShadow();
        }
        catch (COMException exception)
        {
            ReleaseShadow();
            System.Diagnostics.Trace.TraceWarning(
                $"Card shadow unavailable: {exception.Message}");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _backdrop.SystemBackdrop = null;
        ReleaseShadow();
    }

    private void ReleaseShadow()
    {
        ElementCompositionPreview.SetElementChildVisual(_shadowHost, null);
        _shadowVisual?.Dispose();
        _shadow?.Dispose();
        _shadowFill?.Dispose();
        _shadowVisual = null;
        _shadow = null;
        _shadowFill = null;
    }

    private void UpdateShadow()
    {
        var highContrast = IsHighContrast();
        _backdrop.Opacity = highContrast ? 1 : 0.6;
        if (_shadow is null)
        {
            return;
        }

        _shadow.Color = ((SolidColorBrush)Application.Current.Resources["CardShadowBrush"]).Color;
        _shadow.Opacity = highContrast
            ? 0
            : ActualTheme == ElementTheme.Light ? 0.26f : 0.45f;
        if (_shadowFill is not null)
        {
            _shadowFill.Color = Background switch
            {
                AcrylicBrush acrylic => acrylic.TintColor,
                SolidColorBrush solid => solid.Color,
                _ => Microsoft.UI.Colors.Transparent,
            };
        }
    }

    private static bool IsHighContrast()
    {
        var settings = new HighContrastSettings
        {
            Size = (uint)Marshal.SizeOf<HighContrastSettings>(),
        };
        return SystemParametersInfo(
                   0x0042,
                   settings.Size,
                   ref settings,
                   0) &&
               (settings.Flags & 1) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastSettings
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref HighContrastSettings settings,
        uint flags);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arrangedSize = base.ArrangeOverride(finalSize);
        _shadowHost.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        _backdrop.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        _surface.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        if (_shadowVisual is not null)
        {
            _shadowVisual.Size = new Vector2(
                (float)finalSize.Width,
                (float)finalSize.Height);
        }
        return arrangedSize;
    }

}
