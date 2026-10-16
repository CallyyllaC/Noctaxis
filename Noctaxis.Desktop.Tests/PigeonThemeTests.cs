using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Pigeon, the supporter family dedicated to Noctaxis's first supporter (docs/supporter-themes.md): rock-pigeon
/// slate and silver with restrained teal and violet. Contrast, colour vision, map and matrix coverage come from
/// the suites every family runs through; these tests pin what is particular to Pigeon.
/// </summary>
public sealed partial class MainViewModelTests
{
    private const string Pigeon = "supporter.pigeon";
    private static ThemeDefinition PigeonFamily => BuiltInPalettes.Definitions.Single(theme => theme.Id == Pigeon);

    [Fact]
    public void PigeonIsASupporterFamilyDedicatedToTheFirstSupporter()
    {
        var family = PigeonFamily;
        Assert.True(family.IsSupporter);
        Assert.Equal("Pigeon", family.DisplayName);
        Assert.Contains("first supporter", family.Description);
        Assert.Equal("Pigeon", Noctaxis.Desktop.ViewModels.AppearanceOption.ThemeName(Pigeon, family.DisplayName));
        Assert.Empty(ThemeDefinitionValidator.Validate(family));
    }

    /// <summary>Without the entitlement Pigeon is listed but locked, and asking for it gives Noctaxis; with it, Pigeon.</summary>
    [Fact]
    public void PigeonIsLockedWithoutTheSupporterEntitlement()
    {
        var locked = new ThemeCatalogue();
        Assert.False(locked.IsAvailable(PigeonFamily));
        Assert.Contains(locked.Themes, theme => theme.Id == Pigeon);
        var resolved = locked.Resolve(new(Pigeon, AppearanceMode: AppearanceMode.Dark));
        Assert.Equal(ThemeCatalogue.DefaultId, resolved.Definition.Id);
        Assert.NotNull(resolved.FallbackReason);
        var option = new Noctaxis.Desktop.ViewModels.AppearanceOption(Pigeon, "Pigeon", true, locked.IsAvailable(PigeonFamily));
        Assert.Equal("Locked · Supporter", option.AvailabilityLabel);

        // The one shared entitlement unlocks it, as it does every supporter family.
        var unlocked = new ThemeCatalogue(isAvailable: _ => true);
        Assert.Equal(Pigeon, unlocked.Resolve(new(Pigeon, AppearanceMode: AppearanceMode.Light)).Definition.Id);
    }

    /// <summary>
    /// Slate and silver carry the surfaces; the neck's teal is the accent and its violet sheen marks focus and
    /// selection. Neither hue becomes a background, and Dark and Light are authored separately.
    /// </summary>
    [Theory]
    [InlineData(AppearanceMode.Dark)]
    [InlineData(AppearanceMode.Light)]
    public void PigeonKeepsGreenAndPurpleAsRestrainedAccents(AppearanceMode mode)
    {
        var p = new ThemeCatalogue(isAvailable: _ => true).Resolve(new(Pigeon, AppearanceMode: mode)).Palette;
        HslColor Hsl(string token) => Color.Parse(p[token]).ToHsl();
        foreach (var surface in new[] { "WindowBackground", "ApplicationBackground", "SurfaceBackground", "ElevatedSurface",
                     "InputBackground", "CardBackground", "ButtonBackground", "ButtonHover", "ButtonPressed", MapThemeTokens.Land })
            Assert.True(Hsl(surface).S < .2, $"{mode} {surface} should be a slate or silver neutral, not a hue ({Hsl(surface).S:F2})");
        Assert.InRange(Hsl("Accent").H, 150, 185);       // Emerald-teal.
        Assert.InRange(Hsl("FocusBorder").H, 255, 290);  // Violet.
        Assert.InRange(Hsl("SelectionBackground").H, 250, 290);
        Assert.InRange(Hsl(MapThemeTokens.Bearing).H, 20, 40); // The pigeon's orange eye.
        var other = mode == AppearanceMode.Dark ? PigeonFamily.Light! : PigeonFamily.Dark;
        Assert.NotEqual(other.Palette["Accent"], p["Accent"]);
        Assert.NotEqual(other.Palette["SurfaceBackground"], p["SurfaceBackground"]);
    }

    /// <summary>Normal, hovered, pressed, selected, disabled and focused each look different, in every variant.</summary>
    [Theory]
    [InlineData(AppearanceMode.Dark, ColourVisionMode.None)]
    [InlineData(AppearanceMode.Dark, ColourVisionMode.Protanopia)]
    [InlineData(AppearanceMode.Dark, ColourVisionMode.Deuteranopia)]
    [InlineData(AppearanceMode.Dark, ColourVisionMode.Tritanopia)]
    [InlineData(AppearanceMode.Light, ColourVisionMode.None)]
    [InlineData(AppearanceMode.Light, ColourVisionMode.Protanopia)]
    [InlineData(AppearanceMode.Light, ColourVisionMode.Deuteranopia)]
    [InlineData(AppearanceMode.Light, ColourVisionMode.Tritanopia)]
    public void PigeonsInteractionStatesStayDistinct(AppearanceMode mode, ColourVisionMode vision)
    {
        var p = new ThemeCatalogue(isAvailable: _ => true).Resolve(new(Pigeon, vision, AppearanceMode: mode)).Palette;
        Color C(string token) => Color.Parse(p[token]);
        var states = new[] { "ButtonBackground", "ButtonHover", "ButtonPressed", "SelectionBackground" };
        Assert.Equal(states.Length, states.Select(state => p[state]).Distinct().Count());
        Assert.NotEqual(p["ButtonDisabled"], p["ButtonBackground"]);
        Assert.True(ColourScience.Contrast(C("FocusBorder"), C("SurfaceBackground")) >= 3, $"{mode}/{vision}: focus ring");
        Assert.True(ColourScience.Contrast(C("SelectionForeground"), C("SelectionBackground")) >= 4.5, $"{mode}/{vision}: selected text");
        Assert.True(ColourScience.Contrast(C("AccentForeground"), C("Accent")) >= 4.5, $"{mode}/{vision}: text on the accent");
        Assert.True(ColourScience.Contrast(C("DisabledText"), C("SurfaceBackground")) >= 3, $"{mode}/{vision}: disabled text stays legible");
        Assert.True(ColourScience.Contrast(C("PrimaryText"), C("DisabledText")) > 1.2, $"{mode}/{vision}: disabled text looks disabled");
    }

    /// <summary>
    /// Visual comparison captures of the Planner (map, Camera frame and overlays), Locations and Settings in
    /// Pigeon: Dark and Light, every colour-vision variant at 100%, and both modes at 150% and 200% text. Opt-in,
    /// like the other appearance captures: set NOCTAXIS_PIGEON_CAPTURES to a folder.
    /// </summary>
    [AvaloniaFact]
    public async Task PigeonRepresentativeCaptures()
    {
        var output = Environment.GetEnvironmentVariable("NOCTAXIS_PIGEON_CAPTURES");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var catalogue = new OpenNgcTargetCatalogue();
        var location = new SavedLocation(Guid.NewGuid(), "North Lincolnshire scouting location", new(53.6, -0.4), "UTC",
            Notes: "A saved location with a longer description to exercise larger text.");
        var store = new FakeStore(new(1, new AppSettings(), [location],
            PlanningSession.Default(Instant.FromUtc(2026, 9, 12, 20, 0), "UTC"), null));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        var app = Application.Current!;
        // Unlocked as the supporter captures are: by an injected availability, never by a licence.
        using var themes = new ThemeService(app, new ThemeCatalogue(isAvailable: _ => true));
        vm.AttachThemeService(themes);
        var window = new MainWindow { Width = 1440, Height = 1000, DataContext = vm };
        window.Show();
        try
        {
            var tabs = (TabControl)((Grid)window.Content!).Children[1];
            var runs = new List<(AppearanceMode Mode, ColourVisionMode Vision, double Scale)>();
            foreach (var mode in new[] { AppearanceMode.Dark, AppearanceMode.Light })
            {
                runs.AddRange(Enum.GetValues<ColourVisionMode>().Select(vision => (mode, vision, 1d)));
                runs.Add((mode, ColourVisionMode.None, 1.5));
                runs.Add((mode, ColourVisionMode.None, 2));
            }
            foreach (var (mode, vision, scale) in runs)
            {
                await vm.ApplyAppearanceAsync(new(Pigeon, vision, scale, mode));
                Assert.Equal(Pigeon, themes.EffectiveTheme.Definition.Id);
                for (var page = 0; page < 3; page++)
                {
                    tabs.SelectedIndex = page;
                    Dispatcher.UIThread.RunJobs();
                    using var capture = new RenderTargetBitmap(new PixelSize(1440, 1000));
                    capture.Render(window);
                    capture.Save(Path.Combine(output, $"pigeon-{mode}-{vision}-{scale * 100:0}-page-{page}.png"), PngBitmapEncoderOptions.Default);
                }
            }
        }
        finally { window.Close(); ((App)app).Themes!.Apply(new()); }
    }
}
