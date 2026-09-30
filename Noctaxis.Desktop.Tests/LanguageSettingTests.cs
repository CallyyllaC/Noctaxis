using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    [CoversSettingsInput("SettingsLanguage")]
    public async Task TheLanguageSettingDefaultsToBritishEnglishAndIsSavedAsItsIdentifier()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = DefaultStore();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();

        // One choice for now. Its identifier is technical and fixed; its name is resource text.
        var option = Assert.Single(vm.LanguageOptions);
        Assert.Equal(new LanguageOption("en-GB", Strings.Get("Settings_Language_Name_en_GB")), option);
        Assert.Equal("English (United Kingdom)", option.Label);
        Assert.Equal(option, vm.SettingsLanguage);
        Assert.Equal("en-GB", vm.Settings.Language);

        // Choosing it behaves like every other setting: applied and saved at once, no restart.
        vm.SettingsLanguage = option;
        vm.SettingsLanguage = null;
        vm.SettingsUnits = "UK";
        await vm.SettleSettingsAsync();
        await vm.FlushAsync();
        Assert.Equal("en-GB", store.State.Settings.Language);
        Assert.Equal("UK", store.State.Settings.Units);
        Assert.False(vm.HasPendingRestart);
        Assert.Equal(AppLanguages.SupportedIds, vm.LanguageOptions.Select(item => item.Id));
        Assert.All(AppLanguages.SupportedIds, id => Assert.True(Strings.TryGet("Settings_Language_Name_" + id.Replace('-', '_'), out _), id));
    }

    [Fact]
    public async Task ASavedLanguageThisVersionDoesNotShipBecomesTheDefault()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var store = DefaultStore(new AppSettings(Language: "fr-FR"));
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        await vm.InitializeAsync();
        Assert.Equal("en-GB", vm.Settings.Language);
        Assert.Equal("en-GB", vm.SettingsLanguage!.Id);
    }

    [AvaloniaFact]
    public async Task SettingsShowsLanguageWithItsNameFromTheResources()
    {
        var (vm, window) = await CreateLocalisedWindowAsync();
        try
        {
            var pages = (TabControl)((Grid)window.Content!).Children[1];
            pages.SelectedItem = pages.Items.OfType<TabItem>().Single(item => Equals(item.Header, Strings.Get("Shell_Settings_Tab")));
            var categories = window.FindControl<TabControl>("SettingsCategories")!;
            categories.SelectedItem = categories.Items.OfType<TabItem>().Single(item => Equals(item.Header, Strings.Get("Settings_General_Tab")));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var choice = window.FindControl<ComboBox>("LanguageChoice")!;
            Assert.Equal(Strings.Get("Settings_General_Language_AccessibleName"), AutomationProperties.GetName(choice));
            Assert.Equal("Language", AutomationProperties.GetName(choice));
            Assert.Same(vm.LanguageOptions, choice.ItemsSource);
            Assert.Equal(vm.LanguageOptions[0], choice.SelectedItem);
            // No second, map-only language control exists anywhere in the window.
            var markup = TestMarkup.ReadMainWindow() + File.ReadAllText(Path.Combine(TestPaths.DesktopProject, "Controls", "PlannerLayersControl.axaml"));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(markup, "LanguageOptions"));
        }
        finally { window.Close(); }
    }
}
