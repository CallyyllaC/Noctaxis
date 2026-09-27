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
        // Celestial and chart data colours as one object for custom-drawn controls (see CelestialThemeTokens).
        next[CelestialThemeTokens.ColourSet] = CelestialColourSet.FromPalette(EffectiveTheme.Palette);
        // The saved-location preview is rendered from these theme roles (see Mapping/Preview).
        var light = EffectiveTheme.AppearanceMode == AppearanceMode.Light;
        var highContrastFamily = EffectiveTheme.Definition.Id == "builtin.highcontrast";
        next[LocationPreviewStyleKey] = Mapping.Preview.LocationPreviewStyle.FromPalette(EffectiveTheme.Palette, light, highContrastFamily);
        // Card scrim over the preview, from the card surface; enlarged text makes it fully opaque.
        var card = Color.Parse(EffectiveTheme.Palette[LocationCardTokens.Surface]);
        foreach (var (name, alpha) in new (string, byte)[] { ("CardOpaque", 255), ("CardScrim", 242), ("CardFade", 168), ("CardTransparent", 0) })
            next[name] = Color.FromArgb(Preferences.TextScale > 1.2 ? (byte)255 : alpha, card.R, card.G, card.B);
        // Location cards lift slightly off the page; High Contrast relies on its outlines instead of a shadow.
        next["LocationCardShadow"] = highContrastFamily ? default
            : BoxShadows.Parse(light ? "0 2 10 0 #1A1B2433" : "0 6 18 0 #40000000");
        foreach (var (name, size) in Typography)
            next[name] = size * Preferences.TextScale;
        // Only this text-bearing layout grows; this is not whole-window scaling.
        next["LocationCardFullWidth"] = Preferences.TextScale > 1.2;
        next["DatePickerMinimumWidth"] = 150 + 170 * (Preferences.TextScale - 1);
        next["TabItemHeaderFontSize"] = 20 * Preferences.TextScale;
        var highContrast = EffectiveTheme.Definition.Id == "builtin.highcontrast";
        // Hover and press thicken the border in every theme, so state never rests on a subtle fill change alone.
        double hover = 2, pressed = highContrast ? 3 : 2, selected = highContrast ? 4 : 2, focus = 3;
        next["StateHoverThickness"] = new Thickness(hover);
        next["StatePressedThickness"] = new Thickness(pressed);
        next["StateSelectedThickness"] = new Thickness(selected);
        next["StateFocusThickness"] = new Thickness(focus);
        // State emphasis must never change layout: a state that resized a control would move the region the
        // pointer is tested against, so hovering near an edge could toggle the state indefinitely. Controls
        // keep one border width in every state; the heavier state border is an inset ring (BoxShadow, which
        // takes no layout space) or, on location cards, an overlay. "Ring" keys sit inside a 1px border and
        // make up the rest of the state width; "Outline" keys are the full width, for borderless items.
        void Ring(string key, string role, double width)
        {
            var colour = Color.Parse(EffectiveTheme.Palette[role]);
            next[key] = new BoxShadows(new BoxShadow { IsInset = true, Spread = width, Color = colour });
        }
        Ring("StateHoverRing", "StrongBorder", hover - 1);
        Ring("StatePressedRing", "StrongBorder", pressed - 1);
        Ring("StateFocusRing", "FocusBorder", focus - 1);
        Ring("StateSelectedRing", "Accent", selected - 1);
        Ring("AccentHoverRing", "Accent", hover - 1);
        Ring("AccentPressedRing", "AccentPressed", pressed - 1);
        Ring("StateFocusOutline", "FocusBorder", focus);
        Ring("StateSelectedOutline", "Accent", selected);
        next["StateHoverInset"] = new Thickness(hover - 1);
        next["StateSelectedInset"] = new Thickness(selected - 1);
        next["StateFocusInset"] = new Thickness(focus - 1);
        FluentResourceBridge.Apply(next);
        next["LocationCardHeight"] = 240 + 240 * (Preferences.TextScale - 1);
        _application.RequestedThemeVariant = EffectiveTheme.AppearanceMode == AppearanceMode.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        var dictionaries = _application.Resources.MergedDictionaries;
        if (_resources is null) dictionaries.Add(next);
        else dictionaries[dictionaries.IndexOf(_resources)] = next;
        _resources = next;
    }
    /// <summary>Resource key of the <see cref="Mapping.Preview.LocationPreviewStyle"/> for the current theme.</summary>
    public const string LocationPreviewStyleKey = "LocationPreviewStyle";
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
