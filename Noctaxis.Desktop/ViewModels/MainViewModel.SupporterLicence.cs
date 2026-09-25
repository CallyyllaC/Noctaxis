using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Supporter;
using Noctaxis.Desktop.Services;

namespace Noctaxis.Desktop.ViewModels;

public partial class MainViewModel
{
    public static Uri KoFiUri { get; } = new("https://ko-fi.com/callyyllac");
    [ObservableProperty] private string _supporterLicenceInput = string.Empty;
    [ObservableProperty] private string _supporterLicenceStatus = "Not licensed";
    [ObservableProperty] private bool _isEditingSupporterLicence;

    public bool HasAwooSupporterEntitlement { get; private set; }
    public bool IsSupporterLicenceStored => !string.IsNullOrWhiteSpace(Settings.AwooSupporterLicenceCode);
    public bool IsSupporterLicenceEditorVisible => !HasAwooSupporterEntitlement || IsEditingSupporterLicence;

    private void LoadSupporterLicenceState()
    {
        var code = Settings.AwooSupporterLicenceCode;
        var result = string.IsNullOrWhiteSpace(code)
            ? null
            : _supporterLicenceVerifier.Verify(code);
        SetSupporterState(result?.IsAwooSupporter == true, result);
        SupporterLicenceInput = string.Empty;
        IsEditingSupporterLicence = false;
    }

    private void SetSupporterState(bool enabled, SupporterLicenceResult? result)
    {
        var forcedUnlicensed = string.Equals(Environment.GetEnvironmentVariable("NOCTAXIS_FORCE_UNLICENSED"), "1", StringComparison.Ordinal);
        HasAwooSupporterEntitlement = enabled && !forcedUnlicensed;
        _themeService?.SetSupporterEntitlement(HasAwooSupporterEntitlement);
        SupporterLicenceStatus = forcedUnlicensed && enabled
            ? "Supporter licence disabled by development override."
            : result is null ? "Not licensed" : FriendlyStatus(result);
        OnPropertyChanged(nameof(HasAwooSupporterEntitlement));
        OnPropertyChanged(nameof(IsSupporterLicenceStored));
        OnPropertyChanged(nameof(IsSupporterLicenceEditorVisible));
        // Replacing the picker items can echo selection changes through its two-way binding.
        // Refresh under the existing appearance guard so availability cannot rewrite the request.
        _loadingAppearance = true;
        try
        {
            OnPropertyChanged(nameof(ThemeOptions));
            LoadAppearance();
        }
        finally { _loadingAppearance = false; }
    }

    private static string FriendlyStatus(SupporterLicenceResult result) => result.Status switch
    {
        SupporterLicenceVerificationStatus.Valid when result.IsAwooSupporter => "✓ Supporter licence active",
        SupporterLicenceVerificationStatus.UnsupportedVersion => "This licence uses a newer format that this version of Noctaxis does not support.",
        SupporterLicenceVerificationStatus.UnknownKey => "This licence was signed with an unknown Awoo key. Updating Noctaxis may be required.",
        SupporterLicenceVerificationStatus.Malformed => "This licence code is not recognised.",
        _ => "This licence could not be verified."
    };

    [RelayCommand]
    private async Task ActivateSupporterLicenceAsync()
    {
        var candidate = SupporterLicenceInput.Trim();
        var result = _supporterLicenceVerifier.Verify(candidate);
        if (!result.IsAwooSupporter)
        {
            SupporterLicenceStatus = FriendlyStatus(result);
            return;
        }

        Settings = Settings with { AwooSupporterLicenceCode = candidate };
        IsEditingSupporterLicence = false;
        SupporterLicenceInput = string.Empty;
        SetSupporterState(true, result);
        OnPropertyChanged(nameof(Settings));
        await PersistAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void ReplaceSupporterLicence()
    {
        IsEditingSupporterLicence = true;
        SupporterLicenceInput = string.Empty;
        SupporterLicenceStatus = "Enter a replacement licence code.";
        OnPropertyChanged(nameof(IsSupporterLicenceEditorVisible));
    }

    [RelayCommand]
    private async Task RemoveSupporterLicenceAsync()
    {
        Settings = Settings with { AwooSupporterLicenceCode = null };
        SupporterLicenceInput = string.Empty;
        IsEditingSupporterLicence = false;
        SetSupporterState(false, null);
        OnPropertyChanged(nameof(Settings));
        await PersistAsync(CancellationToken.None);
    }

    [RelayCommand]
    private void OpenKoFi()
    {
        if (_externalUriLauncher.TryOpen(KoFiUri, out var error)) return;
        StatusMessage = string.IsNullOrWhiteSpace(error)
            ? "Could not open Ko-fi in the default browser."
            : $"Could not open Ko-fi: {error}";
    }
}
