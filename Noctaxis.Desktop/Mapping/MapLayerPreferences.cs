namespace Noctaxis.Desktop.Mapping;

/// <summary>Storage-neutral preference DTO. No availability, active state, renderer or diagnostics.</summary>
public sealed record MapLayerPreference(string LayerId, bool? IsVisible = null, double? Opacity = null);

public static class MapLayerPreferences
{
    public static IReadOnlyList<MapLayerPreference> Capture(MapLayerStateController runtime) =>
        runtime.Composition.Layers.Where(x => x.PersistedPreferences != MapLayerCapabilities.None)
            .Select(x => new MapLayerPreference(x.Id,
                x.PersistedPreferences.HasFlag(MapLayerCapabilities.Visibility) ? runtime[x.Id].IsVisible : null,
                x.PersistedPreferences.HasFlag(MapLayerCapabilities.Opacity) ? runtime[x.Id].Opacity : null)).ToArray();

    /// <summary>Unknown IDs and invalid stored values are ignored. Restore never changes transient state.</summary>
    public static void Restore(MapLayerStateController runtime, IEnumerable<MapLayerPreference> preferences)
    {
        var definitions = runtime.Composition.Layers.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var preference in preferences)
        {
            if (preference is null || string.IsNullOrWhiteSpace(preference.LayerId) ||
                !definitions.TryGetValue(preference.LayerId, out var definition)) continue;
            if (definition.PersistedPreferences.HasFlag(MapLayerCapabilities.Visibility) && preference.IsVisible is { } visible)
                runtime.SetVisibility(definition.Id, visible);
            if (definition.PersistedPreferences.HasFlag(MapLayerCapabilities.Opacity) && preference.Opacity is { } opacity &&
                double.IsFinite(opacity) && opacity >= 0 && opacity <= 1)
                runtime.SetOpacity(definition.Id, opacity);
        }
    }
}
