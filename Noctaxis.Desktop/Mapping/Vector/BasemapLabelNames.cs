namespace Noctaxis.Desktop.Mapping.Vector;

/// <summary>
/// Chooses the name a map label shows. OpenMapTiles-schema tiles (OpenFreeMap) carry every name of a
/// feature as properties of that feature: <c>name</c> (the local name, as mapped in OpenStreetMap),
/// <c>name:&lt;language&gt;</c> for some seventy languages (<c>name:en</c>, <c>name:de</c>,
/// <c>name:zh-Hans</c> …), the older <c>name_en</c> and <c>name_de</c>, <c>name_int</c>, and the
/// script variants <c>name:latin</c> and <c>name:nonlatin</c>. The tile request has no language; the
/// choice is made here, when a tile is drawn, so one downloaded tile serves every language.
/// </summary>
public static class BasemapLabelNames
{
    // Names that are usable when there is neither a name in the wanted language nor a local name.
    private static readonly string[] LastResort = ["name:latin", "name_int", "name:nonlatin", "name_en"];

    /// <summary>
    /// The name to draw for <paramref name="language"/> (a culture identifier such as <c>en-GB</c>):
    /// <list type="number">
    /// <item>the name in that language, most specific first (<c>name:en-GB</c>, <c>name:en</c>,
    /// then the older <c>name_en</c>);</item>
    /// <item>the local name, <c>name</c>;</item>
    /// <item>any other name the feature has: <c>name:latin</c>, <c>name_int</c>,
    /// <c>name:nonlatin</c>, <c>name_en</c>, then the first remaining <c>name:*</c> or
    /// <c>name_*</c> in key order.</item>
    /// </list>
    /// Blank values are skipped at every step, so a missing translation never removes a label. Null
    /// only when the feature has no name at all.
    /// </summary>
    public static string? Resolve(VectorTileFeature feature, string? language)
    {
        ArgumentNullException.ThrowIfNull(feature);
        foreach (var key in LanguageKeys(language))
            if (Usable(feature, key) is { } translated) return translated;
        if (Usable(feature, "name") is { } local) return local;
        foreach (var key in LastResort)
            if (Usable(feature, key) is { } fallback) return fallback;
        foreach (var key in feature.Properties.Keys
                     .Where(key => key.StartsWith("name:", StringComparison.Ordinal) || key.StartsWith("name_", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
            if (Usable(feature, key) is { } any) return any;
        return null;
    }

    /// <summary>The property keys that hold a name in a language, most specific first.</summary>
    internal static IEnumerable<string> LanguageKeys(string? language)
    {
        var tag = language?.Trim();
        if (string.IsNullOrEmpty(tag)) yield break;
        // en-GB, then en: a regional or script variant falls back to its language.
        for (var current = tag; current.Length > 0;)
        {
            yield return "name:" + current;
            var cut = current.LastIndexOf('-');
            if (cut <= 0) { yield return "name_" + current; break; }
            current = current[..cut];
        }
    }

    private static string? Usable(VectorTileFeature feature, string key) =>
        feature.Text(key) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;
}
