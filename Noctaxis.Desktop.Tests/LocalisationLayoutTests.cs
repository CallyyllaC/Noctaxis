using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    private static readonly double[] LocalisationTextScales = [1, 1.5, 2];

    /// <summary>
    /// Every screen, at every text size, with every text about a third longer: nothing may be cut off,
    /// trimmed or pushed outside its container that fits in English. This is the check a future
    /// translation relies on; it is not a pixel comparison.
    /// </summary>
    [AvaloniaFact]
    public async Task LongerTranslatedTextStillFitsOnEveryScreenAtEveryTextSize()
    {
        var english = await AuditScreensAsync(expand: false);
        var expanded = await AuditScreensAsync(expand: true);
        var report = Environment.GetEnvironmentVariable("NOCTAXIS_LAYOUT_AUDIT");
        if (!string.IsNullOrWhiteSpace(report))
            File.WriteAllLines(report, english.SelectMany(screen => screen.Value.Select(p => $"english  | {screen.Key} | #{p.Index} {p.Kind}: {p.Text}"))
                .Concat(expanded.SelectMany(screen => screen.Value.Select(p => $"expanded | {screen.Key} | #{p.Index} {p.Kind}: {p.Text}"))));

        Assert.Equal(english.Keys, expanded.Keys);
        Assert.True(english.Count >= 3 * (2 + 9 + 1) + 3, $"only {english.Count} screens were audited");
        // The same screens fit in English...
        Assert.All(english, screen => Assert.True(screen.Value.Count == 0,
            $"{screen.Key}: {string.Join("; ", screen.Value.Select(p => $"{p.Kind}: {p.Text}"))}"));
        // ...and still fit when every text is about 35% longer.
        Assert.All(expanded, screen => Assert.True(screen.Value.Count == 0,
            $"{screen.Key}: {string.Join("; ", screen.Value.Select(p => $"{p.Kind}: {p.Text}"))}"));
    }

    private async Task<Dictionary<string, IReadOnlyList<LayoutAudit.Problem>>> AuditScreensAsync(bool expand)
    {
        using var scope = expand ? PseudoLocalisation.Apply() : null;
        var results = new Dictionary<string, IReadOnlyList<LayoutAudit.Problem>>();
        var catalogue = new OpenNgcTargetCatalogue();
        var now = new DateTimeOffset(2026, 3, 5, 21, 0, 0, TimeSpan.Zero);
        SavedLocation[] locations =
        [
            new(Guid.NewGuid(), "Kielder Observatory car park", new GeoCoordinate(55.2327, -2.6163), "Europe/London",
                Notes: "Dark site with a wide northern horizon", IsFavourite: true, LastUsedUtc: now, DateAddedUtc: now),
            new(Guid.NewGuid(), "Brigg", new GeoCoordinate(53.5587, -0.4805), "Europe/London", DateAddedUtc: now)
        ];
        var store = new FakeStore(new PersistedState(1, new AppSettings(), locations,
            PlanningSession.Default(Instant.FromUtc(2026, 3, 5, 21, 0), "UTC"), null));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        var themes = ((App)Application.Current!).Themes!;
        vm.AttachThemeService(themes);
        var search = new LocationSearchViewModel(new FakeLocationSearchProvider(), NullLogger<LocationSearchViewModel>.Instance);
        var window = new MainWindow(vm, new DesktopDialogService(search)) { Width = 1280, Height = 820 };
        var map = window.FindControl<PlannerMapView>("PlannerMap")!;
        foreach (var (id, layer) in map.CompositionForTesting.NativeLayerOrder)
            if (id is "basemap" or Mapping.PlannerMapComposition.BasemapReferenceId) layer.Enabled = false; // Offline.
        TestMaps.OfflinePip(window);
        window.Show();
        try
        {
            var pages = (TabControl)((Grid)window.Content!).Children[1];
            var categories = window.FindControl<TabControl>("SettingsCategories")!;
            foreach (var scale in LocalisationTextScales)
            {
                await vm.ApplyAppearanceAsync(new("builtin.noctaxis", TextScale: scale));
                void Audit(string screen, Visual root)
                {
                    Dispatcher.UIThread.RunJobs();
                    (root as Avalonia.Layout.Layoutable ?? window).UpdateLayout();
                    results[$"{scale:P0} {screen}"] = LayoutAudit.Problems(root);
                }
                pages.SelectedIndex = 0; Audit("Locations", window);
                pages.SelectedIndex = 1; Audit("Planner", window);
                var layers = window.FindControl<PlannerLayersControl>("PlannerLayers")!;
                var button = layers.FindControl<Button>("LayersButton")!;
                button.Flyout!.ShowAt(button);
                Audit("Planner layers", window);
                button.Flyout.Hide();
                pages.SelectedIndex = 2;
                for (var category = 0; category < categories.ItemCount; category++)
                {
                    categories.SelectedIndex = category;
                    Audit($"Settings category {category}", window);
                }
                foreach (var (name, dialog) in new (string, Window)[]
                         {
                             ("Confirmation dialog", new ConfirmationDialog(Noctaxis.Core.Localisation.Strings.Get("Dialogs_ClearTerrainCache_Title"),
                                 Noctaxis.Core.Localisation.Strings.Get("Dialogs_ClearTerrainCache_Message"), Noctaxis.Core.Localisation.Strings.Get("Dialogs_ClearTerrainCache_Confirm"))),
                             ("Edit location dialog", new SavedLocationEditDialog(locations[0])),
                             ("Search dialog", new LocationSearchDialog(search))
                         })
                {
                    dialog.Show();
                    try { Audit(name, dialog); }
                    finally { dialog.Close(); }
                }
            }
        }
        finally { window.Close(); themes.Apply(new()); }
        return results;
    }
}
