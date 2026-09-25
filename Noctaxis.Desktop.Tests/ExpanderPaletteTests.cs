using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Tests;

public sealed class ExpanderPaletteTests
{
    public static IEnumerable<object[]> Themes() => BuiltInPalettes.Definitions.SelectMany(theme =>
        new[] { AppearanceMode.Dark, AppearanceMode.Light }.SelectMany(appearance =>
            Enum.GetValues<ColourVisionMode>().Select(vision => new object[] { theme.Id, appearance, vision })));

    [AvaloniaTheory] [MemberData(nameof(Themes))]
    public void HeadersKeepThemedFillBorderAndReadableContent(string id, AppearanceMode appearance, ColourVisionMode vision)
    {
        using var service = new ThemeService(Application.Current!, new ThemeCatalogue(isAvailable: _ => true));
        service.Apply(new(id, vision, AppearanceMode: appearance));
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = $"{id} / {appearance}" });
        panel.Children.Add(new Slider { Minimum = 0, Maximum = 100, Value = 65 });
        var states = new[] { "Collapsed", "Collapsed hover", "Collapsed pressed", "Expanded", "Expanded hover", "Expanded pressed", "Disabled", "Expanded disabled" };
        var expanders = states.Select(state => new Expander { Header = state, IsExpanded = state.StartsWith("Expanded"),
            IsEnabled = !state.Contains("disabled", StringComparison.OrdinalIgnoreCase), Content = new TextBlock { Text = "Section content" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch }).ToArray();
        foreach (var expander in expanders) panel.Children.Add(expander);
        var window = new Window { Width = 680, Height = 950, Content = panel };
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            for (var i = 0; i < expanders.Length; i++)
            {
                var header = expanders[i].GetVisualDescendants().OfType<ToggleButton>().Single();
                ((IPseudoClasses)header.Classes).Set(":pointerover", states[i].Contains("hover") || states[i].Contains("pressed"));
                ((IPseudoClasses)header.Classes).Set(":pressed", states[i].Contains("pressed"));
            }
            Dispatcher.UIThread.RunJobs();
            var fills = new List<Color>();
            for (var i = 0; i < expanders.Length; i++)
            {
                var header = expanders[i].GetVisualDescendants().OfType<ToggleButton>().Single();
                var border = header.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ToggleButtonBackground");
                var fill = ((ISolidColorBrush)border.Background!).Color; fills.Add(fill);
                var stroke = ((ISolidColorBrush)border.BorderBrush!).Color;
                var text = header.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");
                var chevron = header.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
                Assert.Equal(Color.Parse(service.EffectiveTheme.Palette[i < 6 ? "Accent" : "SubtleBorder"]), stroke);
                if (i is 1 or 2 or 4 or 5) Assert.NotEqual(Colors.Black, fill);
                if (i < 6)
                {
                    Assert.True(Contrast(fill, ((ISolidColorBrush)text.Foreground!).Color) >= 4.5, $"Unreadable {id}/{appearance}/{states[i]} text");
                    Assert.True(Contrast(fill, ((ISolidColorBrush)chevron.Stroke!).Color) >= 3, $"Unreadable {id}/{appearance}/{states[i]} chevron");
                }
                Assert.True(header.Bounds.Width > 0 && header.Bounds.Height > 0);
            }
            Assert.NotEqual(fills[0], fills[1]); Assert.NotEqual(fills[1], fills[2]);
            Assert.NotEqual(fills[3], fills[4]); Assert.NotEqual(fills[4], fills[5]);
            Assert.NotEqual(fills[1], fills[4]);
            if (vision == ColourVisionMode.None) AppearancePolishTests.Capture(window, $"expander-{id}-{appearance}");
        }
        finally { window.Close(); }
    }

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Linear(byte value) { var x = value / 255d; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        var x = L(a); var y = L(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }

    [Theory] [InlineData(AppearanceMode.Dark)] [InlineData(AppearanceMode.Light)]
    public void NeonUsesCyanAndFuchsiaInPersistentControlRoles(AppearanceMode appearance)
    {
        var palette = new ThemeCatalogue(isAvailable: _ => true).Resolve(new("supporter.noctaxis-neon", AppearanceMode: appearance)).Palette;
        var primary = Color.Parse(palette["Accent"]); var secondary = Color.Parse(palette["SliderFill"]);
        Assert.True(primary.G > primary.R && primary.B > primary.R);
        Assert.True(secondary.R > secondary.G * 2 && secondary.B > secondary.G * 2);
        Assert.NotEqual(palette["ButtonBackground"], palette["ButtonHover"]);
        Assert.True(Contrast(Color.Parse(palette["ButtonHover"]), Color.Parse(palette["PrimaryText"])) >= 4.5);
    }

    [Fact]
    public void FoxfireDarkRoomIsNeutralWhileAccentsRemainOrange()
    {
        var p = new ThemeCatalogue(isAvailable: _ => true).Resolve(new("supporter.foxfire", AppearanceMode: AppearanceMode.Dark)).Palette;
        foreach (var role in new[] { "WindowBackground", "ApplicationBackground", "SurfaceBackground", "ElevatedSurface", "InputBackground", "CardBackground", "ButtonBackground", "MapChromeBackground" })
        {
            var c = Color.Parse(p[role]); var channels = new[] { c.R, c.G, c.B };
            Assert.True(channels.Max() - channels.Min() <= 8, $"Tinted structural surface: {role}");
            Assert.True(channels.Max() < 50, $"Bright structural surface: {role}");
        }
        var accent = Color.Parse(p["Accent"]);
        Assert.True(accent.R > accent.G && accent.G > accent.B);
        Assert.Equal(p["Accent"], p["SliderFill"]);
    }
}
