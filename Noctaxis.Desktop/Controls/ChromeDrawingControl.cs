using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Controls;

/// <summary>Cached presentation properties for custom-drawn chrome; never used to colour scientific rasters.</summary>
public abstract class ChromeDrawingControl : Control
{
    public static readonly StyledProperty<IBrush> ChromeBackgroundProperty =
        AvaloniaProperty.Register<ChromeDrawingControl, IBrush>(nameof(ChromeBackground), Brushes.Transparent);
    public static readonly StyledProperty<IBrush> ChromeForegroundProperty =
        AvaloniaProperty.Register<ChromeDrawingControl, IBrush>(nameof(ChromeForeground), Brushes.Gray);
    public static readonly StyledProperty<double> ChromeFontSizeProperty =
        AvaloniaProperty.Register<ChromeDrawingControl, double>(nameof(ChromeFontSize), 10);
    public IBrush ChromeBackground { get => GetValue(ChromeBackgroundProperty); set => SetValue(ChromeBackgroundProperty, value); }
    public IBrush ChromeForeground { get => GetValue(ChromeForegroundProperty); set => SetValue(ChromeForegroundProperty, value); }
    public double ChromeFontSize { get => GetValue(ChromeFontSizeProperty); set => SetValue(ChromeFontSizeProperty, value); }
    /// <summary>
    /// The theme's colours and size are only defaults (<see cref="ThemeDefault"/>): a value set in markup (the
    /// Camera frame's direction pad lies transparent over the picture) or by a style stays set when the theme's
    /// resources are replaced.
    /// </summary>
    protected ChromeDrawingControl()
    {
        this.BindThemeDefault(ChromeBackgroundProperty, "SurfaceBackground");
        this.BindThemeDefault(ChromeForegroundProperty, "SecondaryText");
        this.BindThemeDefault(ChromeFontSizeProperty, "TextCaption");
    }
    static ChromeDrawingControl()
    {
        AffectsRender<ChromeDrawingControl>(ChromeBackgroundProperty, ChromeForegroundProperty, ChromeFontSizeProperty);
        AffectsMeasure<ChromeDrawingControl>(ChromeFontSizeProperty);
    }
    protected FormattedText ChromeText(string text, double width = double.PositiveInfinity) => new(text,
        System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"),
        ChromeFontSize, ChromeForeground) { MaxTextWidth = Math.Max(1, width) };
}
