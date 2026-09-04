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

public sealed record WeatherFieldGroupViewModel(
    string Label,
    IReadOnlyList<WeatherFieldOptionViewModel> Fields);

public partial class CameraProfileEditorViewModel : ObservableObject
{
    private readonly Action<CameraProfileEditorViewModel> _remove;

    public CameraProfileEditorViewModel(CameraProfile profile, Action<CameraProfileEditorViewModel> remove)
    {
        Id = profile.Id;
        _displayName = profile.DisplayName;
        _sensorWidthMillimetres = profile.SensorWidthMillimetres;
        _sensorHeightMillimetres = profile.SensorHeightMillimetres;
        _manufacturer = profile.Manufacturer ?? string.Empty;
        _model = profile.Model ?? string.Empty;
        _remove = remove;
    }

    public string Id { get; }
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private double _sensorWidthMillimetres;
    [ObservableProperty] private double _sensorHeightMillimetres;
    [ObservableProperty] private string _manufacturer;
    [ObservableProperty] private string _model;
    public CameraProfile Profile => new(Id, DisplayName, SensorWidthMillimetres,
        SensorHeightMillimetres, EmptyToNull(Manufacturer), EmptyToNull(Model));
    public bool IsValid => Profile.IsValid;
    public string? ValidationMessage => Profile.ValidationMessage;

    [RelayCommand]
    private void Remove() => _remove(this);

    partial void OnDisplayNameChanged(string value) => NotifyValidation();
    partial void OnSensorWidthMillimetresChanged(double value) => NotifyValidation();
    partial void OnSensorHeightMillimetresChanged(double value) => NotifyValidation();

    private void NotifyValidation()
    {
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ValidationMessage));
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public partial class LensProfileEditorViewModel : ObservableObject
{
    private readonly Action<LensProfileEditorViewModel> _remove;

    public LensProfileEditorViewModel(LensProfile profile, Action<LensProfileEditorViewModel> remove)
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
    public string TypeText => Profile.IsValid ? Profile.IsPrime ? "Prime lens" : "Zoom lens" : string.Empty;

    [RelayCommand]
    private void Remove() => _remove(this);

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
