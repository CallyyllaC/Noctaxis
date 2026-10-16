using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;
using P = Noctaxis.Core.Viability.ShotViabilityPolicy;

namespace Noctaxis.Core.Viability;

/// <summary>
/// The shot-viability score (<see cref="ShotViabilityPolicy.Id"/>): each metric scored by its own curve, then a
/// weighted mean of the metrics that could be scored. Pure and deterministic: the same input always gives the
/// same assessment, and nothing here touches the network, the disk or the user interface.
/// </summary>
/// <remarks>docs/shot-viability-scoring.md describes every rule here.</remarks>
public static class ShotViabilityScorer
{
    public static ShotViabilityAssessment Assess(ShotViabilityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var profile = ProfileOf(input.Target);
        var clearance = TerrainClearance(input);
        ShotViabilityMetricResult[] metrics =
        [
            CloudCover(input),
            Darkness(input, profile),
            TargetAltitude(input, profile, clearance),
            Terrain(input, profile, clearance),
            UsefulWindow(input, profile),
            Moon(input, profile),
            LightPollution(input, profile),
            FramePosition(input),
            FramingFit(input, profile),
            ImageSampling(input, profile)
        ];
        return Aggregate(input, profile, metrics);
    }

    /// <summary>Which assumptions apply to <paramref name="target"/>.</summary>
    public static ShotViabilityProfile ProfileOf(AstralTarget target) =>
        target.IsSun ? ShotViabilityProfile.Sun
        : target.IsMoon ? ShotViabilityProfile.Moon
        : target.Category is AstralTargetCategory.Star or AstralTargetCategory.DoubleStar ? ShotViabilityProfile.PointSource
        : ShotViabilityProfile.DeepSky;

    /// <summary>Whether a terrain horizon can be read: calculated, covered, complete and not empty.</summary>
    public static bool HasUsableTerrain(TerrainHorizonProfile? terrain) =>
        terrain is { TerrainCalculationsEnabled: true, HasTerrainCoverage: true, IsComplete: true, Samples.Count: > 0 };

    /// <summary>The target's apparent angular size in arcminutes (major, minor), when it is reliably known.</summary>
    public static (double Major, double Minor)? AngularSizeOf(AstralTarget target)
    {
        if (target.IsSun) return (P.SunDiameterArcminutes, P.SunDiameterArcminutes);
        if (target.IsMoon) return (P.MoonDiameterArcminutes, P.MoonDiameterArcminutes);
        if (target.MajorAxisArcminutes is not (> 0 and var major) || !double.IsFinite(major)) return null;
        // Only a major axis: treated as round, the conservative reading for the smaller axis.
        var minor = target.MinorAxisArcminutes is > 0 and var value && double.IsFinite(value) ? Math.Min(value, major) : major;
        return (major, minor);
    }

    private static ShotViabilityAssessment Aggregate(ShotViabilityInput input, ShotViabilityProfile profile,
        IReadOnlyList<ShotViabilityMetricResult> metrics)
    {
        var applicable = metrics.Where(metric => metric.Status != ShotViabilityMetricStatus.NotApplicable).Sum(metric => metric.Weight);
        var evaluated = metrics.Where(metric => metric.IsEvaluated).ToArray();
        var evaluatedWeight = evaluated.Sum(metric => metric.Weight);
        double? score = evaluatedWeight > 0
            ? Math.Clamp(evaluated.Sum(metric => metric.Score!.Value * metric.Weight) / evaluatedWeight, 0, 100)
            : null;
        var withContributions = metrics.Select(metric => metric.IsEvaluated
            ? metric with
            {
                WeightedContribution = metric.Score!.Value * metric.Weight / evaluatedWeight,
                MaximumContribution = 100 * metric.Weight / evaluatedWeight
            }
            : metric).ToArray();
        var coverage = applicable > 0 ? evaluatedWeight / applicable : 0;
        var assessment = new ShotViabilityAssessment(P.Id, input.Target, input.Instant, profile, score,
            ShotViabilityRating.Unavailable, coverage, P.ConfidenceFor(coverage), withContributions);
        // The rating follows the number shown, so 79.6 shown as 80 reads Excellent.
        return assessment.RoundedScore is int shown ? assessment with { Rating = P.RatingFor(shown) } : assessment;
    }

    // ---- Cloud cover --------------------------------------------------------------------------------------

    private static ShotViabilityMetricResult CloudCover(ShotViabilityInput input)
    {
        const ShotViabilityMetric metric = ShotViabilityMetric.CloudCover;
        var weather = input.Weather;
        if (weather is null) return Unavailable(metric, Strings.Get("Planning_Viability_Cloud_NoForecast"));
        if (weather.State == DataState.Loading) return Unavailable(metric, Strings.Get("Planning_Viability_Cloud_Loading"));
        if (weather.State == DataState.Stale || weather.Conditions is { IsStale: true })
            return Unavailable(metric, Strings.Get("Planning_Viability_Cloud_Stale"));
        if (weather.State != DataState.Ready || weather.Conditions is not { } conditions)
            return Unavailable(metric, Strings.Get("Planning_Viability_Cloud_NoForecast"));
        if (Math.Abs((conditions.ForecastInstant - input.Instant).TotalMinutes) > P.MaximumForecastOffset.TotalMinutes)
            return Unavailable(metric, Strings.Get("Planning_Viability_Cloud_NotForThisTime"));
        if (conditions.CloudCoverPercent is not double cloud || !double.IsFinite(cloud) || cloud is < 0 or > 100)
            return Unavailable(metric, Strings.Get("Planning_Viability_Cloud_NoCloudData"));

        var score = P.CloudCover.Evaluate(cloud);
        var key = cloud <= 10 ? "Planning_Viability_Cloud_Clear_Format"
            : cloud <= 25 ? "Planning_Viability_Cloud_MostlyClear_Format"
            : cloud <= 60 ? "Planning_Viability_Cloud_Partly_Format"
            : cloud < P.CloudCoverCriticalPercent ? "Planning_Viability_Cloud_Heavy_Format"
            : "Planning_Viability_Cloud_Overcast_Format";
        return Evaluated(metric, score, Strings.Format(key, cloud), [Percent("cloud-cover", cloud)],
            cloud >= P.CloudCoverCriticalPercent ? ShotViabilitySeverity.Critical : null);
    }

    // ---- Darkness -------------------------------------------------------------------------------------

    private static ShotViabilityMetricResult Darkness(ShotViabilityInput input, ShotViabilityProfile profile)
    {
        const ShotViabilityMetric metric = ShotViabilityMetric.Darkness;
        if (profile is ShotViabilityProfile.Sun or ShotViabilityProfile.Moon)
            return NotApplicable(metric, Strings.Get("Planning_Viability_Darkness_NotApplicable"));
        if (input.SunAltitudeDegrees is not double sun || !double.IsFinite(sun))
            return Unavailable(metric, Strings.Get("Planning_Viability_Darkness_Unavailable"));

        var score = P.Darkness.Evaluate(sun);
        var explanation = sun <= P.AstronomicalDarknessDegrees ? Strings.Format("Planning_Viability_Darkness_Astronomical_Format", -sun)
            : sun <= P.NauticalDarknessDegrees ? Strings.Format("Planning_Viability_Darkness_AstronomicalTwilight_Format", -sun)
            : sun <= P.CivilTwilightDegrees ? Strings.Format("Planning_Viability_Darkness_NauticalTwilight_Format", -sun)
            : sun < 0 ? Strings.Format("Planning_Viability_Darkness_CivilTwilight_Format", -sun)
            : Strings.Get("Planning_Viability_Darkness_Daylight");
        return Evaluated(metric, score, explanation, [Degrees("sun-altitude", sun)],
            sun >= 0 ? ShotViabilitySeverity.Critical : null);
    }

    // ---- Target altitude ------------------------------------------------------------------------------

    private static ShotViabilityMetricResult TargetAltitude(ShotViabilityInput input, ShotViabilityProfile profile, double? clearance)
    {
        const ShotViabilityMetric metric = ShotViabilityMetric.TargetAltitude;
        var altitude = input.TargetPosition.AltitudeDegrees;
        if (!double.IsFinite(altitude) || altitude is < -90 or > 90)
            return Unavailable(metric, Strings.Get("Planning_Viability_Altitude_Unavailable"));
        var solar = profile is ShotViabilityProfile.Sun or ShotViabilityProfile.Moon;
        var score = (solar ? P.SolarSystemAltitude : P.DeepSkyAltitude).Evaluate(altitude);
        var key = altitude < 0 ? "Planning_Viability_Altitude_BelowHorizon_Format"
            : solar ? altitude < 10 ? "Planning_Viability_Altitude_SolarLow_Format" : "Planning_Viability_Altitude_SolarWellPlaced_Format"
            : altitude < P.AcceptableAltitudeDegrees ? "Planning_Viability_Altitude_Low_Format"
            : altitude < P.FavourableAltitudeDegrees ? "Planning_Viability_Altitude_Acceptable_Format"
            : "Planning_Viability_Altitude_High_Format";
        // Below the astronomical horizon is hidden, unless the terrain horizon is lower still (a view down from
        // a summit), which the terrain metric then judges.
        ShotViabilitySeverity? severity = altitude < 0 ? clearance is > 0 ? ShotViabilitySeverity.Caution : ShotViabilitySeverity.Critical : null;
        return Evaluated(metric, score, Strings.Format(key, Math.Abs(altitude)), [Degrees("target-altitude", altitude)], severity);
    }

    // ---- Terrain clearance ----------------------------------------------------------------------------

    private static double? TerrainClearance(ShotViabilityInput input) =>
        HasUsableTerrain(input.Terrain) && input.Terrain!.EffectiveAltitudeAt(input.TargetPosition.AzimuthDegrees) is double horizon &&
        double.IsFinite(horizon) && double.IsFinite(input.TargetPosition.AltitudeDegrees)
            ? input.TargetPosition.AltitudeDegrees - horizon : null;

    private static ShotViabilityMetricResult Terrain(ShotViabilityInput input, ShotViabilityProfile profile, double? clearance)
    {
        const ShotViabilityMetric metric = ShotViabilityMetric.TerrainClearance;
        if (input.Terrain is { TerrainCalculationsEnabled: false })
            return Unavailable(metric, Strings.Get("Planning_Viability_Terrain_Disabled"));
        if (input.Terrain is { IsComplete: false })
            return Unavailable(metric, Strings.Get("Planning_Viability_Terrain_Loading"));
        if (clearance is not double value)
            return Unavailable(metric, Strings.Get("Planning_Viability_Terrain_Unavailable"));

        // Landscape entering the frame, as the Camera frame measures it: the largest share of the frame's height
        // terrain covers across its width, beyond the frame's own minimum.
        double? landscape = input.FrameTerrain?.Where(sample => sample.FrameTerrainHorizonDegrees.HasValue)
            .Select(sample => Math.Clamp(sample.EffectiveCoverage, 0, 1)).DefaultIfEmpty(double.NaN).Max();
        if (landscape is double share && !double.IsFinite(share)) landscape = null;

        var score = P.TerrainClearance.Evaluate(value);
        if (value > 0 && landscape is double covered && profile is ShotViabilityProfile.DeepSky or ShotViabilityProfile.PointSource)
            score *= 1 - P.LandscapeMaximumPenalty * Smoothstep.Evaluate(0, P.LandscapeFullPenaltyCoverage, covered);

        List<ShotViabilityMeasurement> measurements = [Degrees("terrain-clearance", value)];
        if (landscape is double frame) measurements.Add(Percent("frame-landscape", frame * 100));
        string explanation;
        ShotViabilitySeverity? severity = null;
        if (value <= 0)
        {
            explanation = Strings.Format("Planning_Viability_Terrain_Hidden_Format", -value);
            severity = ShotViabilitySeverity.Critical;
        }
        else if (value <= P.MarginalClearanceDegrees)
        {
            explanation = Strings.Format("Planning_Viability_Terrain_Marginal_Format", value);
            severity = ShotViabilitySeverity.Caution;
        }
        else explanation = landscape is > 0.01
            ? Strings.Format("Planning_Viability_Terrain_ClearWithLandscape_Format", value, landscape.Value * 100)
            : Strings.Format("Planning_Viability_Terrain_Clear_Format", value);
        return Evaluated(metric, score, explanation, measurements, severity);
    }

    // ---- Useful time window ---------------------------------------------------------------------------

    private static ShotViabilityMetricResult UsefulWindow(ShotViabilityInput input, ShotViabilityProfile profile)
    {
        const ShotViabilityMetric metric = ShotViabilityMetric.UsefulWindow;
        if (profile is ShotViabilityProfile.Sun or ShotViabilityProfile.Moon)
            return NotApplicable(metric, Strings.Get("Planning_Viability_Window_NotApplicable"));
        if (input.Window is not { } paths || UsefulWindowCalculator.Evaluate(paths, input.Instant, input.Terrain) is not { } window)
            return Unavailable(metric, Strings.Get(input.WindowPending ? "Planning_Viability_Window_Calculating" : "Planning_Viability_Window_Unavailable"));

        var hours = window.Remaining.TotalHours;
        var score = P.UsefulWindow.Evaluate(hours);
        string explanation;
        if (!window.IsOpen)
            explanation = window.NextOpensIn is { } opens
                ? Strings.Format("Planning_Viability_Window_ClosedNext_Format", opens.TotalHours, window.NextLasts!.Value.TotalHours)
                : Strings.Format("Planning_Viability_Window_ClosedNone_Format", P.UsefulWindowHorizon.TotalHours);
        else explanation = window.IsAtLeast ? Strings.Format("Planning_Viability_Window_AtLeast_Format", hours)
            : window.Limit switch
            {
                UsefulWindowLimit.Darkness => Strings.Format("Planning_Viability_Window_UntilTwilight_Format", hours),
                UsefulWindowLimit.Altitude => Strings.Format("Planning_Viability_Window_UntilLow_Format", hours),
                _ => Strings.Format("Planning_Viability_Window_UntilTerrain_Format", hours)
            };
        if (!window.TerrainConsidered) explanation += " " + Strings.Get("Planning_Viability_Window_TerrainNotConsidered");
        return Evaluated(metric, score, explanation,
            [new ShotViabilityMeasurement("useful-hours", hours, "h", Strings.Format("Planning_Viability_Value_Hours_Format", hours))]);
    }

    // ---- Moon interference ----------------------------------------------------------------------------

    private static ShotViabilityMetricResult Moon(ShotViabilityInput input, ShotViabilityProfile profile)
    {
        const ShotViabilityMetric metric = ShotViabilityMetric.Moon;
        if (profile is ShotViabilityProfile.Sun or ShotViabilityProfile.Moon)
            return NotApplicable(metric, Strings.Get("Planning_Viability_Moon_NotApplicable"));
        if (input.MoonPosition is not { } moon || !double.IsFinite(moon.AltitudeDegrees) || !double.IsFinite(moon.AzimuthDegrees))
            return Unavailable(metric, Strings.Get("Planning_Viability_Moon_Unavailable"));
        if (moon.AltitudeDegrees < 0)
            return Evaluated(metric, 100, Strings.Get("Planning_Viability_Moon_Down"), [Degrees("moon-altitude", moon.AltitudeDegrees)]);
        if (input.MoonIlluminatedFraction is not double lit || !double.IsFinite(lit) || lit is < 0 or > 1)
            return Unavailable(metric, Strings.Get("Planning_Viability_Moon_Unavailable"));

        var separation = AngularSeparation(moon, input.TargetPosition);
        var penalty = (100 - P.MoonIllumination.Evaluate(lit)) * P.MoonSeparation.Evaluate(separation) / 100 *
                      Smoothstep.Evaluate(0, P.MoonFullEffectAltitudeDegrees, moon.AltitudeDegrees);
        var score = Math.Clamp(100 - penalty, 0, 100);
        var key = score >= P.CautionBelowScore ? "Planning_Viability_Moon_LittleEffect_Format" : "Planning_Viability_Moon_ReducesContrast_Format";
        return Evaluated(metric, score, Strings.Format(key, lit * 100, separation),
        [
            new ShotViabilityMeasurement("moon-illumination", lit * 100, "%", Strings.Format("Planning_Viability_Value_Moon_Format", lit * 100, separation)),
            Degrees("moon-separation", separation),
            Degrees("moon-altitude", moon.AltitudeDegrees)
        ]);
    }

    // ---- Light pollution ------------------------------------------------------------------------------

    private static ShotViabilityMetricResult LightPollution(ShotViabilityInput input, ShotViabilityProfile profile)
    {
        const ShotViabilityMetric metric = ShotViabilityMetric.LightPollution;
        if (profile is ShotViabilityProfile.Sun or ShotViabilityProfile.Moon)
            return NotApplicable(metric, Strings.Get("Planning_Viability_LightPollution_NotApplicable"));
        var data = input.LightPollution;
        if (data is null || data.Status == LightPollutionDataStatus.NotInstalled)
            return Unavailable(metric, Strings.Get("Planning_Viability_LightPollution_NotInstalled"));
        if (data.Status == LightPollutionDataStatus.Loading)
            return Unavailable(metric, Strings.Get("Planning_Viability_LightPollution_Loading"));
        if (data.Status == LightPollutionDataStatus.OutsideCoverage)
            return Unavailable(metric, Strings.Get("Planning_Viability_LightPollution_OutsideCoverage"));
        if (data.Status != LightPollutionDataStatus.Available || data.ArtificialToNaturalRatio is not double ratio ||
            !double.IsFinite(ratio) || ratio < 0)
            return Unavailable(metric, Strings.Get("Planning_Viability_LightPollution_Unreadable"));

        var lost = P.MagnitudesLost(ratio);
        var brightness = 1 + ratio;
        var explanation = ratio <= P.DarkSiteRatio ? Strings.Format("Planning_Viability_LightPollution_Dark_Format", ratio * 100)
            : ratio <= P.RuralRatio ? Strings.Format("Planning_Viability_LightPollution_Rural_Format", ratio * 100)
            : ratio <= P.SomePollutionRatio ? Strings.Format("Planning_Viability_LightPollution_Some_Format", ratio * 100)
            : ratio <= P.SuburbanRatio ? Strings.Format("Planning_Viability_LightPollution_Suburban_Format", brightness)
            : ratio <= P.BrightSuburbanRatio ? Strings.Format("Planning_Viability_LightPollution_BrightSuburban_Format", brightness)
            : Strings.Format("Planning_Viability_LightPollution_Urban_Format", brightness);
        return Evaluated(metric, P.LightPollution.Evaluate(lost), explanation,
        [
            new ShotViabilityMeasurement("artificial-brightness-ratio", ratio, "×natural",
                Strings.Format("Planning_Viability_Value_Ratio_Format", brightness)),
            new ShotViabilityMeasurement("magnitudes-lost", lost, "mag", Strings.Format("Planning_Viability_Value_Magnitudes_Format", lost)),
            new ShotViabilityMeasurement("zenith-sky-brightness", P.NaturalSkyMagnitude - lost, "mag/arcsec²",
                Strings.Format("Planning_Viability_Value_SkyBrightness_Format", P.NaturalSkyMagnitude - lost))
        ]);
    }

    /// <summary>The great-circle angle between two horizontal directions, in degrees.</summary>
    public static double AngularSeparation(HorizontalCoordinate a, HorizontalCoordinate b)
    {
        var radians = Angles.DegreesToRadians;
        var cos = Math.Sin(a.AltitudeDegrees * radians) * Math.Sin(b.AltitudeDegrees * radians) +
                  Math.Cos(a.AltitudeDegrees * radians) * Math.Cos(b.AltitudeDegrees * radians) *
                  Math.Cos((a.AzimuthDegrees - b.AzimuthDegrees) * radians);
        return Math.Acos(Math.Clamp(cos, -1, 1)) * Angles.RadiansToDegrees;
    }

    // ---- Framing -------------------------------------------------------------------------------------

    private static bool HasCamera(ShotViabilityCamera? camera) => camera is not null &&
        double.IsFinite(camera.BearingDegrees) && double.IsFinite(camera.PitchDegrees) &&
        camera.HorizontalFovDegrees is > 0 and < 180 && camera.VerticalFovDegrees is > 0 and < 180;

    private static ShotViabilityMetricResult FramePosition(ShotViabilityInput input)
    {
        const ShotViabilityMetric metric = ShotViabilityMetric.FramePosition;
        if (!HasCamera(input.Camera)) return Unavailable(metric, Strings.Get("Planning_Viability_Frame_NoCamera"));
        var camera = input.Camera!;
        var target = input.TargetPosition;
        if (!double.IsFinite(target.AltitudeDegrees) || !double.IsFinite(target.AzimuthDegrees))
            return Unavailable(metric, Strings.Get("Planning_Viability_Altitude_Unavailable"));
        var projected = CameraFrameGeometry.Project(Angles.NormaliseSignedDegrees(target.AzimuthDegrees - camera.BearingDegrees),
            target.AltitudeDegrees, camera.HorizontalFovDegrees, camera.VerticalFovDegrees, camera.PitchDegrees);
        // Behind the camera is as far outside the frame as it gets.
        var offset = projected is { } at ? Math.Max(Math.Abs(at.Across), Math.Abs(at.Up)) : double.PositiveInfinity;
        if (offset > 1)
            return Evaluated(metric, 0, Strings.Get("Planning_Viability_Frame_Outside"), [], ShotViabilitySeverity.Critical);
        var near = offset > P.FrameNearEdge;
        return Evaluated(metric, P.FramePosition.Evaluate(offset),
            Strings.Get(near ? "Planning_Viability_Frame_NearEdge" : "Planning_Viability_Frame_Inside"),
            [new ShotViabilityMeasurement("frame-offset", offset * 100, "%", Strings.Format("Planning_Viability_Value_Offset_Format", offset * 100))],
            near ? ShotViabilitySeverity.Caution : null);
    }

    private static ShotViabilityMetricResult FramingFit(ShotViabilityInput input, ShotViabilityProfile profile)
    {
        const ShotViabilityMetric metric = ShotViabilityMetric.FramingFit;
        if (profile == ShotViabilityProfile.PointSource) return NotApplicable(metric, Strings.Get("Planning_Viability_Size_PointSource"));
        if (AngularSizeOf(input.Target) is not var (major, minor)) return Unavailable(metric, Strings.Get("Planning_Viability_Size_Unknown"));
        if (!HasCamera(input.Camera)) return Unavailable(metric, Strings.Get("Planning_Viability_Frame_NoCamera"));
        var camera = input.Camera!;
        var longSide = Math.Max(camera.HorizontalFovDegrees, camera.VerticalFovDegrees) * 60;
        var shortSide = Math.Min(camera.HorizontalFovDegrees, camera.VerticalFovDegrees) * 60;
        // Best alignment: the long axis along the frame's long side. Rotating the camera is the photographer's call.
        var fill = Math.Max(major / longSide, minor / shortSide);
        var explanation = fill <= P.FramingComfortableFill ? Strings.Format("Planning_Viability_Fit_Comfortable_Format", fill * 100)
            : fill <= 1 ? Strings.Format("Planning_Viability_Fit_Tight_Format", fill * 100)
            : Strings.Format("Planning_Viability_Fit_TooLarge_Format", fill);
        return Evaluated(metric, P.FramingFit.Evaluate(fill), explanation,
        [
            new ShotViabilityMeasurement("frame-fill", fill * 100, "%", Strings.Format("Planning_Viability_Value_Fill_Format", fill * 100)),
            new ShotViabilityMeasurement("target-size", major, "arcmin", Strings.Format("Planning_Viability_Value_Size_Format", major, minor))
        ], fill > 1 ? ShotViabilitySeverity.Caution : ShotViabilitySeverity.Good);
    }

    private static ShotViabilityMetricResult ImageSampling(ShotViabilityInput input, ShotViabilityProfile profile)
    {
        const ShotViabilityMetric metric = ShotViabilityMetric.ImageSampling;
        if (profile == ShotViabilityProfile.PointSource) return NotApplicable(metric, Strings.Get("Planning_Viability_Size_PointSource"));
        if (AngularSizeOf(input.Target) is not var (major, minor)) return Unavailable(metric, Strings.Get("Planning_Viability_Size_Unknown"));
        if (input.Sensor is not { FocalLengthMillimetres: > 0 and var focal } sensor || !double.IsFinite(focal) ||
            sensor.FramingMultiplier is not > 0 || !double.IsFinite(sensor.FramingMultiplier))
            return Unavailable(metric, Strings.Get("Planning_Viability_Sampling_NoFocalLength"));
        if (sensor.PixelPitchMicrometres is not (> 0 and var pitch) || !double.IsFinite(pitch))
            return Unavailable(metric, Strings.Get("Planning_Viability_Sampling_NoResolution"));

        // Rectilinear: an angle θ spans 2 f tan(θ / 2) on the sensor.
        var pixelMillimetres = pitch / 1000 * sensor.FramingMultiplier;
        double Pixels(double arcminutes) => 2 * focal * Math.Tan(arcminutes / 60 / 2 * Angles.DegreesToRadians) / pixelMillimetres;
        var (across, along) = (Pixels(major), Pixels(minor));
        var key = along < P.SamplingVeryLimitedPixels ? "Planning_Viability_Sampling_VeryLimited_Format"
            : along < P.SamplingSmallPixels ? "Planning_Viability_Sampling_Small_Format"
            : along < P.SamplingModestPixels ? "Planning_Viability_Sampling_Modest_Format"
            : along < P.SamplingGoodPixels ? "Planning_Viability_Sampling_Good_Format"
            : "Planning_Viability_Sampling_Excellent_Format";
        return Evaluated(metric, P.ImageSampling.Evaluate(along), Strings.Format(key, across, along),
            [new ShotViabilityMeasurement("target-pixels", along, "px", Strings.Format("Planning_Viability_Value_Pixels_Format", across, along))]);
    }

    // ---- Results ------------------------------------------------------------------------------------

    private static ShotViabilityMetricResult Evaluated(ShotViabilityMetric metric, double score, string explanation,
        IReadOnlyList<ShotViabilityMeasurement> measurements, ShotViabilitySeverity? severity = null)
    {
        var bounded = Math.Clamp(double.IsFinite(score) ? score : 0, 0, 100);
        return new(metric, ShotViabilityMetricStatus.Evaluated, measurements, bounded, P.WeightOf(metric),
            severity ?? (bounded < P.CautionBelowScore ? ShotViabilitySeverity.Caution : ShotViabilitySeverity.Good), explanation);
    }

    private static ShotViabilityMetricResult Unavailable(ShotViabilityMetric metric, string explanation) =>
        new(metric, ShotViabilityMetricStatus.Unavailable, [], null, P.WeightOf(metric), ShotViabilitySeverity.None, explanation);

    private static ShotViabilityMetricResult NotApplicable(ShotViabilityMetric metric, string explanation) =>
        new(metric, ShotViabilityMetricStatus.NotApplicable, [], null, P.WeightOf(metric), ShotViabilitySeverity.None, explanation);

    private static ShotViabilityMeasurement Percent(string quantity, double value) =>
        new(quantity, value, "%", Strings.Format("Planning_Viability_Value_Percent_Format", value));

    private static ShotViabilityMeasurement Degrees(string quantity, double value) =>
        new(quantity, value, "°", Strings.Format("Planning_Viability_Value_Degrees_Format", value));
}
