namespace Noctaxis.Core.Calculations;

/// <summary>
/// Where a direction falls in the camera's frame, as a rectilinear lens images it: a perspective (gnomonic)
/// view whose edges are at half the field of view. The Camera frame draws by this rule, and the shot-viability
/// score places the target by it, so both always agree.
/// </summary>
public static class CameraFrameGeometry
{
    private const double Radians = Math.PI / 180;

    /// <summary>
    /// Where a direction is imaged: <paramref name="bearingOffsetDegrees"/> from the camera's bearing and at
    /// <paramref name="altitudeDegrees"/> (absolute), for a camera pitched <paramref name="pitchDegrees"/>, as
    /// fractions across (−1 to 1, rightwards) and up (−1 to 1, upwards) the frame; null when it is behind the
    /// camera.
    /// </summary>
    public static (double Across, double Up)? Project(double bearingOffsetDegrees, double altitudeDegrees,
        double horizontalFieldOfView, double verticalFieldOfView, double pitchDegrees)
    {
        var (sinA, cosA) = Math.SinCos(altitudeDegrees * Radians);
        var (sinB, cosB) = Math.SinCos(bearingOffsetDegrees * Radians);
        var (sinP, cosP) = Math.SinCos(pitchDegrees * Radians);
        var (east, north, zenith) = (sinB * cosA, cosB * cosA, sinA);
        var forward = north * cosP + zenith * sinP;
        if (forward <= 1e-9) return null;
        var right = east;
        var up = -north * sinP + zenith * cosP;
        return (right / forward / Math.Tan(horizontalFieldOfView / 2 * Radians), up / forward / Math.Tan(verticalFieldOfView / 2 * Radians));
    }
}
