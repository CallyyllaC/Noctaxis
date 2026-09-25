using Mapsui.Layers;

namespace Noctaxis.Desktop.Mapping;

/// <summary>Applies native properties without replacing layers, then requests one native redraw.</summary>
public sealed class MapsuiLayerStateAdapter(IEnumerable<ILayer> layers, Action requestRedraw) : IMapLayerStateAdapter
{
    private readonly ILayer[] _layers = layers.ToArray();
    public void Apply(MapLayerState state)
    {
        var changed = false;
        foreach (var layer in _layers)
        {
            if (layer.Enabled != state.IsRenderable) { layer.Enabled = state.IsRenderable; changed = true; }
            if (layer.Opacity != state.Opacity) { layer.Opacity = state.Opacity; changed = true; }
        }
        if (changed) requestRedraw();
    }
}

/// <summary>Connects custom/shared rendering without requiring a native layer or new surface.</summary>
public sealed class DelegateMapLayerStateAdapter(Action<MapLayerState> apply) : IMapLayerStateAdapter
{
    public void Apply(MapLayerState state) => apply(state);
}
