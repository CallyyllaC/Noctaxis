using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>Settings category navigation, confirmation of destructive actions, and card interaction geometry.</summary>
public sealed partial class MainViewModelTests
{
    private static readonly (string Header, string Title)[] SettingsCategoryTitles =
    [
        ("General", "General"), ("Appearance", "Appearance and accessibility"), ("Equipment", "Equipment"),
        ("Planner map", "Planner map"), ("Weather", "Weather"), ("Celestial objects", "Celestial objects"),
        ("Data", "Data and storage"), ("Supporter", "Supporter"),
    ];

    private async Task<(MainViewModel Vm, MainWindow Window, TabControl Categories)> OpenSettingsAsync(string theme, double textScale, double width)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, CreateSupporterStore(new AppSettings()), new FakeExporter());
        await vm.InitializeAsync();
        var service = ((App)Application.Current!).Themes!;
        vm.AttachThemeService(service);
        await vm.ApplyAppearanceAsync(new(theme, TextScale: textScale));
        var window = new MainWindow { Width = width, Height = 900, DataContext = vm };
        window.Show();
        var pages = (TabControl)((Grid)window.Content!).Children[1];
        pages.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        return (vm, window, window.FindControl<TabControl>("SettingsCategories")!);
    }

    private static string VisibleSettingsTitle(TabControl categories) =>
        categories.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("settingsTitle") && t.IsEffectivelyVisible).Text!;

    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", 1.0, 1440)]
    [InlineData("builtin.noctaxis", 1.5, 1100)]
    [InlineData("builtin.highcontrast", 2.0, 1440)]
    [InlineData("builtin.highcontrast", 2.0, 720)]
    public async Task SettingsNavigation_IsARailOfEightCategoriesThatShowTheirContent(string theme, double textScale, double width)
    {
        var (_, window, categories) = await OpenSettingsAsync(theme, textScale, width);
        try
        {
            Assert.Equal(Avalonia.Controls.Dock.Left, categories.TabStripPlacement);
            var items = categories.GetVisualDescendants().OfType<TabItem>().Where(item => item.IsVisible).ToArray();
            Assert.Equal(SettingsCategoryTitles.Select(c => c.Header), items.Select(item => (string?)item.Header));
            // A vertical rail: one column, in order, beside (never above) the content.
            Assert.True(items.Zip(items.Skip(1)).All(pair => pair.Second.Bounds.Top >= pair.First.Bounds.Bottom - 0.5));
            var content = categories.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_SelectedContentHost");
            var railRight = items.Max(item => item.TranslatePoint(new Point(item.Bounds.Width, 0), categories)!.Value.X);
            Assert.True(railRight <= content.TranslatePoint(new Point(0, 0), categories)!.Value.X + 0.5, "the rail overlaps the content");
            Assert.True(railRight <= categories.Bounds.Width * 0.34 + 1, "the rail takes more than about a third of the width");

            foreach (var (item, (header, title)) in items.Zip(SettingsCategoryTitles))
            {
                // Pointer navigation.
                window.MouseDown(item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value, MouseButton.Left);
                window.MouseUp(item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.True(item.IsSelected, $"{header} is not selected after a click");
                Assert.Equal(title, VisibleSettingsTitle(categories));
                // Names never clip, at any text size or width; long names wrap within the rail instead.
                var label = item.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == header);
                Assert.True(label.TextLayout.Width <= label.Bounds.Width + 1, $"{theme}/{textScale}/{width}: '{header}' is clipped");
                Assert.True(label.TextLayout.Height <= label.Bounds.Height + 1, $"{theme}/{textScale}/{width}: '{header}' is clipped vertically");
                // Selection is not colour alone: the selected category is also set in a heavier weight.
                Assert.Equal(FontWeight.SemiBold, item.FontWeight);
                Assert.All(items.Where(other => other != item), other => Assert.NotEqual(FontWeight.SemiBold, other.FontWeight));
            }
            AppearancePolishTests.Capture(window, $"settings-navigation-{theme}-{textScale:0.0}-{width}");
        }
        finally { window.Close(); ((App)Application.Current!).Themes!.Apply(new()); }
    }

    [AvaloniaFact]
    public async Task SettingsNavigation_WorksFromTheKeyboardWithAVisibleFocusAndAccessibleSelection()
    {
        var (_, window, categories) = await OpenSettingsAsync("builtin.noctaxis", 1.0, 1200);
        try
        {
            var items = categories.GetVisualDescendants().OfType<TabItem>().Where(item => item.IsVisible).ToArray();
            items[0].Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            Assert.True(items[0].IsFocused);
            var ring = items[0].GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_LayoutRoot").BoxShadow;
            Assert.True(ring.Count > 0 && ring[0].IsInset, "keyboard focus draws a focus ring on the category");
            foreach (var expected in new[] { "Appearance", "Equipment", "Planner map" })
            {
                window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(expected, (string?)((TabItem)categories.SelectedItem!).Header);
                Assert.Equal(SettingsCategoryTitles.Single(category => category.Header == expected).Title, VisibleSettingsTitle(categories));
            }
            window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Equipment", (string?)((TabItem)categories.SelectedItem!).Header);

            // Screen readers see a named list of tabs, each named, with its selection state.
            var railPeer = ControlAutomationPeer.CreatePeerForElement(categories);
            Assert.Equal("Settings categories", railPeer.GetName());
            foreach (var item in items)
            {
                var peer = ControlAutomationPeer.CreatePeerForElement(item);
                Assert.Equal((string?)item.Header, peer.GetName());
                Assert.Equal(AutomationControlType.TabItem, peer.GetAutomationControlType());
                Assert.Equal(item.IsSelected, Assert.IsAssignableFrom<ISelectionItemProvider>(peer).IsSelected);
            }
        }
        finally { window.Close(); }
    }

    // ---- Destructive actions ----------------------------------------------------------------------

    [Fact]
    public async Task RemovingACameraOrLensAsksFirst_CancelKeepsItAndConfirmRemovesAndSavesIt()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = DefaultStore();
        var dialogs = new FakeDialogs { ConfirmDestructiveResult = false };
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(), dialogs: dialogs);
        await vm.InitializeAsync();
        vm.AddCameraProfileCommand.Execute(null);
        vm.EquipmentCameraEditors[^1].DisplayName = "Sony A6300";
        vm.AddLensProfileCommand.Execute(null);
        vm.EquipmentLensEditors[^1].DisplayName = "Samyang 12 mm";
        await vm.SettleSettingsAsync();
        Assert.Equal(2, store.State.Settings.Equipment!.Cameras!.Count);

        var camera = vm.EquipmentCameraEditors.Single(c => c.DisplayName == "Sony A6300");
        await camera.RemoveCommand.ExecuteAsync(null);
        var request = Assert.Single(dialogs.DestructiveRequests);
        Assert.Equal("Remove camera?", request.Title);
        Assert.Contains("“Sony A6300” will be removed from your equipment.", request.Message);
        Assert.Equal("Remove camera", request.ConfirmLabel);
        Assert.Contains(camera, vm.EquipmentCameraEditors);                       // Cancelled: untouched.
        Assert.Equal(2, store.State.Settings.Equipment!.Cameras!.Count);

        dialogs.ConfirmDestructiveResult = true;
        await camera.RemoveCommand.ExecuteAsync(null);
        Assert.DoesNotContain(camera, vm.EquipmentCameraEditors);
        await vm.SettleSettingsAsync();
        Assert.DoesNotContain(store.State.Settings.Equipment!.Cameras!, c => c.DisplayName == "Sony A6300");
        Assert.DoesNotContain(vm.Cameras, c => c.DisplayName == "Sony A6300");

        var lens = vm.EquipmentLensEditors.Single(l => l.DisplayName == "Samyang 12 mm");
        dialogs.ConfirmDestructiveResult = false;
        await lens.RemoveCommand.ExecuteAsync(null);
        Assert.Equal("Remove lens?", dialogs.DestructiveRequests[^1].Title);
        Assert.Contains(lens, vm.EquipmentLensEditors);
        dialogs.ConfirmDestructiveResult = true;
        await lens.RemoveCommand.ExecuteAsync(null);
        await vm.SettleSettingsAsync();
        Assert.DoesNotContain(store.State.Settings.Equipment!.Lenses!, l => l.DisplayName == "Samyang 12 mm");
    }

    [Fact]
    public async Task AConfirmedRemovalThatCannotBeSavedIsReportedNotPresentedAsSaved()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = new FlakyStore(new PersistedState(4, new AppSettings(), [],
            PlanningSession.Default(Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(), dialogs: new FakeDialogs());
        await vm.InitializeAsync();
        vm.AddCameraProfileCommand.Execute(null);
        await vm.SettleSettingsAsync();
        var cameras = store.State.Settings.Equipment!.Cameras!.Count;

        store.Fail = true;
        await vm.EquipmentCameraEditors[^1].RemoveCommand.ExecuteAsync(null);
        await vm.SettleSettingsAsync();
        Assert.True(vm.HasOperationalNotice);
        Assert.Contains("could not be saved", vm.OperationalNotice);
        Assert.Equal(cameras, store.State.Settings.Equipment!.Cameras!.Count);
    }

    [Fact]
    public async Task RestoringDefaultObjectsAsksOnlyWhenItWouldReplaceTheUsersChoices()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var dialogs = new FakeDialogs { ConfirmDestructiveResult = false };
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, DefaultStore(), new FakeExporter(), dialogs: dialogs);
        await vm.InitializeAsync();
        await vm.RestoreCelestialDefaultsCommand.ExecuteAsync(null);
        Assert.Empty(dialogs.DestructiveRequests);                                // Already the defaults.

        vm.AddSettingsCelestialObjectCommand.Execute(catalogue.Get("M31"));
        await vm.RestoreCelestialDefaultsCommand.ExecuteAsync(null);
        Assert.Equal("Clear objects?", Assert.Single(dialogs.DestructiveRequests).Title);
        Assert.Contains(vm.CelestialObjects, item => item.TargetId == "openngc:NGC0224");
        dialogs.ConfirmDestructiveResult = true;
        await vm.RestoreCelestialDefaultsCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.CelestialObjects, item => item.TargetId == "openngc:NGC0224");
    }

    [Fact]
    public async Task TerrainCacheSizeIsALimitNeverAnInstructionToEmptyTheCache()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = DefaultStore();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        vm.SettingsTerrainCacheLimitGiB = 0;
        await vm.SettleSettingsAsync();
        Assert.Equal((long)(MainViewModel.MinimumTerrainCacheLimitGiB * 1024 * 1024 * 1024), store.State.Settings.TerrainCacheLimitBytes);
        Assert.Equal(MainViewModel.MinimumTerrainCacheLimitGiB, vm.SettingsTerrainCacheLimitGiB);
    }

    [AvaloniaFact]
    public void ConfirmationDialogFavoursCancel_EnterAndEscapeNeverConfirm()
    {
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        try
        {
            foreach (var key in new[] { PhysicalKey.Enter, PhysicalKey.Escape })
            {
                var dialog = new ConfirmationDialog("Remove camera?", "“Sony A6300” will be removed from your equipment.", "Remove camera");
                var result = dialog.ShowDialog<bool>(owner);
                Dispatcher.UIThread.RunJobs();
                var cancel = dialog.FindControl<Button>("CancelButton")!;
                var confirm = dialog.FindControl<Button>("ConfirmButton")!;
                Assert.True(cancel.IsFocused, "Cancel has the initial focus");
                Assert.Contains("destructive", confirm.Classes);
                Assert.Equal("Remove camera", confirm.Content);
                Assert.False(confirm.IsDefault);
                dialog.KeyPressQwerty(key, RawInputModifiers.None); // Either key closes the dialog on key-down.
                Dispatcher.UIThread.RunJobs();
                Assert.True(result.IsCompleted, $"{key} did not close the dialog");
                Assert.False(result.Result, $"{key} confirmed the removal");
            }
        }
        finally { owner.Close(); }
    }

    // ---- Location cards ---------------------------------------------------------------------------

    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", 1.0)] [InlineData("builtin.noctaxis", 2.0)] [InlineData("builtin.highcontrast", 1.5)]
    public async Task LocationCardsKeepTheirGeometryWhenHoveredFocusedOrSelected(string theme, double textScale)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        SavedLocation[] locations = [.. Enumerable.Range(0, 4).Select(i => new SavedLocation(Guid.NewGuid(), "Location with a longer name " + i,
            new(53.6 + i, -.4), "UTC", Notes: "A description long enough to wrap across two lines inside the card at larger text sizes."))];
        var store = new FakeStore(new(1, new AppSettings(), locations, PlanningSession.Default(Instant.FromUtc(2026, 9, 12, 20, 0), "UTC"), null));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        var service = ((App)Application.Current!).Themes!;
        vm.AttachThemeService(service);
        await vm.ApplyAppearanceAsync(new(theme, TextScale: textScale));
        var window = new MainWindow { Width = 1200, Height = 2600, DataContext = vm }; // Tall enough for every card row.
        window.Show();
        try
        {
            window.MouseMove(new Point(2, 2));
            Dispatcher.UIThread.RunJobs();
            var cards = window.GetVisualDescendants().OfType<Control>()
                .Where(c => c.Classes.Contains("editorialLocationCard") || c.Classes.Contains("addLocationCard")).ToArray();
            Assert.Equal(5, cards.Length);
            Rect[] Geometry() => [.. cards.Select(c => new Rect(c.TranslatePoint(new Point(0, 0), window)!.Value, c.Bounds.Size))];
            var baseline = Geometry();
            var desired = cards.Select(c => c.DesiredSize).ToArray();
            void AssertStable(string state, bool contentUnchanged = true)
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(baseline, Geometry());
                // Interaction states never change what a card measures (the grid sizes rows from it); selection may
                // legitimately show more content (the Current location badge) within the same row.
                if (contentUnchanged) Assert.Equal(desired, cards.Select(c => c.DesiredSize).ToArray());
            }
            foreach (var card in cards)
            {
                var size = card.Bounds.Size;
                // Centre, each edge (just inside) and a corner: the pointer's position relative to the visual
                // hover changes must not move anything.
                foreach (var inset in new[] { 0.5, 1, 2 })
                foreach (var point in new[] { new Point(size.Width / 2, size.Height / 2), new Point(size.Width / 2, inset), new Point(size.Width / 2, size.Height - inset),
                             new Point(inset, size.Height / 2), new Point(size.Width - inset, size.Height / 2), new Point(14, 14) })
                {
                    window.MouseMove(card.TranslatePoint(point, window)!.Value);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    AssertStable($"hover at {point}");
                    // Whatever the answer at an edge, it must not change from frame to frame.
                    var first = card.IsPointerOver;
                    for (var frame = 0; frame < 3; frame++)
                    {
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                        AssertStable($"hover at {point}, frame {frame}");
                        Assert.Equal(first, card.IsPointerOver);
                    }
                    if (inset >= 2 || point == new Point(size.Width / 2, size.Height / 2))
                        Assert.True(first, $"{theme}/{textScale}: pointer at {point} is not over its card");
                }
            }
            window.MouseMove(new Point(2, 2));
            vm.Locations.Saved[1].IsSelected = true;
            AssertStable("selected", contentUnchanged: false);
            vm.Locations.Saved[1].IsSelected = false;
            AssertStable("deselected");
            cards[0].GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("favouriteStar")).Focus(NavigationMethod.Tab);
            AssertStable("focus within");
        }
        finally { window.Close(); service.Apply(new()); }
    }
}
