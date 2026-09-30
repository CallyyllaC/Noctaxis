using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;

namespace Noctaxis.Desktop.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// Terrarium tiles for the Planner map's terrain relief, from the same download and disk cache as
    /// the terrain calculations. Null when terrain is unavailable to this instance (tests, previews).
    /// </summary>
    public ITerrariumTileSource? TerrainReliefSource { get; private set; }

    public TerrainReliefPreferences TerrainReliefMapPreferences => (Settings.TerrainRelief ?? new()).Normalised();

    /// <summary>Applies an edit from the Layers panel; <see cref="CommitPlannerLayersAsync"/> saves it.</summary>
    public void PreviewTerrainRelief(bool visible, double opacityPercent)
    {
        var current = TerrainReliefMapPreferences;
        var next = new TerrainReliefPreferences(visible, opacityPercent / 100).Normalised();
        if (current == next) return;
        Settings = Settings with { TerrainRelief = next };
        OnPropertyChanged(nameof(TerrainReliefMapPreferences));
    }
}
