using Avalonia.Media;

namespace Noctaxis.Desktop.Controls;

/// <summary>Colour treatment for environmental camera-cone regions.</summary>
public static class CameraOverlayColourPolicy
{
    public static Color Grayscale(Color colour, byte? alpha = null)
    {
        var luminance = (byte)Math.Clamp((int)Math.Round(
            colour.R * 0.2126 + colour.G * 0.7152 + colour.B * 0.0722), 0, byte.MaxValue);
        return Color.FromArgb(alpha ?? colour.A, luminance, luminance, luminance);
    }
}
