using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Mapping;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    [CoversSettingsInput("SettingsLightPollutionColorMap")]
    public async Task LightPollutionPaletteSaveReloadResetAndThemeIndependence()
    {
        var catalogue=new OpenNgcTargetCatalogue(); var store=CreateSupporterStore(new AppSettings());
        var vm=CreateViewModel(new FakePlanning(catalogue),catalogue,store,new FakeExporter());
        await vm.InitializeAsync(); Assert.Equal("grayscale",vm.SettingsLightPollutionColorMap.Id);
        vm.SettingsLightPollutionColorMap=LightPollutionColorMaps.Resolve("turbo");
        Assert.Equal("grayscale",vm.LightPollutionMapPreferences.PaletteId);
        vm.ResetSettingsEditorCommand.Execute(null); Assert.Equal("grayscale",vm.SettingsLightPollutionColorMap.Id);
        vm.SettingsLightPollutionColorMap=LightPollutionColorMaps.Resolve("cividis");
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal(new LightPollutionPreferences(false,.5,"cividis"),store.State.Settings.LightPollution);
        await using var paths=new AtlasTestData(); var disk=new JsonUserDataStore(paths,NullLogger<JsonUserDataStore>.Instance);
        await disk.SaveAsync(store.State,CancellationToken.None);
        var reload=CreateViewModel(new FakePlanning(catalogue),catalogue,disk,new FakeExporter());
        await reload.InitializeAsync(); Assert.Equal("cividis",reload.SettingsLightPollutionColorMap.Id);
        await reload.ApplyAppearanceAsync(new AppearancePreferences(AppearanceMode: AppearanceMode.Light));
        Assert.Equal("cividis",reload.SettingsLightPollutionColorMap.Id); Assert.Equal("cividis",reload.LightPollutionMapPreferences.PaletteId);
        reload.ResetSettingsEditorCommand.Execute(null); Assert.Equal("grayscale",reload.SettingsLightPollutionColorMap.Id);
        Assert.Equal("cividis",reload.LightPollutionMapPreferences.PaletteId); // Reset is still an unsaved edit.
        await reload.SaveSettingsCommand.ExecuteAsync(null); Assert.Equal("grayscale",reload.LightPollutionMapPreferences.PaletteId);
    }

    [Fact]
    [CoversSettingsInput("UseColourVisionSafeLightPollutionPaletteCommand")]
    public async Task TurboWithAColourVisionModeOffersCividisWithoutChangingTheChoice()
    {
        var catalogue=new OpenNgcTargetCatalogue(); var store=CreateSupporterStore(new AppSettings(LightPollution:new(true,.5,"turbo")));
        var vm=CreateViewModel(new FakePlanning(catalogue),catalogue,store,new FakeExporter());
        await vm.InitializeAsync(); Assert.Equal("turbo",vm.SettingsLightPollutionColorMap.Id);
        // No colour-vision mode: Turbo is the user's informed choice and no notice is shown.
        Assert.False(vm.HasLightPollutionColourVisionAdvice); Assert.Empty(vm.LightPollutionColourVisionAdvice);
        Assert.Contains("Not colour-vision safe",vm.LightPollutionColorMapDescription);
        var notified=new List<string?>(); vm.PropertyChanged+=(_,e)=>notified.Add(e.PropertyName);
        vm.SelectedColourVisionValue=ColourVisionMode.Deuteranopia;
        Assert.Contains(nameof(vm.HasLightPollutionColourVisionAdvice),notified);
        Assert.True(vm.HasLightPollutionColourVisionAdvice); Assert.Contains("Cividis",vm.LightPollutionColourVisionAdvice);
        // Selecting the mode never overrides the palette by itself.
        Assert.Equal("turbo",vm.SettingsLightPollutionColorMap.Id); Assert.Equal("turbo",vm.LightPollutionMapPreferences.PaletteId);
        vm.UseColourVisionSafeLightPollutionPaletteCommand.Execute(null);
        Assert.Equal("cividis",vm.SettingsLightPollutionColorMap.Id); Assert.False(vm.HasLightPollutionColourVisionAdvice);
        Assert.Equal("turbo",vm.LightPollutionMapPreferences.PaletteId); // Still an editor change until saved.
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Equal("cividis",store.State.Settings.LightPollution!.PaletteId);
        vm.SettingsLightPollutionColorMap=LightPollutionColorMaps.Resolve("viridis");
        Assert.False(vm.HasLightPollutionColourVisionAdvice);
    }

    [Theory]
    [InlineData("null")] [InlineData("\"\"")] [InlineData("\"future\"")] [InlineData("42")]
    public async Task LightPollutionPaletteMalformedDiskValuePreservesOtherSettings(string value)
    {
        await using var paths=new AtlasTestData(); var disk=new JsonUserDataStore(paths,NullLogger<JsonUserDataStore>.Instance);
        var state=CreateSupporterStore(new AppSettings(LightPollution:new(true,.31,"turbo"))).State;
        await disk.SaveAsync(state,CancellationToken.None);
        var file=Path.Combine(paths.Root,"state.json");
        var json=System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        json["Settings"]!["LightPollution"]!["PaletteId"]=System.Text.Json.Nodes.JsonNode.Parse(value);
        await File.WriteAllTextAsync(file,json.ToJsonString());
        var catalogue=new OpenNgcTargetCatalogue();
        var vm=CreateViewModel(new FakePlanning(catalogue),catalogue,disk,new FakeExporter());
        await vm.InitializeAsync(); Assert.Equal("grayscale",vm.LightPollutionMapPreferences.PaletteId);
        Assert.True(vm.LightPollutionMapPreferences.IsVisible); Assert.Equal(.31,vm.LightPollutionMapPreferences.Opacity);
        Assert.True(File.Exists(file));
    }
}
