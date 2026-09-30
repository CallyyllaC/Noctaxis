using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;
using Noctaxis.Core.Persistence;
using NodaTime;

namespace Noctaxis.Core.Tests;

public sealed partial class LocalisationTests
{
    [Fact]
    public void TheLanguageDefaultsToBritishEnglishWhichIsTheOnlyOneShipped()
    {
        Assert.Equal("en-GB", AppLanguages.DefaultId);
        Assert.Equal(["en-GB"], AppLanguages.SupportedIds);
        Assert.Equal("en-GB", new AppSettings().Language);
        Assert.Equal("en-GB", new AppSettings().EffectiveLanguage);
        Assert.Equal("en-GB", AppLanguages.Current);
    }

    [Theory]
    [InlineData(null), InlineData(""), InlineData("  "), InlineData("fr-FR"), InlineData("not a language"), InlineData("en")]
    public void AnUnknownOrMissingLanguageIsTheDefault(string? stored) =>
        Assert.Equal("en-GB", (new AppSettings() with { Language = stored! }).EffectiveLanguage);

    [Fact]
    public void TheLanguageIdentifierIsMatchedTheSameInEveryCulture()
    {
        // tr-TR has its own casing rules for i/I: an identifier must never be compared with them.
        foreach (var culture in new[] { "en-GB", "de-DE", "tr-TR", "ar-SA" })
        {
            Assert.Equal("en-GB", InCulture(culture, () => AppLanguages.Normalise("EN-gb")));
            Assert.Equal("en-GB", InCulture(culture, () => AppLanguages.Normalise(" en-GB ")));
            Assert.Equal("en-GB", InCulture(culture, () => AppLanguages.Culture("en-GB").Name));
        }
    }

    [Fact]
    public async Task TheLanguageIsSavedAsItsIdentifierAndReadBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-language-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new Paths(directory);
            var file = JsonUserDataStore.StateFilePath(paths);
            Assert.Equal(Path.Combine(directory, "state.json"), file);
            // Nothing saved yet: the default, without creating anything.
            Assert.Equal("en-GB", AppLanguages.ReadSaved(file));
            Assert.False(Directory.Exists(directory));

            var state = new PersistedState(4, new AppSettings(), [], PlanningSession.Default(Instant.FromUtc(2026, 3, 5, 21, 30), "UTC"), null);
            // Saved under another culture: the identifier is written as it is, never formatted or translated.
            await InCultureAsync("de-DE", () => new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).SaveAsync(state, CancellationToken.None));
            Assert.Contains("\"Language\": \"en-GB\"", await File.ReadAllTextAsync(file));
            Assert.Equal("en-GB", AppLanguages.ReadSaved(file));
            var loaded = await new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).LoadAsync(CancellationToken.None);
            Assert.Equal("en-GB", loaded.Settings.Language);

            // A file from before the setting existed, one naming a language this version lacks, and a damaged one.
            var json = await File.ReadAllTextAsync(file);
            await File.WriteAllTextAsync(file, json.Replace("\"Language\": \"en-GB\",", "").Replace(",\r\n    \"Language\": \"en-GB\"", "").Replace(",\n    \"Language\": \"en-GB\"", ""));
            Assert.DoesNotContain("Language", await File.ReadAllTextAsync(file));
            Assert.Equal("en-GB", AppLanguages.ReadSaved(file));
            Assert.Equal("en-GB", (await new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).LoadAsync(CancellationToken.None)).Settings.EffectiveLanguage);
            await File.WriteAllTextAsync(file, json.Replace("\"Language\": \"en-GB\"", "\"Language\": \"xx-YY\""));
            Assert.Equal("en-GB", AppLanguages.ReadSaved(file));
            Assert.Equal("en-GB", (await new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).LoadAsync(CancellationToken.None)).Settings.EffectiveLanguage);
            await File.WriteAllTextAsync(file, "{ not json");
            Assert.Equal("en-GB", AppLanguages.ReadSaved(file));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("en-GB", "1,234.5", "05/03/2026")]
    [InlineData("de-DE", "1.234,5", "05.03.2026")]
    [InlineData("tr-TR", "1.234,5", "5.03.2026")]
    [InlineData("fa-IR", null, null)]
    public void ApplyingTheLanguageChoosesTheTextAndLeavesTheRegionalFormatAlone(string region, string? number, string? date)
    {
        var saved = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture,
            CultureInfo.DefaultThreadCurrentCulture, CultureInfo.DefaultThreadCurrentUICulture);
        try
        {
            // The user's regional format, as the operating system gives it, and some other UI language.
            var regional = CultureInfo.GetCultureInfo(region);
            CultureInfo.CurrentCulture = regional;
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            var units = new AppSettings(Units: "Imperial");

            // An unsupported request applies the default language rather than failing.
            AppLanguages.Apply("fr-FR");
            AppLanguages.Apply("en-GB"); // Applying again changes nothing.

            Assert.Equal("en-GB", AppLanguages.Current);
            Assert.Equal("en-GB", CultureInfo.CurrentUICulture.Name);
            Assert.Equal("en-GB", CultureInfo.DefaultThreadCurrentUICulture?.Name);
            // The language is not the region: number and date formats are still the user's.
            Assert.Same(regional, CultureInfo.CurrentCulture);
            Assert.Equal(saved.Item3, CultureInfo.DefaultThreadCurrentCulture);
            if (number is not null) Assert.Equal(number, 1234.5.ToString("N1", CultureInfo.CurrentCulture));
            if (date is not null) Assert.Equal(date, new DateTime(2026, 3, 5).ToString("d", CultureInfo.CurrentCulture));
            // English text, formatted for the region, in the units the user chose: three separate things.
            Assert.Equal(Strings.Entries("Planning")["Planning_Visibility_DataUnavailable"], Strings.Get("Planning_Visibility_DataUnavailable"));
            Assert.Equal("Imperial", units.Units);
            Assert.Same(Noctaxis.Core.Measurements.MeasurementProfile.Imperial, units.MeasurementProfile);
            // Machine values are what they always were.
            Assert.Equal("1234.5", 1234.5.ToString(CultureInfo.InvariantCulture));
            Assert.Equal("12/2042/1323", new Noctaxis.Core.Environment.TerrariumTileKey(12, 2042, 1323).Id);
            Assert.Equal("en-GB", AppLanguages.Normalise("EN-gb"));
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = saved.Item3;
            CultureInfo.DefaultThreadCurrentUICulture = saved.Item4;
            CultureInfo.CurrentCulture = saved.Item1;
            CultureInfo.CurrentUICulture = saved.Item2;
        }
    }
}
