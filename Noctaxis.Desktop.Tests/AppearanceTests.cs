using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

public sealed class AppearanceTests
{
    public static IEnumerable<object[]> Variants => from t in BuiltInPalettes.Definitions
        from a in new[] { AppearanceMode.Dark, AppearanceMode.Light }
        from m in Enum.GetValues<ColourVisionMode>() select new object[] { t.Id, m, a };
    [Theory, MemberData(nameof(Variants))]
    public void BuiltInsAreCompleteAndAlwaysAvailable(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var catalogue = new ThemeCatalogue(isAvailable: _ => true);
        var resolved = catalogue.Resolve(new(id, mode, AppearanceMode: appearance));
        Assert.Equal(id, resolved.Definition.Id);
        Assert.Equal(mode, resolved.Mode);
        Assert.Null(resolved.FallbackReason);
        Assert.Empty(ThemeDefinitionValidator.Validate(resolved.Definition));
        Assert.All(BuiltInPalettes.RequiredTokens, t => Assert.True(resolved.Palette.ContainsKey(t)));
    }
    [Theory]
    [InlineData(true, AppearanceMode.Dark)]
    [InlineData(false, AppearanceMode.Light)]
    [InlineData(null, AppearanceMode.Dark)]
    public void SystemFollowsPlatformWithDarkFallback(bool? dark, AppearanceMode expected) =>
        Assert.Equal(expected, new ThemeCatalogue().Resolve(new(AppearanceMode: AppearanceMode.System), dark).AppearanceMode);

    [Fact]
    public void UnknownUnavailableAndIncompleteThemesFailSafely()
    {
        var extra = new ThemeDefinition("supporter.sample", "Test only", true,
            new ThemePaletteDefinition(new Dictionary<string, string> { ["Accent"] = "#AABBCC" }, new Dictionary<ColourVisionMode, IReadOnlyDictionary<string, string>>()), null);
        var denied = new ThemeCatalogue([extra]);
        Assert.Equal("builtin.noctaxis", denied.Resolve(new(extra.Id)).Definition.Id);
        Assert.Equal("builtin.noctaxis", denied.Resolve(new("unknown")).Definition.Id);
        var allowed = new ThemeCatalogue([extra], _ => true).Resolve(new(extra.Id, ColourVisionMode.Tritanopia));
        Assert.Equal(ColourVisionMode.None, allowed.Mode);
        Assert.NotNull(allowed.FallbackReason);
        Assert.All(BuiltInPalettes.RequiredTokens, t => Assert.True(allowed.Palette.ContainsKey(t)));
        Assert.NotEmpty(ThemeDefinitionValidator.Validate(extra));
    }
    [Theory]
    [InlineData(-1, 1)] [InlineData(1.2, 1.2)] [InlineData(1.5, 1.5)] [InlineData(2, 2)] [InlineData(7, 2)]
    [InlineData(double.NaN, 1)] [InlineData(double.PositiveInfinity, 1)]
    public void TextScaleIsBounded(double input, double expected) =>
        Assert.Equal(expected, new AppearancePreferences(TextScale: input).Normalised().TextScale);

    private static double Contrast(string foreground, string background)
    {
        static double L(string hex)
        {
            var c = Color.Parse(hex);
            static double Linear(byte b) { var v = b / 255d; return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        var a = L(foreground); var b = L(background);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    [Theory, MemberData(nameof(Variants))]
    public void OrdinaryTextStatusAndFocusMeetContrastTargets(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var p = new ThemeCatalogue().Resolve(new(id, mode, AppearanceMode: appearance)).Palette;
        foreach (var foreground in new[] { "PrimaryText", "SecondaryText", "MutedText", "DisabledText" })
            foreach (var background in new[] { "WindowBackground", "SurfaceBackground", "InputBackground", "ButtonBackground" })
                Assert.True(Contrast(p[foreground], p[background]) >= 4.5, $"{id}/{mode}: {foreground}/{background}");
        foreach (var role in new[] { "Success", "Warning", "Error", "Information" })
            Assert.True(Contrast(p[role], p[role + "Background"]) >= 4.5, $"{id}/{mode}: {role}");
        Assert.True(Contrast(p["AccentForeground"], p["Accent"]) >= 4.5);
        Assert.True(Contrast(p["SelectionForeground"], p["SelectionBackground"]) >= 4.5);
        Assert.True(Contrast(p["MapChromeText"], p["MapChromeBackground"]) >= 4.5);
        Assert.True(Contrast(p["FocusBorder"], p["SurfaceBackground"]) >= 3);
    }

    [AvaloniaFact]
    public void RuntimeBindingsAndDictionaryCountStayStable()
    {
        var app = (App)Application.Current!;
        var themes = app.Themes!;
        var count = app.Resources.MergedDictionaries.Count;
        var text = new TextBlock { Text = "Runtime" };
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("PrimaryText"));
        text.Bind(TextBlock.FontSizeProperty, new DynamicResourceExtension("TextBody"));
        var window = new Window { Content = text };
        window.Show();
        try
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var allocated = GC.GetTotalAllocatedBytes(true);
            var retained = GC.GetTotalMemory(true);
            for (var i = 0; i < 120; i++)
            {
                var id = i % 2 == 0 ? "builtin.light" : "builtin.highcontrast";
                themes.Apply(new(id, (ColourVisionMode)(i % 4), i % 2 == 0 ? 1.5 : 2));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(count, app.Resources.MergedDictionaries.Count);
                Assert.Equal(i % 2 == 0 ? 21d : 28d, text.FontSize);
                Assert.Equal(Color.Parse(themes.EffectiveTheme.Palette["PrimaryText"]), ((ISolidColorBrush)text.Foreground!).Color);
            }
            timer.Stop();
            if (Environment.GetEnvironmentVariable("NOCTAXIS_APPEARANCE_CAPTURES") is { } output)
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "switch-stress.json"), System.Text.Json.JsonSerializer.Serialize(new
                { Switches = 120, timer.Elapsed.TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
                  RetainedBytes = GC.GetTotalMemory(true) - retained, DictionaryCount = count, themes.ResourceCount }));
            }
        }
        finally { window.Close(); themes.Apply(new()); }
    }

    [AvaloniaTheory]
    [InlineData(1)] [InlineData(1.2)] [InlineData(1.5)] [InlineData(2)]
    public void PagesAndDialogsConstructAtEachTextSize(double scale)
    {
        var themes = ((App)Application.Current!).Themes!;
        themes.Apply(new(TextScale: scale));
        var window = new MainWindow();
        try
        {
            window.Show();
            var tabs = (TabControl)((Grid)window.Content!).Children[1];
            for (var page = 0; page < tabs.ItemCount; page++)
            {
                tabs.SelectedIndex = page;
                Dispatcher.UIThread.RunJobs();
                Assert.True(window.Bounds.Width > 0);
            }
            foreach (var dialog in new Window[] { new SavedLocationEditDialog(), new LocationSearchDialog() })
            {
                dialog.Show(); Dispatcher.UIThread.RunJobs(); Assert.True(dialog.Bounds.Height > 0); dialog.Close();
            }
        }
        finally { window.Close(); themes.Apply(new()); }
    }
}
