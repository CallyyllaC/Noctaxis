using CommunityToolkit.Mvvm.ComponentModel;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.ViewModels;

public partial class WeatherFieldOptionViewModel(WeatherField field, string label, bool isEnabled) : ObservableObject
{
    public WeatherField Field { get; } = field;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isEnabled = isEnabled;
}
