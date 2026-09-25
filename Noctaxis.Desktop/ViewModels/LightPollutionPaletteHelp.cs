using Noctaxis.Desktop.Mapping;

namespace Noctaxis.Desktop.ViewModels;

internal static class LightPollutionPaletteHelp
{
    public static IReadOnlyList<ILightPollutionColorMap> Options { get; } = Array.AsReadOnly(
        new[] { "turbo", "viridis", "inferno", "magma", "cividis", "grayscale" }
            .Select(LightPollutionColorMaps.Resolve).ToArray());
    public static string Description(string? id) => id switch
    {
        "turbo" => "Vivid colours with strong visual separation. Best for quickly spotting darker and brighter regions.",
        "viridis" => "Balanced colours designed for clear quantitative comparison.",
        "inferno" => "High-contrast purple, red and yellow palette with a heat-like appearance.",
        "magma" => "Softer purple, magenta and warm-yellow dark-to-light palette.",
        "cividis" => "Blue-to-yellow palette designed to remain useful for many users with colour-vision deficiencies.",
        _ => "Neutral black-to-white representation."
    };
}
