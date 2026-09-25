namespace Noctaxis.Desktop.Controls;

/// <summary>Draw-time contrast only; never used to construct the skyline mesh.</summary>
public static class TerrainFanTone
{
    public const double ReferenceAngleDegrees = 10;
    public const double ContrastExponent = .75;

    public static double Strength(double apparentAltitudeDegrees) =>
        !double.IsFinite(apparentAltitudeDegrees) || apparentAltitudeDegrees <= 0 ? 0 :
        Math.Pow(Math.Clamp(apparentAltitudeDegrees / ReferenceAngleDegrees, 0, 1), ContrastExponent);
}
