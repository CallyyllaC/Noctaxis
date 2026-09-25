using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Input;
using Avalonia.Threading;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;
using Noctaxis.Desktop.Views;
namespace Noctaxis.Desktop.Tests;

public sealed class AppearanceFamilyTests
{
    private sealed class Platform : IAppearancePlatform
    {
        public bool Dark { get; set; }
        public bool? IsDark => Dark;
        private EventHandler? _changed;
        public int Subscribers { get; private set; }
        public event EventHandler? Changed { add { _changed += value; Subscribers++; } remove { _changed -= value; Subscribers--; } }
        public void Change(bool dark) { Dark = dark; _changed?.Invoke(this, EventArgs.Empty); }
    }
    [Theory]
    [InlineData("builtin.dark", "builtin.noctaxis", AppearanceMode.Dark)]
    [InlineData("builtin.light", "builtin.noctaxis", AppearanceMode.Light)]
    [InlineData("builtin.system", "builtin.noctaxis", AppearanceMode.System)]
    [InlineData("builtin.highcontrast", "builtin.highcontrast", AppearanceMode.Dark)]
    public void LegacyJsonMigratesAndWritesOnlyNewShape(string legacy, string family, AppearanceMode mode)
    {
        var json = JsonSerializer.Serialize(new { SelectedThemeId = legacy, ColourVisionMode = 3, TextScale = 1.75 });
        var p = JsonSerializer.Deserialize<AppearancePreferences>(json)!;
        Assert.Equal(new(family, ColourVisionMode.Tritanopia, 1.75, mode), p);
        var saved = JsonSerializer.Serialize(p);
        Assert.DoesNotContain("SelectedThemeId", saved);
        Assert.Contains("ThemeFamilyId", saved);
        Assert.Equal(p, JsonSerializer.Deserialize<AppearancePreferences>(saved));
    }
    [Fact]
    public void InvalidAndUnavailableInputsHaveIndependentFallbacks()
    {
        var p = new AppearancePreferences("unknown", (ColourVisionMode)999, 1.75, (AppearanceMode)999).Normalised();
        Assert.Equal(AppearanceMode.System, p.AppearanceMode);
        Assert.Equal(ColourVisionMode.None, p.ColourVisionMode);
        var resolved = new ThemeCatalogue().Resolve(p, false);
        Assert.Equal("builtin.noctaxis", resolved.Definition.Id);
        Assert.Equal(AppearanceMode.Light, resolved.AppearanceMode);
        var family = BuiltInPalettes.Definitions[0] with { Id = "supporter.test", IsSupporter = true, Light = null };
        var denied = new ThemeCatalogue([family]);
        Assert.Equal("builtin.noctaxis", denied.Resolve(new(family.Id, AppearanceMode: AppearanceMode.Light)).Definition.Id);
        var allowed = new ThemeCatalogue([family], id => id == family.Id);
        var fallback = allowed.Resolve(new(family.Id, AppearanceMode: AppearanceMode.Light));
        Assert.Equal(family.Id, fallback.Definition.Id);
        Assert.Equal(AppearanceMode.Dark, fallback.AppearanceMode);
        Assert.Contains("Missing Light", fallback.FallbackReason);
    }
    [AvaloniaTheory]
    [InlineData("builtin.noctaxis")]
    [InlineData("builtin.highcontrast")]
    public void PlatformEventsRespectModeAndDisposeWithoutAccumulation(string family)
    {
        var app = Application.Current!;
        var count = app.Resources.MergedDictionaries.Count;
        var platform = new Platform { Dark = true };
        using (var service = new ThemeService(app, platform: platform))
        {
            Assert.Equal(1, platform.Subscribers);
            var window = new MainWindow { Width = 1200, Height = 900 };
            window.Show();
            try
            {
                foreach (var mode in Enum.GetValues<AppearanceMode>())
                {
                    service.Apply(new(family, ColourVisionMode.Tritanopia, 1.5, mode));
                    foreach (var dark in new[] { true, false })
                    {
                        platform.Change(dark); Dispatcher.UIThread.RunJobs();
                        Assert.Equal(mode == AppearanceMode.System ? dark ? AppearanceMode.Dark : AppearanceMode.Light : mode, service.EffectiveTheme.AppearanceMode);
                        Assert.Equal(family, service.EffectiveTheme.Definition.Id);
                        Assert.Equal(ColourVisionMode.Tritanopia, service.Preferences.ColourVisionMode);
                        Assert.Equal(1.5, service.Preferences.TextScale);
                        Assert.Equal(count + 1, app.Resources.MergedDictionaries.Count);
                        AppearancePolishTests.Capture(window, $"family-{family}-{mode}-os-{(dark ? "Dark" : "Light")}-Tritanopia");
                    }
                }
                for (var i = 0; i < 60; i++)
                {
                    service.Apply(new(family, AppearanceMode: (AppearanceMode)(i % 3)));
                    platform.Change(i % 2 == 0); Dispatcher.UIThread.RunJobs();
                    Assert.Equal(count + 1, app.Resources.MergedDictionaries.Count);
                }
            }
            finally { window.Close(); }
        }
        Assert.Equal(0, platform.Subscribers);
        platform.Change(false); Dispatcher.UIThread.RunJobs();
        Assert.Equal(count, app.Resources.MergedDictionaries.Count);
        ((App)app).Themes!.Apply(new());
    }
}
