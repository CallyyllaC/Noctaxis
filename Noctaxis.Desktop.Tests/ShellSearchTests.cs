using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The one search field in the application header, the saved-location filtering it drives, and the
/// Add location tile that now carries the active Planner pin.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static readonly SavedLocation[] SearchFixtures =
    [
        new(Guid.NewGuid(), "Kielder Forest", new(55.2, -2.5), "Europe/London", Notes: "Dark-sky park with a good northern horizon."),
        new(Guid.NewGuid(), "Dürrenstein", new(47.8, 15.0), "Europe/Vienna", Notes: "Alpine ridge."),
        new(Guid.NewGuid(), "Brecon Beacons", new(51.9, -3.4), "Europe/London", Notes: "Reservoir car park.")
    ];

    private async Task<(ViewModels.MainViewModel ViewModel, MainWindow Window)> CreateSearchWindowAsync(
        int width = 1440, int height = 1000, IEnumerable<SavedLocation>? locations = null)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = new FakeStore(new(1, new AppSettings(), (locations ?? SearchFixtures).ToArray(),
            PlanningSession.Default(Instant.FromUtc(2026, 9, 12, 20, 0), "UTC"), null));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        vm.AttachThemeService(((App)Application.Current!).Themes!);
        var window = new MainWindow { Width = width, Height = height, DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (vm, window);
    }

    private static TextBox SearchField(Window window) =>
        window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ShellSearch");

    private static string[] ShownNames(ViewModels.MainViewModel vm) =>
        vm.Locations.GridItems.OfType<LocationCardViewModel>().Select(card => card.Name).ToArray();

    // ---- The Current planner location panel is gone ----------------------------------------------

    [AvaloniaFact]
    public async Task TheCurrentLocationPanelAndItsButtonsAreGoneFromLocations()
    {
        var (_, window) = await CreateSearchWindowAsync();
        try
        {
            var page = window.GetVisualDescendants().OfType<LocationsPage>().Single();
            Assert.DoesNotContain(page.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("currentLocationPanel"));
            Assert.DoesNotContain(page.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("currentLocationMarker"));
            // Save current location and Search for a place lived only in that panel; the Add tile and the
            // header search are where those two things happen now.
            // The first-run panel keeps its own Save button; nothing from the removed panel is on the page.
            var labels = page.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible)
                .Select(button => button.Content?.ToString()).Where(text => text is not null).ToArray();
            Assert.DoesNotContain("Save current location", labels);
            Assert.DoesNotContain("Search for a place", labels);
            Assert.Single(page.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("addLocationCard"));
        }
        finally { window.Close(); }
    }

    // ---- The Add location tile carries the pin ---------------------------------------------------

    [AvaloniaFact]
    public async Task TheAddLocationTileShowsTheActivePinAndFollowsIt()
    {
        var (vm, window) = await CreateSearchWindowAsync();
        try
        {
            var add = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("addLocationCard"));
            var coordinates = add.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "AddLocationCoordinates");
            var action = add.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Add location");
            var explanation = add.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Save the current Planner pin");

            // The same wording a saved card uses for its own coordinates, from the one formatter.
            Assert.Equal(LocationCardViewModel.FormatCoordinates(vm.PreviewObserver), coordinates.Text);
            Assert.True(coordinates.IsEffectivelyVisible);
            // Metadata, not a competing headline: quieter than both lines above it.
            Assert.True(coordinates.FontSize < explanation.FontSize, $"{coordinates.FontSize} vs {explanation.FontSize}");
            Assert.True(explanation.FontSize < action.FontSize);
            Assert.Equal(Application.Current!.FindResource("MutedText"), coordinates.Foreground);
            // Not hover-only: the tile's own name says it, and the tile stays a generous target.
            Assert.Equal($"Add location. Save the current Planner pin at {coordinates.Text}.", AutomationProperties.GetName(add));
            Assert.Null(ToolTip.GetTip(add));
            Assert.True(add.Bounds.Width > 300 && add.Bounds.Height > 150);

            // It follows the Planner.
            vm.PreviewObserver = new GeoCoordinate(12.3456, -65.4321);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(LocationCardViewModel.FormatCoordinates(new GeoCoordinate(12.3456, -65.4321)), coordinates.Text);
            Assert.Contains("12.3456", coordinates.Text);
        }
        finally { window.Close(); }
    }

    // ---- One field, page-specific behaviour -------------------------------------------------------

    [AvaloniaFact]
    public async Task OneSearchFieldServesEveryPageAndSaysWhatEachOneSearches()
    {
        var (vm, window) = await CreateSearchWindowAsync();
        try
        {
            // One control, not one per page.
            Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), box => box.Name == "ShellSearch");
            var field = SearchField(window);
            var search = vm.Search;

            foreach (var (page, placeholder, available) in new[]
                     {
                         (0, "Search saved locations", true),
                         (1, "Search for a place or coordinates", true),
                         (2, "Search settings", false)
                     })
            {
                vm.SelectedPageIndex = page;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(placeholder, search.Placeholder);
                Assert.Equal(placeholder, search.AccessibleName);
                Assert.Equal(available, search.IsAvailable);
                if (!available) continue;
                // The field itself carries the page's words, for sighted and assistive use alike.
                Assert.Equal(placeholder, field.PlaceholderText);
                Assert.Equal(placeholder, AutomationProperties.GetName(field));
                Assert.True(field.IsEffectivelyVisible);
            }
            // Settings has no search yet, so no field is shown there rather than a dead one.
            vm.SelectedPageIndex = 2;
            Dispatcher.UIThread.RunJobs();
            Assert.False(field.IsEffectivelyVisible);
            Assert.Equal(ShellSearchScope.Settings, search.Scope);

            // Every placeholder is resource text.
            foreach (var key in new[] { "Shell_Search_SavedLocations_Placeholder", "Shell_Search_Places_Placeholder", "Shell_Search_Settings_Placeholder" })
                Assert.True(Strings.TryGet(key, out var value) && value.Length > 0, key);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MovingToAnotherPageClearsWhatWasTypedForTheLastOne()
    {
        var (vm, window) = await CreateSearchWindowAsync();
        try
        {
            vm.SelectedPageIndex = 0;
            vm.Search.Text = "kielder";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["Kielder Forest"], ShownNames(vm));

            vm.SelectedPageIndex = 1;
            Dispatcher.UIThread.RunJobs();
            // The field is empty for the new page, and the filter it was applying is lifted.
            Assert.Equal(string.Empty, vm.Search.Text);
            Assert.False(vm.Locations.IsFiltered);
            Assert.Equal(3, ShownNames(vm).Length);
        }
        finally { window.Close(); }
    }

    // ---- Locations filtering ---------------------------------------------------------------------

    [AvaloniaFact]
    public async Task TypingFiltersTheSavedCardsWithoutTouchingWhatIsSaved()
    {
        var (vm, window) = await CreateSearchWindowAsync();
        try
        {
            var page = window.GetVisualDescendants().OfType<LocationsPage>().Single();
            int Cards() => page.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("editorialLocationCard"));
            Assert.Equal(3, Cards());

            // Name, the user's own description, and case and accents are all forgiven.
            foreach (var (typed, expected) in new (string, string[])[]
                     {
                         ("kielder", ["Kielder Forest"]),
                         ("FOREST", ["Kielder Forest"]),
                         ("durrenstein", ["Dürrenstein"]),
                         ("reservoir", ["Brecon Beacons"]),
                         ("dark-sky", ["Kielder Forest"]),
                         ("e", ["Kielder Forest", "Dürrenstein", "Brecon Beacons"])
                     })
            {
                vm.Search.Text = typed;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(expected.Order(), ShownNames(vm).Order());
                // Nothing was removed from the saved locations, or from what is stored.
                Assert.Equal(3, vm.Locations.Saved.Count);
                // The way to save the current pin stays on the page while filtering.
                Assert.Single(vm.Locations.GridItems.OfType<AddLocationCardViewModel>());
                Assert.Equal(1, page.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("addLocationCard")));
            }

            vm.Search.Text = string.Empty;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, Cards());
            Assert.False(vm.Locations.IsFiltered);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FavouritesAndSortStillApplyToWhatTheFilterLeaves()
    {
        var (vm, window) = await CreateSearchWindowAsync();
        try
        {
            // Two of the three match "e"; the favourite among them is still kept first.
            vm.Locations.Saved.Single(card => card.Name == "Brecon Beacons").IsFavourite = true;
            vm.Locations.SelectedSortOption = vm.Locations.SortOptions.Single(option => option.Mode == LocationSortMode.Name);
            vm.Search.Text = "e";
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["Brecon Beacons", "Dürrenstein", "Kielder Forest"], ShownNames(vm));
            vm.Locations.Saved.Single(card => card.Name == "Brecon Beacons").IsFavourite = false;
            vm.Locations.SelectedSortOption = vm.Locations.SortOptions.Single(option => option.Mode == LocationSortMode.Name);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["Brecon Beacons", "Dürrenstein", "Kielder Forest"], ShownNames(vm));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NoMatchesSaysSoAndOffersTheWayBack()
    {
        var (vm, window) = await CreateSearchWindowAsync();
        try
        {
            var page = window.GetVisualDescendants().OfType<LocationsPage>().Single();
            var empty = page.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "NoSearchMatches");
            Assert.False(empty.IsEffectivelyVisible);

            vm.Search.Text = "nowhere at all";
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.Locations.HasNoMatches);
            Assert.True(empty.IsEffectivelyVisible);
            Assert.Equal("No saved location matches “nowhere at all”.", vm.Locations.NoMatchesMessage);
            Assert.Contains(empty.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.Locations.NoMatchesMessage);
            Assert.DoesNotContain(page.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("editorialLocationCard"));

            // The button in that state clears the search and brings the list back.
            var clear = empty.GetVisualDescendants().OfType<Button>().Single();
            clear.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(string.Empty, vm.Search.Text);
            Assert.False(vm.Locations.HasNoMatches);
            Assert.Equal(3, ShownNames(vm).Length);
        }
        finally { window.Close(); }
    }

    // ---- Planner search reuses the existing place search -----------------------------------------

    [AvaloniaFact]
    public async Task TheFieldHandsPlannerSearchesToTheExistingPlaceSearch()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var dialogs = new FakeDialogs
        {
            SearchResult = new LocationSearchResult("x", "York", new GeoCoordinate(53.96, -1.08, 20), "England", "UK", "Europe/London", "Test")
        };
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, DefaultStore(), new FakeExporter(), dialogs: dialogs);
        await vm.InitializeAsync();
        vm.SelectedPageIndex = 1;

        // Typing alone does not search: a place search costs a request, so it waits to be submitted.
        vm.Search.Text = "york";
        Assert.Null(dialogs.LastSearchQuery);

        await vm.Search.SubmitCommand.ExecuteAsync(null);

        // The one existing search surface, opened with what was already typed.
        Assert.Equal("york", dialogs.LastSearchQuery);
        Assert.Equal(string.Empty, vm.Search.Text);
        Assert.Equal(53.96, vm.Observer.Latitude, 3);

        // Nothing is searched for an empty field, and Locations never opens the place search.
        dialogs.LastSearchQuery = null;
        vm.Search.Text = "   ";
        await vm.Search.SubmitCommand.ExecuteAsync(null);
        Assert.Null(dialogs.LastSearchQuery);
        vm.SelectedPageIndex = 0;
        vm.Search.Text = "york";
        await vm.Search.SubmitCommand.ExecuteAsync(null);
        Assert.Null(dialogs.LastSearchQuery);
    }

    // ---- Interaction, keyboard and touch ----------------------------------------------------------

    [AvaloniaFact]
    public async Task TheFieldIsReachableByKeyboardAndClearsOnEscape()
    {
        var (vm, window) = await CreateSearchWindowAsync();
        try
        {
            var field = SearchField(window);
            Assert.True(field.Focusable);
            Assert.True(field.IsTabStop);

            field.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            Assert.True(field.IsFocused);
            window.KeyTextInput("kielder");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("kielder", vm.Search.Text);
            Assert.Equal(["Kielder Forest"], ShownNames(vm));

            // The clear button appears only when there is something to clear, and it is named and tappable.
            var clear = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ShellSearchClear");
            Assert.True(clear.IsEffectivelyVisible);
            Assert.Equal("Clear search", AutomationProperties.GetName(clear));
            Assert.True(clear.Bounds.Width >= 28 && clear.Bounds.Height >= 26, $"{clear.Bounds.Width}x{clear.Bounds.Height}");

            // Escape does the same thing without reaching for the button.
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(string.Empty, vm.Search.Text);
            Assert.False(clear.IsEffectivelyVisible);
            Assert.Equal(3, ShownNames(vm).Length);
            // Focus stays in the field: clearing a search does not throw the user out of it.
            Assert.True(field.IsFocused);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(1920, 1080)]
    [InlineData(3440, 1440)]
    [InlineData(720, 700)]
    public async Task TheFieldIsProminentAndBoundedAtEveryPracticalWidth(int width, int height)
    {
        var (vm, window) = await CreateSearchWindowAsync(width, height);
        try
        {
            var field = SearchField(window);
            var pages = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Classes.Contains("appNavigation"));
            var tabs = pages.Items.OfType<TabItem>().ToArray();

            // Wide enough to read as the application's search, and bounded so it never runs across the
            // window; at a narrow width it gives way instead of forcing a scrollbar.
            Assert.True(field.Bounds.Width <= 520, $"the field is {field.Bounds.Width} wide");
            if (width >= 1920) Assert.InRange(field.Bounds.Width, 360, 520);
            // At a narrow width it gives way rather than forcing the strip wider than the window.
            if (width < 900) Assert.True(field.Bounds.Width < 520, $"the field did not shrink: {field.Bounds.Width}");
            Assert.True(field.TranslatePoint(new Point(field.Bounds.Width, 0), window)!.Value.X <= width,
                "the field runs past the window");

            // It sits beside the page names, which keep their size and are not squeezed.
            Assert.Equal(["Locations", "Planner", "Settings"], tabs.Select(tab => (string)tab.Header!));
            var tabsRight = tabs.Max(tab => tab.TranslatePoint(new Point(tab.Bounds.Width, 0), window)!.Value.X);
            var left = field.TranslatePoint(default, window)!.Value.X;
            Assert.True(left > tabsRight, "the field overlaps the page names");
            Assert.All(tabs, tab => Assert.True(tab.Bounds.Height >= 38 && tab.Bounds.Width >= 60));

            // Balanced rather than pushed against the window edge: it is centred in the room left over,
            // and keeps clear of the title bar's own buttons above it.
            if (width >= 1920)
            {
                var slack = width - tabsRight;
                var centre = left + field.Bounds.Width / 2 - tabsRight;
                Assert.InRange(centre, slack * 0.4, slack * 0.6);
                Assert.True(width - (left + field.Bounds.Width) > 100, "the field reaches the window edge");
            }
            // A comfortable target on a touchscreen.
            Assert.True(field.Bounds.Height >= 34, $"the field is {field.Bounds.Height} high");
            _ = vm;
        }
        finally { window.Close(); }
    }

    // ---- The card's text-to-artwork transition ---------------------------------------------------

    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", AppearanceMode.Dark)]
    [InlineData("builtin.noctaxis", AppearanceMode.Light)]
    [InlineData("builtin.highcontrast", AppearanceMode.Dark)]
    [InlineData("supporter.foxfire", AppearanceMode.Light)]
    public async Task TheCardFadesIntoItsArtworkInsteadOfMeetingItAtAnEdge(string id, AppearanceMode appearance)
    {
        var (vm, window) = await CreateSearchWindowAsync(locations: [SearchFixtures[0]]);
        var service = ((App)Application.Current!).Themes!;
        try
        {
            service.SetSupporterEntitlement(true);
            await vm.ApplyAppearanceAsync(new(id, AppearanceMode: appearance));
            Dispatcher.UIThread.RunJobs();
            var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("editorialLocationCard"));
            var scrim = card.GetVisualDescendants().OfType<Border>()
                .Select(border => border.Background as LinearGradientBrush).First(brush => brush is not null)!;
            var stops = scrim.GradientStops.OrderBy(stop => stop.Offset).ToArray();

            // Every stop is the theme's card surface at a different alpha: no hard-coded colour.
            var surface = Color.Parse(service.EffectiveTheme.Palette[Themes.LocationCardTokens.Surface]);
            Assert.All(stops, stop => Assert.Equal((surface.R, surface.G, surface.B), (stop.Color.R, stop.Color.G, stop.Color.B)));
            // It runs left to right and ends fully clear, so the artwork is still the artwork.
            Assert.Equal(0d, scrim.StartPoint.Point.Y - scrim.EndPoint.Point.Y);
            Assert.Equal(255, stops[0].Color.A);
            Assert.Equal(0, stops[^1].Color.A);

            // No single step is large enough to read as an edge, and the ramp is spread across the card.
            var ramp = stops.Where(stop => stop.Color.A is > 0 and < 255).ToArray();
            Assert.True(ramp.Length >= 3, $"only {ramp.Length} intermediate stops");
            for (var index = 1; index < stops.Length; index++)
                Assert.True(Math.Abs(stops[index].Color.A - stops[index - 1].Color.A) <= 100,
                    $"{stops[index - 1].Color.A} to {stops[index].Color.A} at offset {stops[index].Offset}");
            var first = stops.First(stop => stop.Color.A < 255).Offset;
            var clear = stops.First(stop => stop.Color.A == 0).Offset;
            Assert.True(clear - first >= 0.3, $"the fade spans only {clear - first:F2} of the card");
            Assert.True(clear <= 0.8, $"the artwork is not clear until {clear:F2}");

            // The text still sits on a solid backing: the left of the card never drops below the scrim.
            var textRight = card.GetVisualDescendants().OfType<Controls.LocationCardTextGrid>().Single()
                .GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
                .Max(t => t.TranslatePoint(new Point(t.Bounds.Width, 0), card)!.Value.X) / card.Bounds.Width;
            Assert.True(AlphaAt(stops, textRight) >= 168, $"text reaches {textRight:F2}, where the backing is {AlphaAt(stops, textRight)}");
        }
        finally { window.Close(); service.SetSupporterEntitlement(false); service.Apply(new()); }
    }

    private static int AlphaAt(GradientStop[] stops, double offset)
    {
        if (offset <= stops[0].Offset) return stops[0].Color.A;
        for (var index = 1; index < stops.Length; index++)
        {
            if (offset > stops[index].Offset) continue;
            var (before, after) = (stops[index - 1], stops[index]);
            var span = after.Offset - before.Offset;
            var position = span <= 0 ? 0 : (offset - before.Offset) / span;
            return (int)Math.Round(before.Color.A + (after.Color.A - before.Color.A) * position);
        }
        return stops[^1].Color.A;
    }
}
