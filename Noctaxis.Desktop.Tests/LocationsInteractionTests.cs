using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.Themes;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Locations page and the application navigation against the Noctaxis UI rules: no tooltip that
/// only restates a visible action, nothing reachable by right-click alone, and navigation that reads
/// as text rather than as a row of contained tabs.
/// </summary>
public sealed partial class MainViewModelTests
{
    /// <summary>The card's one actions menu: the ••• button's flyout, which a right-click also opens.</summary>
    private static MenuFlyout CardMenu(Border card) => Assert.IsType<MenuFlyout>(
        card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("cornerOverflow")).Flyout);

    /// <summary>
    /// Runs <paramref name="inspect"/> over the menu's items while the menu is open. A flyout applies
    /// its item bindings when it is shown and lets them go when it closes, so an item is only itself
    /// while the menu is on screen.
    /// </summary>
    private static T WithOpenMenu<T>(Border card, Func<MenuItem[], T> inspect)
    {
        var flyout = CardMenu(card);
        flyout.ShowAt(card);
        Dispatcher.UIThread.RunJobs();
        try { return inspect(flyout.Items.OfType<MenuItem>().ToArray()); }
        finally { flyout.Hide(); Dispatcher.UIThread.RunJobs(); }
    }

    /// <summary>
    /// Asks for the context menu at a point, as a right-click or the context-menu key does: the same
    /// routed request the platform raises, from whichever control is under that point.
    /// </summary>
    private static void RequestContextMenu(Window window, Visual target, Point point)
    {
        var inWindow = target.TranslatePoint(point, window)!.Value;
        var hit = window.GetVisualsAt(inWindow).OfType<Control>().FirstOrDefault() ?? (Control)target;
        window.MouseDown(inWindow, MouseButton.Right);
        window.MouseUp(inWindow, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        // The headless backend does not turn a right button release into a context request, so the same
        // routed request the platform raises is raised here, from the control under the pointer.
        hit.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Clicks the middle of a control with the left button, as a mouse or a tap does.</summary>
    private static void Click(Window window, Visual target)
    {
        var centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private async Task<(ViewModels.MainViewModel ViewModel, MainWindow Window, Border Card)> CreateLocationsWindowAsync(
        int width = 1440, int height = 1000)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var location = new SavedLocation(Guid.NewGuid(), "Scouting location", new(53.6, -.4), "UTC",
            Notes: "Dark-sky field by the reservoir.");
        var store = new FakeStore(new(1, new AppSettings(), [location],
            PlanningSession.Default(Instant.FromUtc(2026, 9, 12, 20, 0), "UTC"), null));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        vm.AttachThemeService(((App)Application.Current!).Themes!);
        var window = new MainWindow { Width = width, Height = height, DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("editorialLocationCard"));
        return (vm, window, card);
    }

    // ---- Right-click and the ••• button ----------------------------------------------------------

    [AvaloniaFact]
    public async Task RightClickingACardOpensTheSameMenuAsItsOverflowButton()
    {
        var (vm, window, card) = await CreateLocationsWindowAsync();
        try
        {
            var cardVm = vm.Locations.Saved.Single();
            var flyout = CardMenu(card);
            var overflow = card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("cornerOverflow"));
            var body = card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("locationCardBody"));

            // The visible button opens it, as it always did.
            Click(window, overflow);
            Assert.True(flyout.IsOpen, "the ••• button did not open the menu");
            flyout.Hide();
            Dispatcher.UIThread.RunJobs();

            // A right-click anywhere on the card — including over the body button that fills it — opens
            // the very same menu, so the right button is a shortcut and never the only way in.
            foreach (var point in new[] { new Point(40, 30), new Point(200, 120) })
            {
                RequestContextMenu(window, card, point);
                Assert.True(flyout.IsOpen, $"right-click at {point}");
                flyout.Hide();
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Same(flyout, CardMenu(card));
            _ = body;

            // Every entry is a real action bound to this card; the menu holds nothing else.
            WithOpenMenu(card, items =>
            {
                Assert.Equal(["Edit location", "Regenerate map image", "Refresh settlement imagery", "Delete location"],
                    items.Select(item => (string)item.Header!));
                Assert.Equal(new object?[] { cardVm.EditCommand, cardVm.RefreshMapThumbnailCommand, cardVm.RefreshSettlementCacheCommand, cardVm.DeleteCommand },
                    items.Select(item => item.Command));
                // No passive status entry among the actions, and nothing disabled purely to carry text.
                Assert.DoesNotContain(items, item => item.Command is null);
                Assert.DoesNotContain(items, item => ((string)item.Header!).Contains("WSF", StringComparison.Ordinal));
                return true;
            });

            // The primary click is unchanged: the card still opens its location in the Planner.
            Assert.Same(cardVm.OpenCommand, body.Command);
            Click(window, body);
            Assert.Equal(cardVm.Name, vm.LocationName);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TheOverflowButtonStaysReachableWithoutAPointer()
    {
        var (_, window, card) = await CreateLocationsWindowAsync();
        try
        {
            var flyout = CardMenu(card);
            var overflow = card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("cornerOverflow"));
            Assert.Equal("More location actions", AutomationProperties.GetName(overflow));
            Assert.True(overflow.Focusable);

            overflow.Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            Assert.True(overflow.IsFocused);
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(flyout.IsOpen, "the keyboard did not open the menu");
            flyout.Hide();

            // A comfortable target even though the glyph is small.
            Assert.True(overflow.Bounds.Width >= 34 && overflow.Bounds.Height >= 26,
                $"overflow target is {overflow.Bounds.Width}x{overflow.Bounds.Height}");
        }
        finally { window.Close(); }
    }

    // ---- Tooltips -------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task NoTooltipRestatesAVisibleActionOnTheLocationsPage()
    {
        var (_, window, card) = await CreateLocationsWindowAsync();
        try
        {
            var page = window.GetVisualDescendants().OfType<LocationsPage>().Single();
            var tips = page.GetVisualDescendants().OfType<Control>()
                .Select(control => (Control: control, Tip: ToolTip.GetTip(control)?.ToString()))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Tip))
                .ToArray();
            // What clicking a card, the ••• button or the add tile does is already visible.
            Assert.DoesNotContain(tips, entry => entry.Tip!.Contains("Open location in Planner", StringComparison.Ordinal));
            Assert.DoesNotContain(tips, entry => entry.Tip!.Contains("Save the current Planner pin", StringComparison.Ordinal));
            foreach (var cls in new[] { "locationCardBody", "cornerOverflow", "addLocationCard" })
                Assert.All(page.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains(cls)),
                    button => Assert.Null(ToolTip.GetTip(button)));

            // The star is an icon with no label: its tooltip says which way the toggle goes, so it stays.
            var star = card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("favouriteStar"));
            Assert.Equal("Add to favourites", ToolTip.GetTip(star)?.ToString());
            Assert.Equal(ToolTip.GetTip(star)?.ToString(), AutomationProperties.GetName(star));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProviderStatusIsContextOnTheActionItAffectsNotAMenuEntry()
    {
        var (vm, window, card) = await CreateLocationsWindowAsync();
        try
        {
            var cardVm = vm.Locations.Saved.Single();
            cardVm.ThumbnailMetadata = new SavedLocationThumbnailMetadata(9, cardVm.Id, 53.6, -.4, 13, 512, 280,
                "openstreetmap-standard", "OpenStreetMap", "standard", "standard",
                "\u00A9 OpenStreetMap contributors", null, null, null, DateTimeOffset.UtcNow, 13, 53.6, -.4, 620, 340, 2,
                "hash", "test-v1", "thumbnail.png",
                SettlementProviderId: "wsf-3d", SettlementStatus: nameof(EnvironmentalDataState.Partial));
            Dispatcher.UIThread.RunJobs();
            Assert.True(cardVm.HasSettlementWarning);

            WithOpenMenu(card, items =>
            {
                // The limitation is context on the action it limits, not an entry among the actions.
                var settlement = items.Single(item => (string)item.Header! == "Refresh settlement imagery");
                Assert.Equal("WSF settlement data has partial coverage.", ToolTip.GetTip(settlement)?.ToString());
                Assert.Equal(cardVm.SettlementStatusSummary, ToolTip.GetTip(settlement)?.ToString());
                var regenerate = items.Single(item => (string)item.Header! == "Regenerate map image");
                Assert.Equal(cardVm.SemanticStatusSummary, ToolTip.GetTip(regenerate)?.ToString());
                Assert.All(items, item => Assert.NotNull(item.Command));
                return true;
            });
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// The card images are built from other people's data, so their credit is required and stays. It is
    /// one quiet line under the grid, deduplicated and consistently separated, not a pile of fragments.
    /// </summary>
    [AvaloniaFact]
    public async Task ProviderCreditForTheCardImagesIsShownOnceAndQuietly()
    {
        var (vm, window, card) = await CreateLocationsWindowAsync();
        try
        {
            var cardVm = vm.Locations.Saved.Single();
            Assert.False(vm.Locations.HasThumbnailAttributions); // Nothing rendered yet, nothing to credit.
            cardVm.ThumbnailMetadata = new SavedLocationThumbnailMetadata(9, cardVm.Id, 53.6, -.4, 13, 512, 280,
                "openstreetmap-standard", "OpenStreetMap", "standard", "standard",
                "© OpenStreetMap contributors", "https://www.openstreetmap.org/copyright",
                "Open Database License (ODbL)", "https://opendatacommons.org/licenses/odbl/", DateTimeOffset.UtcNow,
                13, 53.6, -.4, 620, 340, 2, "hash", "test-v1", "thumbnail.png",
                FeatureProviderId: "openstreetmap-overpass",
                FeatureAttributionText: "© OpenStreetMap contributors",
                SettlementProviderId: "wsf-3d", SettlementStatus: nameof(EnvironmentalDataState.Available));
            vm.Locations.RefreshThumbnailAttributions();
            Dispatcher.UIThread.RunJobs();

            // The raster and the road/water features are both OpenStreetMap: credited once, not twice.
            Assert.True(vm.Locations.HasThumbnailAttributions);
            Assert.Equal("© OpenStreetMap contributors · World Settlement Footprint 3D (DLR)",
                vm.Locations.ThumbnailAttributionSummary);

            var line = window.GetVisualDescendants().OfType<LocationsPage>().Single()
                .GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == vm.Locations.ThumbnailAttributionSummary);
            Assert.True(line.IsEffectivelyVisible);
            // Low emphasis, and lined up with the grid rather than floating.
            Assert.Equal(Application.Current!.FindResource("MutedText"), line.Foreground);
            Assert.True(line.FontSize <= 11, $"credit is set at {line.FontSize}");
            Assert.InRange(line.TranslatePoint(default, card)!.Value.X, -2, 2);
        }
        finally { window.Close(); }
    }

    // ---- Application navigation ------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", AppearanceMode.Dark)]
    [InlineData("builtin.noctaxis", AppearanceMode.Light)]
    [InlineData("builtin.highcontrast", AppearanceMode.Light)]
    public async Task NavigationIsTextNotContainedTabs(string id, AppearanceMode appearance)
    {
        var (vm, window, _) = await CreateLocationsWindowAsync();
        var service = ((App)Application.Current!).Themes!;
        try
        {
            await vm.ApplyAppearanceAsync(new(id, AppearanceMode: appearance));
            Dispatcher.UIThread.RunJobs();
            var palette = service.EffectiveTheme.Palette;
            Color C(string token) => Color.Parse(palette[token]);
            Color? Brush(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color : null;
            var pages = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Classes.Contains("appNavigation"));
            var tabs = pages.Items.OfType<TabItem>().ToArray();
            Assert.Equal(["Locations", "Planner", "Settings"], tabs.Select(tab => (string)tab.Header!));
            Border Root(TabItem tab) => tab.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_LayoutRoot");

            foreach (var selected in new[] { 0, 1, 2 })
            {
                pages.SelectedIndex = selected;
                Dispatcher.UIThread.RunJobs();
                foreach (var (tab, index) in tabs.Select((tab, index) => (tab, index)))
                {
                    var isSelected = index == selected;
                    // No item is a filled, bordered box, selected or not.
                    Assert.Equal(0d, Brush(Root(tab).Background)?.A ?? 0);
                    Assert.Equal(0d, Root(tab).BorderThickness.Left);
                    // Selection is accent colour plus weight, and the pipe beneath it; never colour alone.
                    Assert.Equal(isSelected ? C("Accent") : C("SecondaryText"), Brush(tab.Foreground));
                    Assert.Equal(isSelected, tab.FontWeight == FontWeight.SemiBold);
                    var pipe = tab.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_SelectedPipe");
                    if (pipe is not null) Assert.Equal(isSelected, pipe.IsVisible);
                    // The row stays a comfortable target although it reads as plain text.
                    Assert.True(tab.Bounds.Height >= 38, $"{tab.Header} is {tab.Bounds.Height} high");
                    Assert.True(tab.Bounds.Width >= 60, $"{tab.Header} is {tab.Bounds.Width} wide");
                }
                // The whole strip is lighter than the boxed tabs it replaces.
                Assert.True(tabs.Max(tab => tab.Bounds.Bottom) <= 44, "the navigation strip is taller than 44");
            }

            // Keyboard focus stays visible, and moving focus does not change the page.
            pages.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            tabs[2].Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            Assert.True(tabs[2].IsFocused);
            Assert.NotEqual("none", Root(tabs[2]).BoxShadow.ToString());
            Assert.Equal(0, pages.SelectedIndex);
        }
        finally { window.Close(); service.Apply(new()); }
    }

    [AvaloniaFact]
    public async Task NavigationStillSwitchesPagesByPointerAndBinding()
    {
        var (vm, window, _) = await CreateLocationsWindowAsync();
        try
        {
            var pages = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Classes.Contains("appNavigation"));
            var planner = pages.Items.OfType<TabItem>().Single(tab => (string)tab.Header! == "Planner");
            window.MouseDown(planner.TranslatePoint(new Point(planner.Bounds.Width / 2, planner.Bounds.Height / 2), window)!.Value, MouseButton.Left);
            window.MouseUp(planner.TranslatePoint(new Point(planner.Bounds.Width / 2, planner.Bounds.Height / 2), window)!.Value, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, pages.SelectedIndex);
            Assert.Equal(1, vm.SelectedPageIndex);

            vm.SelectedPageIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, pages.SelectedIndex);
        }
        finally { window.Close(); }
    }

    // ---- Layout ---------------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData(1920, 1080)]
    [InlineData(3440, 1440)]
    public async Task TheSavedGridStaysReadableAtStandardAndUltrawideWidths(int width, int height)
    {
        var (vm, window, card) = await CreateLocationsWindowAsync(width, height);
        try
        {
            var page = window.GetVisualDescendants().OfType<LocationsPage>().Single();
            var content = page.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "PageContent");
            // Width is not filled for its own sake: the page keeps a readable measure.
            Assert.True(content.Bounds.Width <= 1720, $"content is {content.Bounds.Width} wide");
            Assert.True(card.Bounds.Width <= 880, $"a card is {card.Bounds.Width} wide");
            Assert.True(card.Bounds.Width >= 460, $"a card is {card.Bounds.Width} wide");
            // Nothing is cut off, and the sort row still sits beside the heading at these widths.
            var sort = page.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "SavedLocationsSort");
            Assert.Equal(0, Grid.GetRow(sort));
            Assert.Empty(LayoutAudit.Problems(page));
            _ = vm;
        }
        finally { window.Close(); }
    }
}
