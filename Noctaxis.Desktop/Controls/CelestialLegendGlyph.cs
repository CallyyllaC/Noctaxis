using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// A target's line sample: a short line in its colour and pattern (solid when it is the focused target), drawn
/// with the same <see cref="CelestialPalette.Stroke"/> as its map ray and timeline path, long enough to show its
/// pattern repeat twice. A screen reader hears the pattern by name.
/// </summary>
public sealed class CelestialLegendGlyph : Control
{
    public static readonly StyledProperty<CelestialRole> RoleProperty =
        AvaloniaProperty.Register<CelestialLegendGlyph, CelestialRole>(nameof(Role));
    public static readonly StyledProperty<bool> IsPrimaryProperty =
        AvaloniaProperty.Register<CelestialLegendGlyph, bool>(nameof(IsPrimary));
    public static readonly StyledProperty<CelestialColourSet> CelestialColoursProperty =
        AvaloniaProperty.Register<CelestialLegendGlyph, CelestialColourSet>(nameof(CelestialColours), CelestialColourSet.Default);

    public CelestialRole Role { get => GetValue(RoleProperty); set => SetValue(RoleProperty, value); }
    public bool IsPrimary { get => GetValue(IsPrimaryProperty); set => SetValue(IsPrimaryProperty, value); }
    public CelestialColourSet CelestialColours { get => GetValue(CelestialColoursProperty); set => SetValue(CelestialColoursProperty, value); }

    static CelestialLegendGlyph()
    {
        AffectsRender<CelestialLegendGlyph>(RoleProperty, IsPrimaryProperty, CelestialColoursProperty);
        RoleProperty.Changed.AddClassHandler<CelestialLegendGlyph>((glyph, _) => glyph.Describe());
        IsPrimaryProperty.Changed.AddClassHandler<CelestialLegendGlyph>((glyph, _) => glyph.Describe());
    }

    /// <summary>Wide enough for two repeats of the longest pattern at its emphasised weight; the row's height.</summary>
    public const double SampleWidth = 44;

    public CelestialLegendGlyph()
    {
        Width = SampleWidth;
        Height = 16;
        Describe();
        // A theme default: a value set on the sample, or a style, wins over it (ThemeDefault).
        this.BindThemeDefault(CelestialColoursProperty, CelestialThemeTokens.ColourSet);
    }

    internal Color ColourForTesting => CelestialColours[Role];

    /// <summary>The pen the sample is drawn with: the target's own <see cref="CelestialPalette.Stroke"/>.</summary>
    internal Pen StrokeForTesting => CelestialPalette.Stroke(CelestialColours, Role, IsPrimary, IsPrimary ? 2.8 : 1.6);

    private void Describe() => Avalonia.Automation.AutomationProperties.SetName(this, CelestialPalette.PatternName(Role, IsPrimary));

    public override void Render(DrawingContext context)
    {
        var y = Bounds.Height / 2;
        context.DrawLine(StrokeForTesting, new Point(1, y), new Point(Bounds.Width - 1, y));
    }
}
