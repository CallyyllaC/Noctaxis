using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Core.Tests;

/// <summary>
/// NX-33 (Opus R-03): partial (priority) horizon profiles are taken while workers are still writing bearings. Each sample
/// in such a profile is either a fully published bearing or its untouched placeholder, never a mixture of the two.
/// Bounded stress: one full build with four workers, sampled concurrently many times.
/// </summary>
public sealed class PartialHorizonConsistencyTests
{
    [Fact]
    public async Task PartialProfilesNeverContainAHalfWrittenBearing()
    {
        var service = new HorizonService(new SteadyGround(), NullLogger<HorizonService>.Instance, degreeOfParallelism: 4);
        var request = new TerrainProfileRequest(AzimuthSampleCount: 360, MaximumDistanceMetres: 2_000, DistanceStepMetres: 100);
        var work = service.StartProfile(new GeoCoordinate(52.0, -1.5), request, CancellationToken.None);
        var partials = 0;
        var random = new Random(33);
        while (!work.CompleteProfile.IsCompleted && partials < 400)
        {
            var bearings = Enumerable.Range(0, 8).Select(_ => random.NextDouble() * 360).ToArray();
            var profile = await work.PrioritiseBearingsAsync(bearings, CancellationToken.None);
            partials++;
            foreach (var sample in profile.Samples) AssertWhole(sample);
        }
        var complete = await work.CompleteProfile;
        Assert.All(complete.Samples, sample => Assert.NotNull(sample.TerrainHorizonElevationDegrees));
        foreach (var sample in complete.Samples) AssertWhole(sample);
        Assert.True(partials > 0);
    }

    /// <summary>A placeholder has nothing; a published bearing has its elevation, distance and sightline together.</summary>
    private static void AssertWhole(TerrainHorizonSample sample)
    {
        var parts = new object?[] { sample.TerrainHorizonElevationDegrees, sample.TerrainHorizonFeatureDistanceMetres, sample.Sightline };
        Assert.True(parts.All(part => part is null) || parts.All(part => part is not null),
            $"bearing {sample.BearingDegrees}: elevation={sample.TerrainHorizonElevationDegrees}, distance={sample.TerrainHorizonFeatureDistanceMetres}, sightline={sample.Sightline?.Count}");
    }

    /// <summary>Terrain everywhere, rising with distance, a little slowly so the build overlaps the partial profiles.</summary>
    private sealed class SteadyGround : ITerrainElevationProvider
    {
        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate, CancellationToken token) =>
            Task.FromResult(new EnvironmentalValue<double>(EnvironmentalDataState.Available, 100, "fixture", "1", "fixture"));

        public async Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken token)
        {
            await Task.Yield();
            var values = coordinates.Select((_, index) => (double?)(100 + index % 50)).ToArray();
            return new ElevationBatchResult(EnvironmentalDataState.Available, values, "fixture", "1", "fixture");
        }
    }
}
