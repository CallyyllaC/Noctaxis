using Avalonia.Media;
using Noctaxis.Core.Domain;
namespace Noctaxis.Desktop.Themes;

public sealed record ThemePaletteDefinition(IReadOnlyDictionary<string, string> Palette,
    IReadOnlyDictionary<ColourVisionMode, IReadOnlyDictionary<string, string>> Variants);
public sealed record ThemeDefinition(string Id, string DisplayName, bool IsSupporter,
    ThemePaletteDefinition Dark, ThemePaletteDefinition? Light,
    string? Description = null, int SchemaVersion = 1);
public sealed record ResolvedTheme(ThemeDefinition Definition, ColourVisionMode Mode,
    IReadOnlyDictionary<string, string> Palette, string? FallbackReason)
{
    public AppearanceMode AppearanceMode { get; init; }
    public IReadOnlyList<string> InheritedTokens { get; init; } = [];
}
public sealed class ThemeCatalogue
{
    public const string DefaultId = "builtin.noctaxis";
    public static IReadOnlySet<ColourVisionMode> AllModes { get; } = Enum.GetValues<ColourVisionMode>().ToHashSet();
    private readonly IReadOnlyList<ThemeDefinition> _themes;
    private readonly Func<string, bool> _isAvailable;
    public ThemeCatalogue(IEnumerable<ThemeDefinition>? extraThemes = null, Func<string, bool>? isAvailable = null)
    {
        _themes = BuiltInPalettes.Definitions.Concat(extraThemes ?? []).ToArray();
        if (_themes.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != _themes.Count) throw new ArgumentException("Theme family IDs must be unique.");
        _isAvailable = isAvailable ?? (_ => false);
    }
    public IReadOnlyList<ThemeDefinition> Themes => _themes;
    public bool IsAvailable(ThemeDefinition theme) => !theme.IsSupporter || _isAvailable(theme.Id);
    public ResolvedTheme Resolve(AppearancePreferences preferences, bool? systemIsDark = null)
    {
        var p = preferences.Normalised();
        var reasons = new List<string>();
        var definition = _themes.FirstOrDefault(t => t.Id == p.ThemeFamilyId && IsAvailable(t));
        if (definition is null) { definition = _themes[0]; reasons.Add($"Family '{p.ThemeFamilyId}' unknown or unavailable; using Noctaxis."); }
        var appearance = p.AppearanceMode == AppearanceMode.System ? systemIsDark == false ? AppearanceMode.Light : AppearanceMode.Dark : p.AppearanceMode;
        var source = appearance == AppearanceMode.Light ? definition.Light : definition.Dark;
        if (source is null) { source = definition.Dark; appearance = AppearanceMode.Dark; reasons.Add("Missing Light palette; using family Dark."); }
        var mode = p.ColourVisionMode;
        if (!source.Variants.TryGetValue(mode, out var selected))
        {
            mode = ColourVisionMode.None;
            selected = source.Variants.GetValueOrDefault(mode) ?? source.Palette;
            reasons.Add("Missing colour-vision variant; using None.");
        }
        var palette = new Dictionary<string, string>(BuiltInPalettes.Definitions[0].Dark.Palette);
        foreach (var (key, value) in selected)
            if (Color.TryParse(value, out _)) palette[key] = value;
        var inherited = BuiltInPalettes.RequiredTokens.Where(key => !selected.TryGetValue(key, out var c) || !Color.TryParse(c, out _)).ToArray();
        if (inherited.Length > 0) reasons.Add("Missing or invalid tokens inherited from Noctaxis Dark: " + string.Join(", ", inherited));
        return new(definition, mode, palette, reasons.Count == 0 ? null : string.Join(" ", reasons)) { AppearanceMode = appearance, InheritedTokens = inherited };
    }
}
public static class ThemeDefinitionValidator
{
    public static IReadOnlyList<string> Validate(ThemeDefinition theme)
    {
        var errors = new List<string>();
        foreach (var (name, source) in new[] { ("Dark", theme.Dark), ("Light", theme.Light) })
        {
            if (source is null) { errors.Add("Missing " + name); continue; }
            foreach (var mode in ThemeCatalogue.AllModes)
            {
                if (!source.Variants.TryGetValue(mode, out var palette)) { errors.Add($"Missing {name}/{mode}"); continue; }
                errors.AddRange(BuiltInPalettes.RequiredTokens.Where(t => !palette.TryGetValue(t, out var c) || !Color.TryParse(c, out _)).Select(t => $"Missing or invalid {name}/{mode}/{t}"));
            }
        }
        return errors;
    }
}
