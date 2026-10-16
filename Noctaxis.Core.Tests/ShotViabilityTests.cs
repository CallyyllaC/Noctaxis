using System.Globalization;
using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Planning;
using Noctaxis.Core.Time;
using Noctaxis.Core.Viability;
using NodaTime;
using P = Noctaxis.Core.Viability.ShotViabilityPolicy;

namespace Noctaxis.Core.Tests;

/// <summary>
/// The shot-viability score (docs/shot-viability-scoring.md): its curves, each metric, the weighted aggregate,
/// what it does without data, and the useful window.
/// </summary>
public sealed class ShotViabilityTests
{
    private static readonly Instant Moment = Instant.FromUtc(2026, 10, 8, 22, 0);
    private static readonly GeoCoordinate Observer = new(54.5, -3.0, 100);

    private static readonly AstralTarget Galaxy = new("test:galaxy", "Test Galaxy", AstralTargetCategory.Galaxy, 0.7, 41, "J2000",
        MajorAxisArcminutes: 20, MinorAxisArcminutes: 10);
    private static readonly AstralTarget Star = new("test:star", "Test Star", AstralTargetCategory.Star, 5, 10, "J2000");
    private static readonly AstralTarget Unsized = new("test:nebula", "Unsized Nebula", AstralTargetCategory.Nebula, 3, 20, "J2000");
    private static readonly AstralTarget Sun = new("sun", "Sun", AstralTargetCategory.Solar, null, null, "OfDate");
    private static readonly AstralTarget Moon = new("moon", "Moon", AstralTargetCategory.Lunar, null, null, "OfDate");

    // ---- Inputs --------------------------------------------------------------------------------------------

    private static WeatherResult Cloud(double? percent, Instant? forecast = null, DataState state = DataState.Ready, bool stale = false) =>
        new(state, new WeatherConditions(forecast ?? Moment, percent, null, null, null, null, null, null, null, null, null,
            null, null, null, null, "Test", Moment, stale), "Test");

    private static TerrainHorizonProfile FlatTerrain(double horizon = 1, Func<double, double>? at = null) =>
        new(Observer, Enumerable.Range(0, 360).Select(bearing => new TerrainHorizonSample(bearing, at?.Invoke(bearing) ?? horizon)).ToArray(),
            true, "Ready", Moment);

    /// <summary>Paths from six hours before the moment to thirty after, every ten minutes, from functions of hours since the moment.</summary>
    private static ShotWindowPaths Paths(Func<double, double> targetAltitude, Func<double, double> sunAltitude,
        Func<double, double>? targetAzimuth = null, string targetId = "test:galaxy")
    {
        var start = Moment - Duration.FromHours(6);
        var end = Moment + Duration.FromHours(30);
        var instants = Enumerable.Range(0, 217).Select(step => start + Duration.FromMinutes(10 * step)).ToArray();
        double Hours(Instant instant) => (instant - Moment).TotalHours;
        return new ShotWindowPaths(targetId, Observer, start, end,
            instants.Select(at => new AstralPathSample(at, new HorizontalCoordinate(targetAzimuth?.Invoke(Hours(at)) ?? 180, targetAltitude(Hours(at))))).ToArray(),
            instants.Select(at => new AstralPathSample(at, new HorizontalCoordinate(0, sunAltitude(Hours(at))))).ToArray());
    }

    /// <summary>An ideal deep-sky night: dark, clear, high, aimed, well sampled, with five hours ahead.</summary>
    private static ShotViabilityInput Ideal(AstralTarget? target = null) => new(
        target ?? Galaxy, Moment, new HorizontalCoordinate(180, 60),
        SunAltitudeDegrees: -30,
        MoonPosition: new HorizontalCoordinate(0, -20), MoonIlluminatedFraction: 0.9,
        Weather: Cloud(3),
        Terrain: FlatTerrain(),
        Camera: new ShotViabilityCamera(180, 60, 10, 6.7),
        Sensor: new ShotViabilitySensor(500, 3.76),
        Window: Paths(_ => 60, hours => hours < 5 ? -30 : -5),
        LightPollution: ShotViabilityLightPollution.Of(0.05));

    private static ShotViabilityAssessment Assess(ShotViabilityInput input) => ShotViabilityScorer.Assess(input);

    // ---- Curves --------------------------------------------------------------------------------------------

    public static TheoryData<string> Curves => new(CurveTable.Keys);

    private static readonly Dictionary<string, (IScoreCurve Curve, double From, double To, int Direction)> CurveTable = new()
    {
        ["cloud"] = (P.CloudCover, -20, 120, -1),
        ["darkness"] = (P.Darkness, -40, 20, -1),
        ["deep-sky altitude"] = (P.DeepSkyAltitude, -20, 95, 1),
        ["solar altitude"] = (P.SolarSystemAltitude, -20, 95, 1),
        ["terrain"] = (P.TerrainClearance, -10, 20, 1),
        ["window"] = (P.UsefulWindow, -2, 24, 1),
        ["moon illumination"] = (P.MoonIllumination, -0.5, 1.5, -1),
        ["moon separation"] = (P.MoonSeparation, 0, 180, -1),
        ["frame position"] = (P.FramePosition, 0, 1, -1),
        ["framing fit"] = (P.FramingFit, 0, 6, -1),
        ["image sampling"] = (P.ImageSampling, 0, 1000, 1),
        ["light pollution"] = (P.LightPollution, -1, 8, -1)
    };

    [Theory]
    [MemberData(nameof(Curves))]
    public void EveryCurveIsBoundedMonotoneAndContinuous(string name)
    {
        var (curve, from, to, direction) = CurveTable[name];
        const int steps = 100_000;
        var step = (to - from) / steps;
        var previous = curve.Evaluate(from);
        for (var index = 1; index <= steps; index++)
        {
            var value = curve.Evaluate(from + index * step);
            Assert.InRange(value, 0, 100);
            // Monotone in its meaningful direction.
            Assert.True(direction * (value - previous) >= -1e-9, $"{name} turns back at {from + index * step}");
            // Continuous: no jump between neighbours a hundred-thousandth of the range apart.
            Assert.True(Math.Abs(value - previous) < 0.5, $"{name} jumps by {value - previous} at {from + index * step}");
            previous = value;
        }
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Equal(0, curve.Evaluate(invalid));
    }

    [Fact]
    public void AnAnchoredCurvePassesExactlyThroughItsAnchorsAndHoldsBeyondThem()
    {
        foreach (var curve in new[] { P.Darkness, P.DeepSkyAltitude, P.SolarSystemAltitude, P.MoonIllumination, P.MoonSeparation,
                     P.FramePosition, P.FramingFit, P.ImageSampling })
        {
            foreach (var (x, score) in curve.Anchors) Assert.Equal(score, curve.Evaluate(x), 9);
            Assert.Equal(curve.Anchors[0].Score, curve.Evaluate(curve.Anchors[0].X - 1000), 9);
            Assert.Equal(curve.Anchors[^1].Score, curve.Evaluate(curve.Anchors[^1].X + 1000), 9);
        }
        Assert.Throws<ArgumentException>(() => new AnchoredCurve((1, 0), (0, 100)));
        Assert.Throws<ArgumentException>(() => new AnchoredCurve((0, 0)));
        Assert.Throws<ArgumentException>(() => new AnchoredCurve((0, 0), (double.NaN, 1)));
    }

    /// <summary>The representative values the scoring document tabulates.</summary>
    [Theory]
    [InlineData("cloud", 0, 100)]
    [InlineData("cloud", 10, 97.0)]
    [InlineData("cloud", 25, 78.7)]
    [InlineData("cloud", 35, 50.6)]
    [InlineData("cloud", 60, 4.2)]
    [InlineData("cloud", 100, 0)]
    [InlineData("darkness", -18, 100)]
    [InlineData("darkness", -15, 88)]
    [InlineData("darkness", -12, 65)]
    [InlineData("darkness", -6, 12)]
    [InlineData("darkness", 0, 0)]
    [InlineData("deep-sky altitude", 20, 30)]
    [InlineData("deep-sky altitude", 30, 65)]
    [InlineData("deep-sky altitude", 45, 100)]
    [InlineData("solar altitude", 0, 40)]
    [InlineData("solar altitude", 10, 90)]
    [InlineData("terrain", 0, 0)]
    [InlineData("terrain", 1, 47.1)]
    [InlineData("terrain", 2, 94.3)]
    [InlineData("window", 0.5, 40.9)]
    [InlineData("window", 1, 65.1)]
    [InlineData("window", 2, 87.8)]
    [InlineData("window", 3, 95.7)]
    [InlineData("moon illumination", 0.2, 85)]
    [InlineData("moon illumination", 1, 10)]
    [InlineData("framing fit", 1, 65)]
    [InlineData("framing fit", 3, 25)]
    [InlineData("image sampling", 25, 30)]
    [InlineData("image sampling", 50, 55)]
    [InlineData("image sampling", 100, 75)]
    [InlineData("image sampling", 200, 95)]
    [InlineData("light pollution", 0, 100)]
    [InlineData("light pollution", 0.3, 90)]
    [InlineData("light pollution", 0.75, 75)]
    [InlineData("light pollution", 1.5, 52)]
    [InlineData("light pollution", 2.5, 30)]
    [InlineData("light pollution", 5, 5)]
    public void TheDocumentedValuesAreTheCurvesOwn(string name, double x, double expected) =>
        Assert.Equal(expected, CurveTable[name].Curve.Evaluate(x), 1);

    [Fact]
    public void TheThresholdsTheBriefCallsAcceptableAreNotCautions()
    {
        // Acceptable: 25% cloud, nautical darkness, 30° up, an hour of useful time.
        Assert.True(P.CloudCover.Evaluate(25) >= P.CautionBelowScore);
        Assert.True(P.Darkness.Evaluate(P.NauticalDarknessDegrees) >= P.CautionBelowScore);
        Assert.True(P.DeepSkyAltitude.Evaluate(P.AcceptableAltitudeDegrees) >= P.CautionBelowScore);
        Assert.True(P.UsefulWindow.Evaluate(1) >= P.CautionBelowScore);
        // Poor: just under those, very poor cloud beyond 60%, under an hour.
        Assert.True(P.DeepSkyAltitude.Evaluate(25) < P.CautionBelowScore);
        Assert.True(P.CloudCover.Evaluate(60) < 5);
        Assert.True(P.UsefulWindow.Evaluate(0.75) < P.CautionBelowScore);
        // Diminishing returns: the fourth hour adds less than the second.
        Assert.True(P.UsefulWindow.Evaluate(4) - P.UsefulWindow.Evaluate(3) < P.UsefulWindow.Evaluate(2) - P.UsefulWindow.Evaluate(1));
    }

    [Fact]
    public void TheWeightsAddUpToOneHundred() =>
        Assert.Equal(100, Enum.GetValues<ShotViabilityMetric>().Sum(P.WeightOf), 9);

    // ---- Aggregate -----------------------------------------------------------------------------------------

    [Fact]
    public void AnIdealNightIsExcellentFullyAssessedAndWithoutWarnings()
    {
        var assessment = Assess(Ideal());
        Assert.Equal(P.Id, assessment.PolicyId);
        Assert.Equal("shot-viability-v2", assessment.PolicyId);
        Assert.Equal(ShotViabilityProfile.DeepSky, assessment.Profile);
        Assert.All(assessment.Metrics, metric => Assert.True(metric.IsEvaluated, $"{metric.Id}: {metric.Explanation}"));
        Assert.Equal(1, assessment.Coverage, 9);
        Assert.Equal(ShotViabilityConfidence.High, assessment.Confidence);
        Assert.True(assessment.Score >= 90, $"score {assessment.Score}");
        Assert.Equal(ShotViabilityRating.Excellent, assessment.Rating);
        Assert.Empty(assessment.Warnings);
        Assert.Equal(Enum.GetValues<ShotViabilityMetric>(), assessment.Metrics.Select(metric => metric.Metric));
    }

    [Fact]
    public void TheScoreIsTheWeightedMeanOfTheScoredMetricsAndTheirContributionsAddUpToIt()
    {
        var assessment = Assess(Ideal() with { Weather = Cloud(40), SunAltitudeDegrees = -13, TargetPosition = new(180, 35) });
        var scored = assessment.Metrics.Where(metric => metric.IsEvaluated).ToArray();
        var expected = scored.Sum(metric => metric.Score!.Value * metric.Weight) / scored.Sum(metric => metric.Weight);
        Assert.Equal(expected, assessment.Score!.Value, 9);
        Assert.Equal(assessment.Score!.Value, scored.Sum(metric => metric.WeightedContribution!.Value), 9);
        Assert.All(scored, metric => Assert.Equal(100 * metric.Weight / scored.Sum(item => item.Weight), metric.MaximumContribution!.Value, 9));
        Assert.Equal((int)Math.Round(assessment.Score.Value, MidpointRounding.AwayFromZero), assessment.RoundedScore);
        Assert.Equal(P.RatingFor(assessment.RoundedScore!.Value), assessment.Rating);
    }

    [Fact]
    public void MissingDataNeverEarnsPointsAndLowersCoverageInstead()
    {
        var complete = Assess(Ideal() with { Weather = Cloud(40) });
        var missing = Assess(Ideal() with { Weather = null });
        var cloud = missing[ShotViabilityMetric.CloudCover];
        Assert.Equal(ShotViabilityMetricStatus.Unavailable, cloud.Status);
        Assert.Null(cloud.Score);
        Assert.Null(cloud.WeightedContribution);
        Assert.Equal(ShotViabilitySeverity.None, cloud.Severity);
        // Not counted as full marks (which would lift the score above the rest), only against coverage.
        var rest = missing.Metrics.Where(metric => metric.IsEvaluated).ToArray();
        Assert.Equal(rest.Sum(metric => metric.Score!.Value * metric.Weight) / rest.Sum(metric => metric.Weight), missing.Score!.Value, 9);
        Assert.Equal(1 - P.CloudCoverWeight / 100, missing.Coverage, 9);
        Assert.Equal(ShotViabilityConfidence.Moderate, missing.Confidence);
        Assert.True(complete.Coverage > missing.Coverage);
    }

    [Fact]
    public void WithNothingToScoreThereIsNoScore()
    {
        var assessment = Assess(new ShotViabilityInput(Unsized, Moment, new HorizontalCoordinate(double.NaN, double.NaN)));
        Assert.Null(assessment.Score);
        Assert.Null(assessment.RoundedScore);
        Assert.Equal(ShotViabilityRating.Unavailable, assessment.Rating);
        Assert.Equal(0, assessment.Coverage);
        Assert.Equal(ShotViabilityConfidence.Low, assessment.Confidence);
    }

    [Fact]
    public void IdenticalInputsGiveIdenticalAssessments()
    {
        var input = Ideal() with { Weather = Cloud(33), SunAltitudeDegrees = -14, MoonPosition = new(150, 25), MoonIlluminatedFraction = 0.4 };
        string Fingerprint(ShotViabilityAssessment assessment) => string.Join(";", assessment.Metrics.Select(metric =>
            FormattableString.Invariant($"{metric.Id}:{metric.Status}:{metric.Score:R}:{metric.Severity}:{metric.Explanation}"))) +
            FormattableString.Invariant($"|{assessment.Score:R}|{assessment.Coverage:R}");
        var first = Fingerprint(Assess(input));
        for (var repeat = 0; repeat < 5; repeat++) Assert.Equal(first, Fingerprint(Assess(input with { })));
    }

    [Fact]
    public void ArbitraryInputsAlwaysScoreWithinZeroToOneHundred()
    {
        var random = new Random(20261008);
        for (var trial = 0; trial < 2_000; trial++)
        {
            double Between(double low, double high) => low + random.NextDouble() * (high - low);
            var input = Ideal() with
            {
                TargetPosition = new(Between(0, 360), Between(-90, 90)),
                SunAltitudeDegrees = random.Next(10) == 0 ? double.NaN : Between(-90, 90),
                MoonPosition = new(Between(0, 360), Between(-90, 90)),
                MoonIlluminatedFraction = Between(-0.2, 1.2),
                Weather = Cloud(random.Next(10) == 0 ? null : Between(-10, 110)),
                Terrain = FlatTerrain(Between(-5, 30)),
                Camera = new ShotViabilityCamera(Between(0, 360), Between(-45, 90), Between(0.5, 120), Between(0.5, 90)),
                Sensor = new ShotViabilitySensor(Between(-10, 2000), Between(0, 10), Between(0.5, 2))
            };
            var assessment = Assess(input);
            if (assessment.Score is double score) Assert.InRange(score, 0, 100);
            Assert.InRange(assessment.Coverage, 0, 1);
            Assert.All(assessment.Metrics.Where(metric => metric.IsEvaluated), metric => Assert.InRange(metric.Score!.Value, 0, 100));
        }
    }

    [Fact]
    public void SeverityIsIndependentOfTheOverallScore()
    {
        // Everything ideal except the camera, which points away: still a high score, with one critical warning.
        var assessment = Assess(Ideal() with { Camera = new ShotViabilityCamera(0, 60, 10, 6.7) });
        Assert.True(assessment.Score >= 80, $"score {assessment.Score}");
        var warning = Assert.Single(assessment.Warnings);
        Assert.Equal(ShotViabilityMetric.FramePosition, warning.Metric);
        Assert.Equal(ShotViabilitySeverity.Critical, assessment.HighestSeverity);
    }

    // ---- Cloud cover ---------------------------------------------------------------------------------------

    [Fact]
    public void UnknownForecastsStayUnknown()
    {
        foreach (var weather in new[]
                 {
                     Cloud(null), new WeatherResult(DataState.Loading, null, "Loading"), Cloud(10, stale: true),
                     Cloud(10, state: DataState.Stale), new WeatherResult(DataState.Error, null, "Failed"),
                     Cloud(10, forecast: Moment + Duration.FromHours(3)), Cloud(double.NaN), Cloud(140)
                 })
            Assert.Equal(ShotViabilityMetricStatus.Unavailable, Assess(Ideal() with { Weather = weather })[ShotViabilityMetric.CloudCover].Status);
        // A forecast for the same hour is used.
        Assert.True(Assess(Ideal() with { Weather = Cloud(10, forecast: Moment + Duration.FromMinutes(30)) })[ShotViabilityMetric.CloudCover].IsEvaluated);
    }

    [Theory]
    [InlineData(5, ShotViabilitySeverity.Good)]
    [InlineData(25, ShotViabilitySeverity.Good)]
    [InlineData(45, ShotViabilitySeverity.Caution)]
    [InlineData(75, ShotViabilitySeverity.Caution)]
    [InlineData(95, ShotViabilitySeverity.Critical)]
    public void CloudCoverSeverityFollowsItsBands(double cloud, ShotViabilitySeverity severity) =>
        Assert.Equal(severity, Assess(Ideal() with { Weather = Cloud(cloud) })[ShotViabilityMetric.CloudCover].Severity);

    // ---- Darkness and altitude -----------------------------------------------------------------------------

    [Theory]
    [InlineData(-25, ShotViabilitySeverity.Good)]
    [InlineData(-12, ShotViabilitySeverity.Good)]
    [InlineData(-8, ShotViabilitySeverity.Caution)]
    [InlineData(5, ShotViabilitySeverity.Critical)]
    public void DarknessIsJudgedFromTheSunsAltitude(double sun, ShotViabilitySeverity severity)
    {
        var darkness = Assess(Ideal() with { SunAltitudeDegrees = sun })[ShotViabilityMetric.Darkness];
        Assert.Equal(severity, darkness.Severity);
        Assert.Equal(P.Darkness.Evaluate(sun), darkness.Score!.Value, 9);
        Assert.Equal(sun, darkness.Measurements.Single().Value);
    }

    [Fact]
    public void ATargetBelowTheHorizonIsCriticalUnlessTheTerrainHorizonIsLowerStill()
    {
        var hidden = Assess(Ideal() with { TargetPosition = new(180, -3), Terrain = null })[ShotViabilityMetric.TargetAltitude];
        Assert.Equal(0, hidden.Score);
        Assert.Equal(ShotViabilitySeverity.Critical, hidden.Severity);
        // From a summit the terrain horizon can be below the astronomical one.
        var summit = Assess(Ideal() with { TargetPosition = new(180, -1), Terrain = FlatTerrain(-4) });
        Assert.Equal(ShotViabilitySeverity.Caution, summit[ShotViabilityMetric.TargetAltitude].Severity);
        Assert.Equal(ShotViabilitySeverity.Good, summit[ShotViabilityMetric.TerrainClearance].Severity);
    }

    // ---- Terrain -------------------------------------------------------------------------------------------

    [Fact]
    public void AnObstructedTargetScoresNearZeroWithACriticalWarning()
    {
        var assessment = Assess(Ideal() with { TargetPosition = new(180, 15), Terrain = FlatTerrain(20) });
        var terrain = assessment[ShotViabilityMetric.TerrainClearance];
        Assert.Equal(0, terrain.Score);
        Assert.Equal(ShotViabilitySeverity.Critical, terrain.Severity);
        Assert.Equal(-5, terrain.Measurements[0].Value, 9);
        Assert.Contains("hides the target", terrain.Explanation);
        Assert.Equal(ShotViabilityMetric.TerrainClearance, assessment.Warnings[0].Metric);
        // Exactly on the terrain horizon is hidden too.
        Assert.Equal(0, Assess(Ideal() with { TargetPosition = new(180, 20), Terrain = FlatTerrain(20) })[ShotViabilityMetric.TerrainClearance].Score);
    }

    [Fact]
    public void TerrainClearanceIsANarrowTransitionThenExcellent()
    {
        double Score(double clearance) => Assess(Ideal() with { TargetPosition = new(180, 40 + clearance), Terrain = FlatTerrain(40) })
            [ShotViabilityMetric.TerrainClearance].Score!.Value;
        Assert.True(Score(0.2) < 10);
        Assert.InRange(Score(1), 40, 55);
        Assert.True(Score(3) > 99);
        Assert.Equal(ShotViabilitySeverity.Caution,
            Assess(Ideal() with { TargetPosition = new(180, 41.5), Terrain = FlatTerrain(40) })[ShotViabilityMetric.TerrainClearance].Severity);
    }

    [Fact]
    public void LandscapeInTheFrameIsAMildDeepSkyPenaltyAndNoneForTheMoon()
    {
        FramingTerrainObstructionSample[] frame = [new(175, true, 1000, EffectiveCoverage: 0.6, RawCoverage: 0.6, FrameTerrainHorizonDegrees: 58),
            new(185, false, EffectiveCoverage: 0.1, RawCoverage: 0.1, FrameTerrainHorizonDegrees: 50)];
        var clear = Assess(Ideal())[ShotViabilityMetric.TerrainClearance];
        var landscape = Assess(Ideal() with { FrameTerrain = frame })[ShotViabilityMetric.TerrainClearance];
        Assert.Equal(clear.Score!.Value * (1 - P.LandscapeMaximumPenalty), landscape.Score!.Value, 6);
        Assert.Equal(60, landscape.Measurements.Single(measurement => measurement.Quantity == "frame-landscape").Value, 6);
        Assert.Contains("landscape fills up to 60%", landscape.Explanation);
        var moon = Assess(Ideal(Moon) with { FrameTerrain = frame })[ShotViabilityMetric.TerrainClearance];
        Assert.Equal(clear.Score!.Value, moon.Score!.Value, 9);
        // Samples from the legacy horizontal path (no frame scan) say nothing about the picture.
        var legacy = Assess(Ideal() with { FrameTerrain = [new FramingTerrainObstructionSample(180, true, 500)] });
        Assert.Equal(clear.Score!.Value, legacy[ShotViabilityMetric.TerrainClearance].Score!.Value, 9);
    }

    [Fact]
    public void TerrainThatIsOffLoadingOrMissingIsUnavailable()
    {
        foreach (var terrain in new[]
                 {
                     TerrainHorizonProfile.Disabled(Observer, Moment, 1.5), FlatTerrain() with { IsComplete = false },
                     new TerrainHorizonProfile(Observer, [], false, "Loading", Moment), FlatTerrain() with { HasDemCoverage = false }, null
                 })
            Assert.Equal(ShotViabilityMetricStatus.Unavailable, Assess(Ideal() with { Terrain = terrain })[ShotViabilityMetric.TerrainClearance].Status);
        Assert.Contains("turned off", Assess(Ideal() with { Terrain = TerrainHorizonProfile.Disabled(Observer, Moment, 1.5) })
            [ShotViabilityMetric.TerrainClearance].Explanation);
    }

    // ---- Moon ----------------------------------------------------------------------------------------------

    [Fact]
    public void AMoonBelowTheHorizonCostsNothing()
    {
        var moon = Assess(Ideal() with { MoonPosition = new(180, -1), MoonIlluminatedFraction = 1 })[ShotViabilityMetric.Moon];
        Assert.Equal(100, moon.Score);
        Assert.Equal(ShotViabilitySeverity.Good, moon.Severity);
    }

    [Fact]
    public void MoonlightWorsensWithIlluminationNearnessAndHeight()
    {
        double Score(double lit, double azimuth, double altitude) => Assess(Ideal() with
        {
            MoonPosition = new(azimuth, altitude), MoonIlluminatedFraction = lit
        })[ShotViabilityMetric.Moon].Score!.Value;
        Assert.True(Score(0.15, 180, 40) >= 85, "a thin Moon is generally favourable");
        for (var tenth = 0; tenth < 10; tenth++) Assert.True(Score((tenth + 1) / 10d, 180, 40) <= Score(tenth / 10d, 180, 40) + 1e-9);
        // Nearer the target is worse; on the horizon it barely matters.
        Assert.True(Score(1, 180, 40) < Score(1, 0, 40));
        Assert.True(Score(1, 0, 1) > Score(1, 0, 40));
        Assert.Equal(ShotViabilitySeverity.Caution, Assess(Ideal() with { MoonPosition = new(185, 50), MoonIlluminatedFraction = 0.95 })
            [ShotViabilityMetric.Moon].Severity);
        Assert.Equal(ShotViabilityMetricStatus.Unavailable, Assess(Ideal() with { MoonPosition = new(185, 50), MoonIlluminatedFraction = null })
            [ShotViabilityMetric.Moon].Status);
    }

    [Fact]
    public void AngularSeparationIsTheGreatCircleAngle()
    {
        Assert.Equal(0, ShotViabilityScorer.AngularSeparation(new(10, 20), new(10, 20)), 6);
        Assert.Equal(90, ShotViabilityScorer.AngularSeparation(new(0, 0), new(90, 0)), 6);
        Assert.Equal(90, ShotViabilityScorer.AngularSeparation(new(0, 0), new(123, 90)), 6);
        Assert.Equal(180, ShotViabilityScorer.AngularSeparation(new(0, 45), new(180, -45)), 6);
    }

    // ---- Framing and sampling ------------------------------------------------------------------------------

    [Fact]
    public void TheTargetsPlaceInTheFrameUsesTheCameraFramesOwnProjection()
    {
        double? Offset(double bearing, double pitch) => Assess(Ideal() with { Camera = new ShotViabilityCamera(bearing, pitch, 10, 6.7) })
            [ShotViabilityMetric.FramePosition].Measurements.SingleOrDefault()?.Value;
        Assert.Equal(0, Offset(180, 60)!.Value, 6);
        var inside = Assess(Ideal())[ShotViabilityMetric.FramePosition];
        Assert.Equal((100d, ShotViabilitySeverity.Good), (inside.Score!.Value, inside.Severity));
        // A target 4.5° from the axis of a 10° frame is near its edge; one 8° away is outside.
        var edge = Assess(Ideal() with { Camera = new ShotViabilityCamera(180, 64.5, 10, 10) })[ShotViabilityMetric.FramePosition];
        Assert.Equal(ShotViabilitySeverity.Caution, edge.Severity);
        Assert.InRange(edge.Score!.Value, 50, 100);
        var outside = Assess(Ideal() with { Camera = new ShotViabilityCamera(180, 68, 10, 10) })[ShotViabilityMetric.FramePosition];
        Assert.Equal((0d, ShotViabilitySeverity.Critical), (outside.Score!.Value, outside.Severity));
        Assert.Equal("The target is outside the current camera frame.", outside.Explanation);
        // Behind the camera.
        Assert.Equal(ShotViabilitySeverity.Critical, Assess(Ideal() with { Camera = new ShotViabilityCamera(0, 0, 10, 10) })[ShotViabilityMetric.FramePosition].Severity);
        Assert.Equal(ShotViabilityMetricStatus.Unavailable, Assess(Ideal() with { Camera = null })[ShotViabilityMetric.FramePosition].Status);
    }

    [Fact]
    public void AnOversizedTargetIsACautionNeverAnImpossibility()
    {
        // 20' × 10' against frames of 120', 22' and 8' along their long sides.
        var comfortable = Assess(Ideal() with { Camera = new ShotViabilityCamera(180, 60, 2, 1.333) })[ShotViabilityMetric.FramingFit];
        Assert.Equal((100d, ShotViabilitySeverity.Good), (comfortable.Score!.Value, comfortable.Severity));
        var tight = Assess(Ideal() with { Camera = new ShotViabilityCamera(180, 60, 22d / 60, 14d / 60) })[ShotViabilityMetric.FramingFit];
        Assert.Equal(ShotViabilitySeverity.Good, tight.Severity);
        Assert.InRange(tight.Score!.Value, 65, 100);
        Assert.Contains("tight fit", tight.Explanation);
        var large = Assess(Ideal() with { Camera = new ShotViabilityCamera(180, 60, 8d / 60, 5d / 60) })[ShotViabilityMetric.FramingFit];
        Assert.Equal(ShotViabilitySeverity.Caution, large.Severity);
        Assert.InRange(large.Score!.Value, P.FramingFit.Evaluate(1000), 40);
        Assert.Contains("mosaic", large.Explanation);
        // The minor axis is checked against the short side: rotating to fit is the photographer's choice.
        var portrait = Assess(Ideal() with { Camera = new ShotViabilityCamera(180, 60, 14d / 60, 22d / 60) })[ShotViabilityMetric.FramingFit];
        Assert.Equal(tight.Score!.Value, portrait.Score!.Value, 9);
    }

    [Fact]
    public void ImageSamplingProjectsTheTargetThroughTheLensOntoTheSensorsPixels()
    {
        // 10' at 500 mm: 2 × 500 × tan(5') = 1.4544 mm, over 3.76 µm pixels.
        var sampling = Assess(Ideal())[ShotViabilityMetric.ImageSampling];
        var minor = 2 * 500 * Math.Tan(10d / 60 / 2 * Math.PI / 180) / 0.00376;
        Assert.Equal(minor, sampling.Measurements.Single().Value, 6);
        Assert.InRange(minor, 380, 390);
        Assert.Equal(ShotViabilitySeverity.Good, sampling.Severity);
        // The framing multiplier scales the sensor, as it does the field of view.
        Assert.Equal(minor / 2, Assess(Ideal() with { Sensor = new ShotViabilitySensor(500, 3.76, 2) })[ShotViabilityMetric.ImageSampling]
            .Measurements.Single().Value, 6);
        // A wide lens leaves a small target only a few dozen pixels: a caution saying how many.
        var wide = Assess(Ideal() with { Sensor = new ShotViabilitySensor(50, 3.76) })[ShotViabilityMetric.ImageSampling];
        Assert.Equal(ShotViabilitySeverity.Caution, wide.Severity);
        Assert.Contains(string.Format(CultureInfo.CurrentCulture, "{0:0} × {1:0} sensor pixels", 2 * minor / 10, minor / 10), wide.Explanation);
    }

    [Fact]
    public void SamplingWithoutAResolutionOrASizeIsUnavailableAndSaysWhy()
    {
        var noResolution = Assess(Ideal() with { Sensor = new ShotViabilitySensor(500, null) })[ShotViabilityMetric.ImageSampling];
        Assert.Equal(ShotViabilityMetricStatus.Unavailable, noResolution.Status);
        Assert.Contains("Equipment", noResolution.Explanation);
        var unsized = Assess(Ideal(Unsized));
        Assert.Equal(ShotViabilityMetricStatus.Unavailable, unsized[ShotViabilityMetric.ImageSampling].Status);
        Assert.Equal(ShotViabilityMetricStatus.Unavailable, unsized[ShotViabilityMetric.FramingFit].Status);
        Assert.Contains("no reliable angular size", unsized[ShotViabilityMetric.FramingFit].Explanation);
        // Only a major axis: treated as round.
        Assert.Equal((12d, 12d), ShotViabilityScorer.AngularSizeOf(Unsized with { MajorAxisArcminutes = 12 }));
    }

    // ---- Target categories ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(AstralTargetCategory.Galaxy, ShotViabilityProfile.DeepSky)]
    [InlineData(AstralTargetCategory.EmissionNebula, ShotViabilityProfile.DeepSky)]
    [InlineData(AstralTargetCategory.GlobularCluster, ShotViabilityProfile.DeepSky)]
    [InlineData(AstralTargetCategory.Other, ShotViabilityProfile.DeepSky)]
    [InlineData(AstralTargetCategory.Star, ShotViabilityProfile.PointSource)]
    [InlineData(AstralTargetCategory.DoubleStar, ShotViabilityProfile.PointSource)]
    public void CataloguedTargetsTakeTheirProfileFromTheirCategory(AstralTargetCategory category, ShotViabilityProfile profile) =>
        Assert.Equal(profile, ShotViabilityScorer.ProfileOf(Galaxy with { Category = category }));

    [Fact]
    public void DaylightSunPhotographyIsNotJudgedByDeepSkyAssumptions()
    {
        var sun = Assess(Ideal(Sun) with { TargetPosition = new(180, 35), SunAltitudeDegrees = 35, MoonPosition = new(170, 30),
            MoonIlluminatedFraction = 0.8, Camera = new ShotViabilityCamera(180, 35, 10, 6.7), Window = null });
        Assert.Equal(ShotViabilityProfile.Sun, sun.Profile);
        foreach (var metric in new[] { ShotViabilityMetric.Darkness, ShotViabilityMetric.Moon, ShotViabilityMetric.UsefulWindow })
            Assert.Equal(ShotViabilityMetricStatus.NotApplicable, sun[metric].Status);
        // Not-applicable metrics count against neither the score nor coverage.
        Assert.Equal(1, sun.Coverage, 9);
        Assert.True(sun.Score >= 90, $"score {sun.Score}");
        Assert.Empty(sun.Warnings);
        // The Sun and Moon have known sizes.
        Assert.True(sun[ShotViabilityMetric.FramingFit].IsEvaluated);
        Assert.True(sun[ShotViabilityMetric.ImageSampling].IsEvaluated);
    }

    [Fact]
    public void ALowMoonIsAPictureNotAFailure()
    {
        var moonrise = Assess(Ideal(Moon) with { TargetPosition = new(90, 2), SunAltitudeDegrees = -3, Camera = new ShotViabilityCamera(90, 2, 10, 6.7) });
        Assert.InRange(moonrise[ShotViabilityMetric.TargetAltitude].Score!.Value, 60, 75);
        Assert.Equal(ShotViabilityMetricStatus.NotApplicable, moonrise[ShotViabilityMetric.Darkness].Status);
        Assert.True(P.SolarSystemAltitude.Evaluate(2) > P.DeepSkyAltitude.Evaluate(2) + 40);
    }

    [Fact]
    public void StarsSkipSizeBasedMetricsButNotTheSky()
    {
        var star = Assess(Ideal(Star));
        Assert.Equal(ShotViabilityProfile.PointSource, star.Profile);
        Assert.Equal(ShotViabilityMetricStatus.NotApplicable, star[ShotViabilityMetric.FramingFit].Status);
        Assert.Equal(ShotViabilityMetricStatus.NotApplicable, star[ShotViabilityMetric.ImageSampling].Status);
        Assert.True(star[ShotViabilityMetric.Darkness].IsEvaluated);
        Assert.Equal(1, star.Coverage, 9);
    }

    // ---- Light pollution -----------------------------------------------------------------------------------

    private static ShotViabilityMetricResult LightPollution(ShotViabilityLightPollution? data, AstralTarget? target = null) =>
        Assess(Ideal(target) with { LightPollution = data })[ShotViabilityMetric.LightPollution];

    /// <summary>The atlas's ratio becomes magnitudes of darkness lost, then a score: the documented values.</summary>
    [Theory]
    [InlineData(0, 0, 100)]
    [InlineData(0.05, 0.053, 98.4)]
    [InlineData(0.33, 0.310, 89.7)]
    [InlineData(1, 0.753, 74.9)]
    [InlineData(3, 1.505, 51.9)]
    [InlineData(9, 2.5, 30)]
    [InlineData(27, 3.618, 13.8)]
    [InlineData(99, 5, 5)]
    public void LightPollutionIsScoredOnTheSkyDarknessItCosts(double ratio, double magnitudes, double score)
    {
        var metric = LightPollution(ShotViabilityLightPollution.Of(ratio));
        Assert.True(metric.IsEvaluated);
        Assert.Equal(magnitudes, metric.Measurements.Single(measurement => measurement.Quantity == "magnitudes-lost").Value, 3);
        Assert.Equal(score, metric.Score!.Value, 1);
        Assert.Equal(ratio, metric.Measurements.Single(measurement => measurement.Quantity == "artificial-brightness-ratio").Value, 9);
        Assert.Equal(P.NaturalSkyMagnitude - P.MagnitudesLost(ratio),
            metric.Measurements.Single(measurement => measurement.Quantity == "zenith-sky-brightness").Value, 9);
    }

    [Fact]
    public void BrighterSkiesNeverScoreHigherAndTheBandsMeetCleanly()
    {
        var previous = double.PositiveInfinity;
        for (var ratio = 0.0; ratio <= 200; ratio += 0.01 + ratio * 0.01)
        {
            var score = LightPollution(ShotViabilityLightPollution.Of(ratio)).Score!.Value;
            Assert.InRange(score, 0, 100);
            Assert.True(score <= previous + 1e-9, $"a brighter sky ({ratio:F2}) scored higher");
            previous = score;
        }
        // Each explanation band boundary is continuous in the score (only the wording changes there).
        foreach (var boundary in new[] { P.DarkSiteRatio, P.RuralRatio, P.SomePollutionRatio, P.SuburbanRatio, P.BrightSuburbanRatio })
        {
            var below = LightPollution(ShotViabilityLightPollution.Of(boundary)).Score!.Value;
            var above = LightPollution(ShotViabilityLightPollution.Of(boundary + 1e-6)).Score!.Value;
            Assert.True(Math.Abs(below - above) < 0.01, $"the score jumps at {boundary}");
        }
    }

    [Theory]
    [InlineData(0.05, "Dark site", ShotViabilitySeverity.Good)]
    [InlineData(0.2, "Rural sky", ShotViabilitySeverity.Good)]
    [InlineData(0.8, "Some light pollution", ShotViabilitySeverity.Good)]
    [InlineData(2, "Suburban sky glow", ShotViabilitySeverity.Caution)]
    [InlineData(6, "Bright suburban sky", ShotViabilitySeverity.Caution)]
    [InlineData(40, "Urban sky glow", ShotViabilitySeverity.Caution)]
    public void LightPollutionIsExplainedPlainlyAndIsNeverCritical(double ratio, string band, ShotViabilitySeverity severity)
    {
        var metric = LightPollution(ShotViabilityLightPollution.Of(ratio));
        Assert.StartsWith(band, metric.Explanation);
        Assert.Equal(severity, metric.Severity);
        Assert.DoesNotContain("Bortle", metric.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownLightPollutionIsUnknownNotADarkSky()
    {
        var known = Assess(Ideal());
        foreach (var data in new ShotViabilityLightPollution?[]
                 {
                     null, new(LightPollutionDataStatus.NotInstalled), new(LightPollutionDataStatus.Loading),
                     new(LightPollutionDataStatus.OutsideCoverage), new(LightPollutionDataStatus.Unreadable),
                     new(LightPollutionDataStatus.Available), ShotViabilityLightPollution.Of(double.NaN), ShotViabilityLightPollution.Of(-1)
                 })
        {
            var assessment = Assess(Ideal() with { LightPollution = data });
            var metric = assessment[ShotViabilityMetric.LightPollution];
            Assert.Equal(ShotViabilityMetricStatus.Unavailable, metric.Status);
            Assert.Null(metric.Score);
            Assert.Equal(ShotViabilitySeverity.None, metric.Severity);
            Assert.Equal(1 - P.LightPollutionWeight / 100, assessment.Coverage, 9);
            Assert.StartsWith("Unknown", metric.Explanation.Replace("Reading", "Unknown"));
        }
        Assert.Contains("Settings", LightPollution(new(LightPollutionDataStatus.NotInstalled)).Explanation);
        Assert.Contains("outside", LightPollution(new(LightPollutionDataStatus.OutsideCoverage)).Explanation);
        Assert.Equal(1, known.Coverage, 9);
    }

    [Fact]
    public void LightPollutionCountsByItsWeightInTheOverallScore()
    {
        var dark = Assess(Ideal());
        var city = Assess(Ideal() with { LightPollution = ShotViabilityLightPollution.Of(50) });
        var lp = city[ShotViabilityMetric.LightPollution];
        var change = (dark[ShotViabilityMetric.LightPollution].Score!.Value - lp.Score!.Value) * P.LightPollutionWeight / 100;
        Assert.Equal(dark.Score!.Value - change, city.Score!.Value, 9);
        // A bright sky is advisory: a city shot of a well-placed target still scores, and is only a warning.
        Assert.True(city.Score > 80, $"score {city.Score}");
        Assert.Equal(ShotViabilitySeverity.Caution, city.HighestSeverity);
    }

    [Fact]
    public void LightPollutionAppliesToDeepSkyAndStarsButNotTheSunOrMoon()
    {
        Assert.True(LightPollution(ShotViabilityLightPollution.Of(1)).IsEvaluated);
        Assert.True(LightPollution(ShotViabilityLightPollution.Of(1), Star).IsEvaluated);
        Assert.Equal(ShotViabilityMetricStatus.NotApplicable, LightPollution(ShotViabilityLightPollution.Of(1), Sun).Status);
        Assert.Equal(ShotViabilityMetricStatus.NotApplicable, LightPollution(ShotViabilityLightPollution.Of(1), Moon).Status);
    }

    // ---- Useful window -------------------------------------------------------------------------------------

    private static UsefulWindowResult Window(ShotWindowPaths paths, TerrainHorizonProfile? terrain = null) =>
        UsefulWindowCalculator.Evaluate(paths, Moment, terrain)!;

    [Fact]
    public void TheWindowEndsAtTwilight()
    {
        var window = Window(Paths(_ => 60, hours => -24 + 4 * hours)); // −12° at +3 h.
        Assert.True(window.IsOpen);
        Assert.Equal(3, window.Remaining.TotalHours, 2);
        Assert.Equal(UsefulWindowLimit.Darkness, window.Limit);
        Assert.False(window.IsAtLeast);
    }

    [Fact]
    public void TheWindowEndsWhenTheTargetSinksBelowThirtyDegreesOrBehindTerrain()
    {
        var sinking = Window(Paths(hours => 50 - 10 * hours, _ => -30)); // 30° at +2 h.
        Assert.Equal((2d, UsefulWindowLimit.Altitude), (Math.Round(sinking.Remaining.TotalHours, 2), sinking.Limit));
        var terrain = FlatTerrain(at: bearing => bearing is > 170 and < 190 ? 45 : 1);
        var hidden = Window(Paths(hours => 50 - 5 * hours, _ => -30), terrain); // Behind a 45° ridge at +1 h.
        Assert.Equal((1d, UsefulWindowLimit.Terrain), (Math.Round(hidden.Remaining.TotalHours, 2), hidden.Limit));
        Assert.True(hidden.TerrainConsidered);
        // Without terrain the same path is only limited by its altitude.
        var noTerrain = Window(Paths(hours => 50 - 5 * hours, _ => -30));
        Assert.Equal(UsefulWindowLimit.Altitude, noTerrain.Limit);
        Assert.False(noTerrain.TerrainConsidered);
    }

    [Fact]
    public void OnlyTheOverlapOfFavourableConditionsCounts()
    {
        // High enough until +2 h; dark only from +1 h. Separately three and two hours; together one.
        var window = Window(Paths(hours => 50 - 10 * hours, hours => -4 - 8 * hours));
        Assert.False(window.IsOpen);
        Assert.Equal(TimeSpan.Zero, window.Remaining.ToTimeSpan());
        Assert.Equal(1, window.NextOpensIn!.Value.TotalHours, 2);
        Assert.Equal(1, window.NextLasts!.Value.TotalHours, 2);
        Assert.Equal(UsefulWindowLimit.Darkness, window.Limit);
        var metric = Assess(Ideal() with { Window = Paths(hours => 50 - 10 * hours, hours => -4 - 8 * hours) })[ShotViabilityMetric.UsefulWindow];
        Assert.Equal(0, metric.Score);
        Assert.Equal(ShotViabilitySeverity.Caution, metric.Severity);
        Assert.Contains("next window opens in 1.0 h and lasts 1.0 h", metric.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AWindowLongerThanTheHorizonIsAtLeastTheHorizon()
    {
        var window = Window(Paths(_ => 70, _ => -40));
        Assert.True(window is { IsOpen: true, IsAtLeast: true });
        Assert.Equal(P.UsefulWindowHorizon.TotalHours, window.Remaining.TotalHours, 6);
        Assert.Contains("At least 12.0 h", Assess(Ideal() with { Window = Paths(_ => 70, _ => -40) })[ShotViabilityMetric.UsefulWindow].Explanation);
    }

    [Fact]
    public void NoFavourableTimeAtAllIsSaid()
    {
        var window = Window(Paths(_ => 10, _ => -30));
        Assert.False(window.IsOpen);
        Assert.Null(window.NextOpensIn);
        Assert.Equal(UsefulWindowLimit.Altitude, window.Limit);
    }

    [Fact]
    public void WindowPathsThatDoNotFitAreUnavailableAndPendingOnesSaySo()
    {
        var paths = Paths(_ => 60, _ => -30);
        var misaligned = paths with { Sun = paths.Sun.Skip(1).ToArray() };
        Assert.Null(UsefulWindowCalculator.Evaluate(misaligned, Moment, null));
        Assert.Null(UsefulWindowCalculator.Evaluate(paths, Moment + Duration.FromDays(3), null));
        Assert.Equal(ShotViabilityMetricStatus.Unavailable, Assess(Ideal() with { Window = misaligned })[ShotViabilityMetric.UsefulWindow].Status);
        var pending = Assess(Ideal() with { Window = null, WindowPending = true })[ShotViabilityMetric.UsefulWindow];
        Assert.Equal(ShotViabilityMetricStatus.Unavailable, pending.Status);
        Assert.Contains("Calculating", pending.Explanation);
    }

    [Fact]
    public void WindowPathsCoverOnlyTheirTargetPlaceAndSpan()
    {
        var paths = Paths(_ => 60, _ => -30);
        Assert.True(paths.Covers("TEST:GALAXY", Observer with { ElevationMetres = 250 }, Moment));
        Assert.False(paths.Covers("test:other", Observer, Moment));
        Assert.False(paths.Covers("test:galaxy", Observer with { Latitude = 54.6 }, Moment));
        Assert.True(paths.Covers("test:galaxy", Observer, Moment + Duration.FromHours(18)));
        Assert.False(paths.Covers("test:galaxy", Observer, Moment + Duration.FromHours(19)));
        Assert.False(paths.Covers("test:galaxy", Observer, Moment - Duration.FromHours(7)));
    }

    [Fact]
    public async Task ThePlanningServicesWindowPathsAreTheTargetsAndTheSunsAtTheSameInstants()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var zones = new TimeZoneResolver();
        var astronomy = new AstronomyEngineService(zones);
        var service = new PlanningService(catalogue, astronomy, new Calculations.LensCalculator(), null!, null!, zones);
        var session = PlanningSession.Default(Moment, "Europe/London") with { TargetId = catalogue.Get("M31").Id, Observer = Observer };
        var start = Moment - Duration.FromTimeSpan(P.WindowPathsBefore);
        var end = Moment + Duration.FromTimeSpan(P.WindowPathsAfter);
        var paths = (await service.CalculateShotWindowPathsAsync(session, start, end, CancellationToken.None))!;
        Assert.Equal(catalogue.Get("M31").Id, paths.TargetId);
        Assert.Equal(paths.Target.Select(sample => sample.Instant), paths.Sun.Select(sample => sample.Instant));
        Assert.Equal(start, paths.Target[0].Instant);
        Assert.Equal(end, paths.Target[^1].Instant);
        Assert.Equal(Duration.FromTimeSpan(P.WindowPathsInterval), paths.Target[1].Instant - paths.Target[0].Instant);
        var sunNow = astronomy.Calculate(catalogue.Get("sun"), Observer, start, new LocalDate(2026, 10, 8), "Europe/London").Horizontal;
        Assert.Equal(sunNow.AltitudeDegrees, paths.Sun[0].Horizontal.AltitudeDegrees, 9);
        Assert.True(paths.Covers(paths.TargetId, Observer, Moment));
        // On an October night in Cumbria M31 is high and the Sun well down: the window is open.
        Assert.True(UsefulWindowCalculator.Evaluate(paths, Moment, null)!.IsOpen);
    }

    // ---- From the Planner ----------------------------------------------------------------------------------

    [Fact]
    public void ThePlannersSunAndMoonAreUsedOnlyForTheMomentTheyDescribe()
    {
        var sun = new TargetPosition(Sun, Moment, new HorizontalCoordinate(0, -30), new TargetEvents(null, null, null));
        var moon = new TargetPosition(Moon, Moment, new HorizontalCoordinate(90, 20), new TargetEvents(null, null, null), 0.3);
        var position = new TargetPosition(Galaxy, Moment, new HorizontalCoordinate(180, 60), new TargetEvents(null, null, null));
        var session = PlanningSession.Default(Moment, "UTC") with { TargetId = Galaxy.Id, Observer = Observer };
        var path = new AstralPath(new LocalDate(2026, 10, 8), "UTC", Duration.FromMinutes(10), [], new TargetEvents(null, null, null), Moment);
        var snapshot = new PlanningSnapshot(session, position, path, new FieldOfView(10, 6.7, 12), FlatTerrain(), new TerrainCrossings(null, null),
            Cloud(5), new AstronomyContext(sun, moon));
        var input = ShotViabilityPlanning.FromSnapshot(snapshot, null, null, null, null, false);
        Assert.Equal(-30, input.SunAltitudeDegrees);
        Assert.Equal(0.3, input.MoonIlluminatedFraction);
        // Scrubbed to a new moment before the Sun and Moon are recalculated: they are left out, not reused.
        var later = Moment + Duration.FromHours(2);
        var scrubbed = ShotViabilityPlanning.FromSnapshot(snapshot with { Position = position with { Instant = later } }, null, null, null, null, false);
        Assert.Null(scrubbed.SunAltitudeDegrees);
        Assert.Null(scrubbed.MoonPosition);
        Assert.Equal(ShotViabilityMetricStatus.Unavailable, Assess(scrubbed)[ShotViabilityMetric.Darkness].Status);
        // Window paths for another target are not used.
        var other = ShotViabilityPlanning.FromSnapshot(snapshot, null, null, null, Paths(_ => 60, _ => -30, targetId: "test:other"), true);
        Assert.Null(other.Window);
        Assert.True(other.WindowPending);
    }

    [Fact]
    public void TheCatalogueCarriesAngularSizesAsNumbersInEveryCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var m31 = new OpenNgcTargetCatalogue().Get("M31");
            Assert.Equal(177.83, m31.MajorAxisArcminutes!.Value, 6);
            Assert.Equal(69.66, m31.MinorAxisArcminutes!.Value, 6);
            Assert.Equal((177.83, 69.66), ShotViabilityScorer.AngularSizeOf(m31));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }
}
