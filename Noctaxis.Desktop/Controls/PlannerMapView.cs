using Noctaxis.Desktop.Mapping;

namespace Noctaxis.Desktop.Controls;

/// <summary>Production composition root; the reusable view and native probes retain their baseline stack.</summary>
public sealed class PlannerMapView : NoctaxisMapView
{
    public ViewModels.PlannerLayersViewModel Layers { get; }
    public PlannerMapView() : base(LightPollutionMapBinding.CreateComposition()) => Layers = new(LayerRuntime);
}
