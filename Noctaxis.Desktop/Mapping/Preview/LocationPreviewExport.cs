using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Themes;

namespace Noctaxis.Desktop.Mapping.Preview;

/// <summary>One themed rendering of a preview: which theme it was made for, and the PNG.</summary>
public sealed record LocationPreviewRendition(string ThemeId, AppearanceMode Appearance, ColourVisionMode ColourVision, byte[] Png)
{
    /// <summary>Stable file name, e.g. <c>noctaxis-dark-none.png</c>.</summary>
    public string FileName => $"{ThemeId.Split('.')[^1]}-{Appearance}-{ColourVision}.png".ToLowerInvariant();
}

/// <summary>
/// Renders a preview scene across themes without any window or screen capture: the base for batch
/// export and visual comparison. Palettes come from the production <see cref="ThemeCatalogue"/>.
/// </summary>
public static class LocationPreviewExport
{
    public static LocationPreviewStyle StyleFor(ThemeCatalogue catalogue, string themeId, AppearanceMode appearance, ColourVisionMode colourVision)
    {
        var resolved = catalogue.Resolve(new(themeId, colourVision, AppearanceMode: appearance));
        return LocationPreviewStyle.FromPalette(resolved.Palette, resolved.AppearanceMode == AppearanceMode.Light,
            resolved.Definition.Id == "builtin.highcontrast");
    }

    /// <summary>Every family × Light/Dark × colour-vision mode the catalogue offers.</summary>
    public static IEnumerable<LocationPreviewRendition> RenderMatrix(LocationPreviewScene scene, ThemeCatalogue catalogue,
        int width, int height)
    {
        foreach (var theme in catalogue.Themes)
        foreach (var appearance in new[] { AppearanceMode.Dark, AppearanceMode.Light })
        foreach (var colourVision in Enum.GetValues<ColourVisionMode>())
            yield return new(theme.Id, appearance, colourVision,
                LocationPreviewRenderer.RenderPng(scene, StyleFor(catalogue, theme.Id, appearance, colourVision), width, height));
    }

    /// <summary>Writes <see cref="RenderMatrix"/> into <paramref name="directory"/> and returns the written paths.</summary>
    public static IReadOnlyList<string> WriteMatrix(LocationPreviewScene scene, ThemeCatalogue catalogue, string directory,
        int width = 512, int height = 280)
    {
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        foreach (var rendition in RenderMatrix(scene, catalogue, width, height))
        {
            var path = Path.Combine(directory, rendition.FileName);
            File.WriteAllBytes(path, rendition.Png);
            paths.Add(path);
        }
        return paths;
    }
}
