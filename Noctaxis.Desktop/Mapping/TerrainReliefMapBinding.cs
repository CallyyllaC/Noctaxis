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
    public const string AttributionText = "Terrain: Mapzen/Tilezen Terrarium";
    public const string AttributionUrl = "https://github.com/tilezen/joerd/blob/master/docs/attribution.md";

    public static MapLayerDefinition Definition { get; } = new(LayerId, MapLayerGroup.Environmental, -20,
        new("Terrain relief", AttributionText, AttributionUrl),
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
    {
        _map = map;
        Source = new TerrainReliefTileSource(elevation);
        Layer = new TerrainReliefTileLayer(Source) { Name = "Terrain relief" };
        map.InstallNativeOverlay(LayerId, [Layer], themeStrength);
        map.Runtime.SetAvailability(LayerId, MapLayerAvailability.Available);
    }

    public TerrainReliefTileSource Source { get; }
    internal TerrainReliefTileLayer Layer { get; }

    /// <summary>The theme's strength changed; the user's own opacity is unchanged.</summary>
    public void ThemeStrengthChanged() => _map.ReapplyLayerState(LayerId);
}
