using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Supporter;
using Noctaxis.Desktop.Services;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>The stored supporter licence as the user sees it. There is deliberately no way to de-license.</summary>
public enum SupporterLicenceState
{
    None,
    Valid,
    Invalid
}

public partial class MainViewModel
{
    public static Uri KoFiUri { get; } = new("https://ko-fi.com/callyyllac");
    [ObservableProperty] private string _supporterLicenceInput = string.Empty;
    [ObservableProperty] private string _supporterLicenceStatus = NoLicenceDetail;
    [ObservableProperty] private string _supporterLicenceInputError = string.Empty;
    [ObservableProperty] private bool _isEditingSupporterLicence;

    private const string NoLicenceDetail = "Add a licence key to unlock the supporter themes.";

    public bool HasAwooSupporterEntitlement { get; private set; }
    public SupporterLicenceState SupporterLicenceState { get; private set; }
    public bool IsSupporterLicenceStored => !string.IsNullOrWhiteSpace(Settings.AwooSupporterLicenceCode);

    /// <summary>The state in words, so it never depends on the tick or cross colour.</summary>
    public string SupporterLicenceStateLabel => SupporterLicenceState switch
    {
        SupporterLicenceState.Valid => "Valid licence",
        SupporterLicenceState.Invalid => "Invalid licence",
        _ => "No licence installed"
    };

    /// <summary>A supplementary mark beside the label; empty when there is no licence.</summary>
    public string SupporterLicenceStateGlyph => SupporterLicenceState switch
    {
        SupporterLicenceState.Valid => "✓",
        SupporterLicenceState.Invalid => "×",
        _ => string.Empty
    };

    public bool IsSupporterLicenceValid => SupporterLicenceState == SupporterLicenceState.Valid;
    public bool IsSupporterLicenceInvalid => SupporterLicenceState == SupporterLicenceState.Invalid;
    public bool HasSupporterLicenceStateGlyph => SupporterLicenceState != SupporterLicenceState.None;

    /// <summary>A valid licence is replaced on request; with none or an invalid one the editor is always offered.</summary>
    public bool IsSupporterLicenceEditorVisible => SupporterLicenceState != SupporterLicenceState.Valid || IsEditingSupporterLicence;
    public bool CanStartSupporterLicenceReplacement => SupporterLicenceState == SupporterLicenceState.Valid && !IsEditingSupporterLicence;
    public bool CanCancelSupporterLicenceReplacement => SupporterLicenceState == SupporterLicenceState.Valid && IsEditingSupporterLicence;
    public string SupporterLicenceSubmitLabel => IsSupporterLicenceStored ? "Replace licence" : "Add licence";
    public bool HasSupporterLicenceInputError => !string.IsNullOrEmpty(SupporterLicenceInputError);

    partial void OnIsEditingSupporterLicenceChanged(bool value) => RaiseSupporterLicencePresentation();
    partial void OnSupporterLicenceInputErrorChanged(string value) => OnPropertyChanged(nameof(HasSupporterLicenceInputError));

    private void LoadSupporterLicenceState()
    {
        var code = Settings.AwooSupporterLicenceCode;
        var result = string.IsNullOrWhiteSpace(code)
            ? null
            : _supporterLicenceVerifier.Verify(code);
        SetSupporterState(result);
        SupporterLicenceInput = string.Empty;
        SupporterLicenceInputError = string.Empty;
        IsEditingSupporterLicence = false;
    }

    private void SetSupporterState(SupporterLicenceResult? result)
    {
        var valid = result?.IsAwooSupporter == true;
        var forcedUnlicensed = string.Equals(Environment.GetEnvironmentVariable("NOCTAXIS_FORCE_UNLICENSED"), "1", StringComparison.Ordinal);
        HasAwooSupporterEntitlement = valid && !forcedUnlicensed;
        SupporterLicenceState = result is null ? SupporterLicenceState.None
            : valid ? SupporterLicenceState.Valid
            : SupporterLicenceState.Invalid;
        _themeService?.SetSupporterEntitlement(HasAwooSupporterEntitlement);
        SupporterLicenceStatus = forcedUnlicensed && valid
            ? "Supporter licence disabled by development override."
            : result is null ? NoLicenceDetail
            : valid ? ValidDetail(result)
            : FriendlyFailure(result) + " Replace it with a valid licence to unlock the supporter themes.";
        OnPropertyChanged(nameof(HasAwooSupporterEntitlement));
        RaiseSupporterLicencePresentation();
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

    private void RaiseSupporterLicencePresentation()
    {
        OnPropertyChanged(nameof(SupporterLicenceState));
        OnPropertyChanged(nameof(SupporterLicenceStateLabel));
        OnPropertyChanged(nameof(SupporterLicenceStateGlyph));
        OnPropertyChanged(nameof(IsSupporterLicenceValid));
        OnPropertyChanged(nameof(IsSupporterLicenceInvalid));
        OnPropertyChanged(nameof(HasSupporterLicenceStateGlyph));
        OnPropertyChanged(nameof(IsSupporterLicenceStored));
        OnPropertyChanged(nameof(IsSupporterLicenceEditorVisible));
        OnPropertyChanged(nameof(CanStartSupporterLicenceReplacement));
        OnPropertyChanged(nameof(CanCancelSupporterLicenceReplacement));
        OnPropertyChanged(nameof(SupporterLicenceSubmitLabel));
    }

    // Only the licence type and issue date are shown: the key id and signature are internal.
    private static string ValidDetail(SupporterLicenceResult result) => result.IssuedAt is { } issued
        ? $"Awoo supporter licence, issued {issued.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}."
        : "Awoo supporter licence.";

    private static string FriendlyFailure(SupporterLicenceResult result) => result.Status switch
    {
        SupporterLicenceVerificationStatus.Valid => "This licence is not an Awoo supporter licence.",
        SupporterLicenceVerificationStatus.UnsupportedVersion => "This licence uses a newer format that this version of Noctaxis does not support.",
        SupporterLicenceVerificationStatus.UnknownKey => "This licence was signed with an unknown Awoo key. Updating Noctaxis may be required.",
        SupporterLicenceVerificationStatus.Malformed => "This licence code is not recognised.",
        _ => "This licence could not be verified."
    };

    /// <summary>Adds or replaces the licence. The candidate is verified first; a rejected one leaves the stored licence untouched.</summary>
    [RelayCommand]
    private async Task ActivateSupporterLicenceAsync()
    {
        var candidate = SupporterLicenceInput.Trim();
        var result = _supporterLicenceVerifier.Verify(candidate);
        if (!result.IsAwooSupporter)
        {
            SupporterLicenceInputError = FriendlyFailure(result) + (IsSupporterLicenceStored
                ? " Your current licence has not been changed."
                : string.Empty);
            return;
        }

        Settings = Settings with { AwooSupporterLicenceCode = candidate };
        SupporterLicenceInput = string.Empty;
        SupporterLicenceInputError = string.Empty;
        SetSupporterState(result);
        IsEditingSupporterLicence = false;
        OnPropertyChanged(nameof(Settings));
        await FlushAsync();
    }

    [RelayCommand]
    private void ReplaceSupporterLicence()
    {
        SupporterLicenceInput = string.Empty;
        SupporterLicenceInputError = string.Empty;
        IsEditingSupporterLicence = true;
    }

    [RelayCommand]
    private void CancelSupporterLicenceReplacement()
    {
        SupporterLicenceInput = string.Empty;
        SupporterLicenceInputError = string.Empty;
        IsEditingSupporterLicence = false;
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
