namespace Noctaxis.Desktop.Mapping;

public enum MapLayerAvailability { Available, Unavailable, Loading }

/// <summary>Cached snapshot, replaced only by an actual state change. Visibility is user intent;
/// IsActive is an internal content gate, and availability never overwrites user intent.</summary>
public sealed record MapLayerState(string Id, bool IsVisible = true, double Opacity = 1,
    MapLayerAvailability Availability = MapLayerAvailability.Available, bool IsActive = true)
{
    public bool IsRenderable => IsActive && IsVisible && Opacity > 0 && Availability == MapLayerAvailability.Available;
}

public interface IMapLayerStateAdapter
{
    void Apply(MapLayerState state);
}

/// <summary>Control-plane state only. Mutations and adapters run on the owning UI thread.
/// No navigation subscriptions or frame-time metadata processing.</summary>
public sealed class MapLayerStateController
{
    private readonly Dictionary<string, MapLayerDefinition> _definitions;
    private readonly Dictionary<string, MapLayerState> _states;
    private readonly Dictionary<string, IMapLayerStateAdapter> _adapters = new(StringComparer.Ordinal);
    public MapComposition Composition { get; }
    public BasemapDefinition ActiveBasemap { get; private set; }
    public string AttributionText { get; private set; } = "";
    public event EventHandler? AttributionChanged;
    public event EventHandler<MapLayerState>? StateChanged;

    public MapLayerStateController(MapComposition composition)
    {
        ArgumentNullException.ThrowIfNull(composition);
        Composition = composition;
        ActiveBasemap = composition.Basemap;
        _definitions = composition.Layers.ToDictionary(x => x.Id, StringComparer.Ordinal);
        _states = composition.Layers.ToDictionary(x => x.Id, x => new MapLayerState(x.Id), StringComparer.Ordinal);
        RefreshAttribution();
    }

    public MapLayerState this[string id] => _states[id];
    internal bool HasAdapter(string id) => _adapters.ContainsKey(id);

    /// <summary>One adapter per registration. Bind once at construction, not on attachment.</summary>
    public void BindAdapter(string id, IMapLayerStateAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var state = _states[id];
        if (_adapters.TryGetValue(id, out var existing))
        {
            if (ReferenceEquals(existing, adapter)) return;
            throw new InvalidOperationException($"Layer '{id}' already has an adapter.");
        }
        adapter.Apply(state);
        _adapters.Add(id, adapter);
    }

    public bool SetVisibility(string id, bool visible)
    {
        Require(id, MapLayerCapabilities.Visibility);
        var current = _states[id];
        return current.IsVisible != visible && Update(current with { IsVisible = visible });
    }

    public bool SetOpacity(string id, double opacity)
    {
        Require(id, MapLayerCapabilities.Opacity);
        if (!double.IsFinite(opacity) || opacity < 0 || opacity > 1)
            throw new ArgumentOutOfRangeException(nameof(opacity), "Opacity must be finite and within [0, 1].");
        var current = _states[id];
        return current.Opacity != opacity && Update(current with { Opacity = opacity });
    }

    public bool SetAvailability(string id, MapLayerAvailability availability)
    {
        if (!Enum.IsDefined(availability)) throw new ArgumentOutOfRangeException(nameof(availability));
        var current = _states[id];
        return current.Availability != availability && Update(current with { Availability = availability });
    }

    // Existing settings/render dependencies may gate content without changing user intent.
    internal bool SetActive(string id, bool active)
    {
        if (id == "basemap") throw new InvalidOperationException("The basemap slot is always active.");
        var current = _states[id];
        return current.IsActive != active && Update(current with { IsActive = active });
    }

    // Called only after successful native activation. Definitions and other states remain stable.
    internal void ActivateBasemap(BasemapDefinition basemap, IMapLayerStateAdapter adapter)
    {
        var state = new MapLayerState("basemap");
        adapter.Apply(state);
        _adapters["basemap"] = adapter;
        _states["basemap"] = state;
        ActiveBasemap = basemap;
        RefreshAttribution();
    }

    private void Require(string id, MapLayerCapabilities capability)
    {
        if ((_definitions[id].Capabilities & capability) == 0)
            throw new InvalidOperationException($"Layer '{id}' does not support {capability}.");
    }

    private bool Update(MapLayerState state)
    {
        _states[state.Id] = state;
        if (_adapters.TryGetValue(state.Id, out var adapter)) adapter.Apply(state);
        RefreshAttribution();
        StateChanged?.Invoke(this, state);
        return true;
    }

    private void RefreshAttribution()
    {
        var credits = Composition.Layers
            .Where(x => x.Group == MapLayerGroup.Basemap || _states[x.Id].IsRenderable)
            .Select(x => x.Group == MapLayerGroup.Basemap ? ActiveBasemap.Provider.AttributionText : x.Provider.AttributionText)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal);
        var text = string.Join(" · ", credits);
        if (text == AttributionText) return;
        AttributionText = text;
        AttributionChanged?.Invoke(this, EventArgs.Empty);
    }
}
