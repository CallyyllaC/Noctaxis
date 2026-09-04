using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Locations;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Terrain;
using BitMiracle.LibTiff.Classic;

namespace Noctaxis.Core.Tests;

public sealed class EnvironmentalIntelligenceTests
{
    [Fact]
    public void LocationEnvironmentCarriesOneCanonicalTerrainElevation()
    {
        var coordinate = new GeoCoordinate(51, -1);
        var environment = new LocationEnvironment(coordinate,
            Value(125, "terrain"), null, null, null, null);
        Assert.Equal(125, environment.TerrainElevation!.Value);
        Assert.Equal("terrain", environment.TerrainElevation.SourceId);
    }

    [Fact]
    public async Task HorizonUsesOnlyCanonicalTerrainProfile()
    {
        var origin = new GeoCoordinate(51, 0);
        var ground = new DirectionalElevation(origin, 0, 500);
        var service = new HorizonService(ground, NullLogger<HorizonService>.Instance);

        var profile = await service.GetProfileAsync(origin,
            new TerrainProfileRequest(8, 2_000, 500, false, 0), default);

        Assert.True(profile.HasTerrainCoverage);
        Assert.True(profile.Samples[0].TerrainHorizonElevationDegrees > 10);
        Assert.InRange(profile.Samples[6].TerrainHorizonElevationDegrees!.Value, -0.001, 0.001);
    }

    [Fact]
    public void GeoTiffRaster_DecodesAndInterpolatesIndependentGrid()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "grid.tif");
        using (var tiff = Tiff.Open(path, "w"))
        {
            tiff.SetField(TiffTag.IMAGEWIDTH, 2);
            tiff.SetField(TiffTag.IMAGELENGTH, 2);
            tiff.SetField(TiffTag.BITSPERSAMPLE, 16);
            tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
            tiff.SetField(TiffTag.ROWSPERSTRIP, 2);
            tiff.SetField(TiffTag.COMPRESSION, Compression.NONE);
            tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
            tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            tiff.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.INT);
            tiff.WriteScanline(Row(100, 200), 0);
            tiff.WriteScanline(Row(300, 400), 1);
            tiff.WriteDirectory();
        }

        var raster = GeoTiffRaster.Load(path);
        Assert.Equal(100, raster.Nearest(new GeoBounds(0, 0, 1, 1), 1, 0));
        Assert.Equal(250, raster.Interpolate(new GeoBounds(0, 0, 1, 1), .5, .5));
        var selective = GeoTiffRaster.ReadNearestBatch(path, new GeoBounds(0, 0, 1, 1),
            [new GeoCoordinate(1, 0), new GeoCoordinate(0, 1)]);
        Assert.Equal(100, selective[0]);
        Assert.Equal(400, selective[1]);

        static byte[] Row(short left, short right)
        {
            var row = new byte[4];
            BitConverter.TryWriteBytes(row.AsSpan(0, 2), left);
            BitConverter.TryWriteBytes(row.AsSpan(2, 2), right);
            return row;
        }
    }

    [Fact]
    public void GeoTiffRaster_WesternLongitudeUsesCorrectColumnAndNorthToSouthRow()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "western-grid.tif");
        using (var tiff = Tiff.Open(path, "w"))
        {
            tiff.SetField(TiffTag.IMAGEWIDTH, 2);
            tiff.SetField(TiffTag.IMAGELENGTH, 2);
            tiff.SetField(TiffTag.BITSPERSAMPLE, 16);
            tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
            tiff.SetField(TiffTag.ROWSPERSTRIP, 2);
            tiff.SetField(TiffTag.COMPRESSION, Compression.NONE);
            tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
            tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            tiff.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.INT);
            tiff.WriteScanline(SignedRow(100, 200), 0);
            tiff.WriteScanline(SignedRow(300, 400), 1);
            tiff.WriteDirectory();
        }

        var raster = GeoTiffRaster.Load(path);
        var bounds = new GeoBounds(53, -4, 54, -3);
        Assert.Equal(100, raster.Interpolate(bounds, 54, -4));
        Assert.Equal(200, raster.Interpolate(bounds, 54, -3));
        Assert.Equal(300, raster.Interpolate(bounds, 53, -4));
        Assert.Equal(175, raster.Interpolate(bounds, 53.75, -3.75));

        static byte[] SignedRow(short left, short right)
        {
            var row = new byte[4];
            BitConverter.TryWriteBytes(row.AsSpan(0, 2), left);
            BitConverter.TryWriteBytes(row.AsSpan(2, 2), right);
            return row;
        }
    }

    [Fact]
    public async Task SharedTileCache_ReusesTilesAndReplacesCorruptContentThroughStaging()
    {
        using var directory = new TemporaryDirectory();
        var cache = new EnvironmentalTileCache(new TestPaths(directory.Path),
            NullLogger<EnvironmentalTileCache>.Instance);
        var descriptor = new EnvironmentalTileDescriptor("source", "v1", "layer", "tile", "bin");
        var acquireCount = 0;
        var stagedValidationObserved = false;
        Task<byte[]?> Acquire(CancellationToken _)
        {
            acquireCount++;
            return Task.FromResult<byte[]?>([1, 2, 3, 4]);
        }
        bool Validate(string path)
        {
            if (path.EndsWith(".tmp", StringComparison.Ordinal)) stagedValidationObserved = true;
            return File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3, 4 });
        }

        var first = await cache.GetOrCreateAsync(descriptor, Acquire, Validate, default);
        var second = await cache.GetOrCreateAsync(descriptor, Acquire, Validate, default);
        Assert.True(first.IsAvailable);
        Assert.True(second.CacheHit);
        Assert.Equal(1, acquireCount);
        Assert.True(stagedValidationObserved);
        Assert.Empty(Directory.EnumerateFiles(cache.RootDirectory, "*.tmp", SearchOption.AllDirectories));

        await File.WriteAllBytesAsync(first.Path!, [9]);
        var repaired = await cache.GetOrCreateAsync(descriptor, Acquire, Validate, default);
        Assert.True(repaired.IsAvailable);
        Assert.Equal(2, acquireCount);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(first.Path!));
    }

    [Fact]
    public async Task SharedTileCache_HonoursCancellation()
    {
        using var directory = new TemporaryDirectory();
        var cache = new EnvironmentalTileCache(new TestPaths(directory.Path),
            NullLogger<EnvironmentalTileCache>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateAsync(
            new EnvironmentalTileDescriptor("source", "1", "layer", "cancel", "bin"),
            _ => Task.FromResult<byte[]?>([1]), _ => true, cancellation.Token));
    }

    [Fact]
    public async Task SharedTileCache_CoalescesConcurrentRequestsAndDoesNotCacheFailure()
    {
        using var directory = new TemporaryDirectory();
        var cache = new EnvironmentalTileCache(new TestPaths(directory.Path),
            NullLogger<EnvironmentalTileCache>.Instance);
        var descriptor = new EnvironmentalTileDescriptor("source", "v1", "layer", "shared", "bin");
        var attempts = 0;
        async Task<byte[]?> Acquire(CancellationToken token)
        {
            Interlocked.Increment(ref attempts);
            await Task.Delay(30, token);
            return [4, 2];
        }

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            cache.GetOrCreateAsync(descriptor, Acquire,
                path => File.ReadAllBytes(path).SequenceEqual(new byte[] { 4, 2 }), default)));

        Assert.Equal(1, attempts);
        Assert.All(concurrent, result => Assert.True(result.IsAvailable));

        var failureDescriptor = descriptor with { TileId = "retry" };
        var failure = await cache.GetOrCreateAsync(failureDescriptor,
            _ => throw new IOException("Offline"), _ => true, default);
        var retry = await cache.GetOrCreateAsync(failureDescriptor,
            _ => Task.FromResult<byte[]?>([7]), path => File.ReadAllBytes(path).SequenceEqual(new byte[] { 7 }),
            default);
        Assert.Equal(EnvironmentalDataState.Unavailable, failure.State);
        Assert.True(retry.IsAvailable);
    }

    [Fact]
    public async Task PlannerEnvironment_AttachesWorldCoverAndIsolatesSettlementFailure()
    {
        var origin = new GeoCoordinate(51, 0);
        var horizons = new HorizonService(new DirectionalElevation(origin, 0, 500),
            NullLogger<HorizonService>.Instance);
        var landCover = new BuiltUpLandCover();
        var service = new PlannerEnvironmentService(horizons, landCover,
            new ThrowingSettlement(), NullLogger<PlannerEnvironmentService>.Instance);

        var snapshot = await service.GetSnapshotAsync(origin, default);

        Assert.Equal(origin, snapshot.ObserverCoordinate);
        Assert.Equal(LandCoverClass.BuiltUp, snapshot.LandCover.Value);
        Assert.Equal(EnvironmentalDataState.Error, snapshot.Settlement.State);
        Assert.True(snapshot.HorizonProfile.HasTerrainCoverage);
        Assert.Contains(snapshot.HorizonProfile.Samples,
            sample => sample.LandCover == LandCoverClass.BuiltUp);
        Assert.InRange(landCover.RequestedCoordinates, 1, snapshot.HorizonProfile.Samples.Count + 1);
    }

    [Fact]
    public void LocationExport_RoundTripsMetadataWithoutEnvironmentalCaches()
    {
        var location = new SavedLocation(Guid.NewGuid(), "Dark Ridge", new GeoCoordinate(51.2, -1.3, 85),
            "Europe/London", "North-facing view", PreferredSensor: SensorPreset.FullFrame,
            IsFavourite: true, RegionDescription: "Wiltshire");
        var service = new LocationTransferService();

        var bytes = service.Export([location]);
        var imported = service.Import(bytes);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Equal(LocationTransferService.CurrentSchemaVersion, imported.SchemaVersion);
        Assert.Equal(location, Assert.Single(imported.Locations));
        Assert.DoesNotContain("EnvironmentalData", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("thumbnail", json, StringComparison.OrdinalIgnoreCase);
    }

    private static EnvironmentalValue<double> Value(double value, string source) =>
        new(EnvironmentalDataState.Available, value, source, "1", "test");

    private sealed class DirectionalElevation(GeoCoordinate origin, double bearing, double peak)
        : ITerrainElevationProvider
    {
        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate,
            CancellationToken cancellationToken) => Task.FromResult(Value(Elevation(coordinate), "ground"));
        public Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates,
            CancellationToken cancellationToken) => Task.FromResult(new ElevationBatchResult(
            EnvironmentalDataState.Available, coordinates.Select(Elevation).Cast<double?>().ToArray(),
            "ground", "1", "test"));
        private double Elevation(GeoCoordinate coordinate)
        {
            var distance = Angles.GreatCircleDistanceMetres(origin, coordinate);
            var direction = distance < 1 ? bearing : Angles.InitialBearing(origin, coordinate);
            return distance is > 900 and < 1_100 && Difference(direction, bearing) < 15 ? peak : 0;
        }
    }

    private sealed class BuiltUpLandCover : ILandCoverProvider
    {
        public int RequestedCoordinates { get; private set; }
        public Task<EnvironmentalValue<LandCoverClass>> GetLandCoverAsync(GeoCoordinate coordinate,
            CancellationToken cancellationToken) => Task.FromResult(new EnvironmentalValue<LandCoverClass>(
            EnvironmentalDataState.Available, LandCoverClass.BuiltUp, "cover", "1", "Synthetic"));
        public Task<LandCoverBatchResult> GetLandCoversAsync(IReadOnlyList<GeoCoordinate> coordinates,
            CancellationToken cancellationToken)
        {
            RequestedCoordinates += coordinates.Count;
            return Task.FromResult(new LandCoverBatchResult(EnvironmentalDataState.Available,
                coordinates.Select(_ => (LandCoverClass?)LandCoverClass.BuiltUp).ToArray(),
                "cover", "1", "Synthetic"));
        }
    }

    private sealed class ThrowingSettlement : ISettlementDataProvider
    {
        public Task<EnvironmentalValue<SettlementRaster>> GetSettlementAsync(GeoRasterRequest request,
            CancellationToken cancellationToken) => throw new IOException("Synthetic failure");
    }

    private static double Difference(double left, double right)
    {
        var difference = Math.Abs(left - right) % 360;
        return Math.Min(difference, 360 - difference);
    }

    private sealed class TestPaths(string path) : IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Noctaxis.Environment.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
