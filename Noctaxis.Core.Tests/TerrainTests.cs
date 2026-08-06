using System.Buffers.Binary;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Terrain;

namespace Noctaxis.Core.Tests;

public sealed class TerrainTests
{
    [Fact]
    public void HgtParser_ReadsBigEndianSignedSamplesAndInterpolates()
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(0, 2), 100);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(2, 2), 200);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(4, 2), -50);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(6, 2), 150);
        var tile = HgtTile.FromBytes(51, -1, bytes, 2);
        Assert.Equal(100, tile.Interpolate(52, -1));
        Assert.Equal(100, tile.Interpolate(51.5, -0.5));
        Assert.Equal((51, -1), HgtTile.ParseTileName("N51W001"));
        Assert.Equal("S01W001", HgtTile.GetTileName(-0.1, -0.1));
    }

    [Fact]
    public void HgtParser_AcceptsCommon1201Resolution()
    {
        var bytes = new byte[1201 * 1201 * 2];
        var tile = HgtTile.FromBytes(0, 0, bytes);
        Assert.Equal(1201, tile.Size);
        Assert.Equal(0, tile.Interpolate(0.5, 0.5));
    }

    [Fact]
    public async Task SyntheticMountain_CreatesPositiveNorthernHorizon()
    {
        var origin = new GeoCoordinate(51, 0, 0);
        var provider = new SrtmTerrainHorizonProvider(new SyntheticElevation(origin));
        var profile = await provider.GetProfileAsync(origin, new TerrainProfileRequest(8, 5_000, 500, false), CancellationToken.None);
        Assert.True(profile.HasDemCoverage);
        Assert.InRange(profile.Samples[0].AltitudeDegrees, 20, 30);
        Assert.Equal(0, profile.Samples[4].AltitudeDegrees);
    }

    [Fact]
    public async Task MissingCoverage_ReturnsHonestFlatProfile()
    {
        var provider = new SrtmTerrainHorizonProvider(new MissingElevation());
        var profile = await provider.GetProfileAsync(new GeoCoordinate(51, 0), new TerrainProfileRequest(8, 2_000, 500), CancellationToken.None);
        Assert.False(profile.HasDemCoverage);
        Assert.Contains("No SRTM coverage", profile.Status);
        Assert.All(profile.Samples, x => Assert.Equal(0, x.AltitudeDegrees));
    }

    private sealed class SyntheticElevation(GeoCoordinate origin) : IElevationSource
    {
        public ValueTask<double?> GetElevationMetresAsync(GeoCoordinate coordinate, CancellationToken cancellationToken)
        {
            var distance = Angles.GreatCircleDistanceMetres(origin, coordinate);
            var bearing = distance < 1 ? 0 : Angles.InitialBearing(origin, coordinate);
            return ValueTask.FromResult<double?>(distance is > 1_800 and < 2_200 && (bearing < 10 || bearing > 350) ? 1000 : 0);
        }
    }

    private sealed class MissingElevation : IElevationSource
    {
        public ValueTask<double?> GetElevationMetresAsync(GeoCoordinate coordinate, CancellationToken cancellationToken) => ValueTask.FromResult<double?>(null);
    }
}
