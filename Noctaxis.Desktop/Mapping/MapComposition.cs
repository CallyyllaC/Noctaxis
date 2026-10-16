namespace Noctaxis.Desktop.Mapping;

/// <summary>
/// The semantic groups, bottom to top. Environmental layers change how the land looks; Reference
/// (the basemap's roads, boundaries and names) sits above them so they never wash it out; Planning
/// and Interaction are Noctaxis's own graphics.
/// </summary>
public enum MapLayerGroup { Basemap, Environmental, Reference, Planning, Interaction }
/// <summary>Street is vector data styled by the theme; Satellite is photographic imagery shown as-is.</summary>
public enum BasemapKind { Street, Satellite }

[Flags]
public enum MapLayerCapabilities { None = 0, Visibility = 1, Opacity = 2 }

/// <summary>
/// Who supplies a layer's content. <paramref name="Credits"/> is what the map owes while it is drawn; a provider
/// with a single plain credit may give <paramref name="AttributionText"/> instead. See <see cref="MapAttributions.Of"/>.
/// </summary>
public sealed record MapProviderMetadata(string DisplayName, string? AttributionText = null,
    string? AttributionUrl = null, string? LicenceName = null, string? LicenceUrl = null,
    IReadOnlyList<MapAttribution>? Credits = null);

/// <summary>
/// Logical content, independent of its rendering backend. Order is within its group.
/// <paramref name="ShownOverImagery"/> is false for content that photographic imagery already
/// shows (terrain relief); it is not drawn while a satellite basemap is active.
/// </summary>
public sealed record MapLayerDefinition(string Id, MapLayerGroup Group, int Order,
    MapProviderMetadata Provider, MapLayerCapabilities Capabilities = MapLayerCapabilities.None,
    MapLayerCapabilities PersistedPreferences = MapLayerCapabilities.None, bool ShownOverImagery = true);

public sealed record BasemapDefinition(BasemapKind Kind, MapProviderMetadata Provider);

/// <summary>
/// Immutable application stack. Semantic groups do not require separate render surfaces:
/// the current environmental shader and planning fill deliberately share one surface.
/// Build only when composition changes, never on navigation or attachment.
/// </summary>
public sealed class MapComposition
{
    public BasemapDefinition Basemap { get; }
    public IReadOnlyList<MapLayerDefinition> Layers { get; }
    /// <summary>Every credit the composition could owe, each once, in layer order.</summary>
    public IReadOnlyList<MapAttribution> Attributions { get; }
    public string AttributionText { get; }

    public MapComposition(BasemapDefinition basemap, IEnumerable<MapLayerDefinition> overlays)
    {
        ArgumentNullException.ThrowIfNull(basemap);
        ArgumentNullException.ThrowIfNull(overlays);
        Basemap = basemap;
        var items = overlays.ToArray();
        if (items.Any(x => x.Group == MapLayerGroup.Basemap || !Enum.IsDefined(x.Group)))
            throw new ArgumentException("Use the exclusive Basemap slot for basemaps.", nameof(overlays));
        const MapLayerCapabilities supported = MapLayerCapabilities.Visibility | MapLayerCapabilities.Opacity;
        if (items.Any(x => (x.Capabilities & ~supported) != 0 || (x.PersistedPreferences & ~x.Capabilities) != 0))
            throw new ArgumentException("Persisted preferences must be supported capabilities.", nameof(overlays));
        if (items.Any(x => string.IsNullOrWhiteSpace(x.Id) || x.Id == "basemap") ||
            items.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new ArgumentException("Layer IDs must be unique and nonempty.", nameof(overlays));
        Layers = Array.AsReadOnly(new[] { new MapLayerDefinition("basemap", MapLayerGroup.Basemap, 0, basemap.Provider) }
            .Concat(items.OrderBy(x => x.Group).ThenBy(x => x.Order).ThenBy(x => x.Id, StringComparer.Ordinal)).ToArray());
        Attributions = MapAttributions.Distinct(Layers.SelectMany(x => MapAttributions.Of(x.Provider)));
        AttributionText = MapAttributions.Line(Attributions);
    }

    public MapComposition WithBasemap(BasemapDefinition basemap) =>
        new(basemap, Layers.Where(x => x.Group != MapLayerGroup.Basemap));
}
