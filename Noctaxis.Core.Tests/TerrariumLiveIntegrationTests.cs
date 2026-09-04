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
        yield return ["Brigg", 53.55865, -0.48052, -50d, 200d];
        yield return ["Blaenau Ffestiniog", 53.00563, -3.95192, 100d, 600d];
        yield return ["Irish Sea", 53.02135, -4.64836, -500d, 100d];
        yield return ["Open Atlantic", 54.28788, -13.06745, -6_000d, 100d];
        yield return ["Ben Nevis", 56.79685, -5.00351, 500d, 1_600d];
        yield return ["Schiphol", 52.3086, 4.7639, -20d, 50d];
    }

    [TerrariumLiveTheory, MemberData(nameof(Locations))]
    [Trait("Category", "LiveTerrain")]
    public async Task OfficialTerrariumSampleIsPhysicallyPlausible(string name,
        double latitude, double longitude, double minimum, double maximum)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Noctaxis-LiveTerrainTests/1.0");
        var cache = new EnvironmentalTileCache(new PlatformUserDataPathProvider(),
            NullLogger<EnvironmentalTileCache>.Instance);
        var provider = new TerrariumTerrainProvider(http, cache,
            NullLogger<TerrariumTerrainProvider>.Instance);
        var result = await provider.GetElevationSampleAsync(new GeoCoordinate(latitude, longitude), default);

        Assert.True(result.Value.HasValue, $"{name}: {result.Value.State}: {result.Value.Message}");
        Assert.InRange(result.Value.Value, minimum, maximum);
        Assert.Equal(TerrariumTerrainProvider.SourceId, result.Value.SourceId);
        Assert.All(result.Diagnostics.RawSamples,
            sample => Assert.NotEqual(TerrainSampleStatus.Error, sample.Status));
        output.WriteLine($"{name}: {result.Value.Value:F3} m; tile={result.Diagnostics.Tile}; " +
                         $"cell={result.Diagnostics.Cell}; resolution={result.Diagnostics.NativeResolutionMetres:F2} m");
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
