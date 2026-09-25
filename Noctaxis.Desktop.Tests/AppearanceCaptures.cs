using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [AvaloniaFact]
    public async Task AppearanceRepresentativeCaptures()
    {
        var output = Environment.GetEnvironmentVariable("NOCTAXIS_APPEARANCE_CAPTURES");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var catalogue = new OpenNgcTargetCatalogue();
        var location = new SavedLocation(Guid.NewGuid(), "North Lincolnshire scouting location", new(53.6, -0.4), "UTC",
            Notes: "A saved location with a longer description to exercise larger text.");
        var store = new FakeStore(new(1, new AppSettings(), [location],
            PlanningSession.Default(Instant.FromUtc(2026, 9, 12, 20, 0), "UTC"), null));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        var theme = ((App)Application.Current!).Themes!;
        vm.AttachThemeService(theme);
        var window = new MainWindow { Width = 1440, Height = 1000, DataContext = vm };
        window.Show();
        try
        {
            var tabs = (TabControl)((Grid)window.Content!).Children[1];
            foreach (var (id, mode, scale) in new (string, ColourVisionMode, double)[]
            {
                ("dark", ColourVisionMode.None, 1), ("light", ColourVisionMode.None, 1),
                ("highcontrast", ColourVisionMode.None, 1), ("dark", ColourVisionMode.Protanopia, 1),
                ("dark", ColourVisionMode.Deuteranopia, 1), ("dark", ColourVisionMode.Tritanopia, 1),
                ("dark", ColourVisionMode.None, 1.2), ("dark", ColourVisionMode.None, 1.5), ("dark", ColourVisionMode.None, 2)
            })
            {
                await vm.ApplyAppearanceAsync(new("builtin." + id, mode, scale));
                for (var page = 0; page < 3; page++)
                {
                    tabs.SelectedIndex = page;
                    Dispatcher.UIThread.RunJobs();
                    using var capture = new RenderTargetBitmap(new PixelSize(1440, 1000));
                    capture.Render(window);
                    capture.Save(Path.Combine(output, $"{id}-{mode}-{scale * 100:0}-page-{page}.png"), PngBitmapEncoderOptions.Default);
                }
                if (scale == 2)
                {
                    var settings = (TabControl)((Grid)((TabItem)tabs.Items[2]!).Content!).Children[0];
                    for (var section = 0; section < settings.ItemCount; section++)
                    {
                        settings.SelectedIndex = section; Dispatcher.UIThread.RunJobs();
                        using var capture = new RenderTargetBitmap(new PixelSize(1440, 1000)); capture.Render(window);
                        capture.Save(Path.Combine(output, $"dark-200-settings-{section}.png"), PngBitmapEncoderOptions.Default);
                    }
                }
            }
        }
        finally { window.Close(); theme.Apply(new()); }
    }
}

