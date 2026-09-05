using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using SkiaSharp;

namespace Noctaxis.Core.Tests;

public sealed class TerrariumTerrainProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "Noctaxis-Terrarium-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(128, 0, 0, 0)]
    [InlineData(128, 100, 128, 100.5)]
    [InlineData(127, 255, 0, -1)]
    [InlineData(85, 8, 0, -11000)]
    [InlineData(0, 0, 0, -32768)]
    [InlineData(255, 255, 255, 32767.99609375)]
    [InlineData(127, 255, 255, -0.00390625)]
    [InlineData(128, 0, 1, 0.00390625)]
    [InlineData(167, 16, 0, 10000)]
    public void DecodeElevationUsesDocumentedTerrariumFormula(
        byte red, byte green, byte blue, double expected) =>
        Assert.Equal(expected, TerrariumTerrainProvider.DecodeElevation(red, green, blue));

    [Fact]
    public void LocatePixelUsesWebMercatorOrientationAndWrapsDateline()
    {
        var equator = TerrariumTerrainProvider.LocatePixel(new GeoCoordinate(0, 0), 1);
        var north = TerrariumTerrainProvider.LocatePixel(new GeoCoordinate(60, 0), 1);
        var east = TerrariumTerrainProvider.LocatePixel(new GeoCoordinate(0, 179.999), 1);
        var west = TerrariumTerrainProvider.LocatePixel(new GeoCoordinate(0, -180), 1);
        var brigg = TerrariumTerrainProvider.LocatePixel(new GeoCoordinate(53.55865, -0.48052), 12);

        Assert.Equal(new TerrariumTileKey(1, 1, 1), equator.Tile);
        Assert.True(north.GlobalPixelY < equator.GlobalPixelY);
        Assert.Equal(1, east.Tile.X);
        Assert.Equal(0, west.Tile.X);
        Assert.Equal(new TerrariumTileKey(12, 2042, 1323), brigg.Tile);
    }

    [Fact]
    public async Task SamplesExactPixelCentreAndRetainsNegativeElevation()
    {
        var key = new TerrariumTileKey(1, 1, 1);
        Seed(key, -35);
        var provider = Provider(1);
        var coordinate = CoordinateAtGlobalPixelCentre(1, 256, 256);

        var sample = await provider.GetElevationSampleAsync(coordinate, default);

        Assert.True(sample.Value.HasValue);
        Assert.Equal(-35, sample.Value.Value);
        Assert.Equal(TerrainSampleStatus.Valid, sample.Diagnostics.Status);
        Assert.Single(sample.Diagnostics.RawSamples);
        Assert.Contains("1/1/1", sample.Diagnostics.Tile);
    }

    [Fact]
    public async Task BilinearInterpolationIsContinuousAcrossAdjacentTileBoundary()
    {
        Seed(new TerrariumTileKey(1, 0, 1), 0);
        Seed(new TerrariumTileKey(1, 1, 1), 100);
        var provider = Provider(1);
        var coordinate = CoordinateAtGlobalPixel(1, 256, 256.5);

        var sample = await provider.GetElevationSampleAsync(coordinate, default);

        Assert.Equal(50, sample.Value.Value, 8);
        Assert.Equal(2, sample.Diagnostics.RawSamples.Count);
        Assert.Contains("1/0/1", sample.Diagnostics.Tile);
        Assert.Contains("1/1/1", sample.Diagnostics.Tile);
    }

    [Fact]
    public async Task ExactTileCornerInterpolatesAllFourAdjacentTiles()
    {
        Seed(new TerrariumTileKey(2, 0, 0), 0);
        Seed(new TerrariumTileKey(2, 1, 0), 100);
        Seed(new TerrariumTileKey(2, 0, 1), 200);
        Seed(new TerrariumTileKey(2, 1, 1), 300);
        var provider = Provider(2);
        var coordinate = CoordinateAtGlobalPixel(2, 256, 256);

        var sample = await provider.GetElevationSampleAsync(coordinate, default);

        Assert.Equal(150, sample.Value.Value, 8);
        Assert.Equal(4, sample.Diagnostics.RawSamples.Count);
    }

    [Fact]
    public async Task ArbitraryInteriorPixelUsesNorthToSouthRowsAndWestToEastColumns()
    {
        var key = new TerrariumTileKey(1, 1, 1);
        SeedGradient(key);
        var provider = Provider(1);
        var coordinate = CoordinateAtGlobalPixelCentre(1, 256 + 10, 256 + 20);

        var sample = await provider.GetElevationSampleAsync(coordinate, default);

        Assert.Equal(2_010, sample.Value.Value, 8);
        var raw = Assert.Single(sample.Diagnostics.RawSamples);
        Assert.Equal(10, raw.Column);
        Assert.Equal(20, raw.Row);
    }

    [Fact]
    public async Task MissingContributingTileReturnsExplicitUnavailableState()
    {
        Seed(new TerrariumTileKey(1, 0, 1), 0);
        var provider = Provider(1);
        var coordinate = CoordinateAtGlobalPixel(1, 256, 256.5);

        var sample = await provider.GetElevationSampleAsync(coordinate, default);

        Assert.False(sample.Value.HasValue);
        Assert.Equal(EnvironmentalDataState.Unavailable, sample.Value.State);
        Assert.Equal(TerrainSampleStatus.Unavailable, sample.Diagnostics.Status);
    }

    [Fact]
    public async Task FailedTileLookupCanRetryAndDoesNotPoisonProvider()
    {
        var key = new TerrariumTileKey(1, 1, 1);
        var provider = new TerrariumTerrainProvider(new HttpClient(), new FixtureCache(_directory),
            NullLogger<TerrariumTerrainProvider>.Instance,
            new TerrariumTerrainOptions(1, 4, FailureRetryDelay: TimeSpan.Zero));
        var coordinate = CoordinateAtGlobalPixelCentre(1, 256, 256);

        var missing = await provider.GetElevationAsync(coordinate, default);
        Seed(key, 42);
        var retry = await provider.GetElevationAsync(coordinate, default);

        Assert.False(missing.HasValue);
        Assert.True(retry.HasValue);
        Assert.Equal(42, retry.Value);
    }

    [Fact]
    public async Task ConcurrentSamplesShareOneDecodedTileLoad()
    {
        var key = new TerrariumTileKey(1, 1, 1);
        Seed(key, 123);
        var cache = new FixtureCache(_directory);
        var provider = new TerrariumTerrainProvider(new HttpClient(), cache,
            NullLogger<TerrariumTerrainProvider>.Instance,
            new TerrariumTerrainOptions(1, 4));
        var coordinate = CoordinateAtGlobalPixelCentre(1, 256, 256);

        var samples = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => provider.GetElevationAsync(coordinate, default)));

        Assert.All(samples, sample => Assert.Equal(123, sample.Value));
        Assert.Equal(1, provider.Metrics.TileLoads);
        Assert.Equal(1, cache.Lookups);
    }

    [Fact]
    public void RejectsWrongSizedPng()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "bad.png");
        using var bitmap = new SKBitmap(2, 2);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using (var stream = File.Create(path)) data.SaveTo(stream);

        Assert.False(TerrariumTile.IsValid(path));
        Assert.Throws<InvalidDataException>(() => TerrariumTile.Load(path));
    }

    [Theory]
    [InlineData(256, 255.75, 50)]
    [InlineData(256, 256, 100)]
    [InlineData(256, 256.25, 150)]
    public async Task NorthSouthBoundary_InterpolatesContinuously(double x, double y, double expected)
    {
        Seed(new TerrariumTileKey(1, 0, 0), 0);
        Seed(new TerrariumTileKey(1, 1, 0), 0);
        Seed(new TerrariumTileKey(1, 0, 1), 200);
        Seed(new TerrariumTileKey(1, 1, 1), 200);
        var sample = await Provider(1).GetElevationAsync(CoordinateAtGlobalPixel(1, x, y), default);
        Assert.True(sample.HasValue);
        Assert.Equal(expected, sample.Value, 7);
    }

    [Fact]
    public async Task DatelineInterpolation_JoinsLastAndFirstTile()
    {
        Seed(new TerrariumTileKey(1, 1, 1), 0);
        Seed(new TerrariumTileKey(1, 0, 1), 100);
        var provider = Provider(1);
        foreach (var longitude in new[] { -180d, 180d, 540d, -540d })
        {
            var sample = await provider.GetElevationAsync(new GeoCoordinate(-1, longitude), default);
            Assert.True(sample.HasValue);
            Assert.Equal(50, sample.Value, 7);
        }
        Assert.Equal(2, provider.Metrics.TileLoads);
    }

    [Fact]
    public async Task InteriorFractionalSample_ReusesDecodedTileUntilNeighbourIsRequired()
    {
        SeedGradient(new TerrariumTileKey(1, 1, 1));
        var cache = new FixtureCache(_directory);
        var provider = new TerrariumTerrainProvider(new HttpClient(), cache,
            NullLogger<TerrariumTerrainProvider>.Instance, new TerrariumTerrainOptions(1, 8));
        for (var index = 0; index < 30; index++)
        {
            var sample = await provider.GetElevationAsync(CoordinateAtGlobalPixel(1, 266.75, 276.75), default);
            Assert.Equal(2035.25, sample.Value, 7);
        }
        Assert.Equal(1, cache.Lookups);
        Assert.Equal(1, provider.Metrics.TileLoads);
        Seed(new TerrariumTileKey(1, 0, 1), 0);
        await provider.GetElevationAsync(CoordinateAtGlobalPixel(1, 256, 276.75), default);
        Assert.Equal(2, cache.Lookups);
        Assert.Equal(2, provider.Metrics.TileLoads);
    }

    [Theory]
    [InlineData(0, 0, 2, 2)]
    [InlineData(60, 0, 2, 1)]
    [InlineData(-60, 0, 2, 2)]
    [InlineData(85, 179.999, 3, 0)]
    [InlineData(-85, -179.999, 0, 3)]
    [InlineData(0, -0.000001, 1, 2)]
    [InlineData(0, 0.000001, 2, 2)]
    public void GeographicAddressing_PreservesHemisphereAndBoundary(double latitude,
        double longitude, int x, int y) => Assert.Equal(new TerrariumTileKey(2, x, y),
        TerrariumTerrainProvider.LocatePixel(new GeoCoordinate(latitude, longitude), 2).Tile);

    private TerrariumTerrainProvider Provider(int zoom) => new(new HttpClient(),
        new FixtureCache(_directory), NullLogger<TerrariumTerrainProvider>.Instance,
        new TerrariumTerrainOptions(zoom, 8));

    private void Seed(TerrariumTileKey key, double elevation)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{key.Zoom}-{key.X}-{key.Y}.png");
        var (red, green, blue) = Encode(elevation);
        using var bitmap = new SKBitmap(TerrariumTerrainOptions.TileSize, TerrariumTerrainOptions.TileSize);
        bitmap.Erase(new SKColor(red, green, blue));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private void SeedGradient(TerrariumTileKey key)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{key.Zoom}-{key.X}-{key.Y}.png");
        using var bitmap = new SKBitmap(TerrariumTerrainOptions.TileSize, TerrariumTerrainOptions.TileSize);
        for (var row = 0; row < TerrariumTerrainOptions.TileSize; row++)
        for (var column = 0; column < TerrariumTerrainOptions.TileSize; column++)
        {
            var (red, green, blue) = Encode(row * 100 + column);
            bitmap.SetPixel(column, row, new SKColor(red, green, blue));
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static (byte Red, byte Green, byte Blue) Encode(double elevation)
    {
        var shifted = elevation + 32_768;
        var red = (byte)Math.Floor(shifted / 256);
        var green = (byte)Math.Floor(shifted % 256);
        var blue = (byte)Math.Round((shifted - Math.Floor(shifted)) * 256);
        return (red, green, blue);
    }

    private static GeoCoordinate CoordinateAtGlobalPixelCentre(int zoom, double x, double y) =>
        CoordinateAtGlobalPixel(zoom, x + .5, y + .5);

    private static GeoCoordinate CoordinateAtGlobalPixel(int zoom, double x, double y)
    {
        var size = TerrariumTerrainOptions.TileSize * (1 << zoom);
        var longitude = x / size * 360 - 180;
        var latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / size))) * Angles.RadiansToDegrees;
        return new GeoCoordinate(latitude, longitude);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class FixtureCache(string directory) : IEnvironmentalTileCache
    {
        private int _lookups;
        public string RootDirectory => directory;
        public int Lookups => Volatile.Read(ref _lookups);

        public Task<EnvironmentalCacheResult> GetOrCreateAsync(EnvironmentalTileDescriptor descriptor,
            Func<CancellationToken, Task<byte[]?>> acquire, Func<string, bool> validate,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EnvironmentalCacheResult> GetOrCreateDetailedAsync(EnvironmentalTileDescriptor descriptor,
            Func<CancellationToken, Task<EnvironmentalAcquisitionResult>> acquire,
            Func<string, bool> validate, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _lookups);
            var path = Path.Combine(directory, descriptor.Layer[1..] + "-" + descriptor.TileId + ".png");
            return Task.FromResult(File.Exists(path)
                ? new EnvironmentalCacheResult(EnvironmentalDataState.Cached, path, true, "Fixture")
                : new EnvironmentalCacheResult(EnvironmentalDataState.TileAbsent, null, false,
                    "Fixture tile absent", 404));
        }
    }
}
