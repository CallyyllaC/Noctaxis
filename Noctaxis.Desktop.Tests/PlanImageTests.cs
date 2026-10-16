using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Export;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Export;
using Noctaxis.Desktop.Mapping.Vector;
using Noctaxis.Desktop.ViewModels;
using NodaTime;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Plan images: one renderer for Save PNG and Copy PNG, a minimal plan embedded in the PNG, and one importer for
/// Open plan image…, Load from clipboard and a dropped file, which applies a plan only once it is valid.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static byte[] PlanPng(NoctaxisPlan plan) => NoctaxisPlanImage.Embed(FakeExporter.Pixel, plan);

    private static (double Latitude, double Longitude, Instant Instant, double Bearing, double Pitch, string? Focus, bool Follow) PlanState(MainViewModel vm) =>
        (vm.Observer.Latitude, vm.Observer.Longitude, vm.Session.Instant, vm.CameraBearingDegrees, vm.CameraPitchDegrees,
            vm.CelestialObjects.FirstOrDefault(item => item.IsPrimary)?.TargetId, vm.FollowFocusedTarget);

    /// <summary>A plan unlike the Planner's default in every field, focused on a target that is not focused (or shown) yet.</summary>
    private static NoctaxisPlan DistinctPlan(MainViewModel vm, bool follow = true, string? target = null) =>
        new(Instant.FromUtc(2026, 10, 7, 21, 0), 52.59302, -3.64697, 293.3, 7.2,
            new NoctaxisPlanFocus(target ?? (vm.CelestialObjects.FirstOrDefault(item => !item.IsVisible) ??
            vm.CelestialObjects.Last(item => !item.IsPrimary)).TargetId, follow));

    /// <summary>A real file, as the picker or a drop hands one over: written to a temporary folder and opened through the window.</summary>
    private static async Task<IStorageFile> FileAsync(Window window, string name, byte[] bytes)
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-plan-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, bytes);
        return (await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path)))!;
    }

    private static DataTransfer Files(params IStorageFile[] files)
    {
        var transfer = new DataTransfer();
        foreach (var file in files) transfer.Add(DataTransferItem.CreateFile(file));
        return transfer;
    }

    private static DataTransfer ClipboardPng(byte[] png)
    {
        var item = new DataTransferItem();
        item.Set(PlanImageSources.PngFormat, png);
        var transfer = new DataTransfer();
        transfer.Add(item);
        return transfer;
    }

    // ---- Export ------------------------------------------------------------------------------------------------

    /// <summary>The exported PNG is the renderer's pixels with the plan embedded: UTC time, place, camera and focus.</summary>
    [Fact]
    public async Task TheExportedPngCarriesTheMinimalPlan()
    {
        var exporter = new FakeExporter();
        var vm = await PlannerAtObserverAsync(new FakePlanning(new Noctaxis.Core.Catalogues.OpenNgcTargetCatalogue()),
            new Noctaxis.Core.Catalogues.OpenNgcTargetCatalogue(), new GeoCoordinate(51.5, -0.12), Instant.FromUtc(2024, 1, 15, 20, 0), exporter);
        vm.CameraBearingDegrees = 123.4;
        vm.CameraPitchDegrees = 5.6;

        var png = await vm.CreateExportPngAsync(CancellationToken.None);
        var plan = NoctaxisPlanImage.Read(png).Plan!;
        Assert.Equal(vm.Session.Instant, plan.TimeUtc);
        Assert.Equal((51.5, -0.12, 123.4, 5.6), (plan.Latitude, plan.Longitude, plan.BearingDegrees, plan.PitchDegrees));
        Assert.Same(plan.GetType(), exporter.LastExport!.Plan.GetType());
        Assert.Equal(plan, exporter.LastExport.Plan);
        using var document = JsonDocument.Parse(PngMetadataWriter.ReadText(png, NoctaxisPlanImage.MetadataKey)!);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.EndsWith("Z", document.RootElement.GetProperty("timeUtc").GetString());
        Assert.Equal(["format", "schemaVersion", "timeUtc", "location", "camera", "focus"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        // The pixels are exactly the renderer's.
        using var rendered = SKBitmap.Decode(FakeExporter.Pixel);
        using var exported = SKBitmap.Decode(png);
        Assert.Equal(rendered.Pixels, exported.Pixels);
    }

    /// <summary>Focus and Follow as they are: none (null), or the focused target's id with Follow.</summary>
    [AvaloniaFact]
    public async Task TheExportedPlanCarriesFocusAndFollowOrNull()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var focused = vm.CelestialObjects.First(item => item.IsVisible);
            focused.FocusCommand.Execute(null);
            vm.FollowFocusedTarget = true;
            Settle(window);
            await vm.WaitForPlannerRefreshAsync();
            var plan = NoctaxisPlanImage.Read(await vm.CreateExportPngAsync(CancellationToken.None)).Plan!;
            Assert.Equal(new NoctaxisPlanFocus(focused.TargetId, true), plan.Focus);

            var unfocused = vm.CurrentPlan() with { Focus = null };
            Assert.Null(NoctaxisPlan.Parse(unfocused.ToJson()).Plan!.Focus);
            Assert.Contains("\"focus\":null", unfocused.ToJson());
        }
        finally { Close(window); }
    }

    /// <summary>A folder of its own for each test's copied files, so tests never touch the user's temporary copies.</summary>
    private static string ClipboardRoot() => Path.Combine(Path.GetTempPath(), "noctaxis-plan-tests", "clipboard-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Save PNG and Copy PNG deliver one image: both handlers take it from the one place; Save PNG writes those bytes
    /// as they are, and Copy PNG's file holds exactly the same bytes, so the same plan.
    /// </summary>
    [AvaloniaFact]
    public async Task SaveAndCopyDeliverTheSameImage()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.DesktopProject, "Views", "MainWindow.axaml.cs"));
        string Body(string method) => source[source.IndexOf($"void {method}(", StringComparison.Ordinal)..][..900];
        Assert.Contains("CreatePlanImageAsync(", Body("ExportPngClick"));
        Assert.Contains("stream.WriteAsync(png)", Body("ExportPngClick"));
        Assert.Contains("CreatePlanImageAsync(", Body("CopyPngClick"));
        Assert.Contains("CopyPngPayloadAsync(png)", Body("CopyPngClick"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"CreateExportPngAsync\("));
        Assert.DoesNotContain("RenderPng", source);
        // No second encoder: nothing on the copy path encodes or re-encodes an image.
        foreach (var file in new[] { Path.Combine("Export", "ClipboardPlanFile.cs"), Path.Combine("Export", "PlanImageSources.cs") })
        {
            var code = File.ReadAllText(Path.Combine(TestPaths.DesktopProject, file));
            Assert.DoesNotContain(".Encode(", code);
            Assert.DoesNotContain(".Save(", code);
        }

        var (vm, window, _) = await CreatePlannerAsync();
        var root = ClipboardRoot();
        try
        {
            vm.CelestialObjects.First(item => item.IsVisible).FocusCommand.Execute(null);
            Settle(window);
            await vm.WaitForPlannerRefreshAsync();
            var png = await window.CreatePlanImageAsync(CancellationToken.None);
            // Save PNG's file, written as its handler writes it.
            var saved = Path.Combine(root, "saved.png");
            Directory.CreateDirectory(root);
            await using (var stream = File.Create(saved)) await stream.WriteAsync(png);

            using var transfer = await window.CopyPngPayloadAsync(png, root);
            var item = Assert.Single(transfer.Items);
            var copied = Assert.IsAssignableFrom<IStorageFile>(item.TryGetFile());
            var copiedPath = copied.TryGetLocalPath()!;
            Assert.Equal(ClipboardPlanFile.FileName, Path.GetFileName(copiedPath));
            Assert.Equal(await File.ReadAllBytesAsync(saved), await File.ReadAllBytesAsync(copiedPath));
            Assert.Equal(PngMetadataWriter.ReadText(png, NoctaxisPlanImage.MetadataKey),
                PngMetadataWriter.ReadText(await File.ReadAllBytesAsync(copiedPath), NoctaxisPlanImage.MetadataKey));
            Assert.Equal(vm.CurrentPlan(), NoctaxisPlanImage.Read(await File.ReadAllBytesAsync(copiedPath)).Plan);
            // The same bytes as raw PNG, and the picture for applications that take only pictures.
            Assert.Equal(png, item.TryGetValue(PlanImageSources.PngFormat));
            using var picture = Assert.IsAssignableFrom<Bitmap>(item.TryGetBitmap());
            Assert.True(picture.PixelSize.Width > 0);
        }
        finally
        {
            Close(window);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>Copying again and again keeps only the newest few files, each named as Save PNG names it, the newest intact.</summary>
    [Fact]
    public async Task RepeatedCopiesKeepOnlyTheNewestFewFiles()
    {
        var root = ClipboardRoot();
        try
        {
            string last = "";
            for (var i = 0; i < 12; i++)
                last = await ClipboardPlanFile.WriteAsync(NoctaxisPlanImage.Embed(FakeExporter.Pixel,
                    new NoctaxisPlan(Instant.FromUtc(2026, 1, 1, 0, i), 1, 2, 3, 4, null)), root);
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            Assert.Equal(ClipboardPlanFile.Kept, files.Length);
            Assert.All(files, file => Assert.Equal(ClipboardPlanFile.FileName, Path.GetFileName(file)));
            Assert.Contains(last, files);
            Assert.Equal(Instant.FromUtc(2026, 1, 1, 0, 11), NoctaxisPlanImage.Read(await File.ReadAllBytesAsync(last)).Plan!.TimeUtc);
            Assert.StartsWith(Path.GetTempPath(), ClipboardPlanFile.DefaultRoot);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // ---- Import --------------------------------------------------------------------------------------------------

    /// <summary>A valid plan restores the place, the moment (in the planning zone), the camera and the focus with Follow.</summary>
    [AvaloniaFact]
    public async Task AValidPlanRestoresTheMinimalState()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var plan = DistinctPlan(vm);
            var outcome = vm.ImportPlanImage(PlanImageSource.Of(PlanPng(plan)));
            // The import is synchronous and reports last. Once the dispatcher runs, the refresh it scheduled reports
            // its own progress in the same status line and, with Follow restored, aims the camera at the target's
            // new position, as after any change; so the restored plan is checked as the import leaves it.
            Assert.True(outcome.Imported, outcome.Message);
            Assert.Equal("Plan imported", outcome.Message);
            Assert.Equal(outcome.Message, vm.StatusMessage);
            Assert.Equal((plan.Latitude, plan.Longitude, plan.TimeUtc, plan.BearingDegrees, plan.PitchDegrees, plan.Focus!.TargetId, true),
                PlanState(vm));
            Settle(window);
            Assert.True(vm.CelestialObjects.Single(item => item.IsPrimary).IsVisible);
            // Nothing derived came with it: the plan is recalculated for the new place, as after any move.
            await vm.WaitForPlannerRefreshAsync();
            Assert.Equal(plan.Latitude, vm.Snapshot!.Session.Observer.Latitude, 6);
            Assert.Equal(plan.TimeUtc, vm.Snapshot.Session.Instant);
            // Follow came back with its target, so the camera now follows it.
            Assert.Equal(plan.Focus.TargetId, vm.Snapshot.Position.Target.Id);
            Assert.Equal(vm.Snapshot.Position.Horizontal.AzimuthDegrees, vm.CameraBearingDegrees, 6);
            Assert.Equal(vm.Snapshot.Position.Horizontal.AltitudeDegrees, vm.CameraPitchDegrees, 6);
        }
        finally { Close(window); }
    }

    [AvaloniaFact]
    public async Task FollowIsRestoredOnlyWithItsTarget()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var withoutFollow = DistinctPlan(vm, follow: false);
            Assert.True(vm.ImportPlanImage(PlanImageSource.Of(PlanPng(withoutFollow))).Imported);
            Assert.Equal((withoutFollow.Focus!.TargetId, false), (PlanState(vm).Focus, vm.FollowFocusedTarget));

            // A target no longer in the list: the plan comes in without focus, and Follow is off.
            vm.FollowFocusedTarget = true;
            var missing = DistinctPlan(vm, follow: true, target: "ngc9999-not-a-target");
            var outcome = vm.ImportPlanImage(PlanImageSource.Of(PlanPng(missing)));
            Assert.True(outcome.Imported);
            Assert.Contains("ngc9999-not-a-target", outcome.Message);
            Assert.Null(PlanState(vm).Focus);
            Assert.False(vm.FollowFocusedTarget);
            Assert.Equal((missing.Latitude, missing.BearingDegrees), (vm.Observer.Latitude, vm.CameraBearingDegrees));

            // No focus at all: Follow is off.
            vm.FollowFocusedTarget = true;
            Assert.True(vm.ImportPlanImage(PlanImageSource.Of(PlanPng(missing with { Focus = null }))).Imported);
            Assert.Null(PlanState(vm).Focus);
            Assert.False(vm.FollowFocusedTarget);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The camera's own rules apply to a valid direction: the bearing wraps, and a pitch below the camera's lowest
    /// stops at its limit. (A pitch that is not a direction at all is refused before anything changes.)
    /// </summary>
    [AvaloniaFact]
    public async Task AnImportedCameraKeepsTheCamerasLimits()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var plan = DistinctPlan(vm, follow: false) with { BearingDegrees = 370, PitchDegrees = -60 };
            Assert.True(vm.ImportPlanImage(PlanImageSource.Of(PlanPng(plan))).Imported);
            Assert.Equal(10, vm.CameraBearingDegrees, 6);
            Assert.Equal(CameraFramingSettings.MinimumCameraPitchDegrees, vm.CameraPitchDegrees, 6);
        }
        finally { Close(window); }
    }

    /// <summary>Anything that is not a valid plan is refused with its reason, and the Planner is exactly as it was.</summary>
    [AvaloniaFact]
    public async Task AFailedImportLeavesThePlannerUntouched()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var before = PlanState(vm);
            using var bitmap = new SKBitmap(2, 2);
            using var ordinary = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
            var plan = DistinctPlan(vm);
            var cases = new (byte[] Bytes, string Message)[]
            {
                (ordinary.ToArray(), "This image has no Noctaxis plan in it."),
                (PngMetadataWriter.AddText(FakeExporter.Pixel, NoctaxisPlanImage.MetadataKey, "{ not json"), "The plan in this image is damaged."),
                (PngMetadataWriter.AddText(FakeExporter.Pixel, NoctaxisPlanImage.MetadataKey, plan.ToJson().Replace("\"schemaVersion\":1", "\"schemaVersion\":7")),
                    "This plan was made by a newer version of Noctaxis. Update Noctaxis to open it."),
                (PngMetadataWriter.AddText(FakeExporter.Pixel, NoctaxisPlanImage.MetadataKey, plan.ToJson().Replace("52.59302", "152.5")), "The plan's location is not valid."),
                (PngMetadataWriter.AddText(FakeExporter.Pixel, NoctaxisPlanImage.MetadataKey, plan.ToJson().Replace("21:00:00Z", "21:00:00")), "The plan's time is not valid."),
                // Beyond the calendar any zone can show: refused before the place changes.
                (PngMetadataWriter.AddText(FakeExporter.Pixel, NoctaxisPlanImage.MetadataKey, plan.ToJson().Replace("2026-10-07T21:00:00Z", "9999-12-31T23:00:00Z")), "The plan's time is not valid."),
                (PngMetadataWriter.AddText(FakeExporter.Pixel, NoctaxisPlanImage.MetadataKey, plan.ToJson().Replace("\"pitchDegrees\":7.2", "\"pitchDegrees\":120")), "The plan's camera direction is not valid."),
                (PngMetadataWriter.AddText(FakeExporter.Pixel, NoctaxisPlanImage.MetadataKey, plan.ToJson().Replace("\"follow\":true", "\"follow\":\"yes\"")), "The plan in this image is damaged."),
                (PngMetadataWriter.AddText(FakeExporter.Pixel, NoctaxisPlanImage.LegacyScoutingCardKey, "{\"exportSchemaVersion\":2}"),
                    "This is a scouting card from an earlier version of Noctaxis. It records what the card showed, not a plan, so it cannot be opened. Export the plan again from the Planner."),
                ([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3], "Only PNG plan images can be imported."),
                ("hello"u8.ToArray(), "That is not an image Noctaxis can read.")
            };
            foreach (var (bytes, message) in cases)
            {
                var outcome = vm.ImportPlanImage(PlanImageSource.Of(bytes));
                Settle(window);
                Assert.False(outcome.Imported);
                Assert.Equal(message, outcome.Message);
                Assert.Equal(message, vm.StatusMessage);
                Assert.Equal(before, PlanState(vm));
            }
        }
        finally { Close(window); }
    }

    // ---- The three ways in -------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task OpenPlanImageReadsThePickedFileThroughTheOneImporter()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var plan = DistinctPlan(vm, follow: false);
            Assert.True((await window.ImportFromFilesAsync([await FileAsync(window, "plan.png", PlanPng(plan))])).Imported);
            Assert.Equal(plan.BearingDegrees, vm.CameraBearingDegrees, 6);

            var before = PlanState(vm);
            var text = await window.ImportFromFilesAsync([await FileAsync(window, "notes.txt", "no image here"u8.ToArray())]);
            Assert.False(text.Imported);
            Assert.Equal("That is not an image Noctaxis can read.", text.Message);
            Assert.Equal(before, PlanState(vm));
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The clipboard's raw PNG imports; an image offered only as a decoded picture says so, since its plan is gone;
    /// an empty or text-only clipboard has nothing to import. No text is read as a plan.
    /// </summary>
    [AvaloniaFact]
    public async Task LoadFromClipboardImportsTheRawPngAndExplainsWhatItCannot()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var plan = DistinctPlan(vm, follow: false);
            Assert.True((await window.ImportFromClipboardAsync(ClipboardPng(PlanPng(plan)))).Imported);
            Assert.Equal(plan.Latitude, vm.Observer.Latitude, 6);
            var before = PlanState(vm);

            var pictureOnly = new DataTransferItem();
            using (var stream = new MemoryStream(PlanPng(plan))) pictureOnly.SetBitmap(new Bitmap(stream));
            var bitmapClipboard = new DataTransfer();
            bitmapClipboard.Add(pictureOnly);
            var lost = await window.ImportFromClipboardAsync(bitmapClipboard);
            Assert.False(lost.Imported);
            Assert.StartsWith("The clipboard image does not contain Noctaxis plan data.", lost.Message);

            var textClipboard = new DataTransfer();
            textClipboard.Add(DataTransferItem.CreateText(plan.ToJson()));
            foreach (var empty in new IAsyncDataTransfer?[] { null, new DataTransfer(), textClipboard })
                Assert.Equal("There is no image to import.", (await window.ImportFromClipboardAsync(empty)).Message);
            Assert.Equal(before, PlanState(vm));
        }
        finally { Close(window); }
    }

    /// <summary>
    /// A copied file comes first: exactly one is imported, even beside raw PNG bytes of another plan; several are
    /// refused rather than one chosen; and a copied file that is not a PNG is refused as any such file is.
    /// </summary>
    [AvaloniaFact]
    public async Task LoadFromClipboardPrefersOneCopiedFile()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var plan = DistinctPlan(vm, follow: false);
            var both = DataTransferItem.CreateFile(await FileAsync(window, "noctaxis-plan.png", PlanPng(plan)));
            both.Set(PlanImageSources.PngFormat, PlanPng(plan with { Latitude = 10 }));
            var clipboard = new DataTransfer();
            clipboard.Add(both);
            Assert.True((await window.ImportFromClipboardAsync(clipboard)).Imported);
            Assert.Equal(plan.Latitude, vm.Observer.Latitude, 6);
            var before = PlanState(vm);

            var other = plan with { Latitude = 10 };
            var two = await window.ImportFromClipboardAsync(Files(await FileAsync(window, "a.png", PlanPng(other)), await FileAsync(window, "b.png", PlanPng(other))));
            Assert.False(two.Imported);
            Assert.Equal("The clipboard holds several files. Copy one plan image at a time.", two.Message);
            var notPng = await window.ImportFromClipboardAsync(Files(await FileAsync(window, "photo.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0, 0])));
            Assert.Equal("Only PNG plan images can be imported.", notPng.Message);
            var text = await window.ImportFromClipboardAsync(Files(await FileAsync(window, "plan.txt", "{\"format\":\"noctaxis-plan\"}"u8.ToArray())));
            Assert.Equal("That is not an image Noctaxis can read.", text.Message);
            Assert.Equal(before, PlanState(vm));
        }
        finally { Close(window); }
    }

    /// <summary>One dropped plan image imports; several, or a file that is not a plan, are refused and change nothing.</summary>
    [AvaloniaFact]
    public async Task DroppingAPlanImageImportsExactlyOne()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            var plan = DistinctPlan(vm, follow: false);
            Assert.True(Avalonia.Input.DragDrop.GetAllowDrop(window.FindControl<Grid>("PlannerPage")!));
            Assert.True((await window.ImportFromDropAsync(Files(await FileAsync(window, "plan.png", PlanPng(plan))))).Imported);
            Assert.Equal(plan.TimeUtc, vm.Session.Instant);
            var before = PlanState(vm);

            var other = plan with { Latitude = 10 };
            var two = await window.ImportFromDropAsync(Files(await FileAsync(window, "a.png", PlanPng(other)), await FileAsync(window, "b.png", PlanPng(other))));
            Assert.Equal("Drop one plan image at a time.", two.Message);
            var unsupported = await window.ImportFromDropAsync(Files(await FileAsync(window, "photo.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 0, 0])));
            Assert.Equal("Only PNG plan images can be imported.", unsupported.Message);
            var damaged = await window.ImportFromDropAsync(Files(await FileAsync(window, "plan.png",
                PngMetadataWriter.AddText(FakeExporter.Pixel, NoctaxisPlanImage.MetadataKey, "{\"format\":\"noctaxis-plan\",\"schemaVersion\":1}"))));
            Assert.False(damaged.Imported);
            Assert.Equal("There is no image to import.", (await window.ImportFromDropAsync(new DataTransfer())).Message);
            Assert.Equal(before, PlanState(vm));
            // The highlight is off when nothing is being dragged.
            Assert.False(window.FindControl<Border>("PlannerDropHint")!.IsVisible);
        }
        finally { Close(window); }
    }

    // ---- Round trip --------------------------------------------------------------------------------------------

    /// <summary>
    /// Export, move everything, then import the image — from a file, from Copy PNG's clipboard payload, and from its
    /// copied file alone — and the plan is back: place, moment, camera, focus and Follow, with Follow off and on. The
    /// weather, terrain and astronomy are the Planner's own, recalculated, never read from the image.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APlanImageOpensBackToThePlanItWasMadeFrom(bool follow)
    {
        var (vm, window, _) = await CreatePlannerAsync();
        var root = ClipboardRoot();
        try
        {
            var target = vm.CelestialObjects.First(item => item.IsVisible);
            target.FocusCommand.Execute(null);
            vm.FollowFocusedTarget = follow;
            vm.CameraBearingDegrees = 211.5;
            vm.CameraPitchDegrees = 12.25;
            Settle(window);
            await vm.WaitForPlannerRefreshAsync();
            var exported = PlanState(vm);
            var png = await vm.CreateExportPngAsync(CancellationToken.None);
            Assert.DoesNotContain("weather", PngMetadataWriter.ReadText(png, NoctaxisPlanImage.MetadataKey)!, StringComparison.OrdinalIgnoreCase);

            foreach (var route in new Func<Task<PlanImportOutcome>>[]
                     {
                         async () => await window.ImportFromFilesAsync([await FileAsync(window, "plan.png", png)]),
                         // Copy PNG's own payload, then the file alone, as Explorer or another file-aware application hands it back.
                         async () => await window.ImportFromClipboardAsync(await window.CopyPngPayloadAsync(png, root)),
                         async () => await window.ImportFromClipboardAsync(
                             Files(Assert.Single((await window.CopyPngPayloadAsync(png, root)).Items).TryGetFile() as IStorageFile
                                 ?? throw new InvalidOperationException("Copy PNG put no file on the clipboard.")))
                     })
            {
                vm.MoveObserver(new GeoCoordinate(10, 20));
                vm.CameraBearingDegrees = 5;
                vm.CameraPitchDegrees = -20;
                vm.ImportPlanImage(PlanImageSource.Of(PlanPng(DistinctPlan(vm, follow: false) with { Focus = null })));
                Settle(window);
                Assert.NotEqual(exported, PlanState(vm));
                Assert.True((await route()).Imported);
                Settle(window);
                var restored = PlanState(vm);
                Assert.Equal(exported.Latitude, restored.Latitude, 6);
                Assert.Equal(exported.Longitude, restored.Longitude, 6);
                Assert.Equal((exported.Instant, exported.Bearing, exported.Pitch, exported.Focus, exported.Follow),
                    (restored.Instant, restored.Bearing, restored.Pitch, restored.Focus, restored.Follow));
            }
        }
        finally
        {
            Close(window);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ---- The sheet -----------------------------------------------------------------------------------------------

    private async Task<PlannerExportSnapshot> CapturedSheetAsync(MainViewModel vm, FakeExporter exporter)
    {
        await vm.WaitForPlannerRefreshAsync();
        await vm.CreateExportPngAsync(CancellationToken.None);
        return exporter.LastExport!;
    }

    private static byte[] SolidPng(int width, int height, SKColor colour)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(colour);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// The sheet: header across the top, naming the target; the clean map beside the weather panel; the target's
    /// direction chart the full width under them; credits at the foot. The map is the composed image with the
    /// location and its coordinates — no Planner map, no chart, no overlay.
    /// </summary>
    [AvaloniaFact]
    public async Task TheSheetHasItsHeaderMapWeatherFullWidthChartAndCredits()
    {
        var exporter = new FakeExporter();
        var (vm, window, _) = await CreatePlannerAsync(exporter: exporter);
        try
        {
            var snapshot = await CapturedSheetAsync(vm, exporter) with
            {
                Map = new ExportMapImage(SolidPng(PlannerExportRenderer.MapWidth * 2, PlannerExportRenderer.MapHeight * 2, SKColors.SeaGreen), 2, ExportMapComposer.Credits)
            };
            Assert.Equal(MainViewModel.ExportWeatherRows, snapshot.Weather.Count);
            Assert.Equal((vm.TimelineStart, vm.TimelineEnd), (snapshot.WindowStart, snapshot.WindowEnd));

            var sheet = PlannerExportRenderer.Compose(snapshot);
            sheet.Measure(new Size(PlannerExportRenderer.Width, PlannerExportRenderer.Height));
            sheet.Arrange(new Rect(0, 0, PlannerExportRenderer.Width, PlannerExportRenderer.Height));
            Rect At(string name)
            {
                var control = sheet.GetLogicalDescendants().OfType<Control>().Single(item => item.Name == name);
                return new Rect(control.TranslatePoint(new Point(), sheet)!.Value, control.Bounds.Size);
            }
            var (header, map, weather, timeline, footer) = (At("ExportHeader"), At("ExportMapFrame"), At("ExportWeatherFrame"), At("ExportTimelineFrame"), At("ExportFooter"));
            Assert.True(header.Bottom <= map.Top && header.Bottom <= weather.Top);
            Assert.True(map.Right <= weather.Left && Math.Abs(map.Top - weather.Top) < 1, $"map {map}, weather {weather}");
            Assert.True(map.Width > 2 * weather.Width);
            Assert.True(timeline.Top >= map.Bottom && timeline.Top >= weather.Bottom);
            Assert.Equal(map.Left, timeline.Left, 1);
            Assert.Equal(weather.Right, timeline.Right, 1);
            Assert.Equal(PlannerExportRenderer.ChartHeight, timeline.Height, 1);
            Assert.True(footer.Top >= timeline.Bottom && footer.Bottom <= PlannerExportRenderer.Height);

            // The header names the target; the coordinates are on the map, not in the header.
            var headerText = sheet.GetLogicalDescendants().OfType<Control>().Single(item => item.Name == "ExportHeader")
                .GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
            Assert.Equal(snapshot.LocationName, headerText[0]);
            Assert.StartsWith(snapshot.Target.Name + " · ", headerText[1]);
            Assert.DoesNotContain(headerText, text => text!.Contains(snapshot.Coordinates));
            Assert.DoesNotContain("Standard Time", headerText[1]);

            var mapArea = sheet.GetLogicalDescendants().OfType<Control>().Single(item => item.Name == "ExportMap");
            Assert.DoesNotContain(mapArea.GetLogicalDescendants(), item => item is NoctaxisMapView or HorizonGraph or ExportDirectionChart);
            Assert.Contains(mapArea.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == snapshot.Coordinates);
            Assert.Contains(mapArea.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == ExportMapComposer.Credits);
            Assert.Single(sheet.GetLogicalDescendants().OfType<ExportDirectionChart>());
            Assert.DoesNotContain(sheet.GetLogicalDescendants(), item => item is HorizonGraph);
            Assert.Contains(sheet.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == snapshot.Weather[0].Clock);
            Assert.Contains("Open-Meteo", snapshot.Footer);

            // The same snapshot draws the same pixels, every time.
            var renderer = new PlannerExportRenderer();
            var first = renderer.RenderPng(snapshot);
            Assert.Equal(first, renderer.RenderPng(snapshot));
            using var decoded = SKBitmap.Decode(first);
            Assert.Equal((PlannerExportRenderer.Width, PlannerExportRenderer.Height), (decoded.Width, decoded.Height));
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The target and the terrain are told apart without colour: the target is a line (solid in view, dotted out of
    /// sight), the terrain a filled silhouette with a firm edge — in the theme's colours, and a colour-vision mode keeps
    /// the patterns and draws the dotted line heavier.
    /// </summary>
    [AvaloniaFact]
    public async Task TargetAndTerrainAreToldApartInEveryColourMode()
    {
        var exporter = new FakeExporter();
        var service = ((App)Application.Current!).Themes!;
        var (vm, window, _) = await CreatePlannerAsync(exporter: exporter);
        try
        {
            var normal = new ExportDirectionChart(await CapturedSheetAsync(vm, exporter));
            var colours = exporter.LastExport!.Theme;
            Assert.Null(normal.VisiblePitchPen.DashStyle);
            Assert.Same(ExportDirectionChart.HiddenPitchDash, normal.HiddenPitchPen.DashStyle);
            Assert.Null(normal.TerrainEdge.DashStyle);
            Assert.Equal(colours.Celestial[exporter.LastExport.Target.Role], ((Avalonia.Media.ISolidColorBrush)normal.VisiblePitchPen.Brush!).Color);
            Assert.Equal(colours.Celestial.TerrainFill, ((Avalonia.Media.ISolidColorBrush)normal.TerrainFill).Color);
            Assert.Equal(colours.Celestial.TerrainOutline, ((Avalonia.Media.ISolidColorBrush)normal.TerrainEdge.Brush!).Color);

            service.Apply(new(ColourVisionMode: ColourVisionMode.Deuteranopia));
            var snapshot = await CapturedSheetAsync(vm, exporter);
            Assert.True(snapshot.Theme.Celestial.EmphasisePatterns);
            var deuteranopia = new ExportDirectionChart(snapshot);
            Assert.Same(ExportDirectionChart.HiddenPitchDash, deuteranopia.HiddenPitchPen.DashStyle);
            Assert.True(deuteranopia.HiddenPitchPen.Thickness > normal.HiddenPitchPen.Thickness);
            Assert.NotEqual(colours.Celestial[snapshot.Target.Role], snapshot.Theme.Celestial[snapshot.Target.Role]);
        }
        finally { service.Apply(new()); Close(window); }
    }

    private sealed class EmptyTiles : IVectorTileFetcher
    {
        public int Requests;
        public Task<byte[]?> FetchAsync(int zoom, int column, int row, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult<byte[]?>([]);
        }
    }

    /// <summary>
    /// The clean map is composed from the basemap's own tiles around the location, at the panel's size and twice its
    /// pixels; when no tile can be had there is no map, and the sheet says so rather than drawing a blank.
    /// </summary>
    [Fact]
    public async Task TheCleanMapIsComposedFromTheBasemapAroundTheLocation()
    {
        var fetcher = new EmptyTiles();
        var map = await ExportMapComposer.ComposeAsync(new ThemedVectorTileSource(fetcher), new GeoCoordinate(52.59, -3.65),
            400, 300, CancellationToken.None);
        Assert.NotNull(map);
        Assert.True(fetcher.Requests is >= 4 and <= 9, $"{fetcher.Requests} tiles");
        using var image = SKBitmap.Decode(map!.Png);
        Assert.Equal((800, 600), (image.Width, image.Height));
        Assert.Equal(ExportMapComposer.Credits, map.Attribution);
        Assert.Contains("OpenStreetMap", map.Attribution);

        Assert.Null(await ExportMapComposer.ComposeAsync(new ThemedVectorTileSource(new NoTiles()), new GeoCoordinate(0, 0), 200, 100, CancellationToken.None));
    }

    private sealed class NoTiles : IVectorTileFetcher
    {
        public Task<byte[]?> FetchAsync(int zoom, int column, int row, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
    }

    /// <summary>Import and Export sit together in the bar, each a compact menu with named items.</summary>
    [AvaloniaFact]
    public async Task ImportIsOneCompactMenuBesideExport()
    {
        var (_, window, _) = await CreatePlannerAsync();
        try
        {
            var import = window.FindControl<Button>("PlannerImportMenu")!;
            Assert.Equal("Import", Avalonia.Automation.AutomationProperties.GetName(import));
            var menu = Assert.IsType<MenuFlyout>(import.Flyout);
            Assert.Equal(["Open plan image…", "Load from clipboard"], menu.Items.OfType<MenuItem>().Select(item => item.Header as string));
            Assert.Equal(Grid.GetColumn(window.FindControl<Button>("PlannerExportMenu")!) + 1, Grid.GetColumn(import));
        }
        finally { Close(window); }
    }
}
