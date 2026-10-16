using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [AvaloniaFact]
    public async Task EquipmentLoadedAfterBindingClearsTheStartupWarningWithoutEditingProfiles()
    {
        var camera = new CameraProfile("saved-camera", "Saved camera", 36, 24);
        var lens = new LensProfile("saved-lens", "Saved lens", 24, 70);
        var equipment = new EquipmentSettings([camera], [lens]);
        var store = DefaultStore(new AppSettings(Equipment: equipment));
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        // MainWindow binds this control before InitializeAsync loads the saved profiles.
        var warning = new StatusText();
        warning.Bind(StatusText.TextProperty, new Binding(nameof(MainViewModel.EquipmentValidationMessage)) { Source = vm });
        Assert.Contains("Add a camera", warning.Text);
        Assert.True(warning.IsVisible);

        await vm.InitializeAsync();

        Assert.Null(warning.Text);
        Assert.False(warning.IsVisible);
        Assert.Equal(camera, Assert.Single(vm.EquipmentCameraEditors).Profile);
        Assert.Equal(lens, Assert.Single(vm.EquipmentLensEditors).Profile);
        Assert.Same(equipment, store.State.Settings.Equipment);
    }

    [AvaloniaTheory]
    [InlineData("camera-name")]
    [InlineData("sensor-width")]
    [InlineData("lens-name")]
    [InlineData("focal-range")]
    public async Task EquipmentWarningStillTracksInvalidRequiredFieldsWithoutSavingThem(string invalidField)
    {
        var equipment = new EquipmentSettings([new CameraProfile("saved-camera", "Saved camera", 36, 24)],
            [new LensProfile("saved-lens", "Saved lens", 24, 70)]);
        var store = DefaultStore(new AppSettings(Equipment: equipment));
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        var warning = new StatusText();
        warning.Bind(StatusText.TextProperty, new Binding(nameof(MainViewModel.EquipmentValidationMessage)) { Source = vm });
        await vm.InitializeAsync();
        var camera = Assert.Single(vm.EquipmentCameraEditors);
        var lens = Assert.Single(vm.EquipmentLensEditors);

        switch (invalidField)
        {
            case "camera-name": camera.DisplayName = ""; break;
            case "sensor-width": camera.SensorWidthMillimetres = 0; break;
            case "lens-name": lens.DisplayName = ""; break;
            case "focal-range": lens.MinimumFocalLengthMillimetres = 100; break;
        }
        await vm.SettleSettingsAsync();
        Assert.False(string.IsNullOrWhiteSpace(warning.Text));
        Assert.True(warning.IsVisible);
        Assert.Same(equipment, store.State.Settings.Equipment);

        camera.DisplayName = "Saved camera";
        camera.SensorWidthMillimetres = 36;
        lens.DisplayName = "Saved lens";
        lens.MinimumFocalLengthMillimetres = 24;
        await vm.SettleSettingsAsync();
        Assert.Null(warning.Text);
        Assert.False(warning.IsVisible);
        Assert.Equal(equipment.Cameras![0], store.State.Settings.Equipment!.Cameras![0]);
        Assert.Equal(equipment.Lenses![0], store.State.Settings.Equipment!.Lenses![0]);
    }
}
