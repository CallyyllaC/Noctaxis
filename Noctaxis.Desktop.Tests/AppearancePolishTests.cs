using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

public sealed class AppearancePolishTests
{
    private sealed class StateButton : Button
    {
        protected override Type StyleKeyOverride => typeof(Button);
        public void SetState(string state) => PseudoClasses.Set(state, true);
    }
    [Theory, MemberData(nameof(AppearanceTests.Variants), MemberType = typeof(AppearanceTests))]
    public void BuiltInChromeIsExplicitAndSemanticVariantsAreDeterministic(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var catalogue = new ThemeCatalogue(isAvailable: _ => true);
        var resolved = catalogue.Resolve(new(id, mode, AppearanceMode: appearance));
        Assert.Empty(resolved.InheritedTokens);
        Assert.Empty(ThemeDefinitionValidator.Validate(resolved.Definition));
        foreach (var key in new[] { "SurfaceBackground", "MapChromeBackground", "CardHoverOverlay", "CardPressedOverlay" })
            Assert.Equal((appearance == AppearanceMode.Light ? resolved.Definition.Light! : resolved.Definition.Dark).Palette[key], resolved.Palette[key]);
        Assert.Equal(resolved.Palette.OrderBy(p => p.Key), catalogue.Resolve(new(id, mode, AppearanceMode: appearance)).Palette.OrderBy(p => p.Key));
        if (mode != ColourVisionMode.None)
            Assert.NotEqual(catalogue.Resolve(new(id, AppearanceMode: appearance)).Palette["Error"], resolved.Palette["Error"]);
        Assert.Equal(4, new[] { "Success", "Warning", "Error", "Information" }.Select(key => resolved.Palette[key]).Distinct().Count());
    }
    [Fact]
    public void MissingChromeIsReportedInsteadOfSilentlyPassingCompleteness()
    {
        var palette = new Dictionary<string, string>(BuiltInPalettes.Definitions[1].Dark.Palette);
        palette.Remove("SurfaceBackground");
        var incomplete = BuiltInPalettes.Definitions[1] with { Id = "test.incomplete", Dark = new ThemePaletteDefinition(palette, new Dictionary<ColourVisionMode, IReadOnlyDictionary<string, string>> { [ColourVisionMode.None] = palette }) };
        Assert.Contains(ThemeDefinitionValidator.Validate(incomplete), e => e.Contains("SurfaceBackground"));
        Assert.Contains("SurfaceBackground", new ThemeCatalogue([incomplete]).Resolve(new(incomplete.Id)).InheritedTokens);
    }
    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", AppearanceMode.Dark)] [InlineData("builtin.noctaxis", AppearanceMode.Light)] [InlineData("builtin.highcontrast", AppearanceMode.Dark)] [InlineData("builtin.highcontrast", AppearanceMode.Light)]
    public void ControlStateMatrixAndSemanticSamples(string id, AppearanceMode appearance)
    {
        var service = ((App)Application.Current!).Themes!;
        service.Apply(new(id, AppearanceMode: appearance));
        var row = new WrapPanel();
        var buttons = new List<StateButton>();
        foreach (var state in new[] { "Normal", "Hover", "Pressed", "Focused", "Disabled", "Primary", "Secondary" })
        {
            var button = new StateButton { Content = state, Margin = new Thickness(8), IsEnabled = state != "Disabled" };
            if (state == "Primary") button.Classes.Add("accent");
            if (state == "Secondary") button.Classes.Add("secondaryAction");
            buttons.Add(button); row.Children.Add(button);
        }
        var tabs = new TabControl { Items = { new TabItem { Header = "Normal tab" }, new TabItem { Header = "Selected tab" } }, SelectedIndex = 1 };
        var list = new ListBox { Items = { "Normal item", "Selected item", "Another item" }, SelectedIndex = 1, Height = 150 };
        var samples = new StackPanel { Spacing = 10 };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = { row, tabs, list, samples } };
        var window = new Window { Width = 1100, Height = 800, Content = panel };
        window.Show(); Dispatcher.UIThread.RunJobs();
        buttons[1].SetState(":pointerover"); buttons[2].SetState(":pressed"); buttons[3].SetState(":focus-visible");
        try
        {
            foreach (var mode in Enum.GetValues<ColourVisionMode>())
            {
                service.Apply(new(id, mode, AppearanceMode: appearance)); samples.Children.Clear();
                foreach (var (role, cue) in new[] { ("Success", "✓ Available"), ("Warning", "! Check conditions"), ("Error", "× Unavailable"), ("Information", "i Details") })
                {
                    var label = new TextBlock { Text = $"{role}: {cue}", FontSize = 24, Padding = new Thickness(10) };
                    label.Bind(TextBlock.ForegroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(role));
                    label.Bind(TextBlock.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(role + "Background"));
                    samples.Children.Add(label);
                }
                Dispatcher.UIThread.RunJobs(); Capture(window, $"states-semantic-{id}-{appearance}-{mode}");
                var presenters = buttons.Select(b => b.GetVisualDescendants().OfType<ContentPresenter>().First()).ToArray();
                if (id == "builtin.highcontrast")
                {
                    Assert.NotEqual(presenters[0].BorderThickness, presenters[1].BorderThickness);
                    Assert.NotEqual(presenters[1].BorderThickness, presenters[2].BorderThickness);
                    Assert.Equal(new Thickness(0), presenters[4].BorderThickness);
                    Assert.NotEqual(((ISolidColorBrush)presenters[3].BorderBrush!).Color, Color.Parse(service.EffectiveTheme.Palette["Accent"]));
                }
                Assert.Equal(Color.Parse(service.EffectiveTheme.Palette["SelectionBackground"]), ((ISolidColorBrush)((TabItem)tabs.Items[1]!).Background!).Color);
            }
        }
        finally { window.Close(); service.Apply(new()); }
    }
    internal static string Output => FindWorkspaceRoot() is { } root ? Path.Combine(root, "artifacts/appearance-polish") : Path.Combine(AppContext.BaseDirectory, "artifacts/appearance-polish");
    private static string? FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Noctaxis.slnx"))) return directory.FullName;
        return null;
    }
    internal static void Capture(Window window, string name)
    {
        Directory.CreateDirectory(Output);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(Output, name + ".png"), PngBitmapEncoderOptions.Default);
    }
    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", AppearanceMode.Dark)] [InlineData("builtin.noctaxis", AppearanceMode.Light)] [InlineData("builtin.highcontrast", AppearanceMode.Dark)] [InlineData("builtin.highcontrast", AppearanceMode.Light)]
    public void ImageCardHoverNeverUsesOpaqueFluentFill(string id, AppearanceMode appearance)
    {
        var service = ((App)Application.Current!).Themes!;
        service.Apply(new(id, AppearanceMode: appearance));
        var button = new Button { Classes = { "locationCardBody" } };
        var card = new Border { Classes = { "editorialLocationCard" }, Width = 500, Height = 220,
            Child = new Grid { Children = { new Border { Background = Brushes.CadetBlue }, button } } };
        var window = new Window { Width = 600, Height = 320, Content = card };
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            foreach (var state in new[] { "normal", "favourite", "selected", "favourite-selected" })
            {
                card.Classes.Set("favourite", state.Contains("favourite"));
                card.Classes.Set("selected", state.Contains("selected"));
                window.MouseMove(new Point(2, 2)); Dispatcher.UIThread.RunJobs();
                Capture(window, $"card-{id}-{appearance}-{state}-normal");
                window.MouseMove(card.TranslatePoint(new Point(200, 100), window)!.Value); Dispatcher.UIThread.RunJobs();
                Capture(window, $"card-{id}-{appearance}-{state}-hover");
                var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First();
                Assert.True(presenter.Background is ISolidColorBrush brush && brush.Color.A < 80,
                    $"Image-backed card has opaque hover fill: {presenter.Background}");
            }
            card.Classes.Add("selected");
            button.Focus(Avalonia.Input.NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Color.Parse(service.EffectiveTheme.Palette["FocusBorder"]), ((ISolidColorBrush)card.BorderBrush!).Color);
            Capture(window, $"card-{id}-{appearance}-selected-focus");
        }
        finally { window.Close(); service.Apply(new()); }
    }
    [AvaloniaFact]
    public void LiveTextScaleRemeasuresSettingsWithoutResizeOrNavigation()
    {
        var service = ((App)Application.Current!).Themes!;
        service.Apply(new());
        var window = new MainWindow { Width = 1100, Height = 900 };
        window.Show();
        var pages = (TabControl)((Grid)window.Content!).Children[1];
        pages.SelectedIndex = 2;
        var settings = (TabControl)((Grid)((TabItem)pages.Items[2]!).Content!).Children[0];
        Dispatcher.UIThread.RunJobs();
        var original = window.Bounds.Size;
        var measurements = new List<string>();
        try
        {
            foreach (var scale in new[] { 1d, 1.1, 1.2, 1.75, 1, 1.5, 2, 1 })
            {
                service.Apply(new(TextScale: scale));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var automatic = settings.GetVisualDescendants().OfType<TabItem>().Select(t => t.Bounds).ToArray();
                Capture(window, $"live-settings-{scale * 100:0}");
                // A forced remeasure must not repair an incorrect automatic result.
                foreach (var element in window.GetVisualDescendants().OfType<Layoutable>()) element.InvalidateMeasure();
                window.InvalidateMeasure(); window.UpdateLayout();
                var forced = settings.GetVisualDescendants().OfType<TabItem>().Select(t => t.Bounds).ToArray();
                measurements.Add($"{scale}: auto={string.Join(';', automatic)} forced={string.Join(';', forced)}");
                Assert.Equal(original, window.Bounds.Size);
                Assert.Equal(automatic, forced);
                Assert.Equal(8, automatic.Length);
                Assert.All(automatic, rect => Assert.True(rect.Width > 0 && rect.Height > 0));
                foreach (var tab in settings.GetVisualDescendants().OfType<TabItem>())
                {
                    var label = tab.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == (string?)tab.Header);
                    Assert.True(label.TextLayout.Width <= label.Bounds.Width + 1, $"Clipped Settings label: {tab.Header}");
                    Assert.True(label.TextLayout.Height <= label.Bounds.Height + 1, $"Clipped Settings height: {tab.Header}");
                    Assert.True(tab.Bounds.Right <= settings.Bounds.Width + 1);
                }
            }
        }
        finally
        {
            Directory.CreateDirectory(Output);
            File.WriteAllLines(Path.Combine(Output, "live-layout.txt"), measurements);
            window.Close(); service.Apply(new());
        }
    }
}
