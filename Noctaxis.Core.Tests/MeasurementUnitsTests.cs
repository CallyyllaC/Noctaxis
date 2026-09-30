using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Measurements;
using Noctaxis.Core.Persistence;
using NodaTime;

namespace Noctaxis.Core.Tests;

public sealed class MeasurementUnitsTests
{
    // ---- Identifiers and persistence --------------------------------------------------------------

    [Theory]
    [InlineData("Metric", MeasurementSystem.Metric)]
    [InlineData("metric", MeasurementSystem.Metric)]
    [InlineData("Imperial", MeasurementSystem.Imperial)]
    [InlineData("IMPERIAL", MeasurementSystem.Imperial)]
    [InlineData("UK", MeasurementSystem.Uk)]
    [InlineData("uk", MeasurementSystem.Uk)]
    [InlineData(" UK ", MeasurementSystem.Uk)]
    // Anything Noctaxis never saved is the default, UK Mixed.
    [InlineData("United Kingdom", MeasurementSystem.Uk)]
    [InlineData("unknown", MeasurementSystem.Uk)]
    [InlineData("", MeasurementSystem.Uk)]
    [InlineData(null, MeasurementSystem.Uk)]
    public void SavedUnitIdentifiers_AreParsedTolerantly(string? value, MeasurementSystem expected) =>
        Assert.Equal(expected, MeasurementUnits.Parse(value));

    [Fact]
    public void ThreeProfilesAreOfferedAndUkMixedIsTheDefault()
    {
        Assert.Equal(["Metric", "UK", "Imperial"], MeasurementUnits.Options);
        Assert.Equal(MeasurementUnits.Options, MeasurementProfile.All.Select(profile => profile.Id));
        Assert.Equal("UK", MeasurementUnits.DefaultId);
        Assert.Equal("UK", new AppSettings().Units);
        Assert.Equal(MeasurementSystem.Uk, new AppSettings().EffectiveMeasurementSystem);
        Assert.Same(MeasurementProfile.UkMixed, new AppSettings().MeasurementProfile);
        Assert.All(MeasurementProfile.All, profile => Assert.Same(profile, MeasurementProfile.For(profile.Id)));
        Assert.All(MeasurementProfile.All, profile => Assert.Same(profile, MeasurementProfile.For(profile.System)));
    }

    [Fact]
    public void IdentifiersAreReadTheSameInEveryCulture()
    {
        foreach (var culture in new[] { "en-GB", "de-DE", "tr-TR", "ar-SA" })
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                // tr-TR lower-cases I to a dotless ı: identifiers must not be compared that way.
                Assert.Equal(MeasurementSystem.Imperial, MeasurementUnits.Parse("IMPERIAL"));
                Assert.Equal(MeasurementSystem.Imperial, MeasurementUnits.Parse("imperial"));
                Assert.Equal(MeasurementSystem.Metric, MeasurementUnits.Parse("METRIC"));
                Assert.Equal("UK", MeasurementUnits.NormaliseId("uk"));
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }
    }

    [Fact]
    public async Task EveryProfileRoundTripsAndOlderFilesKeepTheirMeaning()
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-units-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new TestPaths(directory);
            var file = JsonUserDataStore.StateFilePath(paths);
            JsonUserDataStore Store() => new(paths, NullLogger<JsonUserDataStore>.Instance);
            var session = PlanningSession.Default(Instant.FromUtc(2026, 3, 5, 21, 30), "UTC");
            foreach (var id in MeasurementUnits.Options)
            {
                await Store().SaveAsync(new PersistedState(4, new AppSettings(Units: id), [], session, null), CancellationToken.None);
                Assert.Contains($"\"Units\": \"{id}\"", await File.ReadAllTextAsync(file));
                var loaded = await Store().LoadAsync(CancellationToken.None);
                Assert.Equal(id, loaded.Settings.Units);
                Assert.Same(MeasurementProfile.For(id), loaded.Settings.MeasurementProfile);
            }

            // Values written by earlier versions, exactly as they were saved.
            var json = await File.ReadAllTextAsync(file);
            async Task<AppSettings> LoadWith(string units)
            {
                await File.WriteAllTextAsync(file, json.Replace("\"Units\": \"Imperial\"", units));
                return (await Store().LoadAsync(CancellationToken.None)).Settings;
            }
            Assert.Equal("Metric", (await LoadWith("\"Units\": \"Metric\"")).Units);     // An explicit Metric stays Metric.
            Assert.Equal("Imperial", (await LoadWith("\"Units\": \"Imperial\"")).Units);
            Assert.Equal("UK", (await LoadWith("\"Units\": \"UK\"")).Units);
            Assert.Equal("UK", (await LoadWith("\"Units\": \"United Kingdom\"")).Units);
            // No value, or one Noctaxis never wrote: the default, stored as its identifier.
            Assert.Equal("UK", (await LoadWith("\"Units\": \"furlongs\"")).Units);
            Assert.Equal("UK", (await LoadWith("\"Units\": null")).Units);
            Assert.Equal("UK", (await LoadWith("\"UnitsRemoved\": 0")).Units);
            // The language is stored beside it and is unaffected by any of this.
            Assert.Equal("en-GB", (await LoadWith("\"Units\": \"Imperial\"")).Language);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    // ---- The mapping: every physical quantity under every profile ---------------------------------

    private static readonly (string Profile, string Quantity, double Stored, string Unit, double Shown, int DecimalPlaces)[] Rows =
    [
        // profile, quantity, stored value, unit, shown value, decimal places
        ("Metric", "Temperature", 20, "°C", 20, 0),
        ("Metric", "WindSpeed", 10, "km/h", 36, 0),
        ("Metric", "LongDistance", 12.5, "km", 12.5, 1),
        ("Metric", "ShortDistance", 350, "m", 350, 0),
        ("Metric", "Height", 123.4, "m", 123.4, 0),
        ("Metric", "Precipitation", 25.4, "mm", 25.4, 1),
        ("Metric", "Pressure", 1013.25, "hPa", 1013.25, 0),

        // UK Mixed: miles and miles per hour; everything else metric.
        ("UK", "Temperature", 20, "°C", 20, 0),
        ("UK", "WindSpeed", 10, "mph", 22.369362920544, 0),
        ("UK", "LongDistance", 12.5, "mi", 7.767139902966676, 1),
        ("UK", "ShortDistance", 350, "m", 350, 0),
        ("UK", "Height", 123.4, "m", 123.4, 0),
        ("UK", "Precipitation", 25.4, "mm", 25.4, 1),
        ("UK", "Pressure", 1013.25, "hPa", 1013.25, 0),

        ("Imperial", "Temperature", 20, "°F", 68, 0),
        ("Imperial", "WindSpeed", 10, "mph", 22.369362920544, 0),
        ("Imperial", "LongDistance", 12.5, "mi", 7.767139902966676, 1),
        ("Imperial", "ShortDistance", 350, "ft", 1148.2939632545932, 0),
        ("Imperial", "Height", 123.4, "ft", 404.8556430446194, 0),
        ("Imperial", "Precipitation", 25.4, "in", 1, 2),
        ("Imperial", "Pressure", 1013.25, "inHg", 29.921255347, 2),
    ];

    public static TheoryData<string, string, double, string, double, int> Mapping
    {
        get
        {
            var data = new TheoryData<string, string, double, string, double, int>();
            foreach (var row in Rows) data.Add(row.Profile, row.Quantity, row.Stored, row.Unit, row.Shown, row.DecimalPlaces);
            return data;
        }
    }

    [Theory, MemberData(nameof(Mapping))]
    public void EachQuantityHasItsUnitAndConversionUnderEachProfile(string profileId, string quantity, double stored,
        string unit, double shown, int decimalPlaces)
    {
        var profile = MeasurementProfile.For(profileId);
        var measurement = (MeasurementUnit)typeof(MeasurementProfile).GetProperty(quantity)!.GetValue(profile)!;
        var value = measurement.Of(stored);
        Assert.Equal(unit, value.Unit);
        Assert.Equal(shown, value.Value, 6);
        Assert.Equal(decimalPlaces, value.DecimalPlaces);
        // The conversion is exact both ways, so an edited value is stored as what was typed.
        Assert.Equal(stored, measurement.ToBase(measurement.FromBase(stored)), 9);
        Assert.False(string.IsNullOrWhiteSpace(measurement.Name));
    }

    [Fact]
    public void TheMappingCoversEveryQuantityOfEveryProfile()
    {
        var quantities = typeof(MeasurementProfile).GetProperties()
            .Where(property => property.PropertyType == typeof(MeasurementUnit)).Select(property => property.Name).Order().ToArray();
        Assert.Equal(["Height", "LongDistance", "Precipitation", "Pressure", "ShortDistance", "Temperature", "WindSpeed"], quantities);
        var covered = Rows.Select(row => (row.Profile, row.Quantity)).ToHashSet();
        foreach (var profile in MeasurementProfile.All)
            foreach (var quantity in quantities)
                Assert.Contains((profile.Id, quantity), covered);
    }

    [Fact]
    public void ModernSharedDefinitionsAreUsed()
    {
        Assert.Equal(1, MeasurementProfile.Imperial.LongDistance.FromBase(1.609344), 12);   // The international mile.
        Assert.Equal(1, MeasurementProfile.Imperial.Height.FromBase(0.3048), 12);           // The international foot.
        Assert.Equal(1, MeasurementProfile.Imperial.Precipitation.FromBase(25.4), 12);      // The inch.
        Assert.Equal(60, MeasurementProfile.Imperial.WindSpeed.FromBase(26.8224), 9);       // 60 mph.
        Assert.Equal(32, MeasurementProfile.Imperial.Temperature.FromBase(0), 12);
        Assert.Equal(-40, MeasurementProfile.Imperial.Temperature.FromBase(-40), 12);
        Assert.Equal(100, MeasurementProfile.Metric.WindSpeed.FromBase(27.7777777778), 6);  // 100 km/h.
    }

    [Fact]
    public void ADistanceIsShortOrLongAsItsSizeRequires()
    {
        Assert.Equal(new MeasurementValue(999, "m"), MeasurementProfile.Metric.Distance(999));
        Assert.Equal(new MeasurementValue(1, "km", 1), MeasurementProfile.Metric.Distance(1_000));
        // UK Mixed: metres up to a mile, then miles.
        Assert.Equal(new MeasurementValue(1_600, "m"), MeasurementProfile.UkMixed.Distance(1_600));
        Assert.Equal("mi", MeasurementProfile.UkMixed.Distance(1_609.344).Unit);
        Assert.Equal(1, MeasurementProfile.UkMixed.Distance(1_609.344).Value, 9);
        Assert.Equal("ft", MeasurementProfile.Imperial.Distance(1_600).Unit);
        Assert.Equal("mi", MeasurementProfile.Imperial.Distance(1_700).Unit);

        // Estimates are rounded to the unit's step and never shown as nothing.
        Assert.Equal(new MeasurementValue(350, "m"), MeasurementProfile.Metric.ApproximateDistance(347));
        Assert.Equal(new MeasurementValue(10, "m"), MeasurementProfile.Metric.ApproximateDistance(2));
        Assert.Equal(new MeasurementValue(350, "m"), MeasurementProfile.UkMixed.ApproximateDistance(347));
        Assert.Equal(new MeasurementValue(1_150, "ft"), MeasurementProfile.Imperial.ApproximateDistance(347));
        Assert.Equal(new MeasurementValue(50, "ft"), MeasurementProfile.Imperial.ApproximateDistance(2));
        Assert.Equal("km", MeasurementProfile.Metric.ApproximateDistance(5_000).Unit);
        Assert.Equal("mi", MeasurementProfile.UkMixed.ApproximateDistance(5_000).Unit);
    }

    [Fact]
    public void AScaleBarIsARoundLengthInTheUnitItIsLabelledIn()
    {
        Assert.Equal((5_000d, new MeasurementValue(5, "km")), MeasurementProfile.Metric.ScaleBar(6_000));
        Assert.Equal((500d, new MeasurementValue(500, "m")), MeasurementProfile.Metric.ScaleBar(750));
        var miles = MeasurementProfile.UkMixed.ScaleBar(6_000);
        Assert.Equal(new MeasurementValue(2, "mi"), miles.Label);
        Assert.Equal(3_218.688, miles.Metres, 6);
        Assert.Equal(new MeasurementValue(500, "m"), MeasurementProfile.UkMixed.ScaleBar(750).Label);
        var feet = MeasurementProfile.Imperial.ScaleBar(750);
        Assert.Equal(new MeasurementValue(2_000, "ft"), feet.Label);
        Assert.Equal(609.6, feet.Metres, 6);
        foreach (var profile in MeasurementProfile.All)
            foreach (var metres in new[] { 500d, 750, 1_200, 2_500, 9_999 })
                Assert.InRange(profile.ScaleBar(metres).Metres, metres / 2.5 - 1e-6, metres + 1e-6);
    }

    // ---- The unit comes from the profile; the way the number is written comes from the region ------

    [Theory]
    [InlineData("en-GB", "7.8 mi", "12.5 km", "68 °F", "1.00 in")]
    [InlineData("de-DE", "7,8 mi", "12,5 km", "68 °F", "1,00 in")]
    [InlineData("tr-TR", "7,8 mi", "12,5 km", "68 °F", "1,00 in")]
    public void TheRegionWritesTheNumberAndNeverChoosesTheUnit(string culture, string miles, string kilometres, string fahrenheit, string inches)
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            // Only the regional culture changes; the language of the text stays English.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-GB");
            Assert.Equal(miles, MeasurementProfile.UkMixed.LongDistance.Of(12.5).Format());
            Assert.Equal(miles, MeasurementProfile.Imperial.LongDistance.Of(12.5).Format());
            Assert.Equal(kilometres, MeasurementProfile.Metric.LongDistance.Of(12.5).Format());
            Assert.Equal(fahrenheit, MeasurementProfile.Imperial.Temperature.Of(20).Format());
            Assert.Equal(inches, MeasurementProfile.Imperial.Precipitation.Of(25.4).Format());
            Assert.Equal("Weather visibility: " + miles,
                new FramingVisibilityAssessment(false, null, null, 12_500, "unused").StatusFor(MeasurementProfile.UkMixed));
        }
        finally { (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous; }
    }

    [Fact]
    public void ExportedAndStoredValuesStayInSiWhateverTheProfile()
    {
        // The profile is a presentation choice. What is saved is the identifier and SI numbers.
        var json = JsonSerializer.Serialize(new AppSettings(Units: "Imperial", CameraHeightAboveGroundMetres: 1.7,
            Weather: new WeatherSettings() with { CacheDistanceKilometres = 5 }));
        Assert.Contains("\"Units\":\"Imperial\"", json);
        Assert.Contains("\"CameraHeightAboveGroundMetres\":1.7", json);
        Assert.Contains("\"CacheDistanceKilometres\":5", json);
    }

    private sealed record TestPaths(string Root) : IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => Root;
    }
}
