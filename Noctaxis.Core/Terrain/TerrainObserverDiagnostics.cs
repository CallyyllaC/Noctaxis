using Noctaxis.Core.Environment;
using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Terrain;

public sealed record TerrainObserverDiagnostics(
    ElevationSampleDiagnostics TerrainSample,
    double? ResolvedGroundElevationMetres,
    TerrainSampleStatus ResolvedStatus,
    string ResolutionPolicy,
    LandCoverClass? Classification = null,
    double? ResolvedSurfaceElevationMetres = null,
    bool SurfaceWasAdjusted = false,
    TerrainSurfaceResolutionReason? SurfaceResolutionReason = null);
