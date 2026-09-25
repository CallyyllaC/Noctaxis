using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Tests;

public sealed class SupporterThemeTests
{
    public static IEnumerable<object[]> Families =>
        BuiltInPalettes.Definitions.Where(definition => definition.IsSupporter)
            .Select(definition => new object[] { definition.Id, definition.DisplayName });

    [Fact]
    public void FourSupporterFamiliesAreRegisteredAndGatedAsOneEntitlement()
    {
        var families = BuiltInPalettes.Definitions.Where(definition => definition.IsSupporter).ToArray();
        Assert.Equal(["supporter.noctaxis-neon", "supporter.foxfire", "supporter.deep-space", "supporter.moonlit"], families.Select(f => f.Id));
        Assert.All(families, family => Assert.True(family.IsSupporter));
        var denied = new ThemeCatalogue();
        Assert.All(families, family => Assert.False(denied.IsAvailable(family)));
        var calls = new List<string>();
        var allowed = new ThemeCatalogue(isAvailable: id => { calls.Add(id); return id == "supporter.foxfire"; });
        Assert.True(allowed.IsAvailable(families.Single(f => f.Id == "supporter.foxfire")));
        Assert.False(allowed.IsAvailable(families.Single(f => f.Id == "supporter.moonlit")));
        Assert.Contains("supporter.foxfire", calls);
    }

    [Fact]
    public void PickerMetadataKeepsLockedFamiliesVisibleAndMarked()
    {
        var catalogue = new ThemeCatalogue();
        var options = catalogue.Themes.Select(theme => new Noctaxis.Desktop.ViewModels.AppearanceOption(
            theme.Id, theme.DisplayName, theme.IsSupporter, catalogue.IsAvailable(theme))).ToArray();
        Assert.Equal(catalogue.Themes.Count, options.Length);
        Assert.All(options.Where(option => option.IsSupporter), option =>
        {
            Assert.False(option.IsAvailable);
            Assert.Equal("Locked · Supporter", option.AvailabilityLabel);
        });
        Assert.All(options.Where(option => !option.IsSupporter), option =>
        {
            Assert.True(option.IsAvailable);
            Assert.Equal(string.Empty, option.AvailabilityLabel);
        });
    }

    [Fact]
    public void LegacyUnlockVariableDoesNotBypassEntitlement()
    {
        var previous = Environment.GetEnvironmentVariable("NOCTAXIS_UNLOCK_SUPPORTER_THEMES");
        try
        {
            Environment.SetEnvironmentVariable("NOCTAXIS_UNLOCK_SUPPORTER_THEMES", "1");
            var catalogue = new ThemeCatalogue();
            Assert.All(BuiltInPalettes.Definitions.Where(theme => theme.IsSupporter), theme => Assert.False(catalogue.IsAvailable(theme)));
        }
        finally { Environment.SetEnvironmentVariable("NOCTAXIS_UNLOCK_SUPPORTER_THEMES", previous); }
    }

    [Theory, MemberData(nameof(Families))]
    public void SupporterFamilyHasCompleteDarkLightVisionMatrix(string id, string displayName)
    {
        var family = BuiltInPalettes.Definitions.Single(definition => definition.Id == id);
        Assert.Equal(displayName, family.DisplayName);
        Assert.Empty(ThemeDefinitionValidator.Validate(family));
        var catalogue = new ThemeCatalogue(isAvailable: _ => true);
        foreach (var mode in Enum.GetValues<AppearanceMode>())
        foreach (var vision in Enum.GetValues<ColourVisionMode>())
        {
            var resolved = catalogue.Resolve(new(id, vision, 1, mode), systemIsDark: false);
            Assert.Equal(id, resolved.Definition.Id);
            Assert.Equal(vision, resolved.Mode);
            Assert.Equal(mode == AppearanceMode.System ? AppearanceMode.Light : mode, resolved.AppearanceMode);
            Assert.Empty(resolved.InheritedTokens);
            Assert.Null(resolved.FallbackReason);
        }
        Assert.NotEqual(family.Dark.Palette["ApplicationBackground"], family.Light!.Palette["ApplicationBackground"]);
        Assert.NotEqual(family.Dark.Palette["Accent"], family.Light.Palette["Accent"]);
    }

    [Theory, MemberData(nameof(Families))]
    public void SupporterSystemModeSelectsFamilyPalette(string id, string _)
    {
        var catalogue = new ThemeCatalogue(isAvailable: _ => true);
        Assert.Equal(ThemeCatalogue.AllModes.OrderBy(mode => mode), catalogue.Resolve(new(id, AppearanceMode: AppearanceMode.Dark), true).Definition.Dark.Variants.Keys.OrderBy(mode => mode));
        Assert.Equal(AppearanceMode.Dark, catalogue.Resolve(new(id, AppearanceMode: AppearanceMode.System), true).AppearanceMode);
        Assert.Equal(AppearanceMode.Light, catalogue.Resolve(new(id, AppearanceMode: AppearanceMode.System), false).AppearanceMode);
    }

    [AvaloniaFact]
    public void RepresentativeSupporterCapturesUseInjectedAvailability()
    {
        var app = Application.Current!;
        var count = app.Resources.MergedDictionaries.Count;
        var catalogue = new ThemeCatalogue(isAvailable: _ => true);
        using var service = new ThemeService(app, catalogue);
        var window = new Window { Width = 1000, Height = 620 };
        var content = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
        content.Children.Add(new TextBlock { Text = "Supporter theme preview", FontSize = 28 });
        content.Children.Add(new TextBlock { Text = "Normal    Hover    Pressed    Selected    Focused    Disabled", FontSize = 18 });
        var states = new WrapPanel { ItemSpacing = 12 };
        foreach (var text in new[] { "Normal", "Hover", "Pressed", "Selected", "Focused", "Disabled" })
        {
            var button = new Button { Content = text, Padding = new Thickness(14, 8), IsEnabled = text != "Disabled" };
            if (text == "Selected") button.Classes.Add("accent");
            states.Children.Add(button);
        }
        content.Children.Add(states);
        foreach (var role in new[] { ("Success", "✓ Available"), ("Warning", "! Check"), ("Error", "× Unavailable"), ("Information", "i Details") })
        {
            var label = new TextBlock { Text = role.Item1 + ": " + role.Item2, FontSize = 20, Padding = new Thickness(8) };
            label.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(role.Item1));
            label.Bind(TextBlock.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(role.Item1 + "Background"));
            content.Children.Add(label);
        }
        window.Content = content;
        window.Show();
        try
        {
            foreach (var family in BuiltInPalettes.Definitions.Where(definition => definition.IsSupporter))
            foreach (var mode in new[] { AppearanceMode.Dark, AppearanceMode.Light })
            {
                service.Apply(new(family.Id, AppearanceMode: mode));
                Dispatcher.UIThread.RunJobs();
                using var bitmap = new RenderTargetBitmap(new PixelSize(1000, 620));
                bitmap.Render(window);
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Noctaxis.slnx"))) directory = directory.Parent;
                var path = Path.Combine(directory?.FullName ?? AppContext.BaseDirectory, "artifacts/appearance-supporter");
                Directory.CreateDirectory(path);
                bitmap.Save(Path.Combine(path, $"{family.Id}-{mode}.png"), PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
        Assert.Equal(count + 1, app.Resources.MergedDictionaries.Count);
    }
}
