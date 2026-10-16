using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>
/// D05 (phase-0 audit): syntactically valid state whose structure is invalid (a null where a record is required)
/// is classified before it is migrated or used. A file of this build's schema or older goes through the existing
/// corrupt-state policy: it is moved aside unchanged (never deleted or overwritten) and defaults are used. A newer
/// schema's file is left exactly as it is and not saved over. Nothing escapes as a NullReferenceException.
/// </summary>
public sealed class StateStructuralValidationTests : IDisposable
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 16, 12, 0);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "noctaxis-state-structure-" + Guid.NewGuid().ToString("N"));
    private sealed record Paths(string Root) : IUserDataPathProvider { public string GetApplicationDataDirectory() => Root; }

    private string StateFile => Path.Combine(_directory, "state.json");
    private JsonUserDataStore Store() => new(new Paths(_directory), NullLogger<JsonUserDataStore>.Instance, () => Now);

    private static JsonObject Sample(string name) => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Samples", "Compatibility", name)))!.AsObject();

    public static TheoryData<string, string> MalformedShapes()
    {
        var data = new TheoryData<string, string>();
        foreach (var sample in new[] { "state-v3-a68eff4.json", "state-v4-d735bcf.json" })
            foreach (var change in new[]
                     {
                         "location-null", "location-only-null", "location-name-null", "location-zone-null",
                         "visible-object-null", "configured-object-null", "configured-object-target-null",
                         "camera-null", "lens-null", "weather-field-null"
                     })
                data.Add(sample, change);
        return data;
    }

    private static void Apply(JsonObject state, string change)
    {
        var settings = state["Settings"]!.AsObject();
        var session = state["Session"]!.AsObject();
        var locations = state["Locations"]!.AsArray();
        switch (change)
        {
            case "location-null": locations.Add(null); break;
            case "location-only-null": state["Locations"] = new JsonArray((JsonNode?)null); break;
            case "location-name-null": locations[0]!["Name"] = null; break;
            case "location-zone-null": locations[0]!["TimeZoneId"] = null; break;
            case "visible-object-null": session["VisibleObjects"]!.AsArray().Add(null); break;
            case "configured-object-null": settings["CelestialObjects"]!["ConfiguredObjects"]!.AsArray().Add(null); break;
            case "configured-object-target-null": settings["CelestialObjects"]!["ConfiguredObjects"]![0]!["TargetId"] = null; break;
            case "session-zone-null": session["TimeZoneId"] = null; break;
            case "session-target-null": session["TargetId"] = null; break;
            case "camera-null": Equipment(settings)["Cameras"] = new JsonArray((JsonNode?)null); break;
            case "lens-null": Equipment(settings)["Lenses"] = new JsonArray((JsonNode?)null); break;
            case "weather-field-null": settings["Weather"]!["EnabledFields"]!.AsArray().Add(null); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
    }

    private static JsonObject Equipment(JsonObject settings) =>
        settings["Equipment"] as JsonObject ?? (JsonObject)(settings["Equipment"] = new JsonObject());

    private async Task<string> WriteAsync(JsonObject document)
    {
        Directory.CreateDirectory(_directory);
        var json = document.ToJsonString();
        await File.WriteAllTextAsync(StateFile, json);
        return json;
    }

    [Theory]
    [MemberData(nameof(MalformedShapes))]
    public async Task StructurallyInvalidState_IsMovedAsideUnchangedAndDefaultsAreUsed(string sample, string change)
    {
        var document = Sample(sample);
        Apply(document, change);
        var original = await WriteAsync(document);
        var store = Store();

        var state = await store.LoadAsync(CancellationToken.None);

        Assert.Empty(state.Locations);
        Assert.Null(state.AuthoritativeAsOfUtc);
        Assert.False(File.Exists(StateFile));
        var quarantined = Assert.Single(Directory.GetFiles(_directory, "state.json.corrupt-*"));
        Assert.Equal(original, await File.ReadAllTextAsync(quarantined));
    }

    [Theory]
    [InlineData("location-null")]
    [InlineData("location-name-null")]
    [InlineData("camera-null")]
    public async Task StructurallyInvalidNewerState_IsLeftAloneAndNeverSavedOver(string change)
    {
        var document = Sample("state-v4-d735bcf.json");
        document["Version"] = JsonUserDataStore.CurrentSchemaVersion + 1;
        Apply(document, change);
        var original = await WriteAsync(document);
        var store = Store();

        var state = await store.LoadAsync(CancellationToken.None);

        Assert.True(state.PreservationWarning?.IsNewerVersion);
        Assert.NotNull(store.SaveRefusal);
        await Assert.ThrowsAsync<UserStateReadOnlyException>(() => store.SaveAsync(state, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllTextAsync(StateFile));
        Assert.Empty(Directory.GetFiles(_directory, "state.json.corrupt-*"));
    }

    [Theory]
    [InlineData("state-v3-a68eff4.json")]
    [InlineData("state-v4-d735bcf.json")]
    [InlineData("state-v4-ef58a7b.json")]
    public async Task ValidCompatibilitySamples_StillLoadEveryLocation(string sample)
    {
        var document = Sample(sample);
        var expected = document["Locations"]!.AsArray().Count;
        await WriteAsync(document);

        var state = await Store().LoadAsync(CancellationToken.None);

        Assert.Equal(expected, state.Locations.Count);
        Assert.All(state.Locations, location =>
        {
            Assert.False(string.IsNullOrEmpty(location.Name));
            Assert.NotNull(location.TimeZoneId);
            // Files before schema 4 are migrated to carry each location's elevation state.
            if ((int)document["Version"]! < 4) Assert.NotNull(location.ObserverElevation);
        });
        Assert.NotNull(state.AuthoritativeAsOfUtc);
        Assert.Empty(Directory.GetFiles(_directory, "state.json.corrupt-*"));
    }

    [Theory]
    [InlineData("session-zone-null")]
    [InlineData("session-target-null")]
    public async Task SessionValuesTheLoaderRepairs_StillLoadEverything(string change)
    {
        // A missing session zone falls back to the machine's, as an unknown zone always has, and a missing target
        // is replaced by the saved focus or first shown object: nothing is dereferenced and nothing is lost.
        var document = Sample("state-v4-d735bcf.json");
        var expected = document["Locations"]!.AsArray().Count;
        document["Session"]![change == "session-zone-null" ? "TimeZoneId" : "TargetId"] = null;
        await WriteAsync(document);

        var state = await Store().LoadAsync(CancellationToken.None);

        Assert.Equal(expected, state.Locations.Count);
        Assert.False(string.IsNullOrEmpty(state.Session.TimeZoneId));
        Assert.False(string.IsNullOrEmpty(state.Session.TargetId));
        Assert.Empty(Directory.GetFiles(_directory, "state.json.corrupt-*"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (IOException) { }
    }
}
