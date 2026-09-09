namespace Noctaxis.Desktop.Controls;

/// <summary>Presentation only; physical camera coverage remains unchanged in the assessment and texture.</summary>
public static class TerrainTintPresentation
{
    public static double DisplayStrength(double effectiveCoverage) =>
        double.IsFinite(effectiveCoverage) ? Math.Sqrt(Math.Clamp(effectiveCoverage, 0, 1)) : 0;
}
