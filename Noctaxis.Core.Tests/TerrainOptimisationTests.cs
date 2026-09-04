using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Core.Tests;

public sealed class TerrainOptimisationTests
{
    [Fact]
    public void SlopeComparisonMatchesAtanReference()
    {
        var random = new Random(7411);
        for (var index = 0; index < 10_000; index++)
        {
            var height = random.NextDouble() * 5_000 - 1_000;
            var observer = random.NextDouble() * 2_000;
            var distance = random.NextDouble() * 499_985 + 15;
            var curvature = HorizonService.CurvatureDrop(distance);
            var slope = HorizonService.ElevationSlope(height, observer, curvature, 1d / distance);
            var reference = Math.Atan2(height - observer - curvature, distance) * Angles.RadiansToDegrees;
            Assert.Equal(reference, HorizonService.SlopeToElevationDegrees(slope), 11);
        }
    }

    [Fact]
    public async Task ParallelAndSequentialProfilesAreDeterministic()
    {
        var observer = new GeoCoordinate(53.00737, -3.94847);
        var request = new TerrainProfileRequest(72, 5_000, null, true, 1.7);
        var sequential = await new HorizonService(new MathematicalTerrain(observer),
            NullLogger<HorizonService>.Instance, 1).GetProfileAsync(observer, request, default);
        var parallel = await new HorizonService(new MathematicalTerrain(observer),
            NullLogger<HorizonService>.Instance, 4).GetProfileAsync(observer, request, default);

        for (var index = 0; index < sequential.Samples.Count; index++)
        {
            Assert.Equal(sequential.Samples[index].GroundHorizonElevationDegrees!.Value,
                parallel.Samples[index].GroundHorizonElevationDegrees!.Value, 11);
            Assert.Equal(sequential.Samples[index].EffectiveHorizonFeatureDistanceMetres,
                parallel.Samples[index].EffectiveHorizonFeatureDistanceMetres);
        }
    }

    [Fact]
    public async Task ConcurrentDecodedCacheRequestsShareOneDecode()
    {
        var decodes = 0;
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new BoundedDecodedRasterCache<string, object>(2);
        async Task<object?> Create(CancellationToken _)
        {
            Interlocked.Increment(ref decodes);
            await release.Task;
            return new object();
        }
        var requests = Enumerable.Range(0, 12)
            .Select(_ => cache.GetOrCreateAsync("tile", Create, default)).ToArray();
        release.TrySetResult(true);
        var values = await Task.WhenAll(requests);

        Assert.Equal(1, decodes);
        Assert.All(values, value => Assert.Same(values[0], value));
    }

    [Fact]
    public async Task DecodedCacheIsBoundedAndEvictsOldEntries()
    {
        var decodes = 0;
        var cache = new BoundedDecodedRasterCache<string, object>(2);
        Task<object?> Create(CancellationToken _) { decodes++; return Task.FromResult<object?>(new()); }
        await cache.GetOrCreateAsync("a", Create, default);
        await cache.GetOrCreateAsync("b", Create, default);
        await cache.GetOrCreateAsync("c", Create, default);
        await cache.GetOrCreateAsync("a", Create, default);

        Assert.Equal(4, decodes);
        Assert.Equal(2, cache.Count);
    }

    private sealed class MathematicalTerrain(GeoCoordinate origin) : ITerrainElevationProvider
    {
        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate, CancellationToken token) =>
            Task.FromResult(new EnvironmentalValue<double>(EnvironmentalDataState.Available,
                Elevation(coordinate), "terrain", "1", "Synthetic"));
        public Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates,
            CancellationToken token) => Task.FromResult(new ElevationBatchResult(EnvironmentalDataState.Available,
                coordinates.Select(point => (double?)Elevation(point)).ToArray(), "terrain", "1", "Synthetic"));
        private double Elevation(GeoCoordinate point) => 100 +
            Math.Sin(Angles.InitialBearing(origin, point) * Angles.DegreesToRadians) * 40 +
            Angles.GreatCircleDistanceMetres(origin, point) / 1_000;
    }
}
