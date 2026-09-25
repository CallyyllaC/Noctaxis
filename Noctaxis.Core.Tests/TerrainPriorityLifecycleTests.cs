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

    [Fact]
    public async Task ProducerCancellationStillRejectsWholeGenerationAndDoesNotCacheIt()
    {
        var terrain = new GatedTerrain();
        var service = new HorizonService(terrain, NullLogger<HorizonService>.Instance, 2);
        var observer = new GeoCoordinate(53.55, -.48);
        var request = new TerrainProfileRequest(360, 1000, 1000);
        using var producer = new CancellationTokenSource();
        var work = service.StartProfile(observer, request, producer.Token);
        await terrain.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var priority = work.PrioritiseBearingsAsync([90d, 91d], default);
        producer.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.CompleteProfile);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => priority);
        Assert.Equal(0, service.CompletedCacheDiagnostics.Count);
        terrain.Release.TrySetResult(true);
        var recovered = await service.GetProfileAsync(observer, request, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(360, recovered.EffectiveCompletedBearingCount);
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
