using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Planning;
using Noctaxis.Core.Time;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>
/// Which targets a plan draws is the shown ones alone: being focused, or being the plan's anchor, never adds
/// a hidden target. A saved focus that is hidden or missing is no focus, never a stand-in.
/// </summary>
public sealed class TargetMembershipTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 3, 21, 0);

    // Only the astronomy is used: a plan's targets need no terrain or weather.
    private static PlanningService Service() => new(new OpenNgcTargetCatalogue(), new AstronomyEngineService(new TimeZoneResolver()),
        new LensCalculator(), null!, null!, new TimeZoneResolver());

    /// <summary>
    /// A hidden target is not drawn, even when the plan is calculated around it, on the map's plans and on the
    /// Timeline's paths alike; the shown ones are, in the user's order.
    /// </summary>
    [Fact]
    public async Task OnlyShownTargetsAreDrawnWhateverThePlanIsCalculatedAround()
    {
        var m31 = new OpenNgcTargetCatalogue().Get("M31").Id;
        var session = PlanningSession.Default(Now, "UTC") with
        {
            TargetId = m31,
            VisibleObjects = [new("sun", true, 0), new("moon", true, 1), new(m31, false, 2)]
        };
        var snapshot = await Service().CalculateCoreSnapshotAsync(session, CancellationToken.None);
        Assert.Equal(["sun", "moon"], snapshot.EffectiveObjectPlans.Select(plan => plan.Position.Target.Id));
        Assert.Equal(m31, snapshot.Position.Target.Id);
        var timeline = await Service().CalculateTimelineAsync(session, Now, CancellationToken.None);
        Assert.Equal(["sun", "moon"], timeline.Select(path => path.Target.Id));
    }

    /// <summary>
    /// Focus and anchor: a requested focus that is shown is kept; one that is hidden or missing is no focus, and
    /// the anchor is then the first shown target, never the hidden one, never the last.
    /// </summary>
    [Fact]
    public void AHiddenOrMissingFocusIsNoFocus()
    {
        CelestialObjectSelection[] objects = [new("m42", false, 0), new("sun", true, 1), new("moon", true, 2), new("m31", false, 3)];
        Assert.Equal(("sun", "sun"), CelestialVisibilityPolicy.FocusAndAnchor(objects, "SUN"));
        Assert.Equal((null, "sun"), CelestialVisibilityPolicy.FocusAndAnchor(objects, "m31"));
        Assert.Equal((null, "sun"), CelestialVisibilityPolicy.FocusAndAnchor(objects, "not-a-target"));
        Assert.Equal((null, "sun"), CelestialVisibilityPolicy.FocusAndAnchor(objects, null));
        Assert.Equal(("moon", "moon"), CelestialVisibilityPolicy.FocusAndAnchor(objects, "moon"));
    }
}
