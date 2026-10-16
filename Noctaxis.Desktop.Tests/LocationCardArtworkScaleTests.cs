using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Themes;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// A saved location's artwork at every text size. It used to vanish completely above 120%, because
/// every stop of the card's scrim was forced opaque once text was enlarged, which painted the card
/// surface across the whole card. Enlarging text may squeeze the artwork; it may never hide it.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static readonly double[] CardArtworkScales = [1, 1.1, 1.2, 1.3, 1.5, 1.75, 2];

    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", AppearanceMode.Dark)]
    [InlineData("builtin.noctaxis", AppearanceMode.Light)]
    [InlineData("builtin.highcontrast", AppearanceMode.Light)]
    public async Task CardArtworkSurvivesEveryTextSize(string id, AppearanceMode appearance)
    {
        var (vm, window, card) = await CreateArtworkCardAsync();
        var service = ((App)Application.Current!).Themes!;
        try
        {
            var preview = card.GetVisualDescendants().OfType<LocationPreviewImage>().Single();
            var text = card.GetVisualDescendants().OfType<LocationCardTextGrid>().Single();
            var scrim = card.GetVisualDescendants().OfType<Border>()
                .Select(border => border.Background as LinearGradientBrush).First(brush => brush is not null)!;
            var artworkWidths = new List<double>();

            foreach (var scale in CardArtworkScales)
            {
                await vm.ApplyAppearanceAsync(new(id, TextScale: scale, AppearanceMode: appearance));
                Dispatcher.UIThread.RunJobs();
                var where = $"{id}/{appearance} at {scale * 100:0}%";

                // The preview is still there, still laid out, and still has room to be seen in.
                Assert.True(preview.IsEffectivelyVisible, $"{where}: the preview is not visible");
                Assert.True(preview.Bounds.Width > 0 && preview.Bounds.Height > 0,
                    $"{where}: the preview is {preview.Bounds.Width}x{preview.Bounds.Height}");
                Assert.Equal(1, preview.Opacity);

                // The scrim never covers the whole card: it still reaches nothing by its last moving stop,
                // so a strip of artwork is left uncovered however large the text is.
                var stops = scrim.GradientStops.OrderBy(stop => stop.Offset).ToArray();
                Assert.Equal(0, stops[^1].Color.A);
                var clear = stops.First(stop => stop.Color.A == 0).Offset;
                Assert.True(clear <= 0.9, $"{where}: the scrim does not clear until {clear:F2}");
                Assert.True(stops.Count(stop => stop.Color.A is > 0 and < 255) >= 3,
                    $"{where}: the fade lost its intermediate stops");
                artworkWidths.Add((1 - clear) * card.Bounds.Width);

                // The details take more of the card but never all of it, so a column is left for the map.
                Assert.Equal(2, text.ColumnDefinitions.Count);
                Assert.True(text.ColumnDefinitions[1].Width.Value > 0,
                    $"{where}: the preview's column collapsed to {text.ColumnDefinitions[1].Width}");

                // The text still sits on a solid backing: that is what the scrim is for.
                var textRight = text.GetVisualDescendants().OfType<TextBlock>()
                    .Where(block => block.IsEffectivelyVisible && !string.IsNullOrEmpty(block.Text))
                    .Max(block => block.TranslatePoint(new Point(block.Bounds.Width, 0), card)!.Value.X) / card.Bounds.Width;
                Assert.True(AlphaAt(stops, textRight) >= 168,
                    $"{where}: text reaches {textRight:F2}, where the backing is only {AlphaAt(stops, textRight)}");
            }

            // No cliff anywhere, and in particular not across the 120% to 130% boundary this regressed on.
            var atHundredAndTwenty = artworkWidths[CardArtworkScales.AsSpan().IndexOf(1.2)];
            var atHundredAndThirty = artworkWidths[CardArtworkScales.AsSpan().IndexOf(1.3)];
            Assert.True(atHundredAndThirty > atHundredAndTwenty * 0.75,
                $"{id}/{appearance}: artwork fell from {atHundredAndTwenty:F0}px at 120% to {atHundredAndThirty:F0}px at 130%");
            Assert.All(artworkWidths, strip => Assert.True(strip >= 40, $"only {strip:F0}px of artwork is left uncovered"));
        }
        finally { window.Close(); service.Apply(new()); }
    }

    /// <summary>
    /// The alphas belong to the theme and the offsets to the text size: the two are independent, and
    /// neither ever produces a scrim that covers the card.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1, 0.30, 0.74)]
    [InlineData(1.2, 0.334, 0.774)]
    [InlineData(1.3, 0.351, 0.791)]
    [InlineData(1.5, 0.385, 0.825)]
    [InlineData(2, 0.47, 0.88)]
    public void TheScrimMovesWithTheTextSizeAndItsColoursDoNot(double scale, double solid, double clear)
    {
        var service = ((App)Application.Current!).Themes!;
        try
        {
            service.Apply(new(ThemeCatalogue.DefaultId, TextScale: scale));
            double Number(string key) => (double)Application.Current!.FindResource(key)!;
            byte Alpha(string key) => ((Color)Application.Current!.FindResource(key)!).A;

            // Enlarged text never makes the transparent stop opaque, which is what hid the artwork.
            Assert.Equal<byte[]>([255, 242, 168, 70, 0],
                [Alpha("CardOpaque"), Alpha("CardScrim"), Alpha("CardFade"), Alpha("CardSoft"), Alpha("CardTransparent")]);

            Assert.Equal(solid, Number("CardStopSolid"), 3);
            Assert.Equal(clear, Number("CardStopClear"), 3);
            // The stops stay in order and the fade keeps its shape at every size.
            var offsets = new[] { "CardStopSolid", "CardStopScrim", "CardStopFade", "CardStopSoft", "CardStopClear" }.Select(Number).ToArray();
            Assert.Equal(offsets.Order(), offsets);
            Assert.True(offsets[^1] <= 0.9, $"the fade ends at {offsets[^1]:F2}");
            // The details grow with the text but always leave the map a column.
            var share = Number("LocationCardTextShare");
            Assert.InRange(share, 0.5, 0.67);
            Assert.True(Number("CardStopSolid") < share, "the solid backing stops before the text does");
        }
        finally { service.Apply(new()); }
    }

    private async Task<(ViewModels.MainViewModel ViewModel, MainWindow Window, Border Card)> CreateArtworkCardAsync()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var location = new SavedLocation(Guid.NewGuid(), "Castlethorpe Bridge", new(53.5664, -.5063), "Europe/London",
            Notes: "Dark-sky field by the reservoir, reached from the lane.");
        var store = new FakeStore(new(1, new AppSettings(), [location],
            PlanningSession.Default(Instant.FromUtc(2026, 9, 12, 20, 0), "UTC"), null));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        vm.AttachThemeService(((App)Application.Current!).Themes!);
        // A real preview, so the artwork is something that can be covered up.
        var fixture = new AppearanceMapFixture();
        fixture.Measure(new Size(640, 300));
        fixture.Arrange(new Rect(0, 0, 640, 300));
        var bitmap = new RenderTargetBitmap(new PixelSize(640, 300));
        bitmap.Render(fixture);
        vm.Locations.Saved[0].PreviewScene = LocationPreviewRendererTests.Scene();
        var window = new MainWindow { Width = 1440, Height = 1000, DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("editorialLocationCard"));
        return (vm, window, card);
    }
}
