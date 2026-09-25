using SkiaSharp;

namespace Noctaxis.Desktop.Mapping;

public sealed class LightPollutionDisplayScale
{
    public const string Version = "lorenz-log-stops-v1";
    private static readonly double[] Stops = [.01, .06, .11, .19, .33, .58, 1, 1.73, 3, 5.20, 9, 15.59, 27, 46.77];
    public double Normalize(double lpi)
    {
        if (double.IsNaN(lpi)) throw new ArgumentOutOfRangeException(nameof(lpi));
        if (lpi <= Stops[0]) return 0;
        if (lpi >= Stops[^1]) return 1;
        var upper = 1;
        while (lpi > Stops[upper]) upper++;
        return (upper - 1 + Math.Log(lpi / Stops[upper - 1]) / Math.Log(Stops[upper] / Stops[upper - 1])) / (Stops.Length - 1);
    }
}

public interface ILightPollutionColorMap
{
    string Id { get; }
    string DisplayName { get; }
    int Version => 1;
    SKColor Map(double normalized);
}
public sealed class GrayscaleLightPollutionColorMap : ILightPollutionColorMap
{
    public string Id => "grayscale";
    public string DisplayName => "Grayscale";
    public SKColor Map(double normalized)
    {
        var value = (byte)Math.Round((double.IsNaN(normalized) ? 0 : Math.Clamp(normalized, 0, 1)) * 255);
        return new(value, value, value, 255);
    }
}
