using Mapsui;
using Mapsui.Layers;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Mapping;

/// <summary>
/// A basemap's native layers. <see cref="Geography"/> is drawn at the bottom of the map;
/// <see cref="Reference"/> (roads, boundaries, names), when the basemap can separate it, is drawn in
/// the Reference group above the environmental layers. Imagery has no separable reference.
/// </summary>
public sealed record BasemapLayers(IReadOnlyList<ILayer> Geography, IReadOnlyList<ILayer> Reference);

/// <summary>Mapsui adapter; a basemap may contain multiple native layers, including vector layers.</summary>
public interface IMapsuiBasemap
{
    BasemapDefinition Definition { get; }
    /// <summary>Return fresh, exclusively owned native layers for each activation.</summary>
    IReadOnlyList<ILayer> CreateLayers();
    /// <summary>
    /// Fresh layers for the Planner, split where the basemap supports it. The default is the
    /// whole basemap as geography, with no separate reference pass.
    /// </summary>
    BasemapLayers CreateLayerSet() => new(CreateLayers(), []);
}

/// <summary>
/// The former fixed-style raster street basemap. It is no longer the Planner default because its
/// imagery cannot follow the theme; see <see cref="Vector.OpenFreeMapBasemap"/>.
/// </summary>
public sealed class OpenStreetMapBasemap : IMapsuiBasemap
{
    public BasemapDefinition Definition { get; } = new(BasemapKind.Street,
        new("OpenStreetMap", Credits: [MapAttributions.OpenStreetMap]));

    public IReadOnlyList<ILayer> CreateLayers() => [Mapsui.Tiling.OpenStreetMap.CreateTileLayer()];
}

/// <summary>
/// Owns the Planner's native basemap stack and logical overlay registrations. The MapControl
/// retains ownership of the Map lifetime; removed native layers are disposed here.
/// No navigation subscriptions, timers or attachment-time registrations belong here.
/// </summary>
/// <remarks>
/// Native layers are always positioned from the semantic composition: a registration's native
/// layers sit above those of every registration ordered before it. Nothing inserts at a fixed index.
/// Planning and Interaction content is drawn by the map view's overlay above all native layers.
/// </remarks>
public sealed class PlannerMapComposition
{
    /// <summary>The registration holding the active basemap's reference pass.</summary>
    public const string BasemapReferenceId = "basemap-reference";
    private const string BasemapId = "basemap";
    private IMapsuiBasemap _basemap;
    // Native layers of every installed registration, including the basemap's two passes.
    private readonly Dictionary<string, ILayer[]> _native = new(StringComparer.Ordinal);
    public Map Map { get; } = new();
    /// <summary>Original registered definitions. Read Runtime.ActiveBasemap for the current selection.</summary>
    public MapComposition Composition { get; }
    public MapLayerStateController Runtime { get; }

    public PlannerMapComposition(IMapsuiBasemap? basemap = null, IEnumerable<MapLayerDefinition>? additionalLayers = null)
    {
        _basemap = basemap ?? new Vector.OpenFreeMapBasemap();
        var planner = new MapProviderMetadata("Noctaxis Planner");
        MapLayerDefinition[] overlays =
        [
            // The active basemap's roads, boundaries and names. Its credit is the basemap's own.
            new(BasemapReferenceId, MapLayerGroup.Reference, 0, new MapProviderMetadata("Basemap reference")),
            // Camera-sector shading composites the sector's weather and terrain: planning content.
            new("environmental-shading", MapLayerGroup.Planning, -1, planner),
            new("camera-framing", MapLayerGroup.Planning, 0, planner, MapLayerCapabilities.Visibility),
            new("celestial-rays", MapLayerGroup.Planning, 1, planner, MapLayerCapabilities.Visibility),
            new("observer-pin", MapLayerGroup.Interaction, 0, planner)
        ];
        Composition = new(_basemap.Definition, overlays.Concat(additionalLayers ?? []));
        Runtime = new(Composition);
        var set = CreateLayerSet(_basemap);
        _native[BasemapId] = set.Geography;
        _native[BasemapReferenceId] = set.Reference;
        foreach (var layer in NativeLayers) Map.Layers.Add(SuppressProviderAttribution(layer));
        Runtime.BindAdapter(BasemapId, new MapsuiLayerStateAdapter(set.Geography, Map.RefreshGraphics));
        Runtime.BindAdapter(BasemapReferenceId, new MapsuiLayerStateAdapter(set.Reference, Map.RefreshGraphics));
        ApplyBasemapGates();
    }

    /// <summary>Installed native layers, bottom to top, each with the registration that owns it.</summary>
    public IReadOnlyList<(string Id, ILayer Layer)> NativeLayerOrder =>
        Composition.Layers.SelectMany(x => (_native.TryGetValue(x.Id, out var native) ? native : [])
            .Select(layer => (x.Id, layer))).ToArray();

    private IEnumerable<ILayer> NativeLayers => NativeLayerOrder.Select(x => x.Layer);

    public void ReplaceBasemap(IMapsuiBasemap basemap)
    {
        ArgumentNullException.ThrowIfNull(basemap);
        if (ReferenceEquals(_basemap, basemap)) return;
        var definition = basemap.Definition;
        ArgumentNullException.ThrowIfNull(definition);
        var set = CreateLayerSet(basemap);
        var geographyAdapter = new MapsuiLayerStateAdapter(set.Geography, Map.RefreshGraphics);
        var referenceAdapter = new MapsuiLayerStateAdapter(set.Reference, Map.RefreshGraphics);
        // Prepare/install first, preserving the working stack if creation or installation fails.
        // Each new pass goes directly beneath the pass it replaces, so the order never changes.
        InstallLayers(set.Geography, InsertionIndex(BasemapId), () => geographyAdapter.Apply(new MapLayerState(BasemapId)));
        try
        {
            InstallLayers(set.Reference, InsertionIndex(BasemapReferenceId) + set.Geography.Length,
                () => referenceAdapter.Apply(Runtime[BasemapReferenceId]));
        }
        catch
        {
            foreach (var layer in set.Geography) { Map.Layers.Remove(layer); layer.Dispose(); }
            throw;
        }
        var previous = _native[BasemapId].Concat(_native[BasemapReferenceId]).ToArray();
        foreach (var layer in previous) Map.Layers.Remove(layer);
        _native[BasemapId] = set.Geography;
        _native[BasemapReferenceId] = set.Reference;
        _basemap = basemap;
        Runtime.ActivateBasemap(definition, geographyAdapter);
        Runtime.ReplaceAdapter(BasemapReferenceId, referenceAdapter);
        foreach (var layer in previous) layer.Dispose();
        ApplyBasemapGates();
        // A themeable replacement takes the current theme at once; imagery is never restyled.
        if (_basemapStyle is not null && basemap is Vector.IThemeableBasemap themeable) themeable.ApplyStyle(_basemapStyle);
        if (_labelLanguage is not null && basemap is Vector.ILabelledBasemap labelled) labelled.ApplyLabelLanguage(_labelLanguage);
    }

    private Vector.BasemapStyle? _basemapStyle;
    private string? _labelLanguage;

    /// <summary>
    /// Sets the language the basemap's place names are preferred in: the application language. Only a
    /// basemap that draws its own names follows it, and only its name-bearing tiles are rendered
    /// again; geography, the environmental layers and every download are untouched. Returns true when
    /// the visible names changed language.
    /// </summary>
    public bool ApplyLabelLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        _labelLanguage = language;
        return _basemap is Vector.ILabelledBasemap labelled && labelled.ApplyLabelLanguage(language);
    }

    /// <summary>The adapter currently providing the basemap.</summary>
    public IMapsuiBasemap ActiveBasemap => _basemap;

    /// <summary>
    /// Applies the theme's basemap style. Only a themeable (vector) basemap is restyled, in place:
    /// the native layers, their order, the overlays and all environmental data are untouched.
    /// Photographic imagery ignores the style. Returns true when the visible basemap was restyled.
    /// </summary>
    public bool ApplyBasemapStyle(Vector.BasemapStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        _basemapStyle = style;
        return _basemap is Vector.IThemeableBasemap themeable && themeable.ApplyStyle(style);
    }

    /// <summary>Install a registered native overlay once, in semantic order. Subsequent state
    /// changes use its adapter; this is not called on visibility changes or reattachment.
    /// <paramref name="opacityScale"/> is a theme factor on the user's opacity; after it changes,
    /// call <see cref="ReapplyLayerState"/>.</summary>
    public void InstallNativeOverlay(string id, IReadOnlyList<ILayer> layers, Func<double>? opacityScale = null)
    {
        var definition = Composition.Layers.Single(x => x.Id == id);
        if (definition.Group == MapLayerGroup.Basemap || id == BasemapReferenceId || Runtime.HasAdapter(id))
            throw new InvalidOperationException("The registration already has an adapter or belongs to the basemap.");
        var owned = ValidateLayers(layers);
        var adapter = new MapsuiLayerStateAdapter(owned, Map.RefreshGraphics, opacityScale);
        InstallLayers(owned, InsertionIndex(id), () => adapter.Apply(Runtime[id]));
        _native.Add(id, owned);
        Runtime.BindAdapter(id, adapter);
    }

    /// <summary>Re-applies a registration's unchanged state, for example after its theme factor changed.</summary>
    public void ReapplyLayerState(string id) => Runtime.Reapply(id);

    // Above every native layer of the registrations ordered before it.
    private int InsertionIndex(string id) => Composition.Layers.TakeWhile(x => x.Id != id)
        .Sum(x => _native.TryGetValue(x.Id, out var native) ? native.Length : 0);

    // Content that imagery already shows (terrain relief) is not drawn over it.
    private void ApplyBasemapGates()
    {
        var imagery = Runtime.ActiveBasemap.Kind == BasemapKind.Satellite;
        foreach (var definition in Composition.Layers.Where(x => !x.ShownOverImagery))
            Runtime.SetActive(definition.Id, !imagery);
    }

    private void InstallLayers(IReadOnlyList<ILayer> layers, int index, Action prepare)
    {
        if (layers.Any(x => Map.Layers.Contains(x)))
            throw new ArgumentException("Native layers must be fresh and exclusively owned.", nameof(layers));
        try
        {
            prepare();
            for (var i = 0; i < layers.Count; i++) Map.Layers.Insert(index + i, SuppressProviderAttribution(layers[i]));
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

    /// <summary>
    /// Mapsui draws each layer's own attribution as a link over the map. The Planner credits every active
    /// provider in one strip of its own, from the composition's metadata, so the native links are switched
    /// off rather than drawn on top of it. The layer keeps its source's attribution as data.
    /// </summary>
    private static ILayer SuppressProviderAttribution(ILayer layer)
    {
        layer.Attribution.Enabled = false;
        return layer;
    }

    private static (ILayer[] Geography, ILayer[] Reference) CreateLayerSet(IMapsuiBasemap basemap)
    {
        var set = basemap.CreateLayerSet() ?? throw new ArgumentException("The basemap supplied no layers.", nameof(basemap));
        var geography = ValidateLayers(set.Geography);
        var reference = (set.Reference ?? []).ToArray();
        if (reference.Any(x => x is null) || reference.Distinct().Count() != reference.Length ||
            reference.Intersect(geography).Any())
            throw new ArgumentException("Reference layers must be distinct from each other and from the geography.", nameof(basemap));
        return (geography, reference);
    }

    private static ILayer[] ValidateLayers(IReadOnlyList<ILayer> source)
    {
        var layers = source.ToArray();
        if (layers.Length == 0 || layers.Any(x => x is null) || layers.Distinct().Count() != layers.Length)
            throw new ArgumentException("Supply at least one distinct native layer.", nameof(source));
        return layers;
    }
}
