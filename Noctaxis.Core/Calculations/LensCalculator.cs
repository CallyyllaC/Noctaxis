using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Calculations;

public interface ILensCalculator
{
    FieldOfView Calculate(LensConfiguration configuration);
}

public sealed class LensCalculator : ILensCalculator
{
    public FieldOfView Calculate(LensConfiguration configuration)
    {
        if (configuration.SensorWidthMillimetres <= 0 || configuration.SensorHeightMillimetres <= 0)
            throw new ArgumentOutOfRangeException(nameof(configuration), "Sensor dimensions must be positive.");
        if (configuration.FocalLengthMillimetres <= 0 || configuration.FramingMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(configuration), "Focal length and framing multiplier must be positive.");

        var width = configuration.SensorWidthMillimetres * configuration.FramingMultiplier;
        var height = configuration.SensorHeightMillimetres * configuration.FramingMultiplier;
        if (configuration.Orientation == CameraOrientation.Portrait) (width, height) = (height, width);
        var diagonal = Math.Sqrt(width * width + height * height);
        return new FieldOfView(Fov(width, configuration.FocalLengthMillimetres), Fov(height, configuration.FocalLengthMillimetres), Fov(diagonal, configuration.FocalLengthMillimetres));
    }

    private static double Fov(double dimension, double focalLength) =>
        2 * Math.Atan(dimension / (2 * focalLength)) * Angles.RadiansToDegrees;
}
