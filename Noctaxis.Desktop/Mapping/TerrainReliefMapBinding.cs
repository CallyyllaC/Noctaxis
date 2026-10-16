using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;

namespace Noctaxis.Desktop.Mapping;

/// <summary>
/// Installs terrain relief in the Planner map's Environmental group, beneath light pollution and the
/// basemap's reference pass. It is not drawn over satellite imagery, which already shows the terrain.
/// </summary>
public sealed class TerrainReliefMapBinding
{
    public const string LayerId = "terrain-relief";
    public static string AttributionText => MapAttributions.TilezenTerrarium.Credit;
    public static string AttributionUrl => MapAttributions.TilezenTerrarium.Url!.AbsoluteUri;

    public static MapLayerDefinition Definition { get; } = new(LayerId, MapLayerGroup.Environmental, -20,
        new("Terrain relief", Credits: [MapAttributions.TilezenTerrarium]),
        MapLayerCapabilities.Visibility | MapLayerCapabilities.Opacity,
        MapLayerCapabilities.Visibility | MapLayerCapabilities.Opacity, ShownOverImagery: false);

    public static void ApplyPreferences(PlannerMapComposition map, TerrainReliefPreferences preferences)
    {
        if (!map.Composition.Layers.Any(x => x.Id == LayerId)) return;
        var value = preferences.Normalised();
        MapLayerPreferences.Restore(map.Runtime, [new(LayerId, value.IsVisible, value.Opacity)]);
    }

    private readonly PlannerMapComposition _map;

    /// <param name="themeStrength">The theme's factor on the user's opacity (reduced in High Contrast).</param>
    public TerrainReliefMapBinding(PlannerMapComposition map, ITerrariumTileSource elevation, Func<double> themeStrength)
        : this(map, new TerrainReliefTileSource(elevation), themeStrength) { }

    /// <summary>
    /// Terrain relief from another view's relief source, such as the main map's for the Picture-in-Picture
    /// map: its shaded tiles are computed and cached once for both views. The layer is this view's own.
    /// </summary>
    public TerrainReliefMapBinding(PlannerMapComposition map, TerrainReliefTileSource source, Func<double> themeStrength)
    {
        _map = map;
        Source = source;
        Layer = new TerrainReliefTileLayer(Source) { Name = "Terrain relief" };
        map.InstallNativeOverlay(LayerId, [Layer], themeStrength);
        map.Runtime.SetAvailability(LayerId, MapLayerAvailability.Available);
    }

    public TerrainReliefTileSource Source { get; }
    internal TerrainReliefTileLayer Layer { get; }

    /// <summary>The theme's strength changed; the user's own opacity is unchanged.</summary>
    public void ThemeStrengthChanged() => _map.ReapplyLayerState(LayerId);

    /// <summary>
    /// Draws this view's relief in another tone. Only this layer's drawn tiles are redrawn, once, from the
    /// shared shaded grid; the layer's state, its opacity and every other view are untouched. Returns false when
    /// the tone was already in use.
    /// </summary>
    public bool SetTone(TerrainReliefTileSource.ReliefTone tone)
    {
        if (Layer.Tone == tone) return false;
        Layer.Tone = tone;
        Layer.ClearCache();
        Layer.DataHasChanged();
        return true;
    }
}
