using System.Text;
using System.Text.Json.Nodes;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Export;
using Noctaxis.Core.Locations;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>
/// Exports stay readable across versions. Each format keeps an explicit schema; every supported schema keeps
/// reading the files earlier versions wrote (the fixtures under Samples/Compatibility, written by those writers).
/// Unknown additional fields are allowed; anything that would have to be guessed — a newer schema, a missing or
/// mistyped required value, an unknown enum, a duplicated property — is refused with its reason.
/// </summary>
public sealed class SchemaCompatibilityTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", "Compatibility", name);
    private static string Text(string name) => File.ReadAllText(Fixture(name));

    private static readonly NoctaxisPlan FocusedV1 = new(Instant.FromUtc(2026, 10, 7, 21, 0), 52.59302, -3.64697, 293.3, 7.2,
        new NoctaxisPlanFocus("m31", true));
    private static readonly NoctaxisPlan UnfocusedV1 = new(Instant.FromUtc(2025, 12, 21, 23, 30), -33.86785, 151.20732, 0, -12.5, null);

    // ---- Plan (Noctaxis.Plan, schema 1) ------------------------------------------------------------------------

    [Fact]
    public void ThePlanSchemaIsExplicit()
    {
        Assert.Equal(1, NoctaxisPlan.SchemaVersion);
        Assert.Equal([1], NoctaxisPlan.SupportedSchemaVersions);
        Assert.Contains(NoctaxisPlan.SchemaVersion, NoctaxisPlan.SupportedSchemaVersions);
    }

    /// <summary>Schema-1 documents as written, focused and unfocused, read back to exactly what was meant.</summary>
    [Fact]
    public void SchemaOnePlansReadAsWritten()
    {
        Assert.Equal(FocusedV1, NoctaxisPlan.Parse(Text("plan-v1-focused.json")).Plan);
        Assert.Equal(UnfocusedV1, NoctaxisPlan.Parse(Text("plan-v1-unfocused.json")).Plan);
    }

    /// <summary>A schema-1 plan image as written: the plan read from its PNG chunk.</summary>
    [Fact]
    public void ASchemaOnePlanImageReadsAsWritten()
    {
        var read = NoctaxisPlanImage.Read(File.ReadAllBytes(Fixture("plan-v1.png")));
        Assert.Equal(NoctaxisPlanError.None, read.Error);
        Assert.Equal(FocusedV1, read.Plan);
    }

    /// <summary>
    /// The writer still writes schema 1 byte for byte as it always has. If this fails, the serialised format has
    /// changed: decide whether that is a new schema version before changing the fixture.
    /// </summary>
    [Fact]
    public void TheCurrentWriterStillWritesSchemaOneExactly()
    {
        Assert.Equal(Text("plan-v1-focused.json"), FocusedV1.ToJson());
        Assert.Equal(Text("plan-v1-unfocused.json"), UnfocusedV1.ToJson());
        Assert.Equal(FocusedV1, NoctaxisPlan.Parse(FocusedV1.ToJson()).Plan);
    }

    /// <summary>Additions a later schema-1 writer might make are ignored, at every level.</summary>
    [Fact]
    public void UnknownAdditiveFieldsAreIgnored()
    {
        var json = JsonNode.Parse(Text("plan-v1-focused.json"))!.AsObject();
        json["notes"] = "added later";
        json["weather"] = new JsonObject { ["cloud"] = 20 };
        json["location"]!["elevationMetres"] = 410;
        json["camera"]!["rollDegrees"] = 3;
        json["focus"]!["label"] = "Andromeda";
        Assert.Equal(FocusedV1, NoctaxisPlan.Parse(json.ToJsonString()).Plan);
    }

    /// <summary>A newer schema, even one that looks like schema 1, is refused: its fields may mean something else.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(17)]
    [InlineData(int.MaxValue)]
    public void NewerSchemasAreRefused(int schema)
    {
        var json = JsonNode.Parse(Text("plan-v1-focused.json"))!.AsObject();
        json["schemaVersion"] = schema;
        var read = NoctaxisPlan.Parse(json.ToJsonString());
        Assert.Null(read.Plan);
        Assert.Equal(NoctaxisPlanError.UnsupportedSchema, read.Error);
    }

    public static TheoryData<string, NoctaxisPlanError> Refused => new()
    {
        // Missing required values: never defaulted.
        { "$.focus", NoctaxisPlanError.Malformed },
        { "$.focus.follow", NoctaxisPlanError.Malformed },
        { "$.focus.target", NoctaxisPlanError.Malformed },
        { "$.camera.pitchDegrees", NoctaxisPlanError.Malformed },
        { "$.camera", NoctaxisPlanError.Malformed },
        { "$.location.longitude", NoctaxisPlanError.InvalidCoordinates },
        { "$.timeUtc", NoctaxisPlanError.InvalidTime },
        { "$.schemaVersion", NoctaxisPlanError.Malformed },
        { "$.format", NoctaxisPlanError.Malformed },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void AMissingRequiredValueIsRefused(string path, NoctaxisPlanError error)
    {
        var json = JsonNode.Parse(Text("plan-v1-focused.json"))!.AsObject();
        var parts = path[2..].Split('.');
        var parent = parts[..^1].Aggregate((JsonNode)json, (node, part) => node[part]!).AsObject();
        Assert.True(parent.Remove(parts[^1]));
        var read = NoctaxisPlan.Parse(json.ToJsonString());
        Assert.Null(read.Plan);
        Assert.Equal(error, read.Error);
    }

    /// <summary>Values of the wrong kind, or out of range, are refused rather than read as something near them.</summary>
    [Theory]
    [InlineData("\"follow\":true", "\"follow\":\"true\"", NoctaxisPlanError.Malformed)]
    [InlineData("\"follow\":true", "\"follow\":1", NoctaxisPlanError.Malformed)]
    [InlineData("\"follow\":true", "\"follow\":null", NoctaxisPlanError.Malformed)]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1.5", NoctaxisPlanError.Malformed)]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":0", NoctaxisPlanError.Malformed)]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":-1", NoctaxisPlanError.Malformed)]
    [InlineData("\"pitchDegrees\":7.2", "\"pitchDegrees\":90.5", NoctaxisPlanError.InvalidCamera)]
    [InlineData("\"pitchDegrees\":7.2", "\"pitchDegrees\":-120", NoctaxisPlanError.InvalidCamera)]
    [InlineData("\"pitchDegrees\":7.2", "\"pitchDegrees\":\"7.2\"", NoctaxisPlanError.Malformed)]
    [InlineData("\"bearingDegrees\":293.3", "\"bearingDegrees\":1e400", NoctaxisPlanError.Malformed)]
    [InlineData("\"latitude\":52.59302", "\"latitude\":-90.0001", NoctaxisPlanError.InvalidCoordinates)]
    [InlineData("\"longitude\":-3.64697", "\"longitude\":180.5", NoctaxisPlanError.InvalidCoordinates)]
    [InlineData("\"timeUtc\":\"2026-10-07T21:00:00Z\"", "\"timeUtc\":\"9999-12-31T23:00:00Z\"", NoctaxisPlanError.InvalidTime)]
    [InlineData("\"timeUtc\":\"2026-10-07T21:00:00Z\"", "\"timeUtc\":\"0001-01-01T00:00:00Z\"", NoctaxisPlanError.InvalidTime)]
    [InlineData("\"timeUtc\":\"2026-10-07T21:00:00Z\"", "\"timeUtc\":\"2026-10-07T21:00:00\"", NoctaxisPlanError.InvalidTime)]
    [InlineData("\"format\":\"noctaxis-plan\"", "\"format\":\"Noctaxis-Plan\"", NoctaxisPlanError.Malformed)]
    [InlineData("\"target\":\"m31\"", "\"target\":\"  \"", NoctaxisPlanError.Malformed)]
    public void InvalidValuesAreRefused(string original, string replacement, NoctaxisPlanError error)
    {
        var text = Text("plan-v1-focused.json");
        Assert.Contains(original, text);
        var read = NoctaxisPlan.Parse(text.Replace(original, replacement));
        Assert.Null(read.Plan);
        Assert.Equal(error, read.Error);
    }

    /// <summary>A property given twice could be read either way: the whole plan is refused.</summary>
    [Theory]
    [InlineData("\"camera\":{", "\"camera\":{\"pitchDegrees\":80,")]
    [InlineData("\"format\":\"noctaxis-plan\",", "\"format\":\"noctaxis-plan\",\"schemaVersion\":2,")]
    public void DuplicatedPropertiesAreRefused(string original, string replacement)
    {
        var read = NoctaxisPlan.Parse(Text("plan-v1-focused.json").Replace(original, replacement));
        Assert.Null(read.Plan);
        Assert.Equal(NoctaxisPlanError.Malformed, read.Error);
    }

    /// <summary>Valid values at the edges are accepted with their meaning intact: a bearing beyond 360° is a direction.</summary>
    [Fact]
    public void EdgeValuesKeepTheirMeaning()
    {
        var text = Text("plan-v1-focused.json");
        Assert.Equal(370, NoctaxisPlan.Parse(text.Replace("\"bearingDegrees\":293.3", "\"bearingDegrees\":370")).Plan!.BearingDegrees);
        Assert.Equal(-90, NoctaxisPlan.Parse(text.Replace("\"pitchDegrees\":7.2", "\"pitchDegrees\":-90")).Plan!.PitchDegrees);
        Assert.Equal(90, NoctaxisPlan.Parse(text.Replace("\"latitude\":52.59302", "\"latitude\":90")).Plan!.Latitude);
        Assert.Equal(NoctaxisPlan.LatestTime, NoctaxisPlan.Parse(text.Replace("2026-10-07T21:00:00Z", "9999-12-30T00:00:00Z")).Plan!.TimeUtc);
        Assert.False(NoctaxisPlan.Parse(text.Replace("\"follow\":true", "\"follow\":false")).Plan!.Focus!.Follow);
    }

    // ---- Scouting card (Noctaxis.ExportData, schema 2) ----------------------------------------------------------

    /// <summary>
    /// An earlier scouting card is recognised and named, never read as a plan: its bearing was the target's, and it
    /// has no camera pitch or focus, so a plan made from it would be guesswork.
    /// </summary>
    [Fact]
    public void AnEarlierScoutingCardIsNamedButNotReadAsAPlan()
    {
        var read = NoctaxisPlanImage.Read(File.ReadAllBytes(Fixture("scouting-card-schema2.png")));
        Assert.Null(read.Plan);
        Assert.Equal(NoctaxisPlanError.LegacyScoutingCard, read.Error);
        // A plan image that also carries the old card's chunk is read by its plan.
        var both = NoctaxisPlanImage.Embed(File.ReadAllBytes(Fixture("scouting-card-schema2.png")), FocusedV1);
        Assert.Equal(FocusedV1, NoctaxisPlanImage.Read(both).Plan);
    }

    // ---- Locations (location export, schema 1) ------------------------------------------------------------------

    private static readonly LocationTransferService Locations = new();
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void ASchemaOneLocationExportReadsAsWritten()
    {
        var document = Locations.Import(File.ReadAllBytes(Fixture("locations-v1.json")));
        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 18, 30, 0, TimeSpan.Zero), document.ExportedAtUtc);
        Assert.Collection(document.Locations,
            ridge =>
            {
                Assert.Equal("Dark Ridge", ridge.Name);
                Assert.Equal(new GeoCoordinate(51.2, -1.3, 85), ridge.Coordinate);
                Assert.Equal(SensorPreset.FullFrame, ridge.PreferredSensor);
                Assert.True(ridge.IsFavourite);
            },
            harbour =>
            {
                Assert.Equal(new GeoCoordinate(-33.86785, 151.20732), harbour.Coordinate);
                Assert.Null(harbour.PreferredSensor);
                Assert.Equal("Australia/Sydney", harbour.TimeZoneId);
            });
        // And the current writer round-trips it.
        Assert.Equal(document.Locations, Locations.Import(Locations.Export(document.Locations)).Locations);
    }

    [Fact]
    public void LocationExportsAcceptAdditionsAndRefuseWhatTheyCannotRead()
    {
        JsonObject Fresh() => JsonNode.Parse(Text("locations-v1.json"))!.AsObject();
        JsonObject First(JsonObject json) => json["locations"]![0]!.AsObject();
        InvalidDataException Refused(JsonObject json, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(json))] string? which = null)
        {
            try { Locations.Import(Utf8(json.ToJsonString())); }
            catch (InvalidDataException refused) { return refused; }
            Assert.Fail($"{which} was imported");
            return null!;
        }

        var added = Fresh();
        added["sharedBy"] = "a friend";
        First(added)["rating"] = 5;
        Assert.Equal(2, Locations.Import(Utf8(added.ToJsonString())).Locations.Count);

        var newer = Fresh();
        newer["schemaVersion"] = 2;
        Assert.Contains("newer version", Refused(newer).Message);

        var noList = Fresh();
        noList.Remove("locations");
        Refused(noList);

        var noCoordinate = Fresh();
        First(noCoordinate).Remove("coordinate");
        Refused(noCoordinate);

        var noLongitude = Fresh();
        First(noLongitude)["coordinate"]!.AsObject().Remove("longitude");
        Refused(noLongitude);

        var nullName = Fresh();
        First(nullName)["name"] = null;
        Refused(nullName);

        var offTheGlobe = Fresh();
        First(offTheGlobe)["coordinate"]!["latitude"] = 95;
        Refused(offTheGlobe);

        var unknownSensor = Fresh();
        First(unknownSensor)["preferredSensor"] = 42;
        Refused(unknownSensor);

        var notJson = Assert.Throws<InvalidDataException>(() => Locations.Import(Utf8("{ not json")));
        Assert.Contains("not a readable", notJson.Message);

        var duplicated = Text("locations-v1.json").Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"schemaVersion\": 1,");
        Assert.Throws<InvalidDataException>(() => Locations.Import(Utf8(duplicated)));
    }
}
