using Avalonia.Platform;
namespace Noctaxis.Desktop.Themes;

/// <summary>Small platform preference boundary, also usable by deterministic headless hosts.</summary>
public interface IAppearancePlatform
{
    bool? IsDark { get; }
    event EventHandler? Changed;
}
internal sealed class AppearancePlatform(IPlatformSettings? settings) : IAppearancePlatform
{
    public bool? IsDark => settings is null ? null : settings.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
    private EventHandler? _changed;
    public event EventHandler? Changed
    {
        add { if (_changed is null && settings is not null) settings.ColorValuesChanged += Forward; _changed += value; }
        remove { _changed -= value; if (_changed is null && settings is not null) settings.ColorValuesChanged -= Forward; }
    }
    private void Forward(object? sender, PlatformColorValues values) => _changed?.Invoke(this, EventArgs.Empty);
}
