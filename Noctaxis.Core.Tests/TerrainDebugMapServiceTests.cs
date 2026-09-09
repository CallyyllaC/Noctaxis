using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Core.Tests;

public sealed class TerrainDebugMapServiceTests
{
    [Theory]
    [InlineData(0, 0, 10_000)]
    [InlineData(90, 10_000, 0)]
    [InlineData(180, 0, -10_000)]
    [InlineData(270, -10_000, 0)]
    public void LocalProjectionKeepsCardinalBearingsNorthUp(double bearing,
        double expectedEast, double expectedNorth)
    {
        var observer = new GeoCoordinate(53.6, .14);
        var coordinate = Angles.Destination(observer, bearing, 10_000);
        var local = LocalTerrainMapProjection.ToLocalMetres(observer, coordinate);

        Assert.InRange(local.EastMetres, expectedEast - 15, expectedEast + 15);
        Assert.InRange(local.NorthMetres, expectedNorth - 15, expectedNorth + 15);
    }

    [Fact]
    public async Task BoundedGridUsesOneBulkProductionResolverPass()
    {
        var resolver = new CountingSurfaceResolver();
        var service = new TerrainDebugMapService(resolver);
        var observer = new GeoCoordinate(53.615275, .140637);

        var map = await service.GetMapAsync(observer,
            new TerrainDebugMapRequest(20_000, 128, 128), default);

        Assert.Equal(observer, map.Observer);
        Assert.Equal(16_384, map.CellCount);
        Assert.Equal(1, resolver.PreloadCalls);
        Assert.Equal(1, resolver.ClassificationCalls);
        Assert.Equal(1, resolver.ElevationCalls);
        Assert.Equal(map.CellCount, resolver.CoordinatesPerCall);
        Assert.NotEmpty(map.TerrariumTiles);
    }

    [Fact]
    public async Task GridRowsAndColumnsFollowGeographicAxes()
    {
        var observer = new GeoCoordinate(53.6, .14);
        var request = new TerrainDebugMapRequest(20_000, 16, 16);
        var map = await new TerrainDebugMapService(new CountingSurfaceResolver())
            .GetMapAsync(observer, request, default);
        var grid = map.Coordinates;
        var northWest = LocalTerrainMapProjection.ToLocalMetres(observer, grid[0]);
        var southEast = LocalTerrainMapProjection.ToLocalMetres(observer, grid[^1]);

        Assert.True(northWest.NorthMetres > 0);
        Assert.True(northWest.EastMetres < 0);
        Assert.True(southEast.NorthMetres < 0);
        Assert.True(southEast.EastMetres > 0);
    }

    [Fact]
    public async Task CompletedMapSnapshot_OwnsImmutableResolvedSurfaceData()
    {
        var resolver = new CountingSurfaceResolver();
        var map = await new TerrainDebugMapService(resolver).GetMapAsync(new GeoCoordinate(53, -1),
            new TerrainDebugMapRequest(), default);
        Assert.Equal(20000, map.RangeMetres);
        Assert.Equal(128, map.Width);
        Assert.Equal(128, map.Height);
        resolver.LastValues![0] = -3000;
        Assert.Equal(25, map.SurfaceElevationsMetres[0]);
        Assert.False(map.Coordinates is GeoCoordinate[]);
        Assert.False(map.SurfaceElevationsMetres is double?[]);
        Assert.False(map.RawTerrainElevationsMetres is double?[]);
    }

    [Fact]
    public async Task RepeatedMapReusesOneImmutableSnapshotAndGenerationInvalidatesIt()
    {
        var resolver = new CountingSurfaceResolver();
        var cache = new GenerationCache();
        var service = new TerrainDebugMapService(resolver, cache);
        var observer = new GeoCoordinate(53, -1);
        var first = await service.GetMapAsync(observer, new(), default);
        resolver.LastValues![0] = -999;
        Assert.Same(first, await service.GetMapAsync(observer, new(), default));
        Assert.Equal(25, first.SurfaceElevationsMetres[0]);
        Assert.Equal(1, resolver.ElevationCalls);
        cache.Generation++;
        Assert.NotSame(first, await service.GetMapAsync(observer, new(), default));
        Assert.Equal(2, resolver.ElevationCalls);
        await service.GetMapAsync(observer with { Latitude = 54 }, new(), default);
        await service.GetMapAsync(observer, new(), default);
        Assert.Equal(4, resolver.ElevationCalls); // Only the most recent map is retained.
        await service.GetMapAsync(observer, new(10000, 16, 16), default);
        Assert.Equal(5, resolver.ElevationCalls);
    }

    private sealed class GenerationCache : IEnvironmentalTileCache
    {
        public long Generation;
        public long TerrainGeneration => Generation;
        public string RootDirectory => "unused";
        public Task<EnvironmentalCacheResult> GetOrCreateAsync(EnvironmentalTileDescriptor descriptor,
            Func<CancellationToken, Task<byte[]?>> acquire, Func<string, bool> validate, CancellationToken token) =>
            throw new NotSupportedException();
        public Task<EnvironmentalCacheResult> GetOrCreateDetailedAsync(EnvironmentalTileDescriptor descriptor,
            Func<CancellationToken, Task<EnvironmentalAcquisitionResult>> acquire, Func<string, bool> validate, CancellationToken token) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task MapWithUnavailableClassificationRemainsRetryable()
    {
        var resolver = new CountingSurfaceResolver { ClassificationState = EnvironmentalDataState.Unavailable };
        var service = new TerrainDebugMapService(resolver);
        var observer = new GeoCoordinate(53, -1);
        var incomplete = await service.GetMapAsync(observer, new(), default);
        resolver.ClassificationState = EnvironmentalDataState.Available;
        var complete = await service.GetMapAsync(observer, new(), default);
        Assert.NotSame(incomplete, complete);
        Assert.Equal(2, resolver.ClassificationCalls);
        Assert.Same(complete, await service.GetMapAsync(observer, new(), default));
    }

    private sealed class CountingSurfaceResolver : ITerrainSurfaceResolver
    {
        public EnvironmentalDataState ClassificationState { get; set; } = EnvironmentalDataState.Available;
        public double?[]? LastValues { get; private set; }
        public int PreloadCalls { get; private set; }
        public int ClassificationCalls { get; private set; }
        public int ElevationCalls { get; private set; }
        public int CoordinatesPerCall { get; private set; }

        public Task PreloadAsync(IReadOnlyList<GeoCoordinate> coordinates, CancellationToken cancellationToken)
        {
            PreloadCalls++;
            CoordinatesPerCall = coordinates.Count;
            return Task.CompletedTask;
        }

        public Task<TerrainSurfaceClassificationBatch> GetClassificationsAsync(
            IReadOnlyList<GeoCoordinate> coordinates, CancellationToken cancellationToken)
        {
            ClassificationCalls++;
            return Task.FromResult(new TerrainSurfaceClassificationBatch(ClassificationState,
                Enumerable.Repeat<LandCoverClass?>(LandCoverClass.Grassland, coordinates.Count).ToArray(),
                new TerrainWaterBodyKind[coordinates.Count], "Synthetic"));
        }

        public Task<TerrainSurfaceBatchResult> GetSurfaceElevationsAsync(
            IReadOnlyList<GeoCoordinate> coordinates, TerrainSurfaceClassificationBatch classifications,
            CancellationToken cancellationToken)
        {
            ElevationCalls++;
            var values = Enumerable.Repeat<double?>(25, coordinates.Count).ToArray();
            LastValues = values;
            return Task.FromResult(new TerrainSurfaceBatchResult(EnvironmentalDataState.Available,
                values, values, classifications.Classifications,
                Enumerable.Repeat(TerrainSurfaceResolutionReason.RawTerrainLand, coordinates.Count).ToArray(),
                new bool[coordinates.Count], new TerrainSampleStatus[coordinates.Count], "Synthetic"));
        }

        public Task<TerrainSurfaceSampleResult> GetSurfaceSampleAsync(GeoCoordinate coordinate,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TerrainSurfaceBatchResult> GetSurfaceElevationsAsync(
            IReadOnlyList<GeoCoordinate> coordinates, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
