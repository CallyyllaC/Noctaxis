namespace Noctaxis.Desktop.Mapping;

public enum MapLayerGroup { Basemap, Environmental, Planning, Interaction }
public enum BasemapKind { Street }

[Flags]
public enum MapLayerCapabilities { None = 0, Visibility = 1, Opacity = 2 }

public sealed record MapProviderMetadata(string DisplayName, string? AttributionText = null,
    string? AttributionUrl = null, string? LicenceName = null, string? LicenceUrl = null);

/// <summary>Logical content, independent of its rendering backend. Order is within its group.</summary>
public sealed record MapLayerDefinition(string Id, MapLayerGroup Group, int Order,
    MapProviderMetadata Provider, MapLayerCapabilities Capabilities = MapLayerCapabilities.None,
    MapLayerCapabilities PersistedPreferences = MapLayerCapabilities.None);

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
        AttributionText = string.Join(" · ", Layers.Select(x => x.Provider.AttributionText)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal));
    }

    public MapComposition WithBasemap(BasemapDefinition basemap) =>
        new(basemap, Layers.Where(x => x.Group != MapLayerGroup.Basemap));
}
