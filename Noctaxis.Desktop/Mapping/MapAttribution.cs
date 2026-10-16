using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Mapping;

/// <summary>
/// One credit the map owes while a provider's content is drawn. <paramref name="Label"/> is the compact
/// form shown on the map itself; <paramref name="Credit"/> is the full wording, shown with its links in
/// Map data &amp; licences. <paramref name="Id"/> is what makes two providers' credits the same credit:
/// it is credited once however many active layers owe it.
/// </summary>
/// <remarks>
/// The label always names whoever is being credited. Where the recorded wording is longer, the label
/// drops only the product name, never the credited party, and the full wording stays in the flyout.
/// </remarks>
public sealed record MapAttribution(string Id, string Label, string Credit, Uri? Url = null,
    string? LicenceName = null, Uri? LicenceUrl = null);

/// <summary>
/// The credits of the providers the Planner map draws, each defined once. Layer and basemap metadata
/// refer to these rather than repeating names or links.
/// </summary>
public static class MapAttributions
{
    public static readonly Uri OpenDatabaseLicenceUrl = new("https://opendatacommons.org/licenses/odbl/1-0/");

    public static MapAttribution OpenStreetMap { get; } = new("openstreetmap", MapProvider.Attribution, MapProvider.Attribution,
        new("https://www.openstreetmap.org/copyright"), "ODbL", OpenDatabaseLicenceUrl);

    public static MapAttribution OpenFreeMap { get; } = new("openfreemap", "OpenFreeMap", "OpenFreeMap",
        new("https://openfreemap.org"));

    /// <summary>OpenFreeMap's tiles follow the OpenMapTiles schema, which OpenFreeMap's own attribution credits.</summary>
    public static MapAttribution OpenMapTiles { get; } = new("openmaptiles", "© OpenMapTiles", "© OpenMapTiles",
        new("https://www.openmaptiles.org/"));

    /// <summary>
    /// The terrain tiles combine many elevation sources; the linked page lists each one's own required
    /// attribution, so it is the credit's link.
    /// </summary>
    public static MapAttribution TilezenTerrarium { get; } = new("tilezen-terrarium", "Terrain: Mapzen/Tilezen",
        "Terrain: Mapzen/Tilezen Terrarium", new("https://github.com/tilezen/joerd/blob/master/docs/attribution.md"));

    /// <summary>The publisher states no formal dataset licence, so none is given.</summary>
    public static MapAttribution LightPollutionAtlas { get; } = new(Core.LightPollution.LorenzAtlas.ProviderId,
        "Light pollution: David Lorenz", Core.LightPollution.LorenzAtlas.Name, new(Core.LightPollution.LorenzAtlas.InformationUrl));

    /// <summary>The credits a provider's metadata owes, in order: its own list, or its single legacy credit.</summary>
    public static IReadOnlyList<MapAttribution> Of(MapProviderMetadata provider) =>
        provider.Credits is { Count: > 0 } credits ? credits
        : string.IsNullOrWhiteSpace(provider.AttributionText) ? []
        : [new(provider.AttributionText, provider.AttributionText, provider.AttributionText,
            Uri.TryCreate(provider.AttributionUrl, UriKind.Absolute, out var url) ? url : null,
            provider.LicenceName, Uri.TryCreate(provider.LicenceUrl, UriKind.Absolute, out var licence) ? licence : null)];

    /// <summary>Credits in first-owed order, each once.</summary>
    public static IReadOnlyList<MapAttribution> Distinct(IEnumerable<MapAttribution> credits) =>
        credits.DistinctBy(credit => credit.Id, StringComparer.Ordinal).ToArray();

    /// <summary>The compact line shown on the map.</summary>
    public static string Line(IEnumerable<MapAttribution> credits) => string.Join(" · ", credits.Select(credit => credit.Label));
}
