namespace Noctaxis.Desktop.Controls;

/// <summary>
/// How the Camera frame places an angle: as the camera's own lens does. A rectilinear lens images a direction
/// at the tangent of its angle from the lens's axis, so the frame is a perspective (gnomonic) view, its edges at
/// half the field of view. One rule for the picture, its reference lines and the bearing and pitch scales over
/// it, so a tick is where its angle is drawn.
/// </summary>
public static class CameraFrameProjection
{
    private const double Radians = Math.PI / 180;

    /// <summary>
    /// Where an angle <paramref name="offsetDegrees"/> from the axis falls across a frame spanning
    /// <paramref name="fieldOfViewDegrees"/>: −1 at one edge, 0 at the centre, 1 at the other.
    /// </summary>
    public static double Fraction(double offsetDegrees, double fieldOfViewDegrees) =>
        Math.Tan(Math.Clamp(offsetDegrees, -89.9, 89.9) * Radians) / Math.Tan(fieldOfViewDegrees / 2 * Radians);

    /// <summary>
    /// Where a direction is imaged: <paramref name="bearingOffsetDegrees"/> from the camera's bearing and at
    /// <paramref name="altitudeDegrees"/> (absolute), for a camera pitched <paramref name="pitchDegrees"/>, as
    /// fractions across (−1 to 1, rightwards) and up (−1 to 1, upwards) the frame; null when it is behind the
    /// camera. The inverse of <see cref="Direction"/>, and the one rule the skyline, the frame's reference
    /// lines and both scales are placed by.
    /// </summary>
    public static (double Across, double Up)? Project(double bearingOffsetDegrees, double altitudeDegrees,
        double horizontalFieldOfView, double verticalFieldOfView, double pitchDegrees) =>
        Noctaxis.Core.Calculations.CameraFrameGeometry.Project(bearingOffsetDegrees, altitudeDegrees,
            horizontalFieldOfView, verticalFieldOfView, pitchDegrees);

    /// <summary>
    /// The direction imaged at a point of the frame, given as fractions across it (−1 to 1, rightwards) and up
    /// it (−1 to 1, upwards), for a camera pitched <paramref name="pitchDegrees"/>: its bearing from the camera's
    /// own, and its altitude. Exact, not the small-angle approximation, so a pitched camera's corners are right.
    /// </summary>
    public static (double BearingOffset, double Altitude) Direction(double across, double up,
        double horizontalFieldOfView, double verticalFieldOfView, double pitchDegrees)
    {
        var u = across * Math.Tan(horizontalFieldOfView / 2 * Radians);
        var w = up * Math.Tan(verticalFieldOfView / 2 * Radians);
        var (sin, cos) = Math.SinCos(pitchDegrees * Radians);
        // The camera looks along (0, cos p, sin p); right is (1, 0, 0); up is (0, −sin p, cos p).
        var east = u;
        var north = cos - w * sin;
        var zenith = sin + w * cos;
        return (Math.Atan2(east, north) / Radians, Math.Atan2(zenith, Math.Sqrt(east * east + north * north)) / Radians);
    }
}
