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
        Assert.Equal("Not licensed", vm.SupporterLicenceStatus);
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
        Assert.Equal("✓ Supporter licence active", vm.SupporterLicenceStatus);
        Assert.Empty(vm.SupporterLicenceInput);
        Assert.All(vm.ThemeOptions.Where(option => option.IsSupporter), option => Assert.True(option.IsAvailable));
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
        Assert.Equal(message, vm.SupporterLicenceStatus);
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
    [CoversSettingsInput("RemoveSupporterLicenceCommand")]
    public async Task ValidActivationPersistsAndRemovalLocksThemesWithoutChangingAppearance()
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
        await vm.RemoveSupporterLicenceCommand.ExecuteAsync(null);
        Assert.False(vm.HasAwooSupporterEntitlement);
        Assert.Null(store.State.Settings.AwooSupporterLicenceCode);
        Assert.Equal(appearance, store.State.Settings.Appearance);
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
                    var settingsTabs = (Avalonia.Controls.TabControl)((Avalonia.Controls.Grid)((Avalonia.Controls.TabItem)tabs.Items[2]!).Content!).Children[0];
                    settingsTabs.SelectedIndex = 0;
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
