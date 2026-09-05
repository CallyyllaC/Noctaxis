using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Core.Tests;

public sealed class TerrainSurfaceResolverTests
{
    [Theory]
    [InlineData(120, LandCoverClass.Cropland, null, 120, false,
        TerrainSurfaceResolutionReason.RawTerrainLand)]
    [InlineData(0, LandCoverClass.Cropland, null, 0, false,
        TerrainSurfaceResolutionReason.RawTerrainLand)]
    [InlineData(250, LandCoverClass.PermanentWater, TerrainWaterBodyKind.InlandWater, 250, false,
        TerrainSurfaceResolutionReason.WaterElevationPreserved)]
    [InlineData(-20, LandCoverClass.BareOrSparseVegetation, null, -20, false,
        TerrainSurfaceResolutionReason.RawTerrainLand)]
    [InlineData(-35, LandCoverClass.PermanentWater, null, 0, true,
        TerrainSurfaceResolutionReason.PermanentWaterBathymetryAdjustedToMeanSeaLevel)]
    [InlineData(-3000, LandCoverClass.PermanentWater, null, 0, true,
        TerrainSurfaceResolutionReason.PermanentWaterBathymetryAdjustedToMeanSeaLevel)]
    [InlineData(250, LandCoverClass.PermanentWater, null, 250, false,
        TerrainSurfaceResolutionReason.WaterElevationPreserved)]
    [InlineData(0, LandCoverClass.PermanentWater, null, 0, false,
        TerrainSurfaceResolutionReason.WaterElevationPreserved)]
    [InlineData(-430, LandCoverClass.PermanentWater, TerrainWaterBodyKind.InlandWater, -430, false,
        TerrainSurfaceResolutionReason.WaterElevationPreserved)]
    public void ResolvePreservesLandAndInlandWaterButCorrectsBathymetry(double raw,
        LandCoverClass classification, TerrainWaterBodyKind? waterKind, double expected,
        bool adjusted, TerrainSurfaceResolutionReason reason)
    {
        var result = TerrainSurfaceResolver.Resolve(raw, classification, waterKind);

        Assert.Equal(expected, result.SurfaceElevationMetres);
        Assert.Equal(adjusted, result.WasAdjusted);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public async Task GenuineZeroIsAResolvedWaterSurface()
    {
        var resolver = Resolver(new ConstantTerrain(0), new ConstantCover(LandCoverClass.PermanentWater));

        var result = await resolver.GetSurfaceSampleAsync(new GeoCoordinate(51, -2), default);

        Assert.True(result.SurfaceElevation.HasValue);
        Assert.Equal(EnvironmentalDataState.Water, result.SurfaceElevation.State);
        Assert.Equal(0, result.SurfaceElevation.Value);
        Assert.False(result.Resolution.WasAdjusted);
    }

    [Fact]
    public async Task HorizonUsesResolvedSurfaceAcrossLandWaterLandRay()
    {
        var terrain = new FunctionTerrain(coordinate => coordinate.Longitude switch
        {
            >= .005 and <= .02 => -100,
            > .02 => 50,
            _ => 10
        });
        var cover = new FunctionCover(coordinate => coordinate.Longitude is >= .005 and <= .02
            ? LandCoverClass.PermanentWater : LandCoverClass.Grassland);
        var horizon = new HorizonService(Resolver(terrain, cover), NullLogger<HorizonService>.Instance, 1);

        var profile = await horizon.GetProfileAsync(new GeoCoordinate(0, 0),
            new TerrainProfileRequest(8, 3_000, 1_000, AccountForEarthCurvature: false,
                ObserverHeightAboveGroundMetres: 0), default);

        var east = profile.SightlineAt(90);
        Assert.Equal([-100d, -100d, 50d], east.Select(sample => sample.RawTerrainElevationMetres));
        Assert.Equal([0d, 0d, 50d], east.Select(sample => sample.GroundElevationMetres));
        Assert.All(east.Take(2), sample =>
        {
            Assert.Equal(LandCoverClass.PermanentWater, sample.Classification);
            Assert.True(sample.SurfaceWasAdjusted);
            Assert.Equal(TerrainSampleStatus.Water, sample.Status);
        });
        Assert.Equal(3_000, profile.Samples.Single(sample => sample.BearingDegrees == 90)
            .TerrainHorizonFeatureDistanceMetres);
    }

    [Fact]
    public async Task CoastalBathymetryProducesNegativeHorizonWithoutOccultingPositiveSightline()
    {
        var observer = new GeoCoordinate(53.615275, .140637);
        var terrain = new FunctionTerrain(coordinate =>
            Angles.GreatCircleDistanceMetres(observer, coordinate) < 1 ? 4.147 : -14.13);
        var cover = new FunctionCover(coordinate =>
            Angles.GreatCircleDistanceMetres(observer, coordinate) < 1
                ? LandCoverClass.Grassland
                : LandCoverClass.PermanentWater);
        var horizon = new HorizonService(Resolver(terrain, cover),
            NullLogger<HorizonService>.Instance, 1);

        var profile = await horizon.GetProfileAsync(observer,
            new TerrainProfileRequest(8, 9_300, 9_300,
                ObserverHeightAboveGroundMetres: 1.7), default);
        var north = profile.Samples.Single(sample => sample.BearingDegrees == 0);
        var radial = Assert.Single(north.Sightline!);

        Assert.Equal(-14.13, radial.RawTerrainElevationMetres!.Value, 6);
        Assert.Equal(0, radial.GroundElevationMetres!.Value, 6);
        Assert.True(radial.SurfaceWasAdjusted);
        Assert.InRange(north.TerrainHorizonElevationDegrees!.Value, -.09, -.05);
        Assert.Null(profile.TerrainObstructionAt(0).EffectiveFirstObstructionDistanceMetres);
        Assert.Null(profile.OccultationAt(0, 5).EffectiveFirstObstructionDistanceMetres);
    }

    [Fact]
    public async Task FullProfileClassifiesInBatchesRatherThanPerTerrainSample()
    {
        var cover = new CountingCover(LandCoverClass.Grassland);
        var horizon = new HorizonService(Resolver(new ConstantTerrain(10), cover),
            NullLogger<HorizonService>.Instance, 6);

        var profile = await horizon.GetProfileAsync(new GeoCoordinate(53, -1),
            new TerrainProfileRequest(MaximumDistanceMetres: 1_000), default);

        var terrainSamples = profile.Samples.Sum(sample => sample.Sightline?.Count ?? 0);
        Assert.Equal(24_120, terrainSamples);
        Assert.Equal(1, cover.BatchCalls);
        Assert.Equal(terrainSamples, cover.BatchCoordinates);
        Assert.Equal(1, cover.SingleCalls);
    }

    [Fact]
    public async Task ExplicitlyAbsentWorldCoverTileMeansOpenOceanWithinMappedExtent()
    {
        var provider = new WorldCoverLandCoverProvider(new HttpClient(), new AbsentWorldCoverCache(),
            NullLogger<WorldCoverLandCoverProvider>.Instance);

        var atlantic = await provider.GetLandCoverAsync(new GeoCoordinate(54.28788, -13.06745), default);
        var antarctica = await provider.GetLandCoverAsync(new GeoCoordinate(-75, 0), default);

        Assert.True(atlantic.HasValue);
        Assert.Equal(EnvironmentalDataState.Water, atlantic.State);
        Assert.Equal(LandCoverClass.PermanentWater, atlantic.Value);
        Assert.False(antarctica.HasValue);
    }

    [Theory]
    [InlineData(-3000)]
    [InlineData(0)]
    [InlineData(120)]
    public async Task ClassificationUnavailable_PreservesRawTerrainAndFailureDiagnostics(double elevation)
    {
        var result = await Resolver(new ConstantTerrain(elevation), new UnavailableCover())
            .GetSurfaceSampleAsync(new GeoCoordinate(51, -2), default);
        Assert.True(result.SurfaceElevation.HasValue);
        Assert.Equal(elevation, result.SurfaceElevation.Value);
        Assert.False(result.Resolution.WasAdjusted);
        Assert.Null(result.Resolution.Classification);
        Assert.Equal(TerrainSurfaceResolutionReason.RawTerrainClassificationUnavailable, result.Resolution.Reason);
        Assert.Equal(EnvironmentalDataState.Unavailable, result.Classification.State);
        Assert.Equal("Classification fixture offline", result.Classification.Message);
    }

    [Fact]
    public async Task LocalTerrainMap_UsesProductionResolvedOceanSurfaceRatherThanBathymetry()
    {
        var service = new TerrainDebugMapService(Resolver(new ConstantTerrain(-3000),
            new ConstantCover(LandCoverClass.PermanentWater)));
        var map = await service.GetMapAsync(new GeoCoordinate(53, -1), new TerrainDebugMapRequest(), default);
        Assert.All(map.RawTerrainElevationsMetres, value => Assert.Equal(-3000, value));
        Assert.All(map.SurfaceElevationsMetres, value => Assert.Equal(0, value));
        Assert.All(map.AdjustedSamples, Assert.True);
        Assert.All(map.SampleStatuses, status => Assert.Equal(TerrainSampleStatus.Water, status));
    }

    private sealed class UnavailableCover : ILandCoverProvider
    {
        public Task<EnvironmentalValue<LandCoverClass>> GetLandCoverAsync(GeoCoordinate coordinate,
            CancellationToken token) => Task.FromResult(EnvironmentalValue<LandCoverClass>.Unavailable(
                "worldcover", "test", "Classification fixture offline"));
    }

    private static TerrainSurfaceResolver Resolver(ITerrainElevationProvider terrain,
        ILandCoverProvider cover) => new(terrain, cover, NullLogger<TerrainSurfaceResolver>.Instance);

    private sealed class ConstantTerrain(double elevation) : FunctionTerrain(_ => elevation);

    private class FunctionTerrain(Func<GeoCoordinate, double> value) : ITerrainElevationProvider
    {
        public Task<EnvironmentalValue<double>> GetElevationAsync(GeoCoordinate coordinate,
            CancellationToken cancellationToken) => Task.FromResult(new EnvironmentalValue<double>(
            EnvironmentalDataState.Available, value(coordinate), TerrariumTerrainProvider.SourceId,
            TerrariumTerrainProvider.SourceVersion, "Synthetic"));

        public Task<ElevationBatchResult> GetElevationsAsync(IReadOnlyList<GeoCoordinate> coordinates,
            CancellationToken cancellationToken) => Task.FromResult(new ElevationBatchResult(
            EnvironmentalDataState.Available, coordinates.Select(coordinate => (double?)value(coordinate)).ToArray(),
            TerrariumTerrainProvider.SourceId, TerrariumTerrainProvider.SourceVersion, "Synthetic"));
    }

    private sealed class ConstantCover(LandCoverClass classification) : FunctionCover(_ => classification);

    private class FunctionCover(Func<GeoCoordinate, LandCoverClass> value) : ILandCoverProvider
    {
        public virtual Task<EnvironmentalValue<LandCoverClass>> GetLandCoverAsync(GeoCoordinate coordinate,
            CancellationToken cancellationToken) => Task.FromResult(new EnvironmentalValue<LandCoverClass>(
            EnvironmentalDataState.Available, value(coordinate), "worldcover", "test", "Synthetic"));

        public virtual Task<LandCoverBatchResult> GetLandCoversAsync(IReadOnlyList<GeoCoordinate> coordinates,
            CancellationToken cancellationToken) => Task.FromResult(new LandCoverBatchResult(
            EnvironmentalDataState.Available,
            coordinates.Select(coordinate => (LandCoverClass?)value(coordinate)).ToArray(),
            "worldcover", "test", "Synthetic"));
    }

    private sealed class CountingCover(LandCoverClass classification) : FunctionCover(_ => classification)
    {
        private int _singleCalls;
        private int _batchCalls;
        private int _batchCoordinates;
        public int SingleCalls => Volatile.Read(ref _singleCalls);
        public int BatchCalls => Volatile.Read(ref _batchCalls);
        public int BatchCoordinates => Volatile.Read(ref _batchCoordinates);

        public override Task<EnvironmentalValue<LandCoverClass>> GetLandCoverAsync(GeoCoordinate coordinate,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _singleCalls);
            return base.GetLandCoverAsync(coordinate, cancellationToken);
        }

        public override Task<LandCoverBatchResult> GetLandCoversAsync(IReadOnlyList<GeoCoordinate> coordinates,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _batchCalls);
            Interlocked.Add(ref _batchCoordinates, coordinates.Count);
            return base.GetLandCoversAsync(coordinates, cancellationToken);
        }
    }

    private sealed class AbsentWorldCoverCache : IEnvironmentalTileCache
    {
        public string RootDirectory => "memory";

        public Task<EnvironmentalCacheResult> GetOrCreateAsync(EnvironmentalTileDescriptor descriptor,
            Func<CancellationToken, Task<byte[]?>> acquire, Func<string, bool> validate,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EnvironmentalCacheResult> GetOrCreateDetailedAsync(
            EnvironmentalTileDescriptor descriptor,
            Func<CancellationToken, Task<EnvironmentalAcquisitionResult>> acquire,
            Func<string, bool> validate, CancellationToken cancellationToken) => Task.FromResult(
            new EnvironmentalCacheResult(EnvironmentalDataState.TileAbsent, null, false,
                "Official source returned 404.", 404));
    }
}
