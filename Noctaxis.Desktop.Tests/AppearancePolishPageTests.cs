using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    private sealed class AppearanceMapFixture : Control
    {
        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Brushes.DarkSlateGray, new Rect(Bounds.Size));
            for (var x = 0; x < Bounds.Width; x += 30)
                context.DrawLine(new Pen(Brushes.DarkSeaGreen, 8), new Point(x, 0), new Point(x + 100, Bounds.Height));
            context.DrawLine(new Pen(Brushes.SteelBlue, 22), new Point(0, 130), new Point(Bounds.Width, 40));
        }
    }
    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", AppearanceMode.Dark)] [InlineData("builtin.noctaxis", AppearanceMode.Light)] [InlineData("builtin.highcontrast", AppearanceMode.Dark)] [InlineData("builtin.highcontrast", AppearanceMode.Light)]
    public async Task AppearancePolishPopulatedPagesAndLiveDialogs(string id, AppearanceMode appearance)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var location = new SavedLocation(Guid.NewGuid(), "Scouting location", new(53.6, -.4), "UTC", Notes: "Synthetic image fixture for card interaction validation.");
        var store = new FakeStore(new(1, new AppSettings(CameraFraming: new(TerrainObstructionColour: "#123456")), [location],
            PlanningSession.Default(Instant.FromUtc(2026, 9, 12, 20, 0), "UTC"), null));
        var vm = CreateViewModel(planning, catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        var service = ((App)Application.Current!).Themes!;
        vm.AttachThemeService(service);
        var imageSource = new AppearanceMapFixture(); imageSource.Measure(new Size(640, 300)); imageSource.Arrange(new Rect(0, 0, 640, 300));
        var bitmap = new RenderTargetBitmap(new PixelSize(640, 300)); bitmap.Render(imageSource);
        vm.Locations.Saved[0].MapThumbnail = bitmap;
        var window = new MainWindow { Width = 1440, Height = 1000, DataContext = vm };
        window.Show();
        try
        {
            await vm.ApplyAppearanceAsync(new(id, AppearanceMode: appearance)); Dispatcher.UIThread.RunJobs();
            var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("editorialLocationCard"));
            foreach (var state in new[] { "normal", "favourite", "selected", "selected-favourite" })
            {
                vm.Locations.Saved[0].IsSelected = state.Contains("selected");
                vm.Locations.Saved[0].IsFavourite = state.Contains("favourite");
                window.MouseMove(new Point(2, 2)); Dispatcher.UIThread.RunJobs();
                AppearancePolishTests.Capture(window, $"locations-{id}-{appearance}-{state}");
                window.MouseMove(card.TranslatePoint(new Point(400, 110), window)!.Value); Dispatcher.UIThread.RunJobs();
                AppearancePolishTests.Capture(window, $"locations-{id}-{appearance}-{state}-hover");
            }
            window.MouseMove(new Point(2, 2));
            var pages = (TabControl)((Grid)window.Content!).Children[1]; pages.SelectedIndex = 2;
            var settings = (TabControl)((Grid)((TabItem)pages.Items[2]!).Content!).Children[0];
            var calls = (planning.SnapshotCalculations, planning.EnvironmentRequests, planning.ForcedRefreshes);
            var session = vm.Session;
            foreach (var section in new[] { 0, 1, 4, 6 })
            {
                settings.SelectedIndex = section; Dispatcher.UIThread.RunJobs();
                foreach (var scale in new[] { 1d, 1.1, 1.2, 1.5, 1.75, 2, 1 })
                {
                    await vm.ApplyAppearanceAsync(new(id, TextScale: scale, AppearanceMode: appearance)); Dispatcher.UIThread.RunJobs();
                    AppearancePolishTests.Capture(window, $"settings-{id}-{appearance}-section-{section}-live-{scale * 100:0}");
                    Assert.Same(session, vm.Session);
                    Assert.Equal(calls, (planning.SnapshotCalculations, planning.EnvironmentRequests, planning.ForcedRefreshes));
                    Assert.Equal("#123456", vm.Settings.EffectiveCameraFraming.TerrainObstructionColour);
                }
            }
            foreach (var dialog in new Window[] { new SavedLocationEditDialog(location), new ConfirmationDialog("Remove location", "The saved location will be removed. This is a layout test.", "Remove"), new LocationSearchDialog() })
            {
                dialog.Show();
                foreach (var scale in new[] { 1d, 1.5, 2, 1 })
                {
                    await vm.ApplyAppearanceAsync(new(id, TextScale: scale, AppearanceMode: appearance)); Dispatcher.UIThread.RunJobs();
                    AppearancePolishTests.Capture(dialog, $"dialog-{id}-{appearance}-{dialog.GetType().Name}-live-{scale * 100:0}");
                }
                dialog.Close();
            }
        }
        finally { window.Close(); bitmap.Dispose(); service.Apply(new()); }
    }
}
