using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Persistence;
using Xunit.Abstractions;

namespace Noctaxis.Core.Tests;

public sealed class TerrariumLiveIntegrationTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Locations()
    {
        yield return ["Brigg", 53.55865, -0.48052, -50d, 200d, false];
        yield return ["Blaenau Ffestiniog", 53.00563, -3.95192, 100d, 600d, false];
        yield return ["Irish Sea", 53.02135, -4.64836, -500d, 100d, true];
        yield return ["Open Atlantic", 54.28788, -13.06745, -6_000d, 100d, true];
        yield return ["Ben Nevis", 56.79685, -5.00351, 500d, 1_600d, false];
        yield return ["Schiphol", 52.3086, 4.7639, -20d, 50d, false];
    }

    [TerrariumLiveTheory, MemberData(nameof(Locations))]
    [Trait("Category", "LiveTerrain")]
    public async Task OfficialTerrariumSampleIsPhysicallyPlausible(string name,
        double latitude, double longitude, double minimum, double maximum,
        bool expectedBathymetryCorrection)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Noctaxis-LiveTerrainTests/1.0");
        var cache = new EnvironmentalTileCache(new PlatformUserDataPathProvider(),
            NullLogger<EnvironmentalTileCache>.Instance);
        var provider = new TerrariumTerrainProvider(http, cache,
            NullLogger<TerrariumTerrainProvider>.Instance);
        var worldCover = new WorldCoverLandCoverProvider(http, cache,
            NullLogger<WorldCoverLandCoverProvider>.Instance);
        var resolver = new TerrainSurfaceResolver(provider, worldCover,
            NullLogger<TerrainSurfaceResolver>.Instance);
        var result = await resolver.GetSurfaceSampleAsync(new GeoCoordinate(latitude, longitude), default);

        Assert.True(result.RawTerrain.HasValue, $"{name}: {result.RawTerrain.State}: {result.RawTerrain.Message}");
        Assert.InRange(result.RawTerrain.Value, minimum, maximum);
        Assert.Equal(TerrariumTerrainProvider.SourceId, result.RawTerrain.SourceId);
        Assert.All(result.RawTerrainDiagnostics.RawSamples,
            sample => Assert.NotEqual(TerrainSampleStatus.Error, sample.Status));
        if (expectedBathymetryCorrection)
        {
            Assert.Equal(LandCoverClass.PermanentWater, result.Resolution.Classification);
            Assert.Equal(0, result.SurfaceElevation.Value, 6);
            Assert.True(result.Resolution.WasAdjusted);
        }
        else
        {
            Assert.Equal(result.RawTerrain.Value, result.SurfaceElevation.Value, 6);
            Assert.False(result.Resolution.WasAdjusted);
        }
        output.WriteLine($"{name}: raw={result.RawTerrain.Value:F3} m; resolved={result.SurfaceElevation.Value:F3} m; " +
                         $"classification={result.Resolution.Classification}; adjusted={result.Resolution.WasAdjusted}; " +
                         $"tile={result.RawTerrainDiagnostics.Tile}; resolution={result.RawTerrainDiagnostics.NativeResolutionMetres:F2} m");
    }
}

public sealed class TerrariumLiveTheoryAttribute : TheoryAttribute
{
    public TerrariumLiveTheoryAttribute()
    {
        if (System.Environment.GetEnvironmentVariable("NOCTAXIS_RUN_LIVE_TERRAIN_TESTS") != "1")
            Skip = "Set NOCTAXIS_RUN_LIVE_TERRAIN_TESTS=1 to use official Terrarium tiles.";
    }
}
