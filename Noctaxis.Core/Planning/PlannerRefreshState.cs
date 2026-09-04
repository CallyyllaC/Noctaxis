namespace Noctaxis.Core.Planning;

public enum PlannerRefreshScope
{
    None,
    Observer,
    Astronomy
}

public enum PlannerRefreshPhase
{
    Idle,
    PositionChanged,
    CalculatingAstronomy,
    UpdatingCelestialOverlays,
    UpdatingCameraOverlay,
    LoadingEnvironment,
    Ready,
    Partial,
    Error
}

public enum PlannerRefreshWorkState
{
    NotRequired,
    Pending,
    Running,
    Ready,
    Unavailable,
    Error
}

public enum PlannerPinActivity
{
    None,
    CoreLoading,
    EnvironmentLoading
}

/// <summary>
/// One immutable source of truth for a Planner refresh. Progress is deliberately staged:
/// it represents resolved work, not invented network-download percentages.
/// </summary>
public sealed record PlannerRefreshState(
    long Generation,
    PlannerRefreshScope Scope,
    PlannerRefreshPhase Phase,
    PlannerRefreshWorkState AstronomyState,
    PlannerRefreshWorkState CelestialOverlayState,
    PlannerRefreshWorkState CameraGeometryState,
    PlannerRefreshWorkState WeatherState,
    PlannerRefreshWorkState GroundTerrainState,
    PlannerRefreshWorkState EnvironmentMetadataState,
    string StatusText,
    PlannerRefreshWorkState CameraTerrainState = PlannerRefreshWorkState.NotRequired)
{
    private static readonly (Func<PlannerRefreshState, PlannerRefreshWorkState> State, double Weight)[] Work =
    [
        (state => state.AstronomyState, 20),
        (state => state.CelestialOverlayState, 15),
        (state => state.CameraGeometryState, 15),
        (state => state.CameraTerrainState, 5),
        (state => state.WeatherState, 15),
        (state => state.GroundTerrainState, 20),
        (state => state.EnvironmentMetadataState, 5)
    ];

    public static PlannerRefreshState Idle { get; } = new(0, PlannerRefreshScope.None,
        PlannerRefreshPhase.Idle, PlannerRefreshWorkState.NotRequired,
        PlannerRefreshWorkState.NotRequired, PlannerRefreshWorkState.NotRequired,
        PlannerRefreshWorkState.NotRequired, PlannerRefreshWorkState.NotRequired,
        PlannerRefreshWorkState.NotRequired, string.Empty);

    public static PlannerRefreshState BeginObserver(long generation) => new(generation,
        PlannerRefreshScope.Observer, PlannerRefreshPhase.PositionChanged,
        PlannerRefreshWorkState.Pending, PlannerRefreshWorkState.Pending,
        PlannerRefreshWorkState.Pending, PlannerRefreshWorkState.Pending,
        PlannerRefreshWorkState.Pending, PlannerRefreshWorkState.Pending,
        "Observer placed · starting Planner refresh…", PlannerRefreshWorkState.Pending);

    public static PlannerRefreshState BeginAstronomy(long generation) => new(generation,
        PlannerRefreshScope.Astronomy, PlannerRefreshPhase.CalculatingAstronomy,
        PlannerRefreshWorkState.Pending, PlannerRefreshWorkState.Pending,
        PlannerRefreshWorkState.Pending, PlannerRefreshWorkState.Pending,
        PlannerRefreshWorkState.NotRequired, PlannerRefreshWorkState.NotRequired,
        "Calculating celestial positions…", PlannerRefreshWorkState.NotRequired);

    public bool IsRefreshing => Phase is not (PlannerRefreshPhase.Idle or PlannerRefreshPhase.Ready or
        PlannerRefreshPhase.Partial or PlannerRefreshPhase.Error);

    public bool IsCoreReady => IsResolvedSuccessfully(AstronomyState) &&
                               IsResolvedSuccessfully(CelestialOverlayState) &&
                               IsResolvedSuccessfully(CameraGeometryState);

    public bool HasBlockingError => AstronomyState == PlannerRefreshWorkState.Error;

    public bool HasOptionalFailure => new[] { WeatherState, GroundTerrainState,
            EnvironmentMetadataState, CameraTerrainState }
        .Any(state => state is PlannerRefreshWorkState.Error or PlannerRefreshWorkState.Unavailable);

    public PlannerPinActivity PinActivity => !IsRefreshing || Scope != PlannerRefreshScope.Observer
        ? PlannerPinActivity.None
        : IsCoreReady
            ? PlannerPinActivity.EnvironmentLoading
            : PlannerPinActivity.CoreLoading;

    public double Progress
    {
        get
        {
            if (Scope == PlannerRefreshScope.None) return 0;
            const double pinWeight = 5;
            var denominator = pinWeight;
            var complete = pinWeight;
            foreach (var (selector, weight) in Work)
            {
                var state = selector(this);
                if (state == PlannerRefreshWorkState.NotRequired) continue;
                denominator += weight;
                if (IsResolved(state)) complete += weight;
            }
            return denominator <= 0 ? 1 : Math.Clamp(complete / denominator, 0, 1);
        }
    }

    public static bool IsResolved(PlannerRefreshWorkState state) => state is
        PlannerRefreshWorkState.Ready or PlannerRefreshWorkState.Unavailable or PlannerRefreshWorkState.Error;

    private static bool IsResolvedSuccessfully(PlannerRefreshWorkState state) => state is
        PlannerRefreshWorkState.Ready or PlannerRefreshWorkState.NotRequired;
}
