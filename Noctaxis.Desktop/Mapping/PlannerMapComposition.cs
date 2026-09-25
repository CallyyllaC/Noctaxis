using Mapsui;
using Mapsui.Layers;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Mapping;

/// <summary>Mapsui adapter; a basemap may contain multiple native layers, including vector layers.</summary>
public interface IMapsuiBasemap
{
    BasemapDefinition Definition { get; }
    /// <summary>Return fresh, exclusively owned native layers for each activation.</summary>
    IReadOnlyList<ILayer> CreateLayers();
}

public sealed class OpenStreetMapBasemap : IMapsuiBasemap
{
    public BasemapDefinition Definition { get; } = new(BasemapKind.Street,
        new("OpenStreetMap", MapProvider.Attribution, "https://www.openstreetmap.org/copyright",
            "ODbL", "https://opendatacommons.org/licenses/odbl/1-0/"));

    public IReadOnlyList<ILayer> CreateLayers() => [Mapsui.Tiling.OpenStreetMap.CreateTileLayer()];
}

/// <summary>
/// Owns the Planner's native basemap stack and logical overlay registrations. The MapControl
/// retains ownership of the Map lifetime; removed native layers are disposed here.
/// No navigation subscriptions, timers or attachment-time registrations belong here.
/// </summary>
public sealed class PlannerMapComposition
{
    private IMapsuiBasemap _basemap;
    private ILayer[] _nativeLayers;
    private readonly Dictionary<string, ILayer[]> _nativeOverlays = new(StringComparer.Ordinal);
    public Map Map { get; } = new();
    /// <summary>Original registered definitions. Read Runtime.ActiveBasemap for the current selection.</summary>
    public MapComposition Composition { get; }
    public MapLayerStateController Runtime { get; }

    public PlannerMapComposition(IMapsuiBasemap? basemap = null, IEnumerable<MapLayerDefinition>? additionalLayers = null)
    {
        _basemap = basemap ?? new OpenStreetMapBasemap();
        var planner = new MapProviderMetadata("Noctaxis Planner");
        MapLayerDefinition[] overlays =
        [
            new("environmental-shading", MapLayerGroup.Environmental, 0, planner),
            new("camera-framing", MapLayerGroup.Planning, 0, planner, MapLayerCapabilities.Visibility),
            new("celestial-rays", MapLayerGroup.Planning, 1, planner, MapLayerCapabilities.Visibility),
            new("observer-pin", MapLayerGroup.Interaction, 0, planner)
        ];
        Composition = new(_basemap.Definition, overlays.Concat(additionalLayers ?? []));
        Runtime = new(Composition);
        _nativeLayers = CreateLayers(_basemap);
        foreach (var layer in _nativeLayers) Map.Layers.Add(layer);
        Runtime.BindAdapter("basemap", new MapsuiLayerStateAdapter(_nativeLayers, Map.RefreshGraphics));
    }

    public void ReplaceBasemap(IMapsuiBasemap basemap)
    {
        ArgumentNullException.ThrowIfNull(basemap);
        if (ReferenceEquals(_basemap, basemap)) return;
        var definition = basemap.Definition;
        ArgumentNullException.ThrowIfNull(definition);
        var layers = CreateLayers(basemap);
        var adapter = new MapsuiLayerStateAdapter(layers, Map.RefreshGraphics);
        // Prepare/install first, preserving the working stack if creation or installation fails.
        InstallLayers(layers, 0, () => adapter.Apply(new MapLayerState("basemap")));
        var previous = _nativeLayers;
        foreach (var layer in previous) Map.Layers.Remove(layer);
        _nativeLayers = layers;
        _basemap = basemap;
        Runtime.ActivateBasemap(definition, adapter);
        foreach (var layer in previous) layer.Dispose();
    }

    /// <summary>Install a registered native overlay once, in semantic order. Subsequent state
    /// changes use its adapter; this is not called on visibility changes or reattachment.</summary>
    public void InstallNativeOverlay(string id, IReadOnlyList<ILayer> layers)
    {
        var definition = Composition.Layers.Single(x => x.Id == id);
        if (definition.Group == MapLayerGroup.Basemap || Runtime.HasAdapter(id))
            throw new InvalidOperationException("The registration already has an adapter or is the basemap slot.");
        var owned = ValidateLayers(layers);
        var adapter = new MapsuiLayerStateAdapter(owned, Map.RefreshGraphics);
        var index = _nativeLayers.Length + Composition.Layers.TakeWhile(x => x.Id != id)
            .Sum(x => _nativeOverlays.TryGetValue(x.Id, out var native) ? native.Length : 0);
        InstallLayers(owned, index, () => adapter.Apply(Runtime[id]));
        _nativeOverlays.Add(id, owned);
        Runtime.BindAdapter(id, adapter);
    }

    private void InstallLayers(ILayer[] layers, int index, Action prepare)
    {
        if (layers.Any(x => Map.Layers.Contains(x)))
            throw new ArgumentException("Native layers must be fresh and exclusively owned.", nameof(layers));
        try
        {
            prepare();
            for (var i = 0; i < layers.Length; i++) Map.Layers.Insert(index + i, layers[i]);
        }
        catch
        {
            foreach (var layer in layers)
            {
                Map.Layers.Remove(layer);
                layer.Dispose();
            }
            throw;
        }
    }

    private static ILayer[] CreateLayers(IMapsuiBasemap basemap)
    {
        return ValidateLayers(basemap.CreateLayers());
    }

    private static ILayer[] ValidateLayers(IReadOnlyList<ILayer> source)
    {
        var layers = source.ToArray();
        if (layers.Length == 0 || layers.Any(x => x is null) || layers.Distinct().Count() != layers.Length)
            throw new ArgumentException("Supply at least one distinct native layer.", nameof(source));
        return layers;
    }
}
