using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;
using Noctaxis.Core.Weather;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    private static readonly string[] DesktopDomains = ["Common", "Dialogs", "Locations", "Planner", "Settings", "Shell", "Supporter"];
    private static readonly string[] LocalisationCoreDomains = ["Data", "Equipment", "Export", "Places", "Planning", "Terrain", "Units", "Weather"];

    [GeneratedRegex("\"(?<key>[A-Z][A-Za-z]+_[A-Za-z0-9_]*)\"")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"\{loc:Text\s+(?<key>[A-Za-z0-9_]+)\}")]
    private static partial Regex MarkupKey();

    // ---- Resources ------------------------------------------------------------------------------

    [Fact]
    public void EveryResourceSetLoadsAndAKeyBelongsToExactlyOneOfThem()
    {
        _ = TestMarkup.ReadMainWindow(); // Loads the Desktop module, which registers its resources.
        Assert.Equal(DesktopDomains.Concat(LocalisationCoreDomains).Order(), Strings.RegisteredDomains.Order());
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in Strings.RegisteredDomains)
        {
            var entries = Strings.Entries(domain);
            Assert.NotEmpty(entries);
            foreach (var (key, text) in entries)
            {
                // The domain is the file: a key can only ever be defined in the one file it names.
                Assert.StartsWith(domain + "_", key);
                Assert.False(string.IsNullOrEmpty(text), key);
                Assert.True(all.TryAdd(key, text), $"{key} is defined twice (keys differing only by case conflict)");
                Assert.False(text.StartsWith('⟦'), key);
            }
        }
        Assert.True(all.Count > 600, $"only {all.Count} keys");
    }

    [Fact]
    public void EveryKeyTheSourceRefersToResolvesAndNoResourceIsOrphaned()
    {
        _ = TestMarkup.ReadMainWindow();
        var root = Path.GetDirectoryName(TestPaths.DesktopProject)!;
        var known = Strings.RegisteredDomains.SelectMany(domain => Strings.Entries(domain).Keys).ToHashSet(StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var project in new[] { "Noctaxis.Core", "Noctaxis.Desktop" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                var markup = file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase);
                if (!markup && !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                var source = File.ReadAllText(file);
                foreach (Match match in (markup ? MarkupKey() : KeyLiteral()).Matches(source))
                {
                    var key = match.Groups["key"].Value;
                    if (!Strings.RegisteredDomains.Contains(key[..key.IndexOf('_')])) continue;
                    referenced.Add(key);
                    // A literal is a whole key, or the start of a family of keys completed at run time
                    // (Settings_Weather_Field_ + the field, Dialogs_RemoveCamera + _Title).
                    var family = key.EndsWith('_') ? key : key + "_";
                    if (!known.Contains(key) && !known.Any(candidate => candidate.StartsWith(family, StringComparison.Ordinal)))
                        missing.Add($"{key} ({Path.GetFileName(file)})");
                }
            }
        Assert.True(missing.Count == 0, "Keys used but not defined: " + string.Join(", ", missing));
        var orphans = known.Where(key => !referenced.Contains(key) &&
            !referenced.Any(reference => key.StartsWith(reference.EndsWith('_') ? reference : reference + "_", StringComparison.Ordinal))).ToArray();
        Assert.True(orphans.Length == 0, "Resources nothing refers to: " + string.Join(", ", orphans));
    }

    [Fact]
    public void EveryEnumDrivenKeyExists()
    {
        _ = TestMarkup.ReadMainWindow();
        Assert.All(Enum.GetValues<WeatherField>(), field => Assert.True(Strings.TryGet("Settings_Weather_Field_" + field, out _), field.ToString()));
        foreach (var prefix in new[] { "Dialogs_RemoveCamera", "Dialogs_RemoveLens" })
            Assert.All(new[] { "_Title", "_Unnamed_Message", "_Confirm" }, suffix => Assert.True(Strings.TryGet(prefix + suffix, out _), prefix + suffix));
    }

    // ---- XAML and C# use the same lookup --------------------------------------------------------

    [AvaloniaFact]
    public async Task MarkupAndCodeResolveTheirTextThroughTheSameResources()
    {
        var (vm, window) = await CreateLocalisedWindowAsync();
        try
        {
            // XAML: static text, an accessible name and a binding format string.
            Assert.Equal(Strings.Get("Shell_Window_Title"), window.Title);
            var categories = window.FindControl<TabControl>("SettingsCategories")!;
            Assert.Equal(Strings.Get("Settings_SettingsCategories_AccessibleName"), AutomationProperties.GetName(categories));
            Assert.Equal(Strings.Get("Settings_General_Tab"), categories.Items.OfType<TabItem>().First().Header);
            Assert.Equal("Appearance", Strings.Get("Settings_Appearance_Tab"));
            // C#: view-model text comes from the same place.
            Assert.Equal(Strings.Get("Supporter_Licence_Status_None"), vm.SupporterLicenceStateLabel);
            Assert.Equal("No licence installed", vm.SupporterLicenceStateLabel);
            Assert.Equal(Strings.Format("Planner_Targets_VisibleCount_Format", vm.VisibleCelestialCount, 8), vm.VisibleCelestialCountText);
            Assert.Equal("STALE · ", Strings.Get("Planner_Weather_Stale_Prefix"));
        }
        finally { window.Close(); }
    }

    [Fact]
    public void ParameterisedTextIsOneSentenceWithPlaceholders()
    {
        _ = TestMarkup.ReadMainWindow();
        Assert.Equal("Delete ‘Kielder’? This cannot be undone.", Strings.Format("Dialogs_DeleteLocation_Message_Format", "Kielder"));
        Assert.Equal("“Sony A6300” will be removed from your equipment.", Strings.Format("Dialogs_RemoveEquipment_Named_Message", "Sony A6300"));
        Assert.Equal("Show M31 in Planner", Strings.Format("Settings_Celestial_ShowObjectInPlanner_AccessibleName", "M31"));
        // English keeps its existing singular and plural wording.
        Assert.Equal("1 saved location", Strings.Get("Locations_Summary_One"));
        Assert.Equal("3 saved locations", Strings.Format("Locations_Summary_Many_Format", 3));
    }

    // ---- Accessibility --------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task AccessibleNamesAreTranslatableTextWhileControlNamesStayFixed()
    {
        using var expanded = PseudoLocalisation.Apply();
        var (_, window) = await CreateLocalisedWindowAsync();
        try
        {
            var pages = (TabControl)((Grid)window.Content!).Children[1];
            pages.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            // Found by their fixed names, whatever the language...
            var categories = window.FindControl<TabControl>("SettingsCategories")!;
            var layers = window.FindControl<PlannerLayersControl>("PlannerLayers")!;
            var button = layers.FindControl<Button>("LayersButton")!;
            button.Flyout!.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            var terrain = layers.FindControl<CheckBox>("TerrainReliefToggle")!;
            var slider = layers.FindControl<Slider>("TerrainReliefOpacitySlider")!;
            // ...while what assistive technology reads is resource text (here, the expanded stand-in).
            Assert.Equal(PseudoLocalisation.Expand("Settings categories"), AutomationProperties.GetName(categories));
            Assert.Equal(PseudoLocalisation.Expand("Map layers"), AutomationProperties.GetName(button));
            Assert.Equal(PseudoLocalisation.Expand("Choose the map layers shown in the Planner."), AutomationProperties.GetHelpText(button));
            Assert.Equal(PseudoLocalisation.Expand("Terrain relief"), terrain.Content);
            Assert.Equal(PseudoLocalisation.Expand("Terrain relief opacity"), AutomationProperties.GetName(slider));
            Assert.Equal(PseudoLocalisation.Expand("Adjust how strongly hills and valleys are shaded."), AutomationProperties.GetHelpText(slider));
            button.Flyout.Hide();
            // Every accessible name and help text set in markup comes from a resource, never a literal.
            foreach (var file in Directory.EnumerateFiles(TestPaths.DesktopProject, "*.axaml", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                    file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                Assert.DoesNotMatch("AutomationProperties\\.(Name|HelpText)=\"[^{\"]", File.ReadAllText(file));
                Assert.DoesNotMatch("ToolTip\\.Tip=\"[^{\"]", File.ReadAllText(file));
            }
        }
        finally { window.Close(); }
    }

    // ---- Culture --------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task DisplayedValuesFollowTheCultureWhileMachineValuesStayInvariant()
    {
        var (vm, window) = await CreateLocalisedWindowAsync();
        try
        {
            // Text assembled in code, as a user with a German Windows region sees it: English words,
            // with decimals, thousands and dates in the region's form.
            var metric = Noctaxis.Core.Measurements.MeasurementProfile.Metric;
            Assert.Equal("Effective camera altitude: 123,5 m ASL (ground + 1,7 m camera height)",
                TestCulture.InRegion("de-DE", () => Strings.Format("Planner_Location_EffectiveAltitude_Format",
                    metric.Height.Of(123.45, 1).Format(), metric.Height.Of(1.7, 1).Format())));
            Assert.Equal("1.234,5 MiB / 2 GiB · 12.345 files",
                TestCulture.InRegion("de-DE", () => Strings.Format("Settings_Data_TerrainCache_Usage_Format", 1234.5, 2d, 12345)));
            var date = new DateTimeOffset(2026, 3, 5, 12, 0, 0, TimeSpan.Zero);
            Assert.Equal("Used 05 Mar 2026", TestCulture.InRegion("en-GB", () => Strings.Format("Locations_Card_LastUsed_Date_Format", date)));
            Assert.NotEqual("Used 05 Mar 2026", TestCulture.InRegion("de-DE", () => Strings.Format("Locations_Card_LastUsed_Date_Format", date)));
            Assert.StartsWith("Used ", TestCulture.InRegion("de-DE", () => Strings.Format("Locations_Card_LastUsed_Date_Format", date)));
            Assert.Equal("Prime · 35,5 mm", TestCulture.InRegion("de-DE", () => Strings.Format("Planner_Camera_PrimeLens_Format", 35.5)));
            // The Noctaxis measurement profile, not the region, decides the unit.
            Assert.Equal("7,7 mi", TestCulture.InRegion("de-DE", () => Noctaxis.Core.Measurements.MeasurementUnits.FormatVisibility(12.34,
                Noctaxis.Core.Measurements.MeasurementSystem.Uk)));

            // Machine values: a link handed to a map provider is the same in every culture.
            foreach (var culture in new[] { "en-GB", "de-DE", "fr-FR", "ar-SA" })
            {
                var uri = TestCulture.In(culture, () => ExternalMapService.CreateUri(53.55865, -0.48052, ExternalMapProvider.OpenStreetMap))!;
                Assert.Equal("https://www.openstreetmap.org/?mlat=53.55865&mlon=-0.48052#map=16/53.55865/-0.48052", uri.AbsoluteUri);
            }
            _ = vm;
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BoundValuesAreFormattedForTheCulture()
    {
        // A binding's format string is resource text too; the value it formats follows the region alone.
        var text = TestCulture.InRegion("de-DE", () =>
        {
            var block = new TextBlock { DataContext = 35.5 };
            block.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(".") { StringFormat = Strings.Get("Planner_Camera_FocalLength_Format") });
            var host = new Window { Content = block };
            host.Show();
            try { Dispatcher.UIThread.RunJobs(); return block.Text; }
            finally { host.Close(); }
        });
        Assert.Equal("35,5 mm", text);
    }

    private async Task<(ViewModels.MainViewModel ViewModel, MainWindow Window)> CreateLocalisedWindowAsync()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, CreateSupporterStore(new AppSettings()), new FakeExporter());
        await vm.InitializeAsync();
        var search = new ViewModels.LocationSearchViewModel(new FakeLocationSearchProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ViewModels.LocationSearchViewModel>.Instance);
        var window = new MainWindow(vm, new DesktopDialogService(search)) { Width = 1280, Height = 820 };
        var map = window.FindControl<PlannerMapView>("PlannerMap")!;
        foreach (var (id, layer) in map.CompositionForTesting.NativeLayerOrder)
            if (id is "basemap" or Mapping.PlannerMapComposition.BasemapReferenceId) layer.Enabled = false; // Offline.
        TestMaps.OfflinePip(window);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (vm, window);
    }
}
