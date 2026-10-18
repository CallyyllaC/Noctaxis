using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Locations.Descriptions;

namespace Noctaxis.Core.Tests;

public sealed class LocationDescriptionEngineTests
{
    private static readonly GeoCoordinate Observer = new(53.55, -0.49);

    [Fact]
    public void TheNearestFeatureIsNotNecessarilyTheMostRelevant()
    {
        // A hamlet 150 m away is nearer than the river 300 m away, but the named river is the better description.
        var set = Set(
            Place("Saxby", GeographicFeatureKind.Hamlet, 150, 0),
            River("River Ancholme", -300));

        var description = LocationDescriptionEngine.Describe(Observer, set);

        Assert.NotNull(description);
        Assert.Equal("River Ancholme", description.Primary.Name);
        Assert.Equal(GeographicRelation.Near, description.Primary.Relation);
        Assert.Equal("Saxby", description.Secondary?.Name);
        Assert.Equal(GeographicRelation.Outside, description.Secondary?.Relation);
        Assert.Equal("Near the River Ancholme, outside Saxby", description.Text);
    }

    [Theory]
    [InlineData(450)]
    [InlineData(650)]
    public void ANamedRiverSeveralHundredMetresAwayIsNear(double metres)
    {
        var description = LocationDescriptionEngine.Describe(Observer, Set(River("River Ancholme", metres)));

        Assert.NotNull(description);
        Assert.Equal(GeographicRelation.Near, description.Primary.Relation);
        Assert.Equal("Near the River Ancholme", description.Text);
        Assert.Equal(metres, description.Primary.DistanceMetres, 0);
    }

    [Fact]
    public void FartherAwayTheDirectionFromTheFeatureIsGiven()
    {
        // The river runs north-south 1.2 km east of the observer: the observer is west of it.
        var description = LocationDescriptionEngine.Describe(Observer, Set(River("River Ancholme", 1200)));

        Assert.NotNull(description);
        Assert.Equal(GeographicRelation.DirectionOf, description.Primary.Relation);
        Assert.Equal(CompassPoint.West, description.Primary.Direction);
        Assert.Equal(270, description.Primary.BearingFromFeatureDegrees, 0);
        Assert.Equal("West of the River Ancholme", description.Text);
    }

    [Theory]
    [InlineData(0, 1000, CompassPoint.South)]
    [InlineData(-1000, 0, CompassPoint.East)]
    [InlineData(800, 800, CompassPoint.SouthWest)]
    [InlineData(-800, -800, CompassPoint.NorthEast)]
    public void CompassDirectionsAreWhereTheObserverLiesFromTheFeature(double east, double north, CompassPoint expected)
    {
        var landmark = new GeographicFeature("castle", "Brigg Castle", GeographicFeatureKind.Landmark,
            GeographicGeometryKind.Point, [[At(east, north)]], "test", 1);

        var description = LocationDescriptionEngine.Describe(Observer, Set(landmark));

        Assert.NotNull(description);
        Assert.Equal(GeographicRelation.DirectionOf, description.Primary.Relation);
        Assert.Equal(expected, description.Primary.Direction);
    }

    [Fact]
    public void BearingsAndCompassPointsAreComputedOnTheLocalPlane()
    {
        Assert.Equal(CompassPoint.North, GeographicSpatialAnalysis.ToCompass(359));
        Assert.Equal(CompassPoint.East, GeographicSpatialAnalysis.ToCompass(91));
        Assert.Equal(CompassPoint.SouthWest, GeographicSpatialAnalysis.ToCompass(225));
        var east = GeographicSpatialAnalysis.Project(Observer, At(1000, 0));
        Assert.Equal(1000, east.East, 0);
        Assert.Equal(0, east.North, 3);
        Assert.Equal(90, GeographicSpatialAnalysis.Bearing(east), 3);
    }

    [Fact]
    public void ABankIsClaimedOnlyWhereTheCourseIsStraightAndUnclipped()
    {
        var straight = LocationDescriptionEngine.Describe(Observer, Set(River("River Ancholme", 60)));
        Assert.NotNull(straight);
        Assert.Equal(GeographicRelation.OnBank, straight.Primary.Relation);
        Assert.Equal(CompassPoint.West, straight.Primary.Direction);
        Assert.Equal("On the west bank of the River Ancholme", straight.Text);

        // The closest point is the end of the data (clipped at a tile edge, perhaps): no side is claimed.
        var clipped = new GeographicFeature("clipped", "River Ancholme", GeographicFeatureKind.River,
            GeographicGeometryKind.Line, [[At(60, 0), At(60, 2000)]], "test", 5);
        var atEnd = LocationDescriptionEngine.Describe(Observer, Set(clipped));
        Assert.NotNull(atEnd);
        Assert.Equal(GeographicRelation.By, atEnd.Primary.Relation);
        Assert.Equal("By the River Ancholme", atEnd.Text);

        // A tight meander gives no clear side either.
        var meander = new GeographicFeature("meander", "River Ancholme", GeographicFeatureKind.River,
            GeographicGeometryKind.Line, [[At(60, -1000), At(60, -40), At(140, 0), At(60, 40), At(60, 1000)]], "test", 5);
        Assert.NotEqual(GeographicRelation.OnBank, LocationDescriptionEngine.Describe(Observer, Set(meander))!.Primary.Relation);

        // Standing in the water is not a bank.
        var water = new GeographicFeature("water", null, GeographicFeatureKind.WaterArea, GeographicGeometryKind.Area,
            [Square(0, 0, 100)], "test", 5);
        Assert.Equal(GeographicRelation.By,
            LocationDescriptionEngine.Describe(Observer, Set(River("River Ancholme", 60), water))!.Primary.Relation);
    }

    [Fact]
    public void ContainmentInALargeAreaDoesNotOverrideAMeaningfulFeature()
    {
        var wolds = new GeographicFeature("wolds", "Lincolnshire Wolds", GeographicFeatureKind.ProtectedArea,
            GeographicGeometryKind.Area, [Square(0, 0, 10_000)], "test", 5);

        var description = LocationDescriptionEngine.Describe(Observer, Set(wolds, River("River Ancholme", 400)));

        Assert.NotNull(description);
        Assert.Equal("River Ancholme", description.Primary.Name);
        // Alone, the area still describes the position.
        Assert.Equal("In Lincolnshire Wolds", LocationDescriptionEngine.Describe(Observer, Set(wolds))!.Text);
    }

    [Fact]
    public void AFieldOutsideAVillageIsNotInIt()
    {
        var village = Place("Saxby", GeographicFeatureKind.Village, 300, 0);
        var builtUp = new GeographicFeature("houses", null, GeographicFeatureKind.BuiltUpArea, GeographicGeometryKind.Area,
            [Square(300, 0, 120)], "test", 5);

        Assert.Equal("Outside Saxby", LocationDescriptionEngine.Describe(Observer, Set(village, builtUp))!.Text);

        var onBuiltUpLand = new GeographicFeature("houses", null, GeographicFeatureKind.BuiltUpArea, GeographicGeometryKind.Area,
            [Square(150, 0, 200)], "test", 5);
        var inside = LocationDescriptionEngine.Describe(Observer, Set(village, onBuiltUpLand));
        Assert.Equal(GeographicRelation.In, inside!.Primary.Relation);
        Assert.Equal("In Saxby", inside.Text);
    }

    [Fact]
    public void MissingNamesIncompleteDataAndDistantFeaturesGiveNoDescription()
    {
        var unnamed = new GeographicFeature("unnamed", null, GeographicFeatureKind.River, GeographicGeometryKind.Line,
            [[At(100, -1000), At(100, 1000)]], "test", 5);
        Assert.Null(LocationDescriptionEngine.Describe(Observer, Set(unnamed)));
        Assert.Null(LocationDescriptionEngine.Describe(Observer, GeographicFeatureSet.Unavailable("test")));
        Assert.Null(LocationDescriptionEngine.Describe(Observer, Set()));
        // A settlement far beyond any meaningful reach is not used to avoid a coordinate label.
        Assert.Null(LocationDescriptionEngine.Describe(Observer, Set(Place("Far Town", GeographicFeatureKind.Village, 9000, 0))));
        // Nothing is claimed beyond what the source covers.
        var narrow = new GeographicFeatureSet([River("River Ancholme", 1200)], GeographicCoverage.Complete, 1000, "test");
        Assert.Null(LocationDescriptionEngine.Describe(Observer, narrow));
        var empty = new GeographicFeature("empty", "Nowhere", GeographicFeatureKind.Village, GeographicGeometryKind.Point, [], "test", 5);
        Assert.Null(LocationDescriptionEngine.Describe(Observer, Set(empty)));
    }

    [Fact]
    public void PartialCoverageLowersConfidenceButKeepsWhatIsPresent()
    {
        var partial = new GeographicFeatureSet([River("River Ancholme", 300)], GeographicCoverage.Partial, 2000, "test");

        var description = LocationDescriptionEngine.Describe(Observer, partial);

        Assert.NotNull(description);
        Assert.Equal(GeographicCoverage.Partial, description.Coverage);
        Assert.Equal(DescriptionConfidence.Medium, description.Primary.Confidence);
    }

    [Fact]
    public void DescriptionsNameOnlyFeaturesThatExistAndAreDeterministic()
    {
        var features = new[]
        {
            River("River Ancholme", -500), Place("Saxby", GeographicFeatureKind.Village, 900, 300),
            Place("Brigg", GeographicFeatureKind.Town, 2500, -2000), Place("Wrawby", GeographicFeatureKind.Village, -2600, 0),
            new GeographicFeature("peak", "Barrow Hill", GeographicFeatureKind.Peak, GeographicGeometryKind.Point, [[At(0, 1500)]], "test", 5)
        };
        var first = LocationDescriptionEngine.Describe(Observer, Set(features))!;
        for (var seed = 0; seed < 5; seed++)
        {
            var shuffled = features.ToArray();
            new Random(seed).Shuffle(shuffled);
            var again = LocationDescriptionEngine.Describe(Observer, Set(shuffled))!;
            Assert.Equal(first.Text, again.Text);
            Assert.Equal(first.Primary, again.Primary);
            Assert.Equal(first.Secondary, again.Secondary);
        }
        var names = features.Select(feature => feature.Name!).ToHashSet();
        Assert.Contains(first.Primary.Name, names);
        Assert.True(first.Secondary is null || names.Contains(first.Secondary.Name));
    }

    [Fact]
    public void TheSecondPartNeverRepeatsTheFirst()
    {
        // A river and a village that share a name: one phrase only.
        var description = LocationDescriptionEngine.Describe(Observer,
            Set(River("Ancholme", -300), Place("Ancholme", GeographicFeatureKind.Village, 400, 0)));

        Assert.NotNull(description);
        Assert.Null(description.Secondary);
    }

    [Fact]
    public async Task TheServiceRemembersNearbyAnswersButRetriesWhenDataWasUnavailable()
    {
        var source = new CountingSource(Set(River("River Ancholme", 300)));
        var service = new LocationDescriptionService(source, NullLogger<LocationDescriptionService>.Instance);

        var first = await service.DescribeAsync(Observer, CancellationToken.None);
        var nearby = await service.DescribeAsync(new GeoCoordinate(Observer.Latitude + 0.00005, Observer.Longitude), CancellationToken.None);
        Assert.Equal(first!.Text, nearby!.Text);
        Assert.Equal(1, source.Requests);

        var offline = new CountingSource(GeographicFeatureSet.Unavailable("test"));
        var retrying = new LocationDescriptionService(offline, NullLogger<LocationDescriptionService>.Instance);
        Assert.Null(await retrying.DescribeAsync(Observer, CancellationToken.None));
        Assert.Null(await retrying.DescribeAsync(Observer, CancellationToken.None));
        Assert.Equal(2, offline.Requests);
    }

    private static GeographicFeatureSet Set(params GeographicFeature[] features) =>
        new(features, GeographicCoverage.Complete, 4000, "test");

    /// <summary>A straight north-south river <paramref name="east"/> metres east of the observer, 4 km long.</summary>
    private static GeographicFeature River(string name, double east) => new("river:" + name, name,
        GeographicFeatureKind.River, GeographicGeometryKind.Line, [[At(east, -2000), At(east, 0), At(east, 2000)]], "test", 5);

    private static GeographicFeature Place(string name, GeographicFeatureKind kind, double east, double north) =>
        new("place:" + name, name, kind, GeographicGeometryKind.Point, [[At(east, north)]], "test", 5);

    private static GeographicPoint[] Square(double east, double north, double half) =>
        [At(east - half, north - half), At(east + half, north - half), At(east + half, north + half), At(east - half, north + half)];

    private static GeographicPoint At(double east, double north)
    {
        const double radius = 6_371_008.8;
        var latitude = Observer.Latitude + north / radius * 180 / Math.PI;
        var longitude = Observer.Longitude + east / (radius * Math.Cos(Observer.Latitude * Math.PI / 180)) * 180 / Math.PI;
        return new GeographicPoint(latitude, longitude);
    }

    private sealed class CountingSource(GeographicFeatureSet set) : IGeographicFeatureSource
    {
        public int Requests { get; private set; }
        public Task<GeographicFeatureSet> GetFeaturesAsync(GeoCoordinate centre, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(set);
        }
    }
}
