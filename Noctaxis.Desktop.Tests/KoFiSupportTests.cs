using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Supporter;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    public void SupporterCategoryHoldsTheLicenceControlsUnderItsTitle()
    {
        var document = TestMarkup.LoadMainWindow();
        var supporter = document.Descendants().Single(element => element.Name.LocalName == "TabItem" && element.Attribute("Header")?.Value == "Supporter");
        var root = supporter.Descendants().Single(element => element.Name.LocalName == "StackPanel" && element.Attribute("Classes")?.Value == "settingsCategory");
        var title = root.Elements().First();
        Assert.Equal("Supporter", title.Attribute("Text")?.Value);
        Assert.Equal("settingsTitle", title.Attribute("Classes")?.Value);
        // The licence state comes first, spelled out in words, then the add/replace editor.
        var panels = title.ElementsAfterSelf().Where(element => element.Name.LocalName == "StackPanel").ToArray();
        Assert.Contains(panels[0].Descendants(), element => element.Name.LocalName == "TextBlock" && element.Attribute("Text")?.Value == "{Binding SupporterLicenceStateLabel}");
        var editor = panels[1];
        Assert.Equal("{Binding IsSupporterLicenceEditorVisible}", editor.Attribute("IsVisible")?.Value);
        Assert.Contains(editor.Descendants(), element => element.Name.LocalName == "TextBlock" && element.Attribute("Text")?.Value == "Licence key");
        Assert.Contains(editor.Descendants(), element => element.Name.LocalName == "TextBox" && element.Attribute("Text")?.Value == "{Binding SupporterLicenceInput, Mode=TwoWay}");
        Assert.DoesNotContain(root.Descendants(), element => element.Attribute("Content")?.Value.Contains("Remove", StringComparison.Ordinal) == true);
    }

    [Fact]
    [CoversSettingsInput("OpenKoFiCommand")]
    public void KoFiButtonIsEnabledAndTargetsExactUri()
    {
        var document = TestMarkup.LoadMainWindow();
        var button = Assert.Single(document.Descendants(), element => element.Name.LocalName == "Button" && element.Attribute("Content")?.Value == "Support on Ko-fi");
        Assert.Null(button.Attribute("IsEnabled"));
        Assert.Equal("{Binding OpenKoFiCommand}", button.Attribute("Command")?.Value);
        Assert.Equal("Support on Ko-fi", button.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal("https://ko-fi.com/callyyllac", MainViewModel.KoFiUri.AbsoluteUri);
    }

    [AvaloniaFact]
    public async Task SupportLayoutRemainsValidAt100150And200PercentTextScale()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue,
            CreateSupporterStore(new AppSettings()), new FakeExporter(),
            supporterLicenceVerifier: new FakeVerifier(_ => new(SupporterLicenceVerificationStatus.Malformed)));
        await vm.InitializeAsync();
        var service = ((App)Application.Current!).Themes!;
        vm.AttachThemeService(service);
        var window = new MainWindow { Width = 720, Height = 1000, DataContext = vm };
        window.Show();
        try
        {
            var pages = (TabControl)((Grid)window.Content!).Children[1];
            pages.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            window.SelectSettingsCategory("Supporter");
            foreach (var scale in new[] { 1d, 1.5, 2d })
            {
                await vm.ApplyAppearanceAsync(new("builtin.noctaxis", TextScale: scale));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var headings = window.GetVisualDescendants().OfType<TextBlock>().ToArray();
                var title = Assert.Single(headings, text => text.Text == "Supporter" && text.Classes.Contains("settingsTitle"));
                var licence = Assert.Single(headings, text => text.Text == "Licence key");
                var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => b.Content?.ToString() == "Support on Ko-fi");
                var titlePoint = title.TranslatePoint(new Point(0, 0), window)!.Value;
                var licencePoint = licence.TranslatePoint(new Point(0, 0), window)!.Value;
                var buttonPoint = button.TranslatePoint(new Point(0, 0), window)!.Value;
                Assert.True(licencePoint.Y > titlePoint.Y, $"Licence heading order failed at {scale:P0}");
                Assert.True(buttonPoint.Y > licencePoint.Y, $"Ko-fi button order failed at {scale:P0}");
                foreach (var control in new Control[] { title, licence, button })
                {
                    Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, $"Invalid bounds for {control}");
                    Assert.False(double.IsNaN(control.Bounds.X) || double.IsNaN(control.Bounds.Y));
                }
            }
        }
        finally { window.Close(); service.Apply(new()); }
    }

    [Fact]
    public void BrowserLaunchFailureIsReportedWithoutThrowing()
    {
        var launcher = new FakeUriLauncher(false, "browser unavailable");
        var vm = CreateSupporterViewModel(CreateSupporterStore(new AppSettings()),
            new FakeVerifier(_ => new(SupporterLicenceVerificationStatus.Malformed)), launcher);
        var exception = Record.Exception(() => vm.OpenKoFiCommand.Execute(null));
        Assert.Null(exception);
        Assert.Equal("Could not open Ko-fi: browser unavailable", vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task KoFiDoesNotChangeEntitlementOrPlannerRequests()
    {
        var launcher = new FakeUriLauncher(true, null);
        var catalogue = new OpenNgcTargetCatalogue();
        var planning = new FakePlanning(catalogue);
        var vm = CreateViewModel(planning, catalogue,
            CreateSupporterStore(new AppSettings(AwooSupporterLicenceCode: "valid")), new FakeExporter(),
            supporterLicenceVerifier: new FakeVerifier(_ => new(SupporterLicenceVerificationStatus.Valid, AwooSupporterEntitlements.Supporter)),
            externalUriLauncher: launcher);
        await vm.InitializeAsync();
        var state = vm.HasAwooSupporterEntitlement;
        var core = planning.SnapshotCalculations;
        var environment = planning.EnvironmentRequests;
        var weather = planning.WeatherRequests;
        vm.OpenKoFiCommand.Execute(null);
        Assert.True(launcher.Opened);
        Assert.Equal(MainViewModel.KoFiUri, launcher.LastUri);
        Assert.Equal(state, vm.HasAwooSupporterEntitlement);
        Assert.Equal(core, planning.SnapshotCalculations);
        Assert.Equal(environment, planning.EnvironmentRequests);
        Assert.Equal(weather, planning.WeatherRequests);
    }

    private sealed class FakeUriLauncher(bool opened, string? failure) : IExternalUriLauncher
    {
        public bool Opened { get; private set; }
        public Uri? LastUri { get; private set; }
        public bool TryOpen(Uri uri, out string? error)
        {
            LastUri = uri;
            Opened = opened;
            error = opened ? null : failure;
            return opened;
        }
    }
}
