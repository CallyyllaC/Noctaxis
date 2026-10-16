using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Core.Supporter;
using Noctaxis.Desktop.Themes;
using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.ViewModels;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    [Fact]
    public void AttachedPublicKeyLoadsForTheAwooKeyId()
    {
        var store = AwooTrustedKeyStore.CreateDefault();
        Assert.True(store.TryGet("awoo-2026-01", out var key));
        Assert.Equal("Ed25519", key.Algorithm);
        Assert.Equal(44, key.SubjectPublicKeyInfo.Length);
    }

    [Fact]
    public void TrustedKeyLookupIsExactAndUnknownKeysFailClosed()
    {
        var store = AwooTrustedKeyStore.CreateDefault();
        Assert.False(store.TryGet("awoo-2026-01 ", out _));
        Assert.False(store.TryGet("future-key", out _));
    }

    [Fact]
    public void VerifierBoundaryExposesRequiredStatesAndRejectsReadableText()
    {
        Assert.Contains(SupporterLicenceVerificationStatus.Valid, Enum.GetValues<SupporterLicenceVerificationStatus>());
        Assert.Contains(SupporterLicenceVerificationStatus.Invalid, Enum.GetValues<SupporterLicenceVerificationStatus>());
        Assert.Contains(SupporterLicenceVerificationStatus.UnsupportedVersion, Enum.GetValues<SupporterLicenceVerificationStatus>());
        Assert.Contains(SupporterLicenceVerificationStatus.UnknownKey, Enum.GetValues<SupporterLicenceVerificationStatus>());
        Assert.Contains(SupporterLicenceVerificationStatus.Malformed, Enum.GetValues<SupporterLicenceVerificationStatus>());
        var verifier = new AwooSupporterLicenceVerifier();
        Assert.Equal(SupporterLicenceVerificationStatus.Malformed, verifier.Verify(string.Empty).Status);
        Assert.Equal(SupporterLicenceVerificationStatus.Malformed, verifier.Verify("awoo.supporter").Status);
    }

    [Fact]
    public void EntitlementRequiresTheExactAwooSupporterValue()
    {
        Assert.True(new SupporterLicenceResult(SupporterLicenceVerificationStatus.Valid, "awoo.supporter").IsAwooSupporter);
        Assert.False(new SupporterLicenceResult(SupporterLicenceVerificationStatus.Valid, "awoo.supporter.extra").IsAwooSupporter);
        Assert.False(new SupporterLicenceResult(SupporterLicenceVerificationStatus.Invalid, "awoo.supporter").IsAwooSupporter);
    }

    [Fact]
    [CoversSettingsInput("SupporterLicenceInput")]
    [CoversSettingsInput("ActivateSupporterLicenceCommand")]
    public async Task UnlicensedSettingsStateDoesNotExposeStoredCode()
    {
        var store = CreateSupporterStore(new AppSettings());
        var vm = CreateSupporterViewModel(store, new FakeVerifier(_ => new(SupporterLicenceVerificationStatus.Malformed)));
        await vm.InitializeAsync();
        Assert.False(vm.HasAwooSupporterEntitlement);
        Assert.Equal(SupporterLicenceState.None, vm.SupporterLicenceState);
        Assert.Equal("No licence installed", vm.SupporterLicenceStateLabel);
        Assert.Equal("Add licence", vm.SupporterLicenceSubmitLabel);
        Assert.True(vm.IsSupporterLicenceEditorVisible);
        Assert.Empty(vm.SupporterLicenceInput);
        Assert.DoesNotContain("secret", vm.SupporterLicenceStatus, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    [CoversSettingsInput("SupporterLicenceInput")]
    [CoversSettingsInput("ActivateSupporterLicenceCommand")]
    public async Task ValidInjectedVerifierUnlocksSupporterThemesAndHidesCode()
    {
        var store = CreateSupporterStore(new AppSettings(AwooSupporterLicenceCode: "secret-code"));
        var vm = CreateSupporterViewModel(store, new FakeVerifier(code => code == "secret-code"
            ? new(SupporterLicenceVerificationStatus.Valid, AwooSupporterEntitlements.Supporter)
            : new(SupporterLicenceVerificationStatus.Invalid)));
        using var themes = new ThemeService(Avalonia.Application.Current!);
        vm.AttachThemeService(themes);
        await vm.InitializeAsync();
        Assert.True(vm.HasAwooSupporterEntitlement);
        Assert.Equal("Valid licence", vm.SupporterLicenceStateLabel);
        Assert.Equal("✓", vm.SupporterLicenceStateGlyph);
        Assert.DoesNotContain("secret-code", vm.SupporterLicenceStatus);
        Assert.Empty(vm.SupporterLicenceInput);
        Assert.All(vm.ThemeOptions.Where(option => option.IsSupporter), option => Assert.True(option.IsAvailable));
        Assert.All(vm.ThemeOptions, option => Assert.Equal(option.IsSupporter ? "Supporter" : "Standard", option.AvailabilityLabel));
    }

    [Theory]
    [InlineData(SupporterLicenceVerificationStatus.Malformed, "This licence code is not recognised.")]
    [InlineData(SupporterLicenceVerificationStatus.UnsupportedVersion, "This licence uses a newer format that this version of Noctaxis does not support.")]
    [InlineData(SupporterLicenceVerificationStatus.UnknownKey, "This licence was signed with an unknown Awoo key. Updating Noctaxis may be required.")]
    [InlineData(SupporterLicenceVerificationStatus.Invalid, "This licence could not be verified.")]
    public async Task VerificationFailuresMapToFriendlyStatus(SupporterLicenceVerificationStatus status, string message)
    {
        var store = CreateSupporterStore(new AppSettings());
        var vm = CreateSupporterViewModel(store, new FakeVerifier(_ => new(status)));
        await vm.InitializeAsync();
        vm.SupporterLicenceInput = "candidate";
        await vm.ActivateSupporterLicenceCommand.ExecuteAsync(null);
        Assert.Equal(message, vm.SupporterLicenceInputError);
        Assert.Equal("No licence installed", vm.SupporterLicenceStateLabel);
        Assert.Null(store.State.Settings.AwooSupporterLicenceCode);
    }

    [Fact]
    [CoversSettingsInput("SupporterLicenceInput")]
    [CoversSettingsInput("ActivateSupporterLicenceCommand")]
    [CoversSettingsInput("ReplaceSupporterLicenceCommand")]
    public async Task InvalidReplacementKeepsExistingLicenceAndEntitlement()
    {
        var store = CreateSupporterStore(new AppSettings(AwooSupporterLicenceCode: "valid"));
        var vm = CreateSupporterViewModel(store, new FakeVerifier(code => code == "valid"
            ? new(SupporterLicenceVerificationStatus.Valid, AwooSupporterEntitlements.Supporter)
            : new(SupporterLicenceVerificationStatus.Invalid)));
        await vm.InitializeAsync();
        vm.ReplaceSupporterLicenceCommand.Execute(null);
        vm.SupporterLicenceInput = "bad";
        await vm.ActivateSupporterLicenceCommand.ExecuteAsync(null);
        Assert.True(vm.HasAwooSupporterEntitlement);
        Assert.Equal("valid", store.State.Settings.AwooSupporterLicenceCode);
    }

    [Fact]
    [CoversSettingsInput("SupporterLicenceInput")]
    [CoversSettingsInput("ActivateSupporterLicenceCommand")]
    public async Task ValidActivationPersistsWithoutChangingAppearance()
    {
        var appearance = new AppearancePreferences("builtin.highcontrast", ColourVisionMode.Tritanopia, 1.5, AppearanceMode.Light);
        var store = CreateSupporterStore(new AppSettings(Appearance: appearance));
        var vm = CreateSupporterViewModel(store, new FakeVerifier(code => code == "valid"
            ? new(SupporterLicenceVerificationStatus.Valid, AwooSupporterEntitlements.Supporter)
            : new(SupporterLicenceVerificationStatus.Invalid)));
        await vm.InitializeAsync();
        vm.SupporterLicenceInput = " valid ";
        await vm.ActivateSupporterLicenceCommand.ExecuteAsync(null);
        Assert.True(vm.HasAwooSupporterEntitlement);
        Assert.Equal("valid", store.State.Settings.AwooSupporterLicenceCode);
        Assert.Equal(appearance, store.State.Settings.Appearance);
    }

    [Fact]
    public void ThereIsNoWayToDeLicenseAnInstallation()
    {
        var commands = typeof(MainViewModel).GetProperties()
            .Where(property => property.Name.Contains("Licence", StringComparison.Ordinal) && property.Name.EndsWith("Command", StringComparison.Ordinal))
            .Select(property => property.Name).ToArray();
        Assert.Equal(["ActivateSupporterLicenceCommand", "CancelSupporterLicenceReplacementCommand", "ReplaceSupporterLicenceCommand"], commands.Order());
    }

    [Fact]
    [CoversSettingsInput("SupporterLicenceInput")]
    [CoversSettingsInput("ActivateSupporterLicenceCommand")]
    [CoversSettingsInput("ReplaceSupporterLicenceCommand")]
    [CoversSettingsInput("CancelSupporterLicenceReplacementCommand")]
    public async Task AValidLicenceIsShownAsValidAndCanOnlyBeReplacedByAnotherValidLicence()
    {
        var issued = new DateTimeOffset(2026, 3, 14, 0, 0, 0, TimeSpan.Zero);
        var store = CreateSupporterStore(new AppSettings(AwooSupporterLicenceCode: "first"));
        var vm = CreateSupporterViewModel(store, new FakeVerifier(code => code is "first" or "second"
            ? new(SupporterLicenceVerificationStatus.Valid, AwooSupporterEntitlements.Supporter, 1, "awoo-2026-01", issued)
            : new(SupporterLicenceVerificationStatus.Invalid)));
        await vm.InitializeAsync();

        Assert.Equal(SupporterLicenceState.Valid, vm.SupporterLicenceState);
        Assert.Equal("Valid licence", vm.SupporterLicenceStateLabel);
        Assert.Contains("Awoo supporter licence", vm.SupporterLicenceStatus);
        Assert.Contains("2026", vm.SupporterLicenceStatus);
        Assert.DoesNotContain("awoo-2026-01", vm.SupporterLicenceStatus);
        Assert.DoesNotContain("first", vm.SupporterLicenceStatus);
        Assert.True(vm.CanStartSupporterLicenceReplacement);
        Assert.False(vm.IsSupporterLicenceEditorVisible);

        vm.ReplaceSupporterLicenceCommand.Execute(null);
        Assert.True(vm.IsSupporterLicenceEditorVisible);
        Assert.True(vm.CanCancelSupporterLicenceReplacement);
        Assert.Equal("Replace licence", vm.SupporterLicenceSubmitLabel);
        vm.CancelSupporterLicenceReplacementCommand.Execute(null);
        Assert.False(vm.IsSupporterLicenceEditorVisible);
        Assert.Equal("first", store.State.Settings.AwooSupporterLicenceCode);

        vm.ReplaceSupporterLicenceCommand.Execute(null);
        vm.SupporterLicenceInput = "corrupted";
        await vm.ActivateSupporterLicenceCommand.ExecuteAsync(null);
        Assert.Equal("first", store.State.Settings.AwooSupporterLicenceCode);
        Assert.Equal("Valid licence", vm.SupporterLicenceStateLabel);
        Assert.True(vm.HasAwooSupporterEntitlement);
        Assert.Contains("current licence has not been changed", vm.SupporterLicenceInputError);
        Assert.True(vm.IsSupporterLicenceEditorVisible);

        vm.SupporterLicenceInput = "second";
        await vm.ActivateSupporterLicenceCommand.ExecuteAsync(null);
        Assert.Equal("second", store.State.Settings.AwooSupporterLicenceCode);
        Assert.Equal("Valid licence", vm.SupporterLicenceStateLabel);
        Assert.False(vm.IsSupporterLicenceEditorVisible);
        Assert.False(vm.HasSupporterLicenceInputError);
    }

    [Fact]
    [CoversSettingsInput("SupporterLicenceInput")]
    [CoversSettingsInput("ActivateSupporterLicenceCommand")]
    public async Task AnInvalidStoredLicenceIsShownAsInvalidAndCanBeReplacedWithAValidOne()
    {
        var store = CreateSupporterStore(new AppSettings(AwooSupporterLicenceCode: "corrupted"));
        var vm = CreateSupporterViewModel(store, new FakeVerifier(code => code == "valid"
            ? new(SupporterLicenceVerificationStatus.Valid, AwooSupporterEntitlements.Supporter)
            : new(SupporterLicenceVerificationStatus.Malformed)));
        await vm.InitializeAsync();

        Assert.Equal(SupporterLicenceState.Invalid, vm.SupporterLicenceState);
        Assert.Equal("Invalid licence", vm.SupporterLicenceStateLabel);
        Assert.Equal("×", vm.SupporterLicenceStateGlyph);
        Assert.Contains("not recognised", vm.SupporterLicenceStatus);
        Assert.False(vm.HasAwooSupporterEntitlement);
        Assert.True(vm.IsSupporterLicenceEditorVisible);
        Assert.False(vm.CanCancelSupporterLicenceReplacement);
        Assert.Equal("Replace licence", vm.SupporterLicenceSubmitLabel);

        vm.SupporterLicenceInput = "still bad";
        await vm.ActivateSupporterLicenceCommand.ExecuteAsync(null);
        Assert.Equal("corrupted", store.State.Settings.AwooSupporterLicenceCode);
        Assert.Equal("Invalid licence", vm.SupporterLicenceStateLabel);

        vm.SupporterLicenceInput = "valid";
        await vm.ActivateSupporterLicenceCommand.ExecuteAsync(null);
        Assert.Equal("valid", store.State.Settings.AwooSupporterLicenceCode);
        Assert.Equal("Valid licence", vm.SupporterLicenceStateLabel);
        Assert.True(vm.HasAwooSupporterEntitlement);
    }
    [Fact]
    public async Task ForceUnlicensedOverridesValidVerifierWithoutPersistence()
    {
        var previous = Environment.GetEnvironmentVariable("NOCTAXIS_FORCE_UNLICENSED");
        try
        {
            Environment.SetEnvironmentVariable("NOCTAXIS_FORCE_UNLICENSED", "1");
            var store = CreateSupporterStore(new AppSettings(AwooSupporterLicenceCode: "valid"));
            var vm = CreateSupporterViewModel(store, new FakeVerifier(_ => new(
                SupporterLicenceVerificationStatus.Valid, AwooSupporterEntitlements.Supporter)));
            await vm.InitializeAsync();
            Assert.False(vm.HasAwooSupporterEntitlement);
            Assert.Contains("development override", vm.SupporterLicenceStatus, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("valid", store.State.Settings.AwooSupporterLicenceCode);
        }
        finally { Environment.SetEnvironmentVariable("NOCTAXIS_FORCE_UNLICENSED", previous); }
    }

    [Fact]
    public void UnavailablePersistedFamilyFallsBackWithoutChangingRequest()
    {
        var requested = new AppearancePreferences("supporter.moonlit", AppearanceMode: AppearanceMode.Dark);
        var resolved = new ThemeCatalogue().Resolve(requested);
        Assert.Equal(ThemeCatalogue.DefaultId, resolved.Definition.Id);
        Assert.Equal("supporter.moonlit", requested.ThemeFamilyId);
    }

    [AvaloniaFact]
    public async Task SupporterLicenceCaptures()
    {
        var output = Environment.GetEnvironmentVariable("NOCTAXIS_SUPPORTER_LICENCE_CAPTURES");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var previousForce = Environment.GetEnvironmentVariable("NOCTAXIS_FORCE_UNLICENSED");
        var theme = ((App)Avalonia.Application.Current!).Themes!;
        try
        {
            async Task Capture(string name, AppSettings settings, Func<string, SupporterLicenceResult> verify,
                Action<MainViewModel>? prepare = null, bool force = false)
            {
                Environment.SetEnvironmentVariable("NOCTAXIS_FORCE_UNLICENSED", force ? "1" : null);
                var store = CreateSupporterStore(settings);
                var vm = CreateSupporterViewModel(store, new FakeVerifier(verify));
                vm.AttachThemeService(theme);
                await vm.InitializeAsync();
                prepare?.Invoke(vm);
                var window = new Noctaxis.Desktop.Views.MainWindow { Width = 1440, Height = 1000, DataContext = vm };
                window.Show();
                try
                {
                    var tabs = (Avalonia.Controls.TabControl)((Avalonia.Controls.Grid)window.Content!).Children[1];
                    tabs.SelectedIndex = 2;
                    Dispatcher.UIThread.RunJobs();
                    var settingsTabs = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TabControl>(window, "SettingsCategories")!;
                    settingsTabs.SelectedItem = settingsTabs.Items.Cast<Avalonia.Controls.TabItem>().Single(item => (string?)item.Header == "Supporter");
                    Dispatcher.UIThread.RunJobs();
                    if (settingsTabs.SelectedContent is Avalonia.Controls.ScrollViewer scrollViewer)
                        scrollViewer.Offset = new Avalonia.Vector(0, 10_000);
                    Dispatcher.UIThread.RunJobs();
                    using var bitmap = new RenderTargetBitmap(new Avalonia.PixelSize(1440, 1000));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
                }
                finally { window.Close(); }
            }

            await Capture("unlicensed", new AppSettings(), _ => new(SupporterLicenceVerificationStatus.Malformed));
            await Capture("input-populated", new AppSettings(), _ => new(SupporterLicenceVerificationStatus.Malformed), vm => vm.SupporterLicenceInput = "candidate");
            await Capture("valid-injected", new AppSettings(AwooSupporterLicenceCode: "valid"), _ => new(SupporterLicenceVerificationStatus.Valid, AwooSupporterEntitlements.Supporter));
            await Capture("invalid", new AppSettings(AwooSupporterLicenceCode: "invalid"), _ => new(SupporterLicenceVerificationStatus.Invalid));
            await Capture("locked-themes", new AppSettings(), _ => new(SupporterLicenceVerificationStatus.Malformed));
            await Capture("available-themes", new AppSettings(AwooSupporterLicenceCode: "valid"), _ => new(SupporterLicenceVerificationStatus.Valid, AwooSupporterEntitlements.Supporter));
            await Capture("valid-replacing", new AppSettings(AwooSupporterLicenceCode: "valid"), code => code == "valid" ? new(SupporterLicenceVerificationStatus.Valid, AwooSupporterEntitlements.Supporter) : new(SupporterLicenceVerificationStatus.Malformed), vm => { vm.ReplaceSupporterLicenceCommand.Execute(null); vm.SupporterLicenceInput = "bad"; vm.ActivateSupporterLicenceCommand.Execute(null); });
            await Capture("force-unlicensed", new AppSettings(AwooSupporterLicenceCode: "valid"), _ => new(SupporterLicenceVerificationStatus.Valid, AwooSupporterEntitlements.Supporter), force: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOCTAXIS_FORCE_UNLICENSED", previousForce);
            theme.Apply(new());
        }
    }

    private static MainViewModel CreateSupporterViewModel(FakeStore store, IAwooSupporterLicenceVerifier verifier,
        IExternalUriLauncher? externalUriLauncher = null)
    {
        var catalogue = new OpenNgcTargetCatalogue();
        return CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter(),
            supporterLicenceVerifier: verifier, externalUriLauncher: externalUriLauncher);
    }

    private static FakeStore CreateSupporterStore(AppSettings settings) =>
        new(new PersistedState(1, settings, [], PlanningSession.Default(
            NodaTime.Instant.FromUtc(2024, 1, 1, 0, 0), "UTC"), null));

    private sealed class FakeVerifier(Func<string, SupporterLicenceResult> verify) : IAwooSupporterLicenceVerifier
    {
        public SupporterLicenceResult Verify(string licenceCode) => verify(licenceCode);
    }
}
