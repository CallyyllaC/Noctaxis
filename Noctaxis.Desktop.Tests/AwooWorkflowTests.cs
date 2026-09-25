using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [AvaloniaFact]
    public async Task AwooFrozenVectorDrivesLiveSettingsWorkflowWithoutScientificWork()
    {
        var catalogue = new OpenNgcTargetCatalogue(); var planning = new FakePlanning(catalogue);
        var settings = new AppSettings(Appearance: new("supporter.moonlit"), CameraFraming: new(TerrainObstructionColour: "#123456"));
        var store = CreateSupporterStore(settings);
        var vm = CreateViewModel(planning, catalogue, store, new FakeExporter(), supporterLicenceVerifier: AwooV1Fixture.Verifier());
        using var themes = new ThemeService(Application.Current!);
        vm.AttachThemeService(themes); await vm.InitializeAsync();
        var window = new MainWindow { Width = 1440, Height = 1000, DataContext = vm };
        window.Show();
        try
        {
            var pages = (TabControl)((Grid)window.Content!).Children[1]; pages.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            var picker = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(), picker => Avalonia.Automation.AutomationProperties.GetName(picker) == "Theme");
            void Capture(string name)
            {
                var output = Environment.GetEnvironmentVariable("NOCTAXIS_AWOO_V1_CAPTURES");
                if (string.IsNullOrEmpty(output)) return;
                var settingsTabs = (TabControl)((Grid)((TabItem)pages.Items[2]!).Content!).Children[0];
                if (settingsTabs.SelectedContent is ScrollViewer scroll) scroll.Offset = new Vector(0, 10000);
                Dispatcher.UIThread.RunJobs();
                Directory.CreateDirectory(output);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(1440, 1000));
                bitmap.Render(window);
                bitmap.Save(Path.Combine(output, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            var calls = (planning.SnapshotCalculations, planning.EnvironmentRequests, planning.WeatherRequests, planning.ForcedRefreshes);
            Assert.False(vm.HasAwooSupporterEntitlement);
            Assert.All(picker.Items.Cast<Noctaxis.Desktop.ViewModels.AppearanceOption>().Where(o => o.IsSupporter), o => Assert.False(o.IsAvailable));
            Assert.Equal("builtin.noctaxis", themes.EffectiveTheme.Definition.Id);
            Capture("01-unlicensed");
            vm.SupporterLicenceInput = "bad"; await vm.ActivateSupporterLicenceCommand.ExecuteAsync(null);
            Assert.Null(store.State.Settings.AwooSupporterLicenceCode); Assert.False(vm.HasAwooSupporterEntitlement);
            vm.SupporterLicenceInput = " \n" + AwooV1Fixture.Licence + "\t";
            await vm.ActivateSupporterLicenceCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            Assert.Equal(AwooV1Fixture.Licence, store.State.Settings.AwooSupporterLicenceCode);
            Assert.True(vm.HasAwooSupporterEntitlement); Assert.False(vm.IsSupporterLicenceEditorVisible); Assert.Empty(vm.SupporterLicenceInput);
            Assert.All(picker.Items.Cast<Noctaxis.Desktop.ViewModels.AppearanceOption>().Where(o => o.IsSupporter), o => Assert.True(o.IsAvailable));
            Assert.Equal("supporter.moonlit", themes.EffectiveTheme.Definition.Id);
            Capture("02-activated");
            foreach (var id in new[] { "supporter.noctaxis-neon", "supporter.foxfire", "supporter.deep-space", "supporter.moonlit" })
            {
                picker.SelectedItem = picker.Items.Cast<Noctaxis.Desktop.ViewModels.AppearanceOption>().Single(o => o.Id == id);
                Dispatcher.UIThread.RunJobs(); Assert.Equal(id, themes.EffectiveTheme.Definition.Id);
                Capture(id);
            }
            vm.ReplaceSupporterLicenceCommand.Execute(null); vm.SupporterLicenceInput = AwooV1Fixture.Licence[..^2] + "AA";
            await vm.ActivateSupporterLicenceCommand.ExecuteAsync(null);
            Assert.True(vm.HasAwooSupporterEntitlement); Assert.Equal(AwooV1Fixture.Licence, store.State.Settings.AwooSupporterLicenceCode);
            Capture("03-invalid-replacement");
            var restarted = CreateSupporterViewModel(store, AwooV1Fixture.Verifier());
            restarted.AttachThemeService(themes); await restarted.InitializeAsync();
            Assert.True(restarted.HasAwooSupporterEntitlement); Assert.Empty(restarted.SupporterLicenceInput);
            await vm.RemoveSupporterLicenceCommand.ExecuteAsync(null); Dispatcher.UIThread.RunJobs();
            Assert.Null(store.State.Settings.AwooSupporterLicenceCode); Assert.False(vm.HasAwooSupporterEntitlement);
            Assert.Equal("supporter.moonlit", store.State.Settings.Appearance!.ThemeFamilyId);
            Assert.Equal("builtin.noctaxis", themes.EffectiveTheme.Definition.Id);
            Capture("04-removed");
            Assert.All(picker.Items.Cast<Noctaxis.Desktop.ViewModels.AppearanceOption>().Where(o => o.IsSupporter), o => Assert.False(o.IsAvailable));
            vm.SupporterLicenceInput = AwooV1Fixture.Licence; await vm.ActivateSupporterLicenceCommand.ExecuteAsync(null);
            Assert.Equal("supporter.moonlit", themes.EffectiveTheme.Definition.Id);
            Assert.Equal(settings.EffectiveCameraFraming, store.State.Settings.EffectiveCameraFraming);
            Assert.Equal(calls, (planning.SnapshotCalculations, planning.EnvironmentRequests, planning.WeatherRequests, planning.ForcedRefreshes));
            Assert.DoesNotContain(AwooV1Fixture.Licence, vm.StatusMessage ?? "");
            Assert.DoesNotContain(AwooV1Fixture.Licence, vm.SupporterLicenceStatus);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AwooRealStartupForceUnlicensedAndRestoration()
    {
        var previous = Environment.GetEnvironmentVariable("NOCTAXIS_FORCE_UNLICENSED");
        try
        {
            var store = CreateSupporterStore(new AppSettings(AwooSupporterLicenceCode: AwooV1Fixture.Licence));
            Environment.SetEnvironmentVariable("NOCTAXIS_FORCE_UNLICENSED", "1");
            var forced = CreateSupporterViewModel(store, AwooV1Fixture.Verifier()); await forced.InitializeAsync();
            Assert.False(forced.HasAwooSupporterEntitlement); Assert.Equal(AwooV1Fixture.Licence, store.State.Settings.AwooSupporterLicenceCode);
            Assert.DoesNotContain("FORCE_UNLICENSED", System.Text.Json.JsonSerializer.Serialize(store.State));
            Environment.SetEnvironmentVariable("NOCTAXIS_FORCE_UNLICENSED", null);
            var restored = CreateSupporterViewModel(store, AwooV1Fixture.Verifier()); await restored.InitializeAsync();
            Assert.True(restored.HasAwooSupporterEntitlement);
            var invalid = CreateSupporterViewModel(CreateSupporterStore(new AppSettings(AwooSupporterLicenceCode: "invalid")), AwooV1Fixture.Verifier());
            await invalid.InitializeAsync(); Assert.False(invalid.HasAwooSupporterEntitlement);
        }
        finally { Environment.SetEnvironmentVariable("NOCTAXIS_FORCE_UNLICENSED", previous); }
    }
}
