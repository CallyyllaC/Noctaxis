using Noctaxis.Desktop.Mapping;

namespace Noctaxis.Desktop.Controls;

/// <summary>Production composition root; the reusable view and native probes retain their baseline stack.</summary>
public sealed class PlannerMapView : NoctaxisMapView
{
    public ViewModels.PlannerLayersViewModel Layers { get; }
    public PlannerMapView() : this(LightPollutionMapBinding.CreateComposition()) { }

    private PlannerMapView(PlannerMapComposition composition) : base(composition) => Layers = new(LayerRuntime);

    /// <summary>
    /// The Planner's Picture-in-Picture map: the same composition and layers as this map, as a secondary
    /// view. Its basemap is another view of this map's basemap source, so the decoded tiles, the tile
    /// cache and the downloads are shared rather than repeated.
    /// </summary>
    public PlannerMapView CreatePictureInPicture()
    {
        IMapsuiBasemap? basemap = CompositionForTesting.ActiveBasemap is Mapping.Vector.OpenFreeMapBasemap main
            ? new Mapping.Vector.OpenFreeMapBasemap(main.Source) : null;
        return new PlannerMapView(LightPollutionMapBinding.CreateComposition(basemap)) { IsPictureInPicture = true };
    }
}
