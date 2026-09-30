using Noctaxis.Core.LightPollution;

namespace Noctaxis.Desktop.Tests;

public sealed class LightPollutionRegionTests
{
    [Theory]
    [InlineData(8, 127, 84)]  // crossing Greenwich, northern England
    [InlineData(5, 15, 10)]   // several source tiles wide
    [InlineData(6, 63, 30)]   // the antimeridian: source columns wrap
    [InlineData(5, 20, 5)]    // reaching past the atlas's northern edge
    [InlineData(3, 4, 2)]     // the overview, used at low zoom
    public async Task ARegionReadOnceSamplesExactlyAsTheAtlasDoesWithoutHoldingIt(int zoom, int column, int row)
    {
        await using var paths = new AtlasTestData();
        using var http = new HttpClient(new LightPollutionInstallationTests.Handler((_, _) => throw new InvalidOperationException("No network")));
        var store = new LorenzInstallation(paths, http);
        await paths.SeedAsync(store);
        await using var provider = new LightPollutionDataProvider(store);
        var n = Math.Pow(2, zoom);
        static double Latitude(double y, double tiles) => Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / tiles))) * 180 / Math.PI;
        var overview = zoom <= 4;
        var latitudes = Enumerable.Range(0, 33).Select(i => Latitude(row + (i + .5) / 33, n)).ToArray();
        var longitudes = Enumerable.Range(0, 33).Select(i => (column + (i + .5) / 33) / n * 360 - 180).ToArray();

        LightPollutionDataProvider.Region region;
        var expected = new double?[latitudes.Length * longitudes.Length];
        using (var session = (await provider.OpenAsync())!)
        {
            region = await session.ReadRegionAsync(Latitude(row + 1, n), Latitude(row, n), column / n * 360 - 180, (column + 1) / n * 360 - 180, overview);
            for (var y = 0; y < latitudes.Length; y++)
                for (var x = 0; x < longitudes.Length; x++)
                    expected[y * longitudes.Length + x] = await session.SampleAsync(latitudes[y], longitudes[x], bilinear: true, overview: overview);
        }

        // The lease is released: the region is sampled from its own data.
        var grid = new double[expected.Length];
        region.SampleGrid(latitudes, longitudes, grid);
        for (var y = 0; y < latitudes.Length; y++)
            for (var x = 0; x < longitudes.Length; x++)
            {
                var i = y * longitudes.Length + x;
                Assert.Equal(expected[i], region.Sample(latitudes[y], longitudes[x], bilinear: true));
                Assert.Equal(expected[i] ?? double.NaN, grid[i]);
            }
        Assert.Contains(expected, value => value is not null);
    }
}
