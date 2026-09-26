using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// Target-list key for one celestial object: a short line in the object's pattern (solid when it is
/// the primary target) with its marker, exactly as the chart and map draw it.
/// </summary>
public sealed class CelestialLegendGlyph : Control
{
    public static readonly StyledProperty<CelestialRole> RoleProperty =
        AvaloniaProperty.Register<CelestialLegendGlyph, CelestialRole>(nameof(Role));
    public static readonly StyledProperty<bool> IsPrimaryProperty =
        AvaloniaProperty.Register<CelestialLegendGlyph, bool>(nameof(IsPrimary));
    public static readonly StyledProperty<CelestialColourSet> CelestialColoursProperty =
        AvaloniaProperty.Register<CelestialLegendGlyph, CelestialColourSet>(nameof(CelestialColours), CelestialColourSet.Default);
    public static readonly StyledProperty<IBrush?> HaloProperty =
        AvaloniaProperty.Register<CelestialLegendGlyph, IBrush?>(nameof(Halo));

    public CelestialRole Role { get => GetValue(RoleProperty); set => SetValue(RoleProperty, value); }
    public bool IsPrimary { get => GetValue(IsPrimaryProperty); set => SetValue(IsPrimaryProperty, value); }
    public CelestialColourSet CelestialColours { get => GetValue(CelestialColoursProperty); set => SetValue(CelestialColoursProperty, value); }
    public IBrush? Halo { get => GetValue(HaloProperty); set => SetValue(HaloProperty, value); }

    static CelestialLegendGlyph() => AffectsRender<CelestialLegendGlyph>(RoleProperty, IsPrimaryProperty, CelestialColoursProperty, HaloProperty);

    public CelestialLegendGlyph()
    {
        Width = 30;
        Height = 16;
        this.Bind(CelestialColoursProperty, new DynamicResourceExtension(CelestialThemeTokens.ColourSet));
        this.Bind(HaloProperty, new DynamicResourceExtension("SurfaceBackground"));
    }

    internal Color ColourForTesting => CelestialColours[Role];

    public override void Render(DrawingContext context)
    {
        var brush = new SolidColorBrush(CelestialColours[Role]);
        var y = Bounds.Height / 2;
        context.DrawLine(new Pen(brush, IsPrimary ? 2.8 : 1.6, dashStyle: IsPrimary ? null : CelestialPalette.Dash(Role)),
            new Point(1, y), new Point(Bounds.Width - 1, y));
        CelestialPalette.DrawMarker(context, Role, new Point(Bounds.Width / 2, y), 9, brush, Halo);
    }
}
