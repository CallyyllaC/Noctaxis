using Mapsui.Layers;

namespace Noctaxis.Desktop.Mapping;

/// <summary>
/// Applies native properties without replacing layers, then requests one native redraw.
/// <paramref name="opacityScale"/> lets the theme weaken a layer (High Contrast weakens terrain
/// relief) without touching the user's own opacity.
/// </summary>
public sealed class MapsuiLayerStateAdapter(IEnumerable<ILayer> layers, Action requestRedraw,
    Func<double>? opacityScale = null) : IMapLayerStateAdapter
{
    private readonly ILayer[] _layers = layers.ToArray();
    public void Apply(MapLayerState state)
    {
        var changed = false;
        var opacity = state.Opacity * Math.Clamp(opacityScale?.Invoke() ?? 1, 0, 1);
        foreach (var layer in _layers)
        {
            if (layer.Enabled != state.IsRenderable) { layer.Enabled = state.IsRenderable; changed = true; }
            if (layer.Opacity != opacity) { layer.Opacity = opacity; changed = true; }
        }
        if (changed) requestRedraw();
    }
}

/// <summary>Connects custom/shared rendering without requiring a native layer or new surface.</summary>
public sealed class DelegateMapLayerStateAdapter(Action<MapLayerState> apply) : IMapLayerStateAdapter
{
    public void Apply(MapLayerState state) => apply(state);
}
