using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Core.Tests;

public sealed class TerrainPriorityLifecycleTests
{
    [Fact]
    public async Task CancelledWideCameraWaiterCannotLeaveACompletedProfileWithReservedBearings()
    {
        var terrain = new GatedTerrain();
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 2);
        var observer = new GeoCoordinate(53.55, -.48);
        var request = new TerrainProfileRequest(360, 1000, 1000);
        var work = service.StartProfile(observer, request, default);
        await terrain.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var caller = new CancellationTokenSource();
        var priority = work.PrioritiseBearingsAsync(Enumerable.Range(60, 73).Select(b => (double)b).ToArray(), caller.Token);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => priority);
        terrain.Release.TrySetResult(true);
        var profile = await work.CompleteProfile.WaitAsync(TimeSpan.FromSeconds(5));
        var output = System.Environment.GetEnvironmentVariable("NOCTAXIS_PRIORITY_LIFECYCLE_OUTPUT");
        if (!string.IsNullOrEmpty(output)) File.WriteAllText(output,
            TerrainProfileDiagnostics.ExportValidityJson(profile, 1));
        Assert.True(profile.IsComplete);
        Assert.Equal(360, profile.EffectiveCompletedBearingCount);
        Assert.All(profile.Samples, sample => Assert.NotEmpty(sample.Sightline!));
        var reused = await service.GetProfileAsync(observer, request, default);
        Assert.Same(profile, reused);
        Assert.NotNull(new Noctaxis.Core.Calculations.FramingVisibilityCalculator().Calculate(
            new(DataState.Loading, null, "Offline"), reused, 20, 96, 72,
            cameraFrame: new(0, 40, 5)).CameraDepth);
        Assert.NotNull(new Noctaxis.Core.Calculations.FramingVisibilityCalculator().Calculate(
            new(DataState.Loading, null, "Offline"), reused, 20, 96, 5,
            cameraFrame: new(0, 10, 5)).CameraDepth);
        var moved = await service.GetProfileAsync(observer with { Latitude = 53.56 }, request, default);
        Assert.Equal(360, moved.EffectiveCompletedBearingCount);
    }

    /// <summary>
    /// Replaces an earlier test that asserted the opposite: the creating caller's token used to own
    /// the shared producer, so cancelling it cancelled work other callers were still waiting for.
    /// The producer now belongs to the session. Generation invalidation, which does still reject a
    /// whole build, is covered by TerrainResultCacheTests and HorizonSessionOwnershipTests.
    /// </summary>
    [Fact]
    public async Task CreatingCallerCancellationLeavesTheSharedProducerRunningForOtherConsumers()
    {
        var terrain = new GatedTerrain();
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 2);
        var observer = new GeoCoordinate(53.55, -.48);
        var request = new TerrainProfileRequest(360, 1000, 1000);
        using var creator = new CancellationTokenSource();
        var creating = service.GetProfileAsync(observer, request, creator.Token);
        await terrain.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var joined = service.GetPriorityProfileAsync(observer, request, [90d, 91d], default);

        creator.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creating);
        // Nothing incomplete may be published, and the surviving consumer still owns the build.
        Assert.Equal(0, service.CompletedCacheDiagnostics.Count);
        terrain.Release.TrySetResult(true);
        var priorityProfile = await joined.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(priorityProfile.Samples);
        var recovered = await service.GetProfileAsync(observer, request, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(360, recovered.EffectiveCompletedBearingCount);
        Assert.Equal(1, service.CompletedCacheDiagnostics.Count);
    }

    private sealed class GatedTerrain : ITerrainElevationProvider
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PreloadAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token)
        { Started.TrySetResult(true); await Release.Task.WaitAsync(token); }
        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate, CancellationToken token) =>
            Task.FromResult(new EnvironmentalValue<double>(EnvironmentalDataState.Available, 10, "test", "1", "Flat"));
        public Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token) =>
            Task.FromResult(new ElevationBatchResult(EnvironmentalDataState.Available,
                coordinates.Select(_ => (double?)10).ToArray(), "test", "1", "Flat"));
    }
}
