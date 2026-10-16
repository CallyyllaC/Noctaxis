using System.Text.Json.Serialization;

namespace Noctaxis.Core.Domain;

/// <summary>
/// The Planner's Picture-in-Picture map: a second, smaller map view over the top-left of the main map.
/// It is a map view like the main one — its own centre, zoom and layers — so it is saved as one.
/// </summary>
/// <param name="IsEnabled">Whether the Planner shows it at all. Off, it is not created, so it costs nothing.</param>
/// <param name="FollowPin">Its centre follows the planning pin. Only the centre: its zoom and layers are its own.</param>
/// <param name="CentreLatitude">Its centre while it is not following the pin; absent until it has had one.</param>
/// <param name="CentreLongitude">See <paramref name="CentreLatitude"/>.</param>
/// <param name="ResolutionMetresPerPixel">Its zoom, as ground metres per pixel; absent for the shipped zoom.</param>
/// <param name="TerrainRelief">Its terrain relief layer.</param>
/// <param name="LightPollution">Its light pollution layer. The palette is the main map's, which is the one setting.</param>
/// <param name="ShowRoadsAndLabels">
/// Whether it draws the basemap's roads, boundaries and names. Off by default: it is a terrain-first
/// overview, and the main map has them. The main map's own basemap is never affected.
/// </param>
/// <param name="EnhanceContrast">
/// Whether its terrain relief is drawn in a stronger tone, so a small view reads its terrain clearly. On by
/// default. It changes how the relief is drawn, never what it shows; the main map is never affected.
/// </param>
public sealed record PlannerPipSettings(
    bool IsEnabled = true,
    bool FollowPin = true,
    double? CentreLatitude = null,
    double? CentreLongitude = null,
    double? ResolutionMetresPerPixel = null,
    TerrainReliefPreferences? TerrainRelief = null,
    LightPollutionPreferences? LightPollution = null,
    bool ShowRoadsAndLabels = false,
    bool EnhanceContrast = true)
{
    /// <summary>
    /// What the Picture-in-Picture map shows until it is changed: terrain relief, strongly, over the basemap
    /// and without light pollution — the old terrain minimap's view, on the normal map layers.
    /// </summary>
    public static TerrainReliefPreferences DefaultTerrainRelief { get; } = new(true, 1);
    public static LightPollutionPreferences DefaultLightPollution { get; } = new(false);

    /// <summary>The zoom limits it keeps to: the main map's own range.</summary>
    public const double MinimumResolutionMetresPerPixel = 0.1;
    public const double MaximumResolutionMetresPerPixel = 160_000;

    /// <summary>
    /// The zoom it opens at with none saved, and the one Reset Planner layout returns it to: the view tuned
    /// with the shipped layout, about 25 km across its 349 px at 1920 × 1080.
    /// </summary>
    public const double DefaultResolutionMetresPerPixel = 73.2421875;

    [JsonIgnore]
    public TerrainReliefPreferences EffectiveTerrainRelief => (TerrainRelief ?? DefaultTerrainRelief).Normalised();
    [JsonIgnore]
    public LightPollutionPreferences EffectiveLightPollution => (LightPollution ?? DefaultLightPollution).Normalised();

    /// <summary>The saved centre, when there is a whole and valid one.</summary>
    [JsonIgnore]
    public GeoCoordinate? EffectiveCentre => CentreLatitude is double latitude && CentreLongitude is double longitude &&
        double.IsFinite(latitude) && double.IsFinite(longitude) && Math.Abs(latitude) <= 90 && Math.Abs(longitude) <= 180
        ? new GeoCoordinate(latitude, longitude) : null;

    /// <summary>The saved zoom, when there is a valid one, within the limits; otherwise the shipped zoom.</summary>
    [JsonIgnore]
    public double EffectiveResolution => ResolutionMetresPerPixel is double resolution && double.IsFinite(resolution) && resolution > 0
        ? Math.Clamp(resolution, MinimumResolutionMetresPerPixel, MaximumResolutionMetresPerPixel) : DefaultResolutionMetresPerPixel;
}
