using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>
/// The saved Planner panel layout. It records where the user put each panel and nothing else: the
/// panels that exist belong to the application, and anything in the file that cannot be honoured is
/// corrected rather than obeyed.
/// </summary>
public sealed class PlannerDockLayoutTests
{
    private static readonly IReadOnlyList<PlannerPanelLayout> Defaults =
    [
        new("terrain", Mode: PanelDockMode.Docked, Side: PanelDockSide.Left, DockedSize: 264),
        new("targets", Mode: PanelDockMode.Docked, Side: PanelDockSide.Right, DockedSize: 420),
        new("timeline", Mode: PanelDockMode.Docked, Side: PanelDockSide.Bottom, DockedSize: 248)
    ];

    [Fact]
    public void ADefaultPanelIsDockedAndPinned()
    {
        var panel = new PlannerPanelLayout("targets");
        Assert.Null(panel.SelectedTab);
        Assert.True(panel.IsPinned);
        Assert.Equal(PanelDockMode.Docked, panel.Mode);
        Assert.Equal(PlannerPanelLayout.DefaultDockedSize, panel.DockedSize);
    }

    /// <summary>Pinning is docking, so the two can never contradict each other in a saved file.</summary>
    [Theory]
    [InlineData(PanelDockMode.Docked, true)]
    [InlineData(PanelDockMode.AutoHidden, false)]
    [InlineData(PanelDockMode.Floating, false)]
    public void OnlyADockedPanelIsPinned(PanelDockMode mode, bool pinned) =>
        Assert.Equal(pinned, new PlannerPanelLayout("targets", Mode: mode).IsPinned);

    [Fact]
    public void NumbersOutsideTheirLimitsAreCorrectedAndNeverObeyed()
    {
        var absurd = new PlannerPanelLayout("targets", DockedSize: 9_999_999, FloatingWidth: 2,
            FloatingHeight: -40, FloatingX: double.NaN, FloatingY: double.NegativeInfinity, DockOrder: -7).Normalised();
        Assert.Equal(PlannerPanelLayout.MaximumDockedSize, absurd.DockedSize);
        Assert.Equal(PlannerPanelLayout.MinimumFloatingWidth, absurd.FloatingWidth);
        Assert.Equal(PlannerPanelLayout.MinimumFloatingHeight, absurd.FloatingHeight);
        Assert.Equal(PlannerPanelLayout.DefaultFloatingOffset, absurd.FloatingX);
        Assert.Equal(PlannerPanelLayout.DefaultFloatingOffset, absurd.FloatingY);
        Assert.Equal(0, absurd.DockOrder);
    }

    /// <summary>
    /// A layout arranged on a large monitor, opened on a small one. Nothing may end up where it cannot
    /// be seen or grabbed; the panel shrinks to what there is and comes back inside the workspace.
    /// </summary>
    [Fact]
    public void AFloatingPanelSavedOnALargerDisplayComesBackInsideTheWorkspace()
    {
        var saved = new PlannerPanelLayout("targets", Mode: PanelDockMode.Floating,
            FloatingX: 2_600, FloatingY: 1_300, FloatingWidth: 700, FloatingHeight: 900, DockedSize: 900);
        var clamped = saved.ClampedTo(1_280, 800);
        Assert.InRange(clamped.FloatingX, 0, 1_280 - clamped.FloatingWidth);
        Assert.InRange(clamped.FloatingY, 0, 800 - clamped.FloatingHeight);
        Assert.InRange(clamped.FloatingWidth, PlannerPanelLayout.MinimumFloatingWidth, 1_280);
        Assert.InRange(clamped.FloatingHeight, PlannerPanelLayout.MinimumFloatingHeight, 800);
        // A dock saved wider than the window may not swallow it either.
        Assert.True(clamped.DockedSize <= 1_280 * .8);
        // Nothing is lost by clamping: what still fits is left exactly as the user left it.
        var modest = new PlannerPanelLayout("targets", Mode: PanelDockMode.Floating,
            FloatingX: 100, FloatingY: 90, FloatingWidth: 400, FloatingHeight: 300, DockedSize: 420);
        Assert.Equal(modest, modest.ClampedTo(1_280, 800));
    }

    /// <summary>A workspace with no size yet is not a reason to rewrite the layout.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-10, 500)]
    [InlineData(double.NaN, 500)]
    public void AWorkspaceWithNoSizeLeavesTheLayoutAlone(double width, double height)
    {
        var saved = new PlannerPanelLayout("targets", Mode: PanelDockMode.Floating, FloatingX: 2_000);
        Assert.Equal(saved, saved.ClampedTo(width, height));
    }

    [Fact]
    public void TheApplicationDecidesWhichPanelsExistAndTheFileOnlySaysWhereTheyAre()
    {
        var saved = new PlannerDockLayout(
        [
            // One panel the user moved...
            new("targets", Mode: PanelDockMode.Floating, Side: PanelDockSide.Left, DockedSize: 500),
            // ...one this version no longer has...
            new("retired-panel", Mode: PanelDockMode.AutoHidden),
            // ...and one with an id that is not a panel at all.
            new("  ")
        ]);
        var merged = saved.MergedOver(Defaults);
        Assert.Equal(["terrain", "targets", "timeline"], merged.Select(panel => panel.PanelId));
        var targets = merged.Single(panel => panel.PanelId == "targets");
        Assert.Equal(PanelDockMode.Floating, targets.Mode);
        Assert.Equal(PanelDockSide.Left, targets.Side);
        Assert.Equal(500, targets.DockedSize);
        // A panel the file does not mention — one added since it was written — arrives at its default.
        var terrain = merged.Single(panel => panel.PanelId == "terrain");
        Assert.Equal(Defaults[0], terrain);
    }

    [Fact]
    public void NoSavedLayoutMeansTheDefaultsExactly()
    {
        Assert.Equal(Defaults, PlannerDockLayout.Empty.MergedOver(Defaults));
        Assert.Equal(Defaults, new PlannerDockLayout().MergedOver(Defaults));
        Assert.Empty(new AppSettings().EffectivePlannerDock.EffectivePanels);
        Assert.Null(new AppSettings().PlannerDock);
    }

    /// <summary>
    /// The layout goes through the settings file like every other preference, and comes back as it was
    /// written, including through a file saved before the setting existed.
    /// </summary>
    [Fact]
    public async Task TheLayoutIsSavedWithTheSettingsAndReadBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-dock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new Paths(directory);
            var layout = new PlannerDockLayout(
            [
                new("terrain", Mode: PanelDockMode.AutoHidden, Side: PanelDockSide.Left, DockedSize: 300),
                new("targets", Mode: PanelDockMode.Floating, SelectedTab: "weather", FloatingX: 120, FloatingY: 90,
                    FloatingWidth: 420, FloatingHeight: 360, DockOrder: 2)
            ]);
            var state = new PersistedState(4, new AppSettings(PlannerDock: layout), [],
                PlanningSession.Default(Instant.FromUtc(2026, 3, 5, 21, 30), "UTC"), null);
            await new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).SaveAsync(state, CancellationToken.None);

            var loaded = await new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).LoadAsync(CancellationToken.None);
            Assert.Equal(layout.EffectivePanels, loaded.Settings.EffectivePlannerDock.EffectivePanels);
            // The mode is written as its name, so a saved layout is readable and stays stable.
            Assert.Contains("\"Mode\": \"AutoHidden\"", await File.ReadAllTextAsync(JsonUserDataStore.StateFilePath(paths)));

            // A state file from before panels existed: the layout is simply absent, and the defaults apply.
            var json = await File.ReadAllTextAsync(JsonUserDataStore.StateFilePath(paths));
            var start = json.IndexOf("\"PlannerDock\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(JsonUserDataStore.StateFilePath(paths), json[..(start - 2)] + "\n}");
            var older = await new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).LoadAsync(CancellationToken.None);
            Assert.Null(older.Settings.PlannerDock);
            Assert.Equal(Defaults, older.Settings.EffectivePlannerDock.MergedOver(Defaults));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    // ---- Corner ownership ---------------------------------------------------------------------------

    /// <summary>
    /// The shipped layout: the side docks reach the top corners and the bottom dock runs the full width
    /// beneath them, so the timeline spans the window under the map and the Inspector.
    /// </summary>
    [Fact]
    public void TheBottomDockRunsTheFullWidthByDefault()
    {
        var corners = DockCornerOwnership.Default;
        Assert.Equal(PanelDockSide.Left, corners.Owner(DockCorner.TopLeft));
        Assert.Equal(PanelDockSide.Bottom, corners.Owner(DockCorner.BottomLeft));
        Assert.Equal(PanelDockSide.Right, corners.Owner(DockCorner.TopRight));
        Assert.Equal(PanelDockSide.Bottom, corners.Owner(DockCorner.BottomRight));
        Assert.True(corners.Spans(PanelDockSide.Bottom));
        Assert.False(corners.Spans(PanelDockSide.Left));
        Assert.False(corners.Spans(PanelDockSide.Right));
        Assert.False(corners.Spans(PanelDockSide.Top));
        // A saved Corners object missing a corner takes the shipped owner for it.
        Assert.Equal(PanelDockSide.Bottom, System.Text.Json.JsonSerializer.Deserialize<DockCornerOwnership>(
            """{"TopLeft":"Left","TopRight":"Right"}""", new System.Text.Json.JsonSerializerOptions
            { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!.BottomRight);
        Assert.Equal(DockCornerOwnership.Default, PlannerDockLayout.Empty.EffectiveCorners);
    }

    /// <summary>Exactly one of the two docks that touch a corner owns it, whatever the arrangement.</summary>
    [Fact]
    public void EveryCornerIsOwnedByOneOfItsOwnTwoDocksAndNeverBoth()
    {
        foreach (var corner in DockCornerOwnership.All)
        {
            var (side, end) = DockCornerOwnership.Neighbours(corner);
            Assert.Contains(corner, DockCornerOwnership.CornersOf(side));
            Assert.Contains(corner, DockCornerOwnership.CornersOf(end));
            foreach (var owner in new[] { side, end })
            {
                var ownership = DockCornerOwnership.Default.With(corner, owner);
                Assert.True(ownership.Owns(owner, corner));
                Assert.False(ownership.Owns(owner == side ? end : side, corner));
            }
        }
    }

    /// <summary>Every arrangement the model can express, including the asymmetric ones.</summary>
    [Fact]
    public void EachCornerIsChosenIndependentlyOfTheOthers()
    {
        // The bottom dock running under both side docks, from side docks that ran the full height.
        var sidesWin = DockCornerOwnership.Default.WithSpan(PanelDockSide.Bottom, spans: false);
        Assert.True(sidesWin.Spans(PanelDockSide.Left));
        Assert.True(sidesWin.Spans(PanelDockSide.Right));
        var bottomWins = sidesWin.WithSpan(PanelDockSide.Bottom, spans: true);
        Assert.True(bottomWins.Spans(PanelDockSide.Bottom));
        Assert.False(bottomWins.Spans(PanelDockSide.Left));
        Assert.False(bottomWins.Spans(PanelDockSide.Right));
        // ...and the top corners are untouched by that choice.
        Assert.Equal(PanelDockSide.Left, bottomWins.Owner(DockCorner.TopLeft));
        Assert.Equal(PanelDockSide.Right, bottomWins.Owner(DockCorner.TopRight));

        // Asymmetric: the left dock keeps its bottom corner while the bottom dock takes the other.
        var asymmetric = sidesWin.With(DockCorner.BottomRight, PanelDockSide.Bottom);
        Assert.Equal(PanelDockSide.Left, asymmetric.Owner(DockCorner.BottomLeft));
        Assert.Equal(PanelDockSide.Bottom, asymmetric.Owner(DockCorner.BottomRight));
        Assert.False(asymmetric.Spans(PanelDockSide.Bottom));
        Assert.False(asymmetric.Spans(PanelDockSide.Right));
        Assert.True(asymmetric.Spans(PanelDockSide.Left));

        // A dock told to run the whole way owns both its corners, and told to stop owns neither; from
        // either of those two settled states, asking twice returns the arrangement it started in.
        foreach (var side in Enum.GetValues<PanelDockSide>())
        {
            Assert.True(asymmetric.WithSpan(side, true).Spans(side));
            Assert.False(asymmetric.WithSpan(side, false).Spans(side));
            foreach (var spans in new[] { true, false })
            {
                var settled = asymmetric.WithSpan(side, spans);
                Assert.Equal(settled, settled.WithSpan(side, !spans).WithSpan(side, spans));
            }
        }
    }

    /// <summary>A corner claimed by a dock that does not touch it is not a layout; it is a damaged file.</summary>
    [Theory]
    [InlineData(PanelDockSide.Right)]
    [InlineData(PanelDockSide.Bottom)]
    public void ACornerClaimedByADockThatDoesNotTouchItFallsBackToTheDefault(PanelDockSide impossible)
    {
        var damaged = new DockCornerOwnership(TopLeft: impossible);
        Assert.Equal(PanelDockSide.Left, damaged.Normalised().Owner(DockCorner.TopLeft));
        Assert.Equal(DockCornerOwnership.Default, damaged.Normalised());
        // The same correction is applied to anything read out of a saved layout.
        Assert.Equal(DockCornerOwnership.Default, new PlannerDockLayout(Corners: damaged).EffectiveCorners);
        // And setting an impossible owner is simply not a change.
        Assert.Equal(DockCornerOwnership.Default,
            DockCornerOwnership.Default.With(DockCorner.TopLeft, impossible));
    }

    [Fact]
    public async Task EveryCornerIsSavedAndReadBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-corners-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new Paths(directory);
            var corners = new DockCornerOwnership(PanelDockSide.Top, PanelDockSide.Right,
                PanelDockSide.Bottom, PanelDockSide.Bottom);
            var state = new PersistedState(4, new AppSettings(PlannerDock: new PlannerDockLayout(Corners: corners)), [],
                PlanningSession.Default(Instant.FromUtc(2026, 3, 5, 21, 30), "UTC"), null);
            await new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).SaveAsync(state, CancellationToken.None);
            var loaded = await new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).LoadAsync(CancellationToken.None);
            Assert.Equal(corners, loaded.Settings.EffectivePlannerDock.EffectiveCorners);

            // A layout written before corners existed is the shipped arrangement, not an empty one.
            var file = JsonUserDataStore.StateFilePath(paths);
            var json = await File.ReadAllTextAsync(file);
            var start = json.IndexOf("\"Corners\"", StringComparison.Ordinal);
            var end = json.IndexOf('}', start);
            await File.WriteAllTextAsync(file, json[..start] + "\"Corners\": null" + json[(end + 1)..]);
            var older = await new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).LoadAsync(CancellationToken.None);
            Assert.Equal(DockCornerOwnership.Default, older.Settings.EffectivePlannerDock.EffectiveCorners);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>
    /// A docking value this version cannot read costs the user that panel's position and nothing else.
    /// Quarantining the file over a layout would take their locations and session with it.
    /// </summary>
    [Fact]
    public async Task ADockingValueThisVersionDoesNotKnowCostsOnlyThatPlacement()
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-corners-odd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new Paths(directory);
            var location = new SavedLocation(Guid.NewGuid(), "Kielder", new GeoCoordinate(55.2, -2.6), "Europe/London");
            var state = new PersistedState(4, new AppSettings(PlannerDock: new PlannerDockLayout(
                    [new("terrain", Mode: PanelDockMode.AutoHidden, Side: PanelDockSide.Left)], DockCornerOwnership.Default)),
                [location], PlanningSession.Default(Instant.FromUtc(2026, 3, 5, 21, 30), "UTC"), null);
            await new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).SaveAsync(state, CancellationToken.None);
            var file = JsonUserDataStore.StateFilePath(paths);
            await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file))
                .Replace("\"AutoHidden\"", "\"Collapsed\"", StringComparison.Ordinal)
                .Replace("\"TopLeft\": \"Left\"", "\"TopLeft\": \"Sideways\"", StringComparison.Ordinal));

            var loaded = await new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).LoadAsync(CancellationToken.None);
            Assert.Equal("Kielder", Assert.Single(loaded.Locations).Name);
            var panel = Assert.Single(loaded.Settings.EffectivePlannerDock.EffectivePanels);
            Assert.Equal(PanelDockMode.Docked, panel.Mode);
            Assert.Equal(DockCornerOwnership.Default, loaded.Settings.EffectivePlannerDock.EffectiveCorners);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>
    /// A layout written when panels could be hidden. The hidden flag is not a member any more, so it is
    /// skipped, the panel comes back in the place it was last in, and nothing else in the file — the
    /// rest of the layout, the locations, the session — is lost to it.
    /// </summary>
    [Fact]
    public async Task ALayoutWithHiddenPanelsFromBeforeTheyWereRemovedLoadsWithEveryPanelBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-hidden-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new Paths(directory);
            var location = new SavedLocation(Guid.NewGuid(), "Kielder", new GeoCoordinate(55.2, -2.6), "Europe/London");
            var state = new PersistedState(4, new AppSettings(PlannerDock: new PlannerDockLayout(
                [
                    new("terrain", Mode: PanelDockMode.Floating, Side: PanelDockSide.Left, FloatingX: 300, DockedSize: 280),
                    new("timeline", Mode: PanelDockMode.AutoHidden, Side: PanelDockSide.Bottom, DockedSize: 260)
                ], DockCornerOwnership.Default.WithSpan(PanelDockSide.Bottom, spans: true))),
                [location], PlanningSession.Default(Instant.FromUtc(2026, 3, 5, 21, 30), "UTC"), null);
            await new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).SaveAsync(state, CancellationToken.None);
            // Written as the earlier build wrote it: every panel with its visibility, two of them hidden.
            var file = JsonUserDataStore.StateFilePath(paths);
            var json = await File.ReadAllTextAsync(file);
            var legacy = json
                .Replace("\"PanelId\": \"terrain\",", "\"PanelId\": \"terrain\",\n          \"IsVisible\": false,", StringComparison.Ordinal)
                .Replace("\"PanelId\": \"timeline\",", "\"PanelId\": \"timeline\",\n          \"IsVisible\": false,", StringComparison.Ordinal);
            Assert.Equal(2, legacy.Split("\"IsVisible\": false").Length - 1);
            await File.WriteAllTextAsync(file, legacy);

            var loaded = await new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).LoadAsync(CancellationToken.None);
            // Nothing quarantined: the rest of the state is all there.
            Assert.Equal("Kielder", Assert.Single(loaded.Locations).Name);
            Assert.Empty(Directory.GetFiles(directory, "state.json.corrupt-*"));
            // The once-hidden panels keep the placement they were last given...
            var panels = loaded.Settings.EffectivePlannerDock.EffectivePanels;
            var terrain = panels.Single(panel => panel.PanelId == "terrain");
            Assert.Equal((PanelDockMode.Floating, 300d, 280d), (terrain.Mode, terrain.FloatingX, terrain.DockedSize));
            var timeline = panels.Single(panel => panel.PanelId == "timeline");
            Assert.Equal((PanelDockMode.AutoHidden, 260d), (timeline.Mode, timeline.DockedSize));
            // ...and the layout's corners are untouched by the change.
            Assert.True(loaded.Settings.EffectivePlannerDock.EffectiveCorners.Spans(PanelDockSide.Bottom));
            // Written again, the field is gone from the layout for good. (Target selections have an
            // IsVisible of their own, which is why only the layout's part of the file is checked.)
            await new JsonUserDataStore(paths, NullLogger<JsonUserDataStore>.Instance).SaveAsync(loaded, CancellationToken.None);
            using var rewritten = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(file));
            var dock = rewritten.RootElement.GetProperty("Settings").GetProperty("PlannerDock");
            Assert.DoesNotContain("IsVisible", dock.GetRawText());
            Assert.Equal(2, dock.GetProperty("Panels").GetArrayLength());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed record Paths(string Root) : IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => Root;
    }
}
