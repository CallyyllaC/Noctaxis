namespace Noctaxis.Desktop.Themes;

/// <summary>
/// Semantic map roles supplied by every theme palette. Map code refers to these names only; the
/// selected theme decides the values. Each token is published as a brush and, with a
/// <c>Color</c> suffix, as a colour.
/// <para>
/// Surface and chrome roles follow the application's Light/Dark mode, because they sit beside or
/// behind the rest of the interface. Symbol roles are drawn on map content whose appearance does
/// not follow the theme (street tiles, the grayscale relief minimap), so they are authored for
/// legibility on that content and are the same in a family's Light and Dark palettes.
/// </para>
/// <para>
/// Deliberately not covered: photographic imagery such as satellite basemaps (never recoloured),
/// celestial target colours (<see cref="CelestialThemeTokens"/>, which also drive camera framing and
/// environmental shading), terrain relief/water/no-data pixels and user-owned terrain/framing colours.
/// </para>
/// </summary>
public static class MapThemeTokens
{
    /// <summary>Map canvas behind the basemap: visible before tiles load and beyond their extent.</summary>
    public const string Surface = "MapSurface";

    /// <summary>Translucent panels, attribution and buttons floating over the map.</summary>
    public const string ChromeBackground = "MapChromeBackground";
    public const string ChromeBorder = "MapChromeBorder";
    public const string ChromeText = "MapChromeText";

    /// <summary>Observer symbol body on both the Planner map and the terrain minimap.</summary>
    public const string PinFill = "MapPinFill";
    /// <summary>Observer symbol ring; the location identity colour of the family.</summary>
    public const string PinOutline = "MapPinOutline";
    /// <summary>Loading arc around the pin, drawn over a <see cref="PinFill"/> halo.</summary>
    public const string PinActivity = "MapPinActivity";

    /// <summary>Minimap camera field-of-view wedge.</summary>
    public const string ViewFill = "MapViewFill";
    public const string ViewStroke = "MapViewStroke";
    /// <summary>Minimap selected-bearing line; must stay distinguishable from <see cref="ViewStroke"/>.</summary>
    public const string Bearing = "MapBearing";

    // Basemap style roles. The street basemap is vector data drawn by Noctaxis, so the theme styles
    // the map itself; these follow Light/Dark like the rest of the interface.
    public const string Land = "MapLand";
    public const string BuiltUp = "MapBuiltUp";
    public const string Building = "MapBuilding";
    public const string Water = "MapWater";
    public const string Vegetation = "MapVegetation";
    public const string RoadMinor = "MapRoadMinor";
    public const string RoadMajor = "MapRoadMajor";
    public const string RoadCasing = "MapRoadCasing";
    public const string Rail = "MapRail";
    public const string Boundary = "MapBoundary";
    public const string Label = "MapLabel";
    public const string LabelHalo = "MapLabelHalo";
    public const string WaterLabel = "MapWaterLabel";
    public const string Poi = "MapPoi";

    /// <summary>
    /// A number, not a colour: the theme's factor (0–1) on the Planner's terrain relief opacity.
    /// Relief is neutral light and shade and needs no colour; High Contrast weakens it so land, water
    /// and the reference layer keep their full contrast. The user's own opacity is applied on top.
    /// </summary>
    public const string TerrainReliefStrength = "MapTerrainReliefStrength";

    public static IReadOnlyList<string> Basemap { get; } =
        [Land, BuiltUp, Building, Water, Vegetation, RoadMinor, RoadMajor, RoadCasing, Rail, Boundary,
         Label, LabelHalo, WaterLabel, Poi];

    public static IReadOnlyList<string> ModeFollowing { get; } =
        [Surface, ChromeBackground, ChromeBorder, ChromeText, .. Basemap];

    public static IReadOnlyList<string> Symbols { get; } =
        [PinFill, PinOutline, PinActivity, ViewFill, ViewStroke, Bearing];

    public static IReadOnlyList<string> All { get; } = [.. ModeFollowing, .. Symbols];

    /// <summary>Resource key of the colour counterpart ThemeService publishes for a token.</summary>
    public static string ColourKey(string token) => token + "Color";
}
