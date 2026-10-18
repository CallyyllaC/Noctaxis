using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Locations.Descriptions;
using Noctaxis.Desktop.Mapping.Vector;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Location descriptions from OpenFreeMap tiles replace public Nominatim reverse geocoding: named features come from
/// the tiles the Planner draws, through the shared repository, from a small fixed block of tiles around the pin.
/// </summary>
public sealed class OpenFreeMapGeographicFeatureSourceTests
{
    private const int Column = 4086, Row = 2659;
    // The centre of one zoom-13 tile (about 2.9 km across here; one tile unit is about 0.7 m).
    private static readonly GeoCoordinate Observer = Centre(Column, Row);

    [Fact]
    public async Task NamedFeaturesAreDiscoveredFromTheTilesAroundThePosition()
    {
        var fetcher = new ZoomFetcher(ContextTile, LandmarkTile);
        var source = new OpenFreeMapGeographicFeatureSource(new VectorTileRepository(fetcher));

        var set = await source.GetFeaturesAsync(Observer, CancellationToken.None);

        Assert.Equal(GeographicCoverage.Complete, set.Coverage);
        Assert.Contains("OpenStreetMap", set.Attribution);
        // From the middle of the 3×3 block the tiles reach one and a half tiles (about 4.4 km) in every direction.
        Assert.InRange(set.CoverageRadiusMetres, 4000, 4700);
        Assert.Equal(9, fetcher.Requests.Count(key => key.Zoom == 13));
        Assert.Equal(4, fetcher.Requests.Count(key => key.Zoom == 14));
        Assert.Contains(set.Features, f => f.Kind == GeographicFeatureKind.River && f.Name == "River Ancholme");
        Assert.Contains(set.Features, f => f.Kind == GeographicFeatureKind.Village && f.Name == "Saxby");
        Assert.Contains(set.Features, f => f.Kind == GeographicFeatureKind.Landmark && f.Name == "Saxby Castle");
        Assert.Contains(set.Features, f => f.Kind == GeographicFeatureKind.BuiltUpArea);
        // The river crosses the block's three tile rows; it is one feature, joined across the tile edges.
        var river = Assert.Single(set.Features, f => f.Kind == GeographicFeatureKind.River);
        Assert.Single(river.Parts);
        // A label repeated in each tile's buffer is one place.
        Assert.Single(set.Features, f => f.Name == "Saxby");
        // Unnamed and unsupported features are not candidates.
        Assert.DoesNotContain(set.Features, f => f.Name == "Bus Stop");
    }

    [Fact]
    public async Task TheDescriptionNamesTheRiverAndTheVillageOutsideItsBuiltUpLand()
    {
        var service = new LocationDescriptionService(
            new OpenFreeMapGeographicFeatureSource(new VectorTileRepository(new ZoomFetcher(ContextTile, LandmarkTile))),
            NullLogger<LocationDescriptionService>.Instance);

        var description = await service.DescribeAsync(Observer, CancellationToken.None);

        Assert.NotNull(description);
        Assert.Equal("Near the River Ancholme, outside Saxby", description.Text);
        Assert.Equal(GeographicFeatureKind.River, description.Primary.Kind);
        Assert.InRange(description.Primary.DistanceMetres, 250, 350);
    }

    [Fact]
    public async Task UnavailableTilesGivePartialOrNoCoverageAndNothingIsInvented()
    {
        // The tile east of the position cannot be read: what remains is real, but the reach stops at that tile.
        var partial = new OpenFreeMapGeographicFeatureSource(new VectorTileRepository(new ZoomFetcher(
            (x, y) => x == Column + 1 && y == Row ? null : ContextTile(x, y), LandmarkTile)));
        var set = await partial.GetFeaturesAsync(Observer, CancellationToken.None);
        Assert.Equal(GeographicCoverage.Partial, set.Coverage);
        Assert.InRange(set.CoverageRadiusMetres, 1200, 1600);

        var offline = new OpenFreeMapGeographicFeatureSource(new VectorTileRepository(new ZoomFetcher((_, _) => null, (_, _) => null)));
        var none = await offline.GetFeaturesAsync(Observer, CancellationToken.None);
        Assert.Equal(GeographicCoverage.Unavailable, none.Coverage);
        Assert.Empty(none.Features);
        Assert.Null(LocationDescriptionEngine.Describe(Observer, none));
    }

    [Fact]
    public async Task RepeatedQueriesAreDeterministicAndReadNoTileTwice()
    {
        var fetcher = new ZoomFetcher(ContextTile, LandmarkTile);
        var source = new OpenFreeMapGeographicFeatureSource(new VectorTileRepository(fetcher));

        var first = await source.GetFeaturesAsync(Observer, CancellationToken.None);
        var moved = new GeoCoordinate(Observer.Latitude + .0002, Observer.Longitude);
        var second = await source.GetFeaturesAsync(moved, CancellationToken.None);
        var third = await source.GetFeaturesAsync(Observer, CancellationToken.None);

        Assert.Equal(first.Features.Select(f => f.Id), third.Features.Select(f => f.Id));
        Assert.Equal(LocationDescriptionEngine.Describe(Observer, first)!.Text, LocationDescriptionEngine.Describe(Observer, third)!.Text);
        Assert.NotEmpty(second.Features);
        // A small pin move stays within the same tiles: decoded once, never fetched again.
        Assert.Equal(fetcher.Requests.Count, fetcher.Requests.Distinct().Count());
    }

    /// <summary>
    /// Every zoom-13 tile: the River Ancholme running north–south about 300 m east of the observer's longitude, a
    /// village point about 150 m west of the observer (repeated in its neighbours' buffers) and its houses beyond.
    /// </summary>
    private static byte[]? ContextTile(int x, int y)
    {
        var tile = new VectorTileTestBuilder();
        var riverX = 2048 + 420 + (Column - x) * 4096;
        if (riverX is > -64 and < 4160)
            tile.Layer("waterway").Line([(riverX, -64), (riverX, 4160)], ("class", "river"), ("name", "River Ancholme"));
        var villageX = 2048 - 210 + (Column - x) * 4096;
        var villageY = 2048 + (Row - y) * 4096;
        if (villageX is > -64 and < 4160 && villageY is > -64 and < 4160)
        {
            tile.Layer("place").Point(villageX, villageY, ("class", "village"), ("name", "Saxby"), ("name:en", "Saxby"));
            tile.Layer("landuse").Rectangle(villageX - 500, villageY - 300, villageX - 50, villageY + 300, ("class", "residential"));
        }
        tile.Layer("poi").Point(100, 100, ("class", "bus"), ("name", "Bus Stop"));
        return tile.Build();
    }

    /// <summary>Zoom-14 tiles: a castle about 60 m south-east of the observer.</summary>
    private static byte[]? LandmarkTile(int x, int y)
    {
        var tile = new VectorTileTestBuilder();
        if (x == Column * 2 + 1 && y == Row * 2 + 1) tile.Layer("poi").Point(120, 120, ("class", "castle"), ("name", "Saxby Castle"));
        return tile.Build();
    }

    private static GeoCoordinate Centre(int x, int y)
    {
        var longitude = (x + .5) / 8192 * 360 - 180;
        var latitude = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (y + .5) / 8192))) * 180 / Math.PI;
        return new GeoCoordinate(latitude, longitude);
    }

    private sealed class ZoomFetcher(Func<int, int, byte[]?> context, Func<int, int, byte[]?> landmarks) : IVectorTileFetcher
    {
        public System.Collections.Concurrent.ConcurrentQueue<(int Zoom, int X, int Y)> Requests { get; } = new();

        public Task<byte[]?> FetchAsync(int zoom, int column, int row, CancellationToken cancellationToken)
        {
            Requests.Enqueue((zoom, column, row));
            return Task.FromResult(zoom == 13 ? context(column, row) : landmarks(column, row));
        }
    }
}
