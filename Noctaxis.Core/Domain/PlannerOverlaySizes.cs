namespace Noctaxis.Core.Domain;

/// <summary>
/// How wide the user wants the Planner map's two fixed instruments — the Picture-in-Picture map at the
/// top-left and the Camera frame at the bottom-left — as shares of the map's own width, so they keep their
/// proportion as the window and the panels around the map change. These are preferences: when the map is
/// too small for them, the Planner draws the instruments smaller for the moment and keeps these as they are.
/// </summary>
/// <param name="PipWidthFraction">The Picture-in-Picture map's side, as a share of the map's width.</param>
/// <param name="CameraFrameWidthFraction">The Camera frame card's width, as a share of the map's width.</param>
public sealed record PlannerOverlaySizes(double PipWidthFraction = PlannerOverlaySizes.DefaultPipWidthFraction,
    double CameraFrameWidthFraction = PlannerOverlaySizes.DefaultCameraFrameWidthFraction)
{
    /// <summary>The arrangement tuned on a 1920 × 1080 display: on its map, about 1,578 px wide beside the
    /// shipped Inspector, a 349 px PiP and a 350 px Camera frame card. A smaller map draws them smaller.</summary>
    public const double DefaultPipWidthFraction = .221;
    public const double DefaultCameraFrameWidthFraction = .222;

    /// <summary>The widest a preference can be; the Planner also keeps each to a third of the map when drawn.</summary>
    public const double MinimumFraction = .05, MaximumFraction = .5;

    public PlannerOverlaySizes Normalised() => new(Clamp(PipWidthFraction, DefaultPipWidthFraction),
        Clamp(CameraFrameWidthFraction, DefaultCameraFrameWidthFraction));

    private static double Clamp(double value, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, MinimumFraction, MaximumFraction) : fallback;
}
