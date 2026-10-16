using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>
/// S-V02: state written by a newer build (a schema this build does not know, or enum values it does not
/// define) still opens with the user's data, is never quarantined, and is never saved over.
/// </summary>
public sealed class StateForwardCompatibilityTests : IDisposable
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 8, 12, 0);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "noctaxis-state-forward-" + Guid.NewGuid().ToString("N"));
    private sealed record Paths(string Root) : IUserDataPathProvider { public string GetApplicationDataDirectory() => Root; }

    private string StateFile => Path.Combine(_directory, "state.json");
    private string BackupFile => StateFile + ".bak";
    private JsonUserDataStore Store() => new(new Paths(_directory), NullLogger<JsonUserDataStore>.Instance, () => Now);

    private static JsonObject Sample() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Samples", "Compatibility", "state-v4-d735bcf.json")))!.AsObject();

    private async Task<string> WriteAsync(JsonObject document)
    {
        Directory.CreateDirectory(_directory);
        var json = document.ToJsonString();
        await File.WriteAllTextAsync(StateFile, json);
        return json;
    }

    public static TheoryData<string> UnknownValues => new()
    {
        "weather-field", "lens-preset", "orientation", "location-sensor", "colour-vision", "appearance-mode", "map-provider"
    };

    private static void Apply(JsonObject state, string change)
    {
        var settings = state["Settings"]!.AsObject();
        var lens = state["Session"]!["Lens"]!.AsObject();
        switch (change)
        {
            case "weather-field": settings["Weather"]!["EnabledFields"]!.AsArray().Add("SkyQualityMeter"); break;
            case "lens-preset": lens["Preset"] = "MediumFormat"; break;
            case "orientation": lens["Orientation"] = "Square"; break;
            case "location-sensor": state["Locations"]![0]!["PreferredSensor"] = "MediumFormat"; break;
            case "colour-vision": settings["Appearance"]!["ColourVisionMode"] = "Achromatopsia"; break;
            case "appearance-mode": settings["Appearance"]!["AppearanceMode"] = "Dim"; break;
            case "map-provider": settings["ExternalMapProvider"] = "AppleMaps"; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
    }

    [Fact]
    public async Task CurrentSample_LoadsCompletelyAndMaySave()
    {
        await WriteAsync(Sample());
        var store = Store();
        var state = await store.LoadAsync(CancellationToken.None);
        Assert.Null(state.PreservationWarning);
        Assert.Null(store.SaveRefusal);
        await store.SaveAsync(state, CancellationToken.None);
    }

    [Theory]
    [MemberData(nameof(UnknownValues))]
    public async Task UnknownEnumValue_KeepsEverythingElseAndIsNeverSavedOver(string change)
    {
        var baseline = await LoadedAsync(Sample());
        var document = Sample();
        Apply(document, change);
        var original = await WriteAsync(document);
        var store = Store();

        var state = await store.LoadAsync(CancellationToken.None);

        // The user's data is all there; only the unknown value is replaced by a safe stand-in.
        Assert.Equal(baseline.Locations.Select(location => location.Id), state.Locations.Select(location => location.Id));
        Assert.Equal(baseline.Settings.SelectedTimeZoneId, state.Settings.SelectedTimeZoneId);
        Assert.Equal(baseline.Session.TargetId, state.Session.TargetId);
        var warning = Assert.IsType<PersistedStateWarning>(state.PreservationWarning);
        Assert.False(warning.IsNewerVersion);
        Assert.Single(warning.UnrecognisedValues);
        switch (change)
        {
            case "weather-field":
                Assert.Equal(baseline.Settings.EffectiveWeather.EffectiveFields, state.Settings.EffectiveWeather.EffectiveFields);
                break;
            case "orientation": Assert.Equal(CameraOrientation.Landscape, state.Session.Lens.Orientation); break;
            case "location-sensor": Assert.Null(state.Locations[0].PreferredSensor); break;
            case "colour-vision": Assert.Equal(ColourVisionMode.None, state.Settings.Appearance!.ColourVisionMode); break;
            case "map-provider": Assert.Equal(new AppSettings().ExternalMapProvider, state.Settings.ExternalMapProvider); break;
        }

        // Saving would drop the value, so the store refuses and the file stays exactly as it was.
        await Assert.ThrowsAsync<UserStateReadOnlyException>(() => store.SaveAsync(state, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllTextAsync(StateFile));
        Assert.Empty(Directory.GetFiles(_directory, "state.json.corrupt-*"));
        Assert.False(File.Exists(BackupFile));
    }

    [Fact]
    public async Task NewerSchemaVersion_LoadsReadOnlyAndKeepsTheFile()
    {
        var document = Sample();
        document["Version"] = JsonUserDataStore.CurrentSchemaVersion + 1;
        document["PropertyFromTheFuture"] = new JsonObject { ["Kept"] = true };
        var original = await WriteAsync(document);
        var store = Store();

        var state = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(2, state.Locations.Count);
        var warning = Assert.IsType<PersistedStateWarning>(state.PreservationWarning);
        Assert.True(warning.IsNewerVersion);
        Assert.Equal(JsonUserDataStore.CurrentSchemaVersion + 1, warning.FileVersion);
        await Assert.ThrowsAsync<UserStateReadOnlyException>(() => store.SaveAsync(state, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllTextAsync(StateFile));
        Assert.Empty(Directory.GetFiles(_directory, "state.json.corrupt-*"));
    }

    [Fact]
    public async Task NewerSchemaThisBuildCannotRead_IsNotQuarantinedOrReplaced()
    {
        var document = Sample();
        document["Version"] = JsonUserDataStore.CurrentSchemaVersion + 3;
        document["Session"] = "restructured by a later version";
        var original = await WriteAsync(document);
        var store = Store();

        var state = await store.LoadAsync(CancellationToken.None);

        Assert.Empty(state.Locations);
        Assert.True(Assert.IsType<PersistedStateWarning>(state.PreservationWarning).IsNewerVersion);
        await Assert.ThrowsAsync<UserStateReadOnlyException>(() => store.SaveAsync(state, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllTextAsync(StateFile));
        Assert.Empty(Directory.GetFiles(_directory, "state.json.corrupt-*"));
    }

    [Fact]
    public async Task OwnSchemaThatCannotBeRead_IsStillQuarantined()
    {
        var document = Sample();
        document["Session"] = "not a session";
        await WriteAsync(document);

        var state = await Store().LoadAsync(CancellationToken.None);

        Assert.Empty(state.Locations);
        Assert.Null(state.PreservationWarning);
        Assert.NotEmpty(Directory.GetFiles(_directory, "state.json.corrupt-*"));
    }

    [Fact]
    public async Task Backup_IsNeverReplacedByACorruptFile()
    {
        var store = Store();
        var first = await LoadedAsync(Sample());
        await store.SaveAsync(first, CancellationToken.None);
        await store.SaveAsync(first with { MostRecentLocationId = null }, CancellationToken.None);
        var goodBackup = await File.ReadAllTextAsync(BackupFile);

        // Something outside the application damages the file between saves.
        await File.WriteAllTextAsync(StateFile, "{ damaged");
        await store.SaveAsync(first, CancellationToken.None);

        Assert.Equal(goodBackup, await File.ReadAllTextAsync(BackupFile));
        Assert.Equal(2, (await Store().LoadAsync(CancellationToken.None)).Locations.Count);
    }

    [Fact]
    public async Task FileReplacedByNewerVersionDuringTheSession_IsNotSavedOver()
    {
        var store = Store();
        var state = await LoadedAsync(Sample());
        await store.SaveAsync(state, CancellationToken.None);
        var newer = Sample();
        newer["Version"] = JsonUserDataStore.CurrentSchemaVersion + 1;
        var written = await WriteAsync(newer);

        await Assert.ThrowsAsync<UserStateReadOnlyException>(() => store.SaveAsync(state, CancellationToken.None));
        Assert.Equal(written, await File.ReadAllTextAsync(StateFile));
        // And the refusal holds for the rest of the session.
        await Assert.ThrowsAsync<UserStateReadOnlyException>(() => store.SaveAsync(state, CancellationToken.None));
    }

    [Fact]
    public void EveryPersistedEnum_HasAReviewedTolerantReading()
    {
        var handled = PersistedEnumConverterFactory.ReviewedEnums
            .Concat([typeof(PanelDockSide), typeof(PanelDockMode)]).ToHashSet();
        var missing = PersistedEnums(typeof(PersistedState)).Where(type => !handled.Contains(type)).ToArray();
        Assert.True(missing.Length == 0,
            "Persisted enums without a reviewed fallback: " + string.Join(", ", missing.Select(type => type.Name)));
    }

    /// <summary>Enums reachable through the constructor-bound or settable members of the persisted model.</summary>
    private static HashSet<Type> PersistedEnums(Type root)
    {
        var enums = new HashSet<Type>();
        var seen = new HashSet<Type>();
        void Walk(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying.IsEnum) { enums.Add(underlying); return; }
            if (underlying.IsArray) { Walk(underlying.GetElementType()!); return; }
            if (underlying.IsGenericType) foreach (var argument in underlying.GetGenericArguments()) Walk(argument);
            if (underlying.IsPrimitive || underlying.Namespace?.StartsWith("System", StringComparison.Ordinal) == true ||
                underlying.Namespace?.StartsWith("NodaTime", StringComparison.Ordinal) == true || !seen.Add(underlying))
                return;
            var parameters = underlying.GetConstructors().SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var property in underlying.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (property.GetIndexParameters().Length == 0 &&
                    (property.SetMethod?.IsPublic == true || parameters.Contains(property.Name)) &&
                    property.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is null)
                    Walk(property.PropertyType);
        }
        Walk(root);
        return enums;
    }

    private async Task<PersistedState> LoadedAsync(JsonObject document)
    {
        var directory = Path.Combine(_directory, "baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "state.json"), document.ToJsonString());
        return await new JsonUserDataStore(new Paths(directory), NullLogger<JsonUserDataStore>.Instance, () => Now)
            .LoadAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
