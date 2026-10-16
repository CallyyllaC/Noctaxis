using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mapsui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Locations;
using Noctaxis.Core.LightPollution;
using Noctaxis.Core.Viability;
using Noctaxis.Core.Weather;
using Noctaxis.Desktop;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Export;
using Noctaxis.Desktop.Mapping;
using Noctaxis.Desktop.Mapping.Vector;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;
using Noctaxis.Desktop.Services;
using NodaTime;
using NodaTime.Text;

internal static class Program
{
    internal static string Output = "";
    internal static DemoPaths Paths = null!;
    internal static string[] Shots = [];
    internal static int TimeoutSeconds;
    internal static int Result = 1;
    internal static readonly Instant DemoInstant = InstantPattern.ExtendedIso.Parse("2026-10-09T19:00:00Z").Value;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length != 4) { Console.Error.WriteLine("Usage: Noctaxis.WikiCapture OUTPUT ISOLATED_DATA IDS TIMEOUT_SECONDS"); return 2; }
        Output = Path.GetFullPath(args[0]);
        Paths = new DemoPaths(Path.GetFullPath(args[1]));
        Shots = args[2].Split(',');
        TimeoutSeconds = int.Parse(args[3], CultureInfo.InvariantCulture);
        if (Paths.GetApplicationDataDirectory().Equals(new PlatformUserDataPathProvider().GetApplicationDataDirectory(),
            StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The demo must not use real application data.");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-GB");
        Noctaxis.Core.Localisation.AppLanguages.Apply("en-GB");
        Directory.CreateDirectory(Output);
        try
        {
            SeedAsync().GetAwaiter().GetResult();
            AppBuilder.Configure<CaptureApp>().UsePlatformDetect().WithInterFont().LogToTrace()
                // Render the real menu inside the native window: one client capture, no popup compositing.
                .With(new Win32PlatformOptions { OverlayPopups = Shots.Contains("planner-map-context-menu") })
                .StartWithClassicDesktopLifetime([]);
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Output, "capture-error.txt"), ex.ToString()); Console.Error.WriteLine(ex.Message); }
        return Result;
    }

    private static async Task SeedAsync()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var target = catalogue.Targets.First(t => string.Equals(t.PrimaryIdentifier, "M31", StringComparison.OrdinalIgnoreCase)
            || t.DisplayName.Equals("M31", StringComparison.OrdinalIgnoreCase) || t.Aliases?.Contains("M31") == true);
        var camera = new CameraProfile("wiki-camera", "Full frame · 24 MP", 36, 24,
            ResolutionWidthPixels: 6000, ResolutionHeightPixels: 4000);
        var lens = new LensProfile("wiki-lens", "24–70 mm zoom", 24, 70);
        var shown = new[] { new CelestialObjectSelection("sun"), new CelestialObjectSelection("moon", Order: 1),
            new CelestialObjectSelection(target.Id, Order: 2) };
        var settings = new AppSettings(SelectedTimeZoneId: "Europe/London",
            Equipment: new([camera], [lens]), CelestialObjects: new(shown, target.Id),
            Appearance: new("builtin.noctaxis", AppearanceMode: AppearanceMode.Dark),
            CameraFraming: new(ManualBearingDegrees: 75, CameraPitchDegrees: 5),
            PlannerPip: new(ResolutionMetresPerPixel: 73.2421875,
                TerrainRelief: new(true, 1), LightPollution: new(false)),
            TerrainRelief: new(true, .6));
        var observer = new GeoCoordinate(53.0056, -3.9519);
        var places = new[] {
            new SavedLocation(Guid.Parse("f1d739df-e5c2-4ea3-a6c6-56c9a48c5b21"), "Eryri · demonstration", observer,
                "Europe/London", Notes: "Example camera position. Check access and the actual view.", IsFavourite: true),
            new SavedLocation(Guid.Parse("f1d739df-e5c2-4ea3-a6c6-56c9a48c5b22"), "Bannau Brycheiniog · demonstration",
                new(51.883, -3.436), "Europe/London", Notes: "A second example for the saved-location list.")
        };
        var session = PlanningSession.Default(DemoInstant, "Europe/London") with {
            Observer = observer, SavedLocationId = places[0].Id, TargetId = target.Id, VisibleObjects = shown,
            CameraProfileId = camera.Id, LensProfileId = lens.Id, Lens = new(SensorPreset.FullFrame, 36, 24, 35) };
        var store = new JsonUserDataStore(Paths, NullLogger<JsonUserDataStore>.Instance);
        await store.SaveAsync(new(4, settings, places, session, places[0].Id, observer), default);
    }

    // The production registration hook is internal. Keep this one reflection boundary in tooling
    // rather than changing application access/behaviour or maintaining a second service graph.
    internal static ServiceProvider Services() =>
        (ServiceProvider)(typeof(App).GetMethod("ConfigureServices", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [new Action<IServiceCollection>(s => {
                s.AddSingleton<IUserDataPathProvider>(Paths);
                if (!Shots.Any(IsComparison)) return;
                // Wrap the production provider only in this capture process. Other scenes still use
                // live weather; comparison scenes supply measurements, never a score or UI result.
                var productionWeather = s.Last(d => d.ServiceType == typeof(IWeatherProvider));
                s.AddSingleton<DemoWeather>(sp => new(
                    (IWeatherProvider)productionWeather.ImplementationFactory!(sp), sp.GetRequiredService<IClock>()));
                s.AddSingleton<IWeatherProvider>(sp => sp.GetRequiredService<DemoWeather>());
            })])!);

    internal static bool IsComparison(string id) => id is "viability-favourable" or "viability-compromised";

    internal static async Task PrepareOverviewAtlasAsync(LorenzInstallation destination)
    {
        var cacheRoot = Environment.GetEnvironmentVariable("NOCTAXIS_WIKI_ATLAS_CACHE")
            ?? throw new InvalidOperationException("Run the overview through generate-wiki-screenshots.ps1 to configure its isolated atlas cache.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Noctaxis/0.1 (wiki overview atlas)");
        var cache = new LorenzInstallation(new DemoPaths(Path.GetFullPath(cacheRoot)), http);
        await cache.DetectAsync(timeout.Token);
        if (!cache.IsAvailable)
        {
            if (File.Exists(Path.Combine(cache.StorageDirectory, "current")))
                throw new InvalidDataException("The isolated atlas cache is incomplete. Repair it or choose a fresh cache; existing screenshots are unchanged.");
            Console.WriteLine("Downloading the complete real Lorenz 2025 atlas into the isolated wiki cache.");
            await cache.InstallAsync(new Progress<LorenzInstallProgress>(p => {
                if (p.CompletedTiles % 100 == 0) Console.WriteLine($"Atlas: {p.CompletedTiles}/{p.TotalTiles} tiles");
            }), timeout.Token);
        }
        if (destination.IsAvailable || File.Exists(Path.Combine(destination.StorageDirectory, "current")))
            throw new InvalidOperationException("The overview atlas must be copied into a fresh demonstration profile.");
        // Full validated publisher data, never a synthetic or partial installation. Copy rather than
        // sharing writable files with the cache or reading the photographer's real profile.
        var generation = Path.GetFileName(cache.CurrentDirectory!);
        var target = Path.Combine(destination.StorageDirectory, generation);
        await Task.Run(() => {
            Directory.CreateDirectory(Path.Combine(target, "binary"));
            foreach (var tile in LorenzInstallation.ExpectedTiles())
            {
                timeout.Token.ThrowIfCancellationRequested();
                var name = LorenzAtlas.FileName(tile.X, tile.Y);
                File.Copy(Path.Combine(cache.CurrentDirectory!, "binary", name), Path.Combine(target, "binary", name));
            }
            foreach (var name in new[] { "manifest.json", "overview.f32" })
                File.Copy(Path.Combine(cache.CurrentDirectory!, name), Path.Combine(target, name));
            File.Copy(Path.Combine(cache.StorageDirectory, "current"), Path.Combine(destination.StorageDirectory, "current"));
        }, timeout.Token);
        await destination.DetectAsync(timeout.Token);
        if (!destination.IsAvailable) throw new InvalidDataException("The copied overview atlas did not pass production installation validation.");
    }

    internal static T Hidden<T>(object owner, string property) =>
        (T)(owner.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(owner)!);
}

public sealed class CaptureApp : App
{
    private ServiceProvider? _services;
    public override void OnFrameworkInitializationCompleted()
    {
        // Do not call App's production startup: it reads the real saved language before resolving services.
        var lifetime = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        _services = Program.Services();
        var vm = _services.GetRequiredService<MainViewModel>();
        vm.AttachThemeService(Themes!);
        var window = new MainWindow(vm, _services.GetRequiredService<DesktopDialogService>()) {
            Width = 1440, Height = 1000, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var main = window.FindControl<PlannerMapView>("PlannerMap")!;
        var pip = Program.Hidden<PlannerMapView>(window, "PipForTesting");
        // Default map construction records a user cache path, but no requests start before attachment.
        // Replace both basemaps before Show with the same isolated real tile source.
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Noctaxis/0.1 (wiki screenshot regeneration)");
        var fetcher = new CaptureFetcher(new OpenFreeMapTileFetcher(http,
            Path.Combine(Program.Paths.GetApplicationDataDirectory(), "BasemapCache", "openfreemap")));
        var source = new ThemedVectorTileSource(fetcher);
        foreach (var map in new NoctaxisMapView[] { main, pip })
            Program.Hidden<PlannerMapComposition>(map, "CompositionForTesting").ReplaceBasemap(new OpenFreeMapBasemap(source));
        lifetime.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            var report = new List<object>();
            try
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native wiki captures currently require Windows.");
                if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero)
                    throw new InvalidOperationException("No native application window is available.");
                await vm.InitializeAsync();
                vm.LocalDate = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
                vm.TimeText = "20:00";
                var needsPlanner = Program.Shots.Any(id => id is not ("equipment-settings" or "celestial-objects" or "locations"));
                if (needsPlanner)
                {
                    vm.ShowPlannerCommand.Execute(null);
                    main.ShowView(new(new(53.0056, -3.9519), 45));
                    await ReadyAsync(vm, [main, pip], source, fetcher);
                    // Fail rather than create a geographical example with an unknown terrain preview.
                    if (vm.TerrainFrameDepth is null) throw new InvalidOperationException("Terrain preview did not become available.");
                }
                foreach (var id in Program.Shots)
                {
                    if (id is not ("equipment-settings" or "celestial-objects" or "locations")) vm.ShowPlannerCommand.Execute(null);
                    Control? crop = null;
                    Button? popup = null;
                    ContextMenu? contextMenu = null;
                    object? demonstration = null;
                    switch (id)
                    {
                        case "planner-overview":
                            // Configure the unavailable layer first, so palette/cache notifications
                            // cannot interrupt its first native fetch after atlas activation.
                            vm.SettingsLightPollutionColorMap = LightPollutionColorMaps.Resolve("inferno");
                            main.Layers.ShowTerrainRelief = false;
                            main.Layers.LightPollutionOpacityPercent = 60;
                            main.Layers.ShowLightPollution = true;
                            await Program.PrepareOverviewAtlasAsync(vm.LightPollutionInstallation
                                ?? throw new InvalidOperationException("The production atlas installation is unavailable."));
                            // Installation changes post availability/cache work to the UI dispatcher.
                            // Drain those higher-priority notifications before requesting native data.
                            await Dispatcher.UIThread.InvokeAsync(() => {
                                var composition = Program.Hidden<PlannerMapComposition>(main, "CompositionForTesting");
                                if (!composition.Runtime[LightPollutionMapBinding.LayerId].IsRenderable)
                                    throw new InvalidOperationException("Overview atlas activation has not reached the main map.");
                                composition.Map.Refresh();
                            }, DispatcherPriority.Background);
                            pip.ShowView(new(new(53.0056, -3.9519), 73.2421875));
                            break;
                        case "planner-map-context-menu":
                            var mapControl = Program.Hidden<Mapsui.UI.Avalonia.MapControl>(main, "MapControlForTesting");
                            var contextSession = vm.Session;
                            var contextObserver = vm.Observer;
                            var contextLocation = vm.SelectedLocation;
                            // Away from the pin, PiP, frame and Inspector, in the map's own coordinates.
                            var point = new Point(mapControl.Bounds.Width * .62, mapControl.Bounds.Height * .36);
                            var pressed = new PointerPressedEventArgs(mapControl, new Avalonia.Input.Pointer(1, PointerType.Mouse, true),
                                window, mapControl.TranslatePoint(point, window)!.Value, 0,
                                new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed),
                                KeyModifiers.None);
                            mapControl.RaiseEvent(pressed);
                            mapControl.RaiseEvent(new ContextRequestedEventArgs(pressed));
                            contextMenu = mapControl.ContextMenu ?? throw new InvalidOperationException("Map context menu is missing.");
                            // Native pointer placement follows the user's desktop cursor. Anchor this
                            // real menu to the deterministic requested point instead; no global input.
                            contextMenu.Close();
                            contextMenu.Placement = PlacementMode.AnchorAndGravity;
                            contextMenu.PlacementRect = new Rect(point, new Size(1, 1));
                            contextMenu.PlacementAnchor = PopupAnchor.TopLeft;
                            contextMenu.PlacementGravity = PopupGravity.BottomRight;
                            contextMenu.Open(mapControl);
                            await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Background);
                            var action = contextMenu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() ==
                                Noctaxis.Core.Localisation.Strings.Get("Planner_Location_OpenMaps_Button"));
                            if (!contextMenu.IsOpen || !action.IsVisible || !action.IsEnabled ||
                                TopLevel.GetTopLevel(contextMenu) != window || action.Bounds.Height <= 0)
                                throw new InvalidOperationException("The actual Open in Maps menu item did not render inside the native window.");
                            var menuOrigin = contextMenu.TranslatePoint(default, mapControl)
                                ?? throw new InvalidOperationException("Context menu placement is not attached to the map.");
                            if (!new Rect(mapControl.Bounds.Size).Contains(new Rect(menuOrigin, contextMenu.Bounds.Size)))
                                throw new InvalidOperationException("The context menu would obscure UI outside the map.");
                            if (vm.Session != contextSession || vm.Observer != contextObserver || vm.SelectedLocation != contextLocation)
                                throw new InvalidOperationException("Opening the map menu changed the demonstration plan or location.");
                            demonstration = new { mapPoint = new { point.X, point.Y }, menu = contextMenu.Items.OfType<MenuItem>()
                                .Select(i => i.Header?.ToString()).ToArray(), overlayPopup = true, planUnchanged = true };
                            break;
                        case "timeline": crop = window.FindControl<HorizonGraph>("TimelineChart")!; break;
                        case "map-layers":
                            popup = window.FindControl<PlannerLayersControl>("PlannerLayers")!.FindControl<Button>("LayersButton")!;
                            popup.Flyout!.ShowAt(popup); break;
                        case "shot-viability":
                            if (vm.ShotViability?.RoundedScore is null) throw new InvalidOperationException("Shot viability is not ready.");
                            popup = window.FindControl<Button>("ShotViabilityButton")!;
                            popup.Flyout!.ShowAt(popup); break;
                        case "viability-favourable":
                        case "viability-compromised":
                            var favourable = id == "viability-favourable";
                            var weather = _services.GetRequiredService<DemoWeather>();
                            weather.CloudCoverPercent = favourable ? 5 : 95;
                            vm.LocalDate = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
                            vm.TimeText = favourable ? "23:00" : "19:00";
                            vm.FocalLength = 70;
                            vm.FollowFocusedTarget = true;
                            await ReadyAsync(vm, [main, pip], source, fetcher);
                            await vm.RefreshWeatherCommand.ExecuteAsync(null);
                            await ReadyAsync(vm, [main, pip], source, fetcher);
                            var assessment = vm.ShotViability ?? throw new InvalidOperationException("Comparison assessment is missing.");
                            if (assessment.RoundedScore is null || assessment.Coverage < .9 ||
                                assessment[ShotViabilityMetric.CloudCover].Status != ShotViabilityMetricStatus.Evaluated ||
                                assessment[ShotViabilityMetric.TerrainClearance].Status != ShotViabilityMetricStatus.Evaluated ||
                                assessment[ShotViabilityMetric.FramePosition].Severity != ShotViabilitySeverity.Good)
                                throw new InvalidOperationException("Comparison inputs have not been fully assessed or the target is not centred.");
                            // Quality gates describe the intended example, not replacement scores.
                            if (favourable && (assessment.Rating != ShotViabilityRating.Excellent || assessment.Warnings.Count != 0))
                                throw new InvalidOperationException("Favourable scene is not genuinely favourable: " + vm.ShotViabilityTitle + "; " +
                                    string.Join("; ", assessment.Warnings.Select(w => w.Explanation)));
                            if (!favourable && (assessment.Score >= ShotViabilityPolicy.GoodFrom ||
                                assessment[ShotViabilityMetric.CloudCover].Severity != ShotViabilitySeverity.Critical))
                                throw new InvalidOperationException("Compromised scene does not provide a clear contrast: " + vm.ShotViabilityTitle);
                            demonstration = new {
                                localDate = "2026-10-09", localTime = vm.TimeText, timeZone = "Europe/London",
                                observer = new { latitude = 53.0056, longitude = -3.9519 }, target = assessment.Target.DisplayName,
                                sensorMillimetres = new[] { 36, 24 }, sensorPixels = new[] { 6000, 4000 }, focalLengthMillimetres = 70,
                                followWithCamera = true, simulatedCloudCoverPercent = weather.CloudCoverPercent,
                                policy = assessment.PolicyId, score = assessment.RoundedScore, rating = assessment.Rating.ToString(),
                                coverage = assessment.Coverage, warnings = assessment.Warnings.Select(w => w.Explanation).ToArray(),
                                factors = assessment.Metrics.Select(m => new { m.Id, status = m.Status.ToString(), m.Explanation }).ToArray()
                            };
                            popup = window.FindControl<Button>("ShotViabilityButton")!;
                            popup.Flyout!.ShowAt(popup);
                            break;
                        case "equipment-settings":
                            SelectSettings(window, vm, "Equipment");
                            break;
                        case "celestial-objects":
                            SelectSettings(window, vm, "Celestial objects");
                            var search = vm.SettingsCelestialSearch;
                            var configured = vm.CelestialObjects.Single(t => t.Target.Category == AstralTargetCategory.Galaxy).Target;
                            search.SelectedObjectType = search.ObjectTypes.Single(o => o.Value == configured.Category);
                            search.SelectedConstellation = configured.Constellation!;
                            search.SelectedDesignationFamily = "Messier";
                            search.Query = "M31";
                            var searchTimer = Stopwatch.StartNew();
                            while (search.IsSearching || !search.Results.Any(t => t.Id == configured.Id))
                            {
                                if (searchTimer.Elapsed > TimeSpan.FromSeconds(Program.TimeoutSeconds))
                                    throw new TimeoutException("M31 catalogue search did not settle.");
                                await Task.Delay(100);
                            }
                            break;
                        case "locations":
                            foreach (var card in vm.Locations.Saved)
                                if (!await card.RefreshMapThumbnailAsync(SavedLocationMapRefreshMode.RefreshSource))
                                    throw new InvalidOperationException("A demonstration location image could not be generated.");
                            vm.ShowLocationsCommand.Execute(null);
                            break;
                        case "plan-sheet":
                            var png = await vm.CreateExportPngAsync(default,
                                (centre, width, height, token) => ExportMapComposer.ComposeAsync(source, centre, width, height, token));
                            NativeCapture.ValidatePng(png);
                            await File.WriteAllBytesAsync(Path.Combine(Program.Output, id + ".png"), png);
                            report.Add(new { id, source = "production plan-sheet exporter" });
                            continue;
                        default: throw new ArgumentException("Unknown screenshot: " + id);
                    }
                    await ReadyAsync(vm, vm.SelectedPageIndex == 1 ? [main, pip] : [], source, fetcher);
                    if (id == "planner-overview")
                    {
                        var mainState = Program.Hidden<PlannerMapComposition>(main, "CompositionForTesting").Runtime;
                        var pipState = Program.Hidden<PlannerMapComposition>(pip, "CompositionForTesting").Runtime;
                        if (!mainState[LightPollutionMapBinding.LayerId].IsRenderable ||
                            mainState[TerrainReliefMapBinding.LayerId].IsVisible ||
                            !pipState[TerrainReliefMapBinding.LayerId].IsRenderable ||
                            pipState[LightPollutionMapBinding.LayerId].IsVisible ||
                            vm.LightPollutionInstallation is not { IsAvailable: true, ReadError: null })
                            throw new InvalidOperationException("The overview's independent overlay arrangement is not ready.");
                        demonstration = new {
                            mainMap = new { lightPollution = mainState[LightPollutionMapBinding.LayerId],
                                terrainRelief = mainState[TerrainReliefMapBinding.LayerId], palette = "inferno",
                                resolution = main.CurrentView!.Value.Resolution, renderedAtlasTiles = AtlasRaster(main)!.CachedTileCount },
                            pipMap = new { lightPollution = pipState[LightPollutionMapBinding.LayerId],
                                terrainRelief = pipState[TerrainReliefMapBinding.LayerId], resolution = pip.CurrentView!.Value.Resolution },
                            atlas = LorenzAtlas.Name, atlasDirectory = vm.LightPollutionInstallation.CurrentDirectory,
                            atlasSourceTiles = LorenzAtlas.TileCount
                        };
                    }
                    // Readiness is followed by two completed render/layout turns, then a short quiet
                    // interval checked against fetch/render counters (not a blind startup sleep).
                    await Dispatcher.UIThread.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render);
                    await Dispatcher.UIThread.InvokeAsync(() => window.InvalidateVisual(), DispatcherPriority.Render);
                    await Task.Delay(250);
                    var popupContent = (popup?.Flyout as Flyout)?.Content as Control;
                    var image = id == "shot-viability" || Program.IsComparison(id)
                        ? NativeCapture.PopupPng(window, popupContent!)
                        : NativeCapture.WindowPng(window, crop, popupContent);
                    await File.WriteAllBytesAsync(Path.Combine(Program.Output, id + ".png"), image);
                    report.Add(new { id, source = "native Windows client capture", scale = window.RenderScaling,
                        logicalWidth = window.Width, logicalHeight = window.Height, tiles = fetcher.Successes, demonstration });
                    popup?.Flyout?.Hide();
                    contextMenu?.Close();
                    if (id == "planner-overview" && id != Program.Shots[^1])
                    {
                        // Other scenes intentionally have no atlas (notably the 90%-coverage comparisons).
                        // Remove only this run's copied generation, never the reusable atlas cache.
                        main.Layers.ShowLightPollution = false;
                        main.Layers.LightPollutionOpacityPercent = LightPollutionPreferences.DefaultOpacity * 100;
                        vm.SettingsLightPollutionColorMap = LightPollutionColorMaps.Grayscale;
                        main.Layers.ShowTerrainRelief = true;
                        await vm.LightPollutionInstallation!.RemoveAsync();
                        await ReadyAsync(vm, [main, pip], source, fetcher);
                    }
                    if (Program.IsComparison(id))
                    {
                        // Restore the shared scene so -Only ordering cannot affect other captures.
                        _services.GetRequiredService<DemoWeather>().CloudCoverPercent = null;
                        vm.FollowFocusedTarget = false;
                        vm.CameraBearingDegrees = 75;
                        vm.CameraPitchDegrees = 5;
                        vm.FocalLength = 35;
                        vm.TimeText = "20:00";
                        await ReadyAsync(vm, [main, pip], source, fetcher);
                        await vm.RefreshWeatherCommand.ExecuteAsync(null);
                    }
                }
                await File.WriteAllTextAsync(Path.Combine(Program.Output, "capture-report.json"), JsonSerializer.Serialize(new {
                    planningInstant = Program.DemoInstant.ToString(), dataDirectory = Program.Paths.GetApplicationDataDirectory(),
                    theme = "Noctaxis Dark", screenshots = report
                }, new JsonSerializerOptions { WriteIndented = true }));
                Program.Result = 0;
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(Path.Combine(Program.Output, "capture-error.txt"), ex.ToString());
                try { await File.WriteAllBytesAsync(Path.Combine(Program.Output, "diagnostic-window.png"), NativeCapture.WindowPng(window, null)); }
                catch (Exception captureError) { Console.Error.WriteLine("Diagnostic capture: " + captureError.Message); }
                Console.Error.WriteLine(ex);
            }
            finally { window.Close(); lifetime.Shutdown(Program.Result); Themes?.Dispose(); http.Dispose(); }
        };
    }

    private static void SelectSettings(MainWindow window, MainViewModel vm, string label)
    {
        vm.ShowSettingsCommand.Execute(null);
        var categories = window.GetVisualDescendants().OfType<TabControl>()
            .First(t => t.Items.OfType<TabItem>().Any(i => i.Header?.ToString() == label));
        categories.SelectedItem = categories.Items.OfType<TabItem>().First(i => i.Header?.ToString() == label);
    }

    private static LightPollutionRasterSource? AtlasRaster(NoctaxisMapView map)
    {
        var binding = Program.Hidden<LightPollutionMapBinding?>(map, "LightPollutionForTesting");
        return binding is null ? null : (LightPollutionRasterSource)typeof(LightPollutionMapBinding)
            .GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binding)!;
    }

    private static async Task ReadyAsync(MainViewModel vm, NoctaxisMapView[] maps,
        ThemedVectorTileSource source, CaptureFetcher fetcher)
    {
        var timer = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        long last = -1;
        while (timer.Elapsed < TimeSpan.FromSeconds(Program.TimeoutSeconds))
        {
            var planning = (Task)typeof(MainViewModel).GetMethod("WaitForPlannerRefreshAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
            var busy = maps.Length > 0 && (vm.IsPlannerRefreshing || !planning.IsCompleted ||
                vm.TimelinePaths.Count == 0 || !Program.Hidden<Task>(vm, "ShotWindowCalculationForTesting").IsCompleted || fetcher.Active != 0);
            foreach (var map in maps)
            {
                var composition = Program.Hidden<PlannerMapComposition>(map, "CompositionForTesting");
                busy |= composition.Map.Layers.Any(l => l.Enabled && l.Busy);
                // A visible, available atlas is not ready until real raster content has been generated.
                if (composition.Runtime[LightPollutionMapBinding.LayerId].IsRenderable)
                    busy |= AtlasRaster(map)?.CachedTileCount is not > 0;
            }
            var renders = Program.Hidden<long>(source, "RenderCount");
            var activity = renders + fetcher.Successes;
            if (busy || activity != last) quiet.Restart();
            last = activity;
            if (!busy && (maps.Length == 0 || (fetcher.Successes > 0 && renders > 0)) && quiet.ElapsedMilliseconds >= 2000)
            {
                if (fetcher.Failures > 0) throw new InvalidOperationException("Map tiles failed to load; no screenshots will replace existing images.");
                return;
            }
            await Task.Delay(200);
        }
        var layers = maps.SelectMany(m => Program.Hidden<PlannerMapComposition>(m, "CompositionForTesting").Map.Layers)
            .Where(l => l.Enabled && l.Busy).Select(l => l.Name);
        throw new TimeoutException($"Content did not settle: plannerRefreshing={vm.IsPlannerRefreshing}, " +
            $"status={vm.PlannerRefreshStatusText}, timelinePaths={vm.TimelinePaths.Count}, terrainFrame={vm.TerrainFrameDepth is not null}, " +
            $"activeTiles={fetcher.Active}, successfulTiles={fetcher.Successes}, failedTiles={fetcher.Failures}, " +
            $"vectorRenders={Program.Hidden<long>(source, "RenderCount")}, busyLayers={string.Join(", ", layers)}, " +
            $"atlasRasterTiles={string.Join(",", maps.Select(m => AtlasRaster(m)?.CachedTileCount ?? 0))}.");
    }
}

/// <summary>Explicit demonstration measurements; production astronomy/geometry/scoring stay untouched.</summary>
internal sealed class DemoWeather(IWeatherProvider production, IClock clock) : IWeatherProvider
{
    internal double? CloudCoverPercent { get; set; }
    public Task<WeatherResult> GetWeatherAsync(WeatherRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (CloudCoverPercent is not double cloud) return production.GetWeatherAsync(request, cancellationToken);
        var conditions = new WeatherConditions(request.ApproximateTime,
            cloud, cloud, cloud, cloud, 0, 0, "None", 1, 180, 2, 8, 50, 0, 30,
            "Simulated wiki demonstration weather", clock.GetCurrentInstant());
        return Task.FromResult(new WeatherResult(DataState.Ready, conditions, "Simulated demonstration inputs; not a forecast."));
    }
}

internal sealed record DemoPaths(string Directory) : IUserDataPathProvider
{
    public string GetApplicationDataDirectory() => Directory;
}

internal sealed class CaptureFetcher(IVectorTileFetcher source) : IVectorTileFetcher
{
    internal int Active, Successes, Failures;
    public async Task<byte[]?> FetchAsync(int zoom, int column, int row, CancellationToken token)
    {
        Interlocked.Increment(ref Active);
        try
        {
            var bytes = await source.FetchAsync(zoom, column, row, token);
            if (bytes is { Length: > 0 }) Interlocked.Increment(ref Successes);
            else Interlocked.Increment(ref Failures);
            return bytes;
        }
        catch { Interlocked.Increment(ref Failures); throw; }
        finally { Interlocked.Decrement(ref Active); }
    }
    public void Discard(int zoom, int column, int row) => source.Discard(zoom, column, row);
}
