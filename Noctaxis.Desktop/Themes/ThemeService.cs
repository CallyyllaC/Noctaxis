using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Supporter;

namespace Noctaxis.Desktop.Themes;

/// <summary>One replaceable resource dictionary and one platform subscription for the application lifetime.</summary>
public sealed class ThemeService : IDisposable
{
    private readonly Application _application;
    private readonly IAppearancePlatform _platform;
    private readonly Action<string> _log;
    private ResourceDictionary? _resources;
    private bool _disposed;
    private int _layoutRevision;
    public ThemeCatalogue Catalogue { get; }
    public AwooSupporterEntitlement SupporterEntitlement { get; }
    public AppearancePreferences Preferences { get; private set; } = new();
    public ResolvedTheme EffectiveTheme { get; private set; } = null!;
    public int ResourceCount => _resources?.Count ?? 0;
    public ThemeService(Application application, ThemeCatalogue? catalogue = null, Action<string>? log = null, IAppearancePlatform? platform = null,
        AwooSupporterEntitlement? supporterEntitlement = null)
    {
        _application = application;
        SupporterEntitlement = supporterEntitlement ?? new();
        Catalogue = catalogue ?? new(isAvailable: _ => SupporterEntitlement.HasAwooSupporterEntitlement);
        _log = log ?? (message => System.Diagnostics.Trace.TraceWarning(message));
        _platform = platform ?? new AppearancePlatform(application.PlatformSettings);
        _platform.Changed += PlatformChanged;
        Apply(new());
    }

    public void SetSupporterEntitlement(bool enabled)
    {
        SupporterEntitlement.Set(enabled);
        Apply(Preferences);
    }

    public void Apply(AppearancePreferences preferences)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Dispatcher.UIThread.VerifyAccess();
        var previousScale = Preferences.TextScale;
        Preferences = preferences.Normalised();
        bool? dark = null;
        try { dark = _platform.IsDark; }
        catch (NotSupportedException) { _log("Platform appearance unavailable; using Dark."); }
        EffectiveTheme = Catalogue.Resolve(Preferences, dark);
        if (EffectiveTheme.FallbackReason is { } reason) _log(reason);
        var next = new ResourceDictionary();
        if (_resources is null || previousScale != Preferences.TextScale) _layoutRevision++;
        next["AppearanceLayoutRevision"] = _layoutRevision;
        foreach (var (key, value) in EffectiveTheme.Palette)
        {
            var colour = Color.Parse(value);
            next[key] = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(colour);
            next[key + "Color"] = colour;
        }
        var card = Color.Parse(EffectiveTheme.Palette["CardBackground"]);
        foreach (var (name, alpha) in new (string, byte)[] { ("CardOpaque", 255), ("CardScrim", 242), ("CardFade", 168), ("CardTransparent", 0) })
            next[name] = Color.FromArgb(Preferences.TextScale > 1.2 ? (byte)255 : alpha, card.R, card.G, card.B);
        foreach (var (name, size) in Typography)
            next[name] = size * Preferences.TextScale;
        // Only this text-bearing layout grows; this is not whole-window scaling.
        next["LocationCardFullWidth"] = Preferences.TextScale > 1.2;
        next["DatePickerMinimumWidth"] = 150 + 170 * (Preferences.TextScale - 1);
        next["TabItemHeaderFontSize"] = 20 * Preferences.TextScale;
        var highContrast = EffectiveTheme.Definition.Id == "builtin.highcontrast";
        next["StateHoverThickness"] = new Thickness(highContrast ? 2 : 1);
        next["StatePressedThickness"] = new Thickness(highContrast ? 3 : 1);
        next["StateSelectedThickness"] = new Thickness(highContrast ? 4 : 2);
        next["StateFocusThickness"] = new Thickness(3);
        FluentResourceBridge.Apply(next);
        next["LocationCardHeight"] = 240 + 240 * (Preferences.TextScale - 1);
        _application.RequestedThemeVariant = EffectiveTheme.AppearanceMode == AppearanceMode.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        var dictionaries = _application.Resources.MergedDictionaries;
        if (_resources is null) dictionaries.Add(next);
        else dictionaries[dictionaries.IndexOf(_resources)] = next;
        _resources = next;
    }
    public static IReadOnlyDictionary<string, double> Typography { get; } = new Dictionary<string, double>
    {
        ["TextCaption"] = 10, ["TextSmall"] = 11, ["TextLabel"] = 12,
        ["TextBody"] = 14, ["TextBodyStrong"] = 16, ["TextSubheading"] = 18,
        ["TextHeading"] = 22, ["TextNumeric"] = 24, ["TextTitle"] = 28, ["TextDisplay"] = 32
    };
    private void PlatformChanged(object? sender, EventArgs e)
    {
        if (Preferences.AppearanceMode != AppearanceMode.System) return;
        Dispatcher.UIThread.Post(() => { if (!_disposed && Preferences.AppearanceMode == AppearanceMode.System) Apply(Preferences); });
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _platform.Changed -= PlatformChanged;
        if (_resources is not null) _application.Resources.MergedDictionaries.Remove(_resources);
    }
}
