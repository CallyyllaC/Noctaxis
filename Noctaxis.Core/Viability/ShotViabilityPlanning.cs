using Noctaxis.Core.Domain;
using NodaTime;

namespace Noctaxis.Core.Viability;

/// <summary>
/// Fills a <see cref="ShotViabilityInput"/> from the Planner's own results: its snapshot, the Camera frame's
/// terrain scan and the cached window paths. Nothing is recalculated here and nothing is fetched. A value that
/// belongs to another moment (a Sun or Moon position not yet updated for a new time) is left out, so its
/// metric is unavailable rather than stale.
/// </summary>
public static class ShotViabilityPlanning
{
    public static ShotViabilityInput FromSnapshot(PlanningSnapshot snapshot, ShotViabilityCamera? camera,
        IReadOnlyList<FramingTerrainObstructionSample>? frameTerrain, CameraProfile? cameraProfile,
        ShotWindowPaths? window, bool windowPending, ShotViabilityLightPollution? lightPollution = null)
    {
        var position = snapshot.Position;
        var instant = position.Instant;
        var sun = PositionAt(snapshot, instant, target => target.IsSun, snapshot.Astronomy.Sun);
        var moon = PositionAt(snapshot, instant, target => target.IsMoon, snapshot.Astronomy.Moon);
        var lens = snapshot.Session.Lens;
        var current = window is not null && window.Covers(position.Target.Id, snapshot.Session.Observer, instant) ? window : null;
        return new ShotViabilityInput(position.Target, instant, position.Horizontal,
            sun?.Horizontal.AltitudeDegrees,
            moon?.Horizontal,
            moon?.MoonIlluminatedFraction,
            snapshot.Weather,
            snapshot.Terrain,
            frameTerrain,
            camera,
            new ShotViabilitySensor(lens.FocalLengthMillimetres, cameraProfile?.PixelPitchMicrometres, lens.FramingMultiplier),
            current,
            current is null && windowPending,
            lightPollution);
    }

    private static TargetPosition? PositionAt(PlanningSnapshot snapshot, Instant instant, Func<AstralTarget, bool> body,
        TargetPosition context) =>
        snapshot.EffectiveObjectPlans.Select(plan => plan.Position)
            .FirstOrDefault(candidate => body(candidate.Target) && candidate.Instant == instant)
        ?? (context.Instant == instant ? context : null);
}
