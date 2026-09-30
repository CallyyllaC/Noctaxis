using Noctaxis.Core.Localisation;
using Noctaxis.Desktop.Mapping;

namespace Noctaxis.Desktop.ViewModels;

internal static class LightPollutionPaletteHelp
{
    public static IReadOnlyList<ILightPollutionColorMap> Options { get; } = Array.AsReadOnly(
        new[] { "turbo", "viridis", "inferno", "magma", "cividis", "grayscale" }
            .Select(LightPollutionColorMaps.Resolve).ToArray());
    public static string Description(string? id) => id switch
    {
        "turbo" => Strings.Get("Settings_PlannerMap_Palette_Turbo_Description"),
        "viridis" => Strings.Get("Settings_PlannerMap_Palette_Viridis_Description"),
        "inferno" => Strings.Get("Settings_PlannerMap_Palette_Inferno_Description"),
        "magma" => Strings.Get("Settings_PlannerMap_Palette_Magma_Description"),
        "cividis" => Strings.Get("Settings_PlannerMap_Palette_Cividis_Description"),
        _ => Strings.Get("Settings_PlannerMap_Palette_Grayscale_Description")
    };
}
