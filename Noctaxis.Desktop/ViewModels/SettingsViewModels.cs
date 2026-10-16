using Noctaxis.Core.Localisation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.ViewModels;

public partial class WeatherFieldOptionViewModel(WeatherField field, string label, bool isEnabled) : ObservableObject
{
    public WeatherField Field { get; } = field;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isEnabled = isEnabled;
}

/// <summary>
/// One weather topic in Settings — Clouds, Wind, Twilight — standing for the values behind it: on while any of
/// them is, and turning it on or off turns all of them.
/// </summary>
public sealed partial class WeatherTopicOptionViewModel : ObservableObject
{
    public WeatherTopicOptionViewModel(string label, IReadOnlyList<WeatherFieldOptionViewModel> fields)
    {
        Label = label;
        Fields = fields;
        foreach (var field in fields)
            field.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WeatherFieldOptionViewModel.IsEnabled)) OnPropertyChanged(nameof(IsEnabled)); };
    }

    public string Label { get; }
    public IReadOnlyList<WeatherFieldOptionViewModel> Fields { get; }

    public bool IsEnabled
    {
        get => Fields.Any(option => option.IsEnabled);
        set { foreach (var option in Fields) option.IsEnabled = value; }
    }
}

public sealed record WeatherTopicGroupViewModel(
    string Label,
    IReadOnlyList<WeatherTopicOptionViewModel> Topics);

public partial class CameraProfileEditorViewModel : ObservableObject
{
    private readonly Func<CameraProfileEditorViewModel, Task> _remove;

    public CameraProfileEditorViewModel(CameraProfile profile, Func<CameraProfileEditorViewModel, Task> remove)
    {
        Id = profile.Id;
        _displayName = profile.DisplayName;
        _sensorWidthMillimetres = profile.SensorWidthMillimetres;
        _sensorHeightMillimetres = profile.SensorHeightMillimetres;
        _manufacturer = profile.Manufacturer ?? string.Empty;
        _model = profile.Model ?? string.Empty;
        _resolutionWidthPixels = profile.ResolutionWidthPixels;
        _resolutionHeightPixels = profile.ResolutionHeightPixels;
        _remove = remove;
    }

    public string Id { get; }
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private double _sensorWidthMillimetres;
    [ObservableProperty] private double _sensorHeightMillimetres;
    [ObservableProperty] private string _manufacturer;
    [ObservableProperty] private string _model;
    /// <summary>The optional resolution, as typed: whole pixels, and either dimension may be empty.</summary>
    [ObservableProperty] private decimal? _resolutionWidthPixels;
    [ObservableProperty] private decimal? _resolutionHeightPixels;

    /// <summary>
    /// The profile as saved. The resolution goes with it only when both dimensions are whole, positive pixel
    /// counts; one dimension alone stays in the editor and is saved as no resolution, so it never makes a
    /// half-described sensor.
    /// </summary>
    public CameraProfile Profile => Pixels(ResolutionWidthPixels) is { } width && Pixels(ResolutionHeightPixels) is { } height
        ? Base with { ResolutionWidthPixels = width, ResolutionHeightPixels = height }
        : Base;
    private CameraProfile Base => new(Id, DisplayName, SensorWidthMillimetres,
        SensorHeightMillimetres, EmptyToNull(Manufacturer), EmptyToNull(Model));
    public bool IsValid => Profile.IsValid;
    public string? ValidationMessage => Profile.ValidationMessage;

    /// <summary>Shown while only one dimension of the resolution is filled in.</summary>
    public string? ResolutionHint => (Pixels(ResolutionWidthPixels) is null) != (Pixels(ResolutionHeightPixels) is null)
        ? Strings.Get("Settings_Equipment_ResolutionIncomplete") : null;

    /// <summary>The pixel pitch the sensor's size and resolution give, read-only; empty without a resolution.</summary>
    public string PixelPitchText => Profile.PixelPitchMicrometres is { } pitch
        ? Strings.Format("Settings_Equipment_PixelPitch_Format", pitch) : string.Empty;

    /// <summary>The largest resolution dimension accepted.</summary>
    public const int MaximumResolutionPixels = 1_000_000;

    private static int? Pixels(decimal? value) =>
        value is { } pixels && pixels >= 1 && pixels <= MaximumResolutionPixels && decimal.Truncate(pixels) == pixels ? (int)pixels : null;

    /// <summary>Asks, then removes this profile if confirmed (see MainViewModel).</summary>
    [RelayCommand]
    private Task Remove() => _remove(this);

    partial void OnDisplayNameChanged(string value) => NotifyValidation();
    partial void OnSensorWidthMillimetresChanged(double value) => NotifyValidation();
    partial void OnSensorHeightMillimetresChanged(double value) => NotifyValidation();
    partial void OnResolutionWidthPixelsChanged(decimal? value) => NotifyValidation();
    partial void OnResolutionHeightPixelsChanged(decimal? value) => NotifyValidation();

    private void NotifyValidation()
    {
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(ResolutionHint));
        OnPropertyChanged(nameof(PixelPitchText));
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public partial class LensProfileEditorViewModel : ObservableObject
{
    private readonly Func<LensProfileEditorViewModel, Task> _remove;

    public LensProfileEditorViewModel(LensProfile profile, Func<LensProfileEditorViewModel, Task> remove)
    {
        Id = profile.Id;
        _displayName = profile.DisplayName;
        _minimumFocalLengthMillimetres = profile.MinimumFocalLengthMillimetres;
        _maximumFocalLengthMillimetres = profile.MaximumFocalLengthMillimetres;
        _manufacturer = profile.Manufacturer ?? string.Empty;
        _model = profile.Model ?? string.Empty;
        _remove = remove;
    }

    public string Id { get; }
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private double _minimumFocalLengthMillimetres;
    [ObservableProperty] private double _maximumFocalLengthMillimetres;
    [ObservableProperty] private string _manufacturer;
    [ObservableProperty] private string _model;
    public LensProfile Profile => new(Id, DisplayName, MinimumFocalLengthMillimetres,
        MaximumFocalLengthMillimetres, EmptyToNull(Manufacturer), EmptyToNull(Model));
    public bool IsValid => Profile.IsValid;
    public string? ValidationMessage => Profile.ValidationMessage;
    public string TypeText => Profile.IsValid ? Strings.Get(Profile.IsPrime ? "Settings_Equipment_PrimeLens" : "Settings_Equipment_ZoomLens") : string.Empty;

    /// <summary>Asks, then removes this profile if confirmed (see MainViewModel).</summary>
    [RelayCommand]
    private Task Remove() => _remove(this);

    partial void OnDisplayNameChanged(string value) => NotifyValidation();
    partial void OnMinimumFocalLengthMillimetresChanged(double value) => NotifyValidation();
    partial void OnMaximumFocalLengthMillimetresChanged(double value) => NotifyValidation();

    private void NotifyValidation()
    {
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(TypeText));
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
