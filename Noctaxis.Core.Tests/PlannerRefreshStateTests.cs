using Noctaxis.Core.Planning;

namespace Noctaxis.Core.Tests;

public sealed class PlannerRefreshStateTests
{
    [Fact]
    public void ObserverRefresh_UsesResolvedWeightedStagesAndOptionalFailuresCountAsComplete()
    {
        var state = PlannerRefreshState.BeginObserver(7);

        Assert.Equal(.05, state.Progress, 6);
        Assert.True(state.IsRefreshing);
        Assert.Equal(PlannerPinActivity.CoreLoading, state.PinActivity);

        state = state with
        {
            AstronomyState = PlannerRefreshWorkState.Ready,
            CelestialOverlayState = PlannerRefreshWorkState.Ready,
            CameraGeometryState = PlannerRefreshWorkState.Ready,
            CameraTerrainState = PlannerRefreshWorkState.Ready,
            WeatherState = PlannerRefreshWorkState.Error,
            GroundTerrainState = PlannerRefreshWorkState.Ready,
            EnvironmentMetadataState = PlannerRefreshWorkState.Unavailable,
            Phase = PlannerRefreshPhase.Partial
        };

        Assert.Equal(1, state.Progress);
        Assert.True(state.IsCoreReady);
        Assert.True(state.HasOptionalFailure);
        Assert.False(state.HasBlockingError);
        Assert.False(state.IsRefreshing);
        Assert.Equal(PlannerPinActivity.None, state.PinActivity);
    }

    [Fact]
    public void AstronomyScope_ExcludesStaticEnvironmentFromItsProgressDenominator()
    {
        var state = PlannerRefreshState.BeginAstronomy(8);

        Assert.Equal(PlannerRefreshWorkState.NotRequired, state.GroundTerrainState);
        Assert.InRange(state.Progress, .0714, .0715);
    }
}
