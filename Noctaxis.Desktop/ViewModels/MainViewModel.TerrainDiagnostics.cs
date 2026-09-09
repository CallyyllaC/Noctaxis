using Noctaxis.Core.Domain;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Desktop.ViewModels;

public partial class MainViewModel
{
    private readonly HorizonService? _terrainDiagnostics;
    public string TerrainDiagnosticsStatus => !Settings.EnableTerrainCalculations
        ? "Terrain calculations disabled"
        : !Settings.TerrainDebugOverlay ? "Terrain diagnostics disabled"
        : $"Observer: {Observer.Latitude:F5}, {Observer.Longitude:F5}\n" +
          $"State: {TerrainStatus}\nTerrain generation: {TerrainDebugGeneration}\n" +
          $"Profile updated: {CurrentTerrain?.GeneratedAt.ToString() ?? "Resolving…"}";

    public string TerrainDiagnosticsBearing => EnabledTerrainDiagnosticsProfile is { } profile
        ? $"Camera bearing: {TerrainDebugBearing:F1}°\n" +
          $"First horizontal obstruction: {DiagnosticValue(profile.TerrainObstructionAt(TerrainDebugBearing).EffectiveFirstObstructionDistanceMetres, "m")}\n" +
          $"Terrain horizon angle: {DiagnosticValue(profile.GroundAltitudeAt(TerrainDebugBearing), "°")}\n" +
          $"Target altitude: {DiagnosticValue(TerrainDebugTargetAltitude, "°")}\n" +
          $"Target visibility: {HorizonStatus}"
        : TerrainDiagnosticsStatus;

    public string TerrainDiagnosticsSurface => EnabledTerrainDiagnosticsProfile is { } profile
        ? $"Raw terrain elevation: {DiagnosticValue(profile.ObserverDiagnostics?.TerrainSample.InterpolatedElevationMetres, "m")}\n" +
          $"Resolved surface elevation: {DiagnosticValue(profile.GroundElevationAtObserver is { HasValue: true } ground ? ground.Value : null, "m")}\n" +
          $"Terrain source: {profile.GroundElevationAtObserver?.SourceId ?? "Unavailable"}\n" +
          $"Land-cover classification: {profile.ObserverDiagnostics?.Classification?.ToString() ?? "Not available / not required"}\n" +
          $"Water correction: {(profile.ObserverDiagnostics?.SurfaceWasAdjusted == true ? "Bathymetry raised to the physical water surface" : "No adjustment")}\n" +
          $"Observer ground elevation: {DiagnosticValue(profile.ChosenObserverGroundElevationMetres, "m")}\n" +
          $"Camera height: {profile.ObserverHeightAboveGroundMetres:0.0} m\n" +
          $"Final observer elevation: {DiagnosticValue(profile.ObserverAbsoluteElevationMetres, "m")}\n" +
          GroundElevationSourceText
        : TerrainDiagnosticsStatus;

    public string TerrainDiagnosticsPerformance => !ShowTerrainDebugOverlay ? TerrainDiagnosticsStatus :
        TerrainProfileCacheSummary + (EnabledTerrainDiagnosticsProfile?.PipelineTimings is { } timing
        ? $"Profile generation: {timing.TotalMilliseconds:0.0} ms\n" +
          $"Completed bearings: {timing.CompletedBearingCount}\nSamples per bearing: {timing.RadialSampleCount}\n" +
          $"Parallel workers: {timing.DegreeOfParallelism}"
        : "Timing counters are not available for this profile.");

    private string TerrainProfileCacheSummary => _terrainDiagnostics?.CompletedCacheDiagnostics is { } cache
        ? $"Completed profiles: {cache.Count} / {cache.Capacity}\n" +
          $"Cache hits / misses / evictions: {cache.Hits} / {cache.Misses} / {cache.Evictions}\n"
        : "";

    public string TerrainDiagnosticsAdvanced => EnabledTerrainDiagnosticsProfile?.ObserverDiagnostics is { } diagnostics
        ? $"DEM tile: {diagnostics.TerrainSample.Tile}\nDEM cell: {diagnostics.TerrainSample.Cell}\n" +
          $"DEM resolution: {diagnostics.TerrainSample.Resolution}\nCorrection reason: {diagnostics.SurfaceResolutionReason}\n" +
          $"Observer datum: {CurrentTerrain?.ObserverDatumMessage}\n" +
          $"Local map generation: {TerrainDebugMapGeneration}\nLocal map state: {TerrainDebugMapLoadState}\n" +
          "The copied snapshot includes full source, correction, radial and cache diagnostics."
        : TerrainDiagnosticsStatus;

    private static string DiagnosticValue(double? value, string unit) =>
        value.HasValue ? $"{value.Value:0.0} {unit}" : "Unavailable";
}
