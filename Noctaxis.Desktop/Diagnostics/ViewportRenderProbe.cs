namespace Noctaxis.Desktop.Diagnostics;

/// <summary>Opt-in sink used by the standalone native viewport probe and render tests.</summary>
internal static class ViewportRenderProbe
{
    internal static Action<ViewportRenderSample>? Sink { get; set; }
    internal static bool SynchronizeGpu { get; set; }
    internal static Action<SkiaSharp.GRContext, Controls.EnvironmentalOverlayState>? GpuValidation { get; set; }
}

internal sealed record ViewportRenderSample(bool? Gpu, double TotalMs, double ShaderMs,
    double TerrainMs, double TonePreparationMs, double ProjectionPathMs, double PathBuildMs, double FillMs, double GpuCompletionMs, long ManagedBytes,
    int Patches, int TonePreparations)
{
    public double PriorGpuMs { get; init; }
}
