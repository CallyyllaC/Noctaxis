using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Localisation;
using Noctaxis.Core.Measurements;
using Noctaxis.Core.Persistence;
using NodaTime;

namespace Noctaxis.Core.Tests;

public sealed partial class LocalisationTests
{
    private static readonly string[] CoreDomains = ["Data", "Equipment", "Export", "Places", "Planning", "Terrain", "Units", "Weather"];

    [Fact]
    public void EveryCoreDomainLoadsAndItsKeysCarryItsName()
    {
        Assert.Superset(CoreDomains.ToHashSet(), Strings.RegisteredDomains.Where(CoreDomains.Contains).ToHashSet());
        foreach (var domain in CoreDomains)
        {
            Assert.Contains(domain, Strings.RegisteredDomains);
            var entries = Strings.Entries(domain);
            Assert.NotEmpty(entries);
            Assert.All(entries, entry =>
            {
                Assert.StartsWith(domain + "_", entry.Key);
                Assert.Matches("^[A-Za-z0-9_]+$", entry.Key);
                Assert.False(string.IsNullOrEmpty(entry.Value), entry.Key);
                Assert.Equal(entry.Value, Strings.Get(entry.Key));
            });
            // Keys that differ only by case would be one entry to some tools and two to others.
            Assert.Equal(entries.Count, entries.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void FormattedTextSubstitutesItsValues()
    {
        Assert.Equal("Open-Meteo returned HTTP 503.", Strings.Format("Weather_Error_Http_Format", 503));
        Assert.Equal("Weather unavailable: timed out", Strings.Format("Weather_Error_Unavailable_Format", "timed out"));
        Assert.Equal("Below terrain horizon by 1.5°", Invariant(() => Strings.Format("Planning_Framing_BelowTerrainHorizon_Format", 1.46)));
    }

    [Fact]
    public void EveryFormatCanBeFilledAndKeepsItsPlaceholders()
    {
        foreach (var domain in Strings.RegisteredDomains)
            foreach (var (key, text) in Strings.Entries(domain))
            {
                var indexes = Placeholder().Matches(text).Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
                if (key.EndsWith("_Format", StringComparison.Ordinal)) Assert.NotEmpty(indexes);
                if (indexes.Length == 0) continue;
                // Placeholders are numbered from zero without gaps, so a call site can count on them.
                Assert.Equal(Enumerable.Range(0, indexes.Max() + 1), indexes.Distinct().Order());
            }
    }

    [Fact]
    public void AMissingKeyIsObviousAndReportedNeverBlank()
    {
        var reported = new List<string>();
        void Report(string key) => reported.Add(key);
        Strings.MissingKey += Report;
        try
        {
            Assert.Equal("⟦Weather_No_Such_Key⟧", Strings.Get("Weather_No_Such_Key"));
            Assert.Equal("⟦NoSuchDomain_Key⟧", Strings.Get("NoSuchDomain_Key"));
            Assert.False(Strings.TryGet("Weather_No_Such_Key", out _));
            Assert.Equal(["Weather_No_Such_Key", "NoSuchDomain_Key"], reported);
        }
        finally { Strings.MissingKey -= Report; }
    }

    [Fact]
    public void EnglishIsTheFallbackForALanguageWithNoTranslation()
    {
        var text = InCulture("de-DE", () => Strings.Get("Weather_Summary_PartlyCloudy"));
        Assert.Equal("Partly cloudy", text);
    }

    [Fact]
    public void ATestCanExpandTextWithoutAffectingAnyoneElse()
    {
        Strings.Transform = (_, value) => value + "!";
        try
        {
            Assert.Equal("Partly cloudy!", Strings.Get("Weather_Summary_PartlyCloudy"));
            // Another execution context, as a test running in parallel has, is untouched.
            string? elsewhere = null;
            var thread = new Thread(() => elsewhere = Strings.Get("Weather_Summary_PartlyCloudy"));
            using (ExecutionContext.SuppressFlow()) thread.Start();
            thread.Join();
            Assert.Equal("Partly cloudy", elsewhere);
        }
        finally { Strings.Transform = null; }
    }

    // ---- Culture: presentation follows it, machine formats never do --------------------------------

    [Fact]
    public void DisplayedQuantitiesFollowTheCultureButKeepTheNoctaxisUnit()
    {
        // The units setting decides metres or miles; the culture only decides how the number is written.
        Assert.Equal("12.3 km", InCulture("en-GB", () => MeasurementUnits.FormatVisibility(12.34, MeasurementSystem.Metric)));
        Assert.Equal("12,3 km", InCulture("de-DE", () => MeasurementUnits.FormatVisibility(12.34, MeasurementSystem.Metric)));
        Assert.Equal("7,7 mi", InCulture("de-DE", () => MeasurementUnits.FormatVisibility(12.34, MeasurementSystem.Imperial)));
        Assert.Equal("0,49 in", InCulture("de-DE", () => MeasurementUnits.FormatPrecipitation(12.4, MeasurementSystem.Imperial)));
        Assert.Equal("Weather visibility: 12,3 km", InCulture("de-DE", () => FramingVisibilityAssessment.WeatherVisibilityStatus(12_340, MeasurementProfile.Metric)));
        Assert.Equal("Weather visibility: 7,7 mi", InCulture("de-DE", () => FramingVisibilityAssessment.WeatherVisibilityStatus(12_340, MeasurementProfile.UkMixed)));
    }

    [Fact]
    public void DisplayedDatesFollowTheCulture()
    {
        var start = new DateTime(2026, 3, 5, 21, 30, 0);
        var british = InCulture("en-GB", () => Strings.Format("Export_Sheet_Subject_Format", "M31", start, start.AddDays(1), "GMT"));
        var german = InCulture("de-DE", () => Strings.Format("Export_Sheet_Subject_Format", "M31", start, start.AddDays(1), "GMT"));
        Assert.Equal("M31 · Thu 5 Mar 2026, 21:30 – Fri 6 Mar 2026, 21:30 GMT", british);
        // The region writes the day and month names; the text stays the application's.
        Assert.NotEqual(british, german);
        Assert.Contains("2026, 21:30", german);
        Assert.Contains("M31", german);
        Assert.NotEqual(british, german);
    }

    [Fact]
    public async Task PersistedSettingsAreWrittenAndReadTheSameInEveryCulture()
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-localisation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = PlanningSession.Default(Instant.FromUtc(2026, 3, 5, 21, 30), "UTC") with
            { Observer = new GeoCoordinate(53.55865, -0.48052, 12.5) };
            var state = new PersistedState(1, new AppSettings(CameraHeightAboveGroundMetres: 1.75,
                LightPollution: new LightPollutionPreferences(true, .35), TerrainRelief: new TerrainReliefPreferences(true, .45)),
                [], session, null);
            var paths = new Paths(directory);
            await InCultureAsync("de-DE", () => new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).SaveAsync(state, CancellationToken.None));

            var json = await File.ReadAllTextAsync(Path.Combine(directory, "state.json"));
            Assert.Contains("53.55865", json);
            Assert.Contains("0.35", json);
            Assert.Contains("1.75", json);
            Assert.DoesNotContain("53,55865", json);
            Assert.DoesNotContain("0,35", json);

            foreach (var culture in new[] { "de-DE", "en-GB", "fr-FR" })
            {
                var loaded = await InCultureAsync(culture, () => new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).LoadAsync(CancellationToken.None));
                Assert.Equal(53.55865, loaded.Session.Observer.Latitude);
                Assert.Equal(.35, loaded.Settings.LightPollution!.Opacity);
                Assert.Equal(.45, loaded.Settings.TerrainRelief!.Opacity);
                Assert.Equal(1.75, loaded.Settings.CameraHeightAboveGroundMetres);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void MachineIdentifiersIgnoreTheCulture()
    {
        foreach (var culture in new[] { "de-DE", "fr-FR", "ar-SA", "th-TH" })
        {
            Assert.Equal("12/2042/1323", InCulture(culture, () => new TerrariumTileKey(12, 2042, 1323).Id));
            Assert.Equal(MeasurementUnits.UkId, InCulture(culture, () => MeasurementUnits.NormaliseId("uk")));
            Assert.Equal(MeasurementSystem.Imperial, InCulture(culture, () => MeasurementUnits.Parse("Imperial")));
        }
    }

    [GeneratedRegex(@"\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    private static string Invariant(Func<string> action) => InCulture("en-GB", action);

    private static T InCulture<T>(string name, Func<T> action)
    {
        var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            return action();
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = ui; }
    }

    private static async Task<T> InCultureAsync<T>(string name, Func<Task<T>> action)
    {
        var (culture, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            return await action();
        }
        finally { CultureInfo.CurrentCulture = culture; CultureInfo.CurrentUICulture = ui; }
    }

    private static Task InCultureAsync(string name, Func<Task> action) =>
        InCultureAsync(name, async () => { await action(); return true; });

    private sealed record Paths(string Root) : IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => Root;
    }
}
