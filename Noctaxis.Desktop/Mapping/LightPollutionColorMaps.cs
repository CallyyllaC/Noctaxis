using SkiaSharp;

namespace Noctaxis.Desktop.Mapping;

/// <summary>Immutable singletons; resolution happens once per preference change, never per pixel.</summary>
public static class LightPollutionColorMaps
{
    public static ILightPollutionColorMap Grayscale { get; } = new GrayscaleLightPollutionColorMap();
    public static IReadOnlyList<ILightPollutionColorMap> All { get; } = Array.AsReadOnly<ILightPollutionColorMap>([
        Grayscale,
        new LutLightPollutionColorMap("turbo", "Turbo", LightPollutionPaletteData.Turbo),
        new LutLightPollutionColorMap("viridis", "Viridis", LightPollutionPaletteData.Viridis),
        new LutLightPollutionColorMap("inferno", "Inferno", LightPollutionPaletteData.Inferno),
        new LutLightPollutionColorMap("magma", "Magma", LightPollutionPaletteData.Magma),
        new LutLightPollutionColorMap("cividis", "Cividis", LightPollutionPaletteData.Cividis)
    ]);
    public static ILightPollutionColorMap Resolve(string? id)
    {
        foreach (var palette in All) if (palette.Id == id) return palette;
        return Grayscale;
    }
}

internal sealed class LutLightPollutionColorMap(string id, string displayName, double[] rgb) : ILightPollutionColorMap
{
    public string Id => id;
    public string DisplayName => displayName;
    public SKColor Map(double normalized)
    {
        var position = (double.IsNaN(normalized) ? 0 : Math.Clamp(normalized, 0, 1)) * 255;
        var lower = (int)position;
        var upper = Math.Min(lower + 1, 255);
        var fraction = position - lower;
        return new(Channel(0), Channel(1), Channel(2), 255);
        byte Channel(int channel) => (byte)Math.Round(255 *
            (rgb[lower * 3 + channel] + (rgb[upper * 3 + channel] - rgb[lower * 3 + channel]) * fraction));
    }
}
