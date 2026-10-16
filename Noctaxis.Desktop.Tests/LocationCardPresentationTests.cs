using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Themes;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>Location-card roles for every theme family, Light/Dark and colour-vision mode.</summary>
public sealed class LocationCardThemeTests
{
    public static IEnumerable<object[]> Variants => AppearanceTests.Variants;

    [Theory, MemberData(nameof(Variants))]
    public void LocationCardRolesAreDefinedAndLegible(string id, ColourVisionMode mode, AppearanceMode appearance)
    {
        var resolved = new ThemeCatalogue(isAvailable: _ => true).Resolve(new(id, mode, AppearanceMode: appearance));
        var p = resolved.Palette;
        Assert.Empty(resolved.InheritedTokens);
        foreach (var token in LocationCardTokens.All) Assert.Contains(token, BuiltInPalettes.RequiredTokens);
        var label = $"{id}/{appearance}/{mode}";
        string S(string token) => p[token];
        // Card text sits on the card surface, never on artwork.
        foreach (var text in new[] { "PrimaryText", "SecondaryText" })
            Assert.True(ColourScience.Contrast(S(text), S(LocationCardTokens.Surface)) >= 4.5, $"{label}: {text} on the card");
        Assert.True(ColourScience.Contrast(S("MutedText"), S(LocationCardTokens.Surface)) >= 4.5, $"{label}: muted text on the card");
        // State badges carry text and an icon: legible text, and an outline that marks the badge.
        Assert.True(ColourScience.Contrast(S(LocationCardTokens.BadgeForeground), S(LocationCardTokens.BadgeBackground)) >= 4.5, $"{label}: badge text");
        Assert.True(ColourScience.Contrast(S(LocationCardTokens.BadgeBorder), S(LocationCardTokens.Surface)) >= 3, $"{label}: badge outline");
        Assert.True(ColourScience.Contrast(S(LocationCardTokens.Accent), S(LocationCardTokens.BadgeBackground)) >= 3, $"{label}: marker glyph");
        // Selection and focus are visible against the card and the page.
        foreach (var surface in new[] { LocationCardTokens.Surface, "ApplicationBackground" })
        {
            Assert.True(ColourScience.Contrast(S("Accent"), S(surface)) >= 3, $"{label}: selected border on {surface}");
            Assert.True(ColourScience.Contrast(S("FocusBorder"), S(surface)) >= 3, $"{label}: focus ring on {surface}");
        }
        // The preview lights settlements in the label colour on High Contrast and the family's map point colour otherwise.
        Assert.Equal(S(id == "builtin.highcontrast" ? MapThemeTokens.Label : MapThemeTokens.Poi), S(LocationCardTokens.PreviewGlow));
        // The instrument marks are the family's accent (the label colour in High Contrast): no per-family code.
        Assert.Equal(S(id == "builtin.highcontrast" ? MapThemeTokens.Label : "Accent"), S(LocationCardTokens.PreviewInstrument));
    }

    [Fact]
    public void RolesAreDerivedFromEachFamilyAndAuthoredValuesWin()
    {
        var catalogue = new ThemeCatalogue(isAvailable: _ => true);
        var badges = BuiltInPalettes.Definitions.Select(theme =>
            catalogue.Resolve(new(theme.Id, AppearanceMode: AppearanceMode.Dark)).Palette[LocationCardTokens.BadgeBorder]).ToArray();
        // Each family's own accent, not one shared decorative colour.
        Assert.Equal(badges.Length, badges.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var palette = new Dictionary<string, string>(BuiltInPalettes.Definitions[0].Dark.Palette) { [LocationCardTokens.BadgeBackground] = "#123456" };
        LocationCardTokens.Derive(palette, light: false, highContrast: false);
        Assert.Equal("#123456", palette[LocationCardTokens.BadgeBackground]);
    }
}

public sealed partial class MainViewModelTests
{
    [AvaloniaTheory]
    [InlineData("builtin.noctaxis", AppearanceMode.Dark)] [InlineData("builtin.noctaxis", AppearanceMode.Light)]
    [InlineData("builtin.highcontrast", AppearanceMode.Dark)] [InlineData("builtin.highcontrast", AppearanceMode.Light)]
    [InlineData("supporter.foxfire", AppearanceMode.Light)]
    public async Task LocationCardStatesUseTextAndBorderWeightNotColourAlone(string id, AppearanceMode appearance)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var location = new SavedLocation(Guid.NewGuid(), "Scouting location", new(53.6, -.4), "UTC", Notes: "Dark-sky field by the reservoir.");
        var store = new FakeStore(new(1, new AppSettings(), [location], PlanningSession.Default(Instant.FromUtc(2026, 9, 12, 20, 0), "UTC"), null));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        var service = ((App)Application.Current!).Themes!;
        vm.AttachThemeService(service);
        var window = new MainWindow { Width = 1200, Height = 900, DataContext = vm };
        window.Show();
        try
        {
            service.SetSupporterEntitlement(true);
            await vm.ApplyAppearanceAsync(new(id, AppearanceMode: appearance)); Dispatcher.UIThread.RunJobs();
            var p = service.EffectiveTheme.Palette;
            Color C(string token) => Color.Parse(p[token]);
            Color Brush(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
            var cardVm = vm.Locations.Saved.Single();
            var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("editorialLocationCard"));
            var body = card.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("locationCardBody"));
            // State weight = the card's constant 1px border plus its non-layout state ring (docs/interaction-states.md).
            double Weight() => card.BorderThickness.Left +
                card.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("cardStateRing")).BorderThickness.Left;
            bool BadgeShown(string text) => card.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == text && t.IsEffectivelyVisible);

            foreach (var (favourite, selected) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                cardVm.IsFavourite = favourite; cardVm.IsSelected = selected;
                window.MouseMove(new Point(2, 2)); Dispatcher.UIThread.RunJobs();
                // Favourite: the eyebrow says so and the star is filled; current: a pin badge with text.
                Assert.Equal(favourite, BadgeShown("FAVOURITE LOCATION"));
                Assert.Equal(!favourite, BadgeShown("SAVED LOCATION"));
                Assert.Equal(selected, BadgeShown("Current location"));
                Assert.Equal(C(LocationCardTokens.Surface), Brush(card.Background));
                Assert.Equal(selected ? C("Accent") : C(LocationCardTokens.Border), Brush(card.BorderBrush));
                Assert.Equal(1, card.BorderThickness.Left);
                Assert.True(Weight() > (selected ? 1 : 0));
                // The accessible name states what the badges show.
                Assert.Equal(favourite, cardVm.OpenAccessibleName.Contains("favourite"));
                Assert.Equal(selected, cardVm.OpenAccessibleName.Contains("current Planner location"));
                Assert.Equal(cardVm.OpenAccessibleName, Avalonia.Automation.AutomationProperties.GetName(body));
            }

            // Hover strengthens and thickens the border (not only a fill change), without resizing the card.
            cardVm.IsSelected = false;
            window.MouseMove(card.TranslatePoint(new Point(80, 60), window)!.Value); Dispatcher.UIThread.RunJobs();
            Assert.Equal(C("StrongBorder"), Brush(card.BorderBrush));
            Assert.Equal(2, Weight());
            Assert.Equal(1, card.BorderThickness.Left);
            window.MouseMove(new Point(2, 2)); Dispatcher.UIThread.RunJobs();

            // Keyboard focus shows the focus ring at focus thickness.
            body.Focus(NavigationMethod.Tab); Dispatcher.UIThread.RunJobs();
            Assert.Equal(C("FocusBorder"), Brush(card.BorderBrush));
            Assert.Equal(3, Weight());
            Assert.Equal(1, card.BorderThickness.Left);

            // Text sits in the left half, where the card-surface scrim covers the preview.
            var preview = card.GetVisualDescendants().OfType<LocationPreviewImage>().Single();
            var previewLeft = preview.TranslatePoint(default, card)!.Value.X;
            Assert.InRange(previewLeft, card.Bounds.Width * .3, card.Bounds.Width * .45);
            foreach (var text in card.GetVisualDescendants().OfType<LocationCardTextGrid>().Single().GetVisualDescendants().OfType<TextBlock>()
                         .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)))
                Assert.True(text.TranslatePoint(new Point(text.Bounds.Width, 0), card)!.Value.X <= card.Bounds.Width / 2 + 1, $"'{text.Text}' reaches past the scrim");
            Assert.True(Application.Current!.TryFindResource("CardScrim", out var scrim) && ((Color)scrim!).A >= 240);
            var scrimColour = (Color)scrim!;
            Assert.Equal(C(LocationCardTokens.Surface), Color.FromRgb(scrimColour.R, scrimColour.G, scrimColour.B));

            // The add tile belongs to the same system: card surface, border that strengthens to the accent on hover.
            var add = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("addLocationCard"));
            Assert.Equal(C(LocationCardTokens.Surface), Brush(add.Background));
            Assert.Equal(C("StrongBorder"), Brush(add.BorderBrush));
            window.MouseMove(add.TranslatePoint(new Point(40, 40), window)!.Value); Dispatcher.UIThread.RunJobs();
            Assert.Equal(C("Accent"), Brush(add.BorderBrush));
            window.MouseMove(new Point(2, 2)); Dispatcher.UIThread.RunJobs();

            // Enlarged text takes a larger share of the card over a solid backing, but never the whole
            // card: the scrim still fades away, so the artwork is squeezed rather than painted over.
            await vm.ApplyAppearanceAsync(new(id, TextScale: 1.5, AppearanceMode: appearance)); Dispatcher.UIThread.RunJobs();
            var textGrid = card.GetVisualDescendants().OfType<LocationCardTextGrid>().Single();
            Assert.InRange(textGrid.TextShare, .5, .67);
            Assert.True(textGrid.ColumnDefinitions[1].Width.Value > 0);
            Assert.True(Application.Current.TryFindResource("CardOpaque", out var opaque) && ((Color)opaque!).A == 255);
            Assert.True(Application.Current.TryFindResource("CardTransparent", out var clear) && ((Color)clear!).A == 0);
            Assert.True(Application.Current.TryFindResource("CardStopClear", out var ends) && (double)ends! <= .9);
        }
        finally
        {
            window.Close();
            service.SetSupporterEntitlement(false);
            service.Apply(new());
        }
    }
}
