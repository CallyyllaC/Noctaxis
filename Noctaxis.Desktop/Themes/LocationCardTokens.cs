namespace Noctaxis.Desktop.Themes;

/// <summary>
/// Semantic roles for the Locations page cards. A family may author any of them; otherwise they are
/// derived from its own surface, border, accent and text roles (<see cref="Derive"/>), so every
/// family, Light/Dark palette and colour-vision variant has them without a generic decorative wash.
/// </summary>
public static class LocationCardTokens
{
    /// <summary>Card body; saved cards and the current-location panel.</summary>
    public const string Surface = "LocationCardSurface";
    public const string Border = "LocationCardBorder";
    /// <summary>Small identity motifs: the current-location marker and the add-card glyph.</summary>
    public const string Accent = "LocationCardAccent";
    /// <summary>Shown behind the preview while it renders.</summary>
    public const string PreviewBackground = "LocationPreviewBackground";
    /// <summary>Settlement light in the preview render: luminous on dark themes, a wash and stipple on light ones.</summary>
    public const string PreviewGlow = "LocationPreviewGlow";
    /// <summary>What the preview's edges fall away into: deepening night, open air on pale themes.</summary>
    public const string PreviewHaze = "LocationPreviewHaze";
    /// <summary>The preview's instrument marks: scan ring, ticks, brackets. The family accent; the label colour in High Contrast.</summary>
    public const string PreviewInstrument = "LocationPreviewInstrument";
    /// <summary>State badges ("Current location", "Favourite"): text and an icon, never colour alone.</summary>
    public const string BadgeBackground = "LocationBadgeBackground";
    public const string BadgeForeground = "LocationBadgeForeground";
    public const string BadgeBorder = "LocationBadgeBorder";

    public static IReadOnlyList<string> All { get; } =
        [Surface, Border, Accent, PreviewBackground, PreviewGlow, PreviewHaze, PreviewInstrument, BadgeBackground, BadgeForeground, BadgeBorder];

    /// <summary>Adds any role the palette does not author, from the palette's own roles.</summary>
    public static void Derive(IDictionary<string, string> palette, bool light, bool highContrast)
    {
        var card = palette["CardBackground"];
        var accent = palette["Accent"];
        void Default(string key, string value) => palette.TryAdd(key, value);
        Default(Surface, card);
        Default(Border, palette["SurfaceBorder"]);
        Default(Accent, accent);
        if (highContrast)
        {
            // Legibility first: plain surfaces, full-strength outlines, settlement drawn in the label colour.
            Default(PreviewBackground, card);
            Default(PreviewGlow, palette[MapThemeTokens.Label]);
            Default(PreviewHaze, palette[MapThemeTokens.Land]);
            Default(PreviewInstrument, palette[MapThemeTokens.Label]);
            Default(BadgeBackground, card);
            Default(BadgeForeground, palette["PrimaryText"]);
            Default(BadgeBorder, palette["PrimaryText"]);
            return;
        }
        var land = palette[MapThemeTokens.Land];
        Default(PreviewBackground, land);
        // The family's map point colour lights the settlements (Neon fuchsia, Foxfire copper, Deep Space gold...).
        Default(PreviewGlow, palette[MapThemeTokens.Poi]);
        // Dark: the land deepened with a trace of the accent. Light: the land tinted by the accent, never white fog.
        Default(PreviewHaze, light ? Mix(land, accent, .16) : Mix(Mix(land, accent, .18), "#000000", .35));
        Default(PreviewInstrument, accent);
        Default(BadgeBackground, Mix(card, accent, light ? .12 : .18));
        Default(BadgeForeground, palette["PrimaryText"]);
        Default(BadgeBorder, accent);
    }


    private static string Mix(string background, string foreground, double amount)
    {
        var a = Avalonia.Media.Color.Parse(background);
        var b = Avalonia.Media.Color.Parse(foreground);
        byte Channel(byte x, byte y) => (byte)Math.Round(x + (y - x) * amount);
        return $"#{Channel(a.R, b.R):X2}{Channel(a.G, b.G):X2}{Channel(a.B, b.B):X2}";
    }
}
