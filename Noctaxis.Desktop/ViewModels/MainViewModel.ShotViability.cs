using System.Globalization;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using Noctaxis.Core.LightPollution;
using Noctaxis.Core.Localisation;
using Noctaxis.Core.Viability;
using Noctaxis.Desktop.Controls;
using NodaTime;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>
/// One metric of the shot-viability details, as the Camera frame's popover lists it: its name, its status (an
/// icon, a word for assistive technology and a colour) and one explanation that states its measurement once.
/// Scores, weights and raw measurements stay on the Core assessment; the photographer is not shown the arithmetic.
/// </summary>
public sealed record ShotViabilityRow(
    string Id,
    string Name,
    string Explanation,
    StatusKind Kind,
    string SeverityLabel,
    bool IsScored,
    string AccessibleText);

/// <summary>
/// The Camera frame's shot-viability indicator: a compact 0–100 answer with its warnings, derived on demand from
/// the Planner's current snapshot, the camera's pointing and equipment, and cached window paths. Nothing here
/// blocks planning, changes a setting or makes a network request; it is advisory only.
/// </summary>
/// <remarks>
/// The assessment is recomputed only when one of its inputs changes (the same memoisation as the framing
/// assessment), so dragging the camera or scrubbing the time costs one cheap scoring pass per change. It is
/// derived from the current snapshot alone, so it can never describe a previous place or target.
/// </remarks>
public partial class MainViewModel
{
    private ShotWindowPaths? _shotWindowPaths;
    private CancellationTokenSource? _shotWindowCalculation;
    private ShotViabilityInput? _shotViabilityInput;
    private ShotViabilityAssessment? _shotViability;
    private ShotViabilityAssessment? _shotViabilityRowsSource;
    private IReadOnlyList<ShotViabilityRow> _shotViabilityRows = [];

    /// <summary>The window paths' calculation in flight, if any; tests await it.</summary>
    internal Task ShotWindowCalculationForTesting { get; private set; } = Task.CompletedTask;

    /// <summary>The current assessment, or null with no focused target or no plan yet.</summary>
    public ShotViabilityAssessment? ShotViability
    {
        get
        {
            if (CurrentSnapshot is not { } snapshot || !snapshot.Session.IsTargetFocused)
            {
                _shotViabilityInput = null;
                return _shotViability = null;
            }
            var framing = Settings.EnableTerrainCalculations ? DerivedCameraFraming : null;
            var camera = new ShotViabilityCamera(CameraBearingDegrees, CameraPitchDegrees,
                snapshot.FieldOfView.HorizontalDegrees, snapshot.FieldOfView.VerticalDegrees);
            var input = ShotViabilityPlanning.FromSnapshot(snapshot, camera, framing?.EffectiveTerrainObstructions,
                SelectedCamera, _shotWindowPaths, _shotWindowRequest is not null, LightPollutionAt(snapshot.Session.Observer));
            if (_shotViability is null || _shotViabilityInput != input)
            {
                _shotViability = ShotViabilityScorer.Assess(input);
                _shotViabilityInput = input;
            }
            return _shotViability;
        }
    }

    /// <summary>The score in the Camera frame's header, "78", or a dash when nothing is assessed.</summary>
    public string ShotViabilityScoreText => ShotViability?.RoundedScore is int score
        ? score.ToString(CultureInfo.CurrentCulture)
        : Strings.Get("Planner_Viability_Summary_None");

    /// <summary>The rating after it, "· Good", shown while the header has room; empty when nothing is assessed.</summary>
    public string ShotViabilityRatingText => ShotViability is { RoundedScore: not null } assessment
        ? Strings.Format("Planner_Viability_RatingSuffix_Format", RatingText(assessment.Rating))
        : string.Empty;

    /// <summary>The popover's title: "Shot viability · 78/100 · Good".</summary>
    public string ShotViabilityTitle => ShotViability is { RoundedScore: int score } assessment
        ? Strings.Format("Planner_Viability_Title_Format", score, RatingText(assessment.Rating))
        : Strings.Get("Planner_Viability_Label");

    /// <summary>What the score is about: the focused target, or why there is no score.</summary>
    public string ShotViabilitySubject => ShotViability is { } assessment
        ? Strings.Format("Planner_Viability_Subject_Format", assessment.Target.DisplayName)
        : Strings.Get("Planner_Viability_NoTarget");

    /// <summary>The indicator's icon: the most severe warning's, else a tick, else information.</summary>
    public StatusKind ShotViabilityStatusKind => KindOf(ShotViability?.HighestSeverity ?? ShotViabilitySeverity.None);

    public int ShotViabilityWarningCount => ShotViability?.Warnings.Count ?? 0;
    public bool ShotViabilityHasWarnings => ShotViabilityWarningCount > 0;

    /// <summary>How much could be assessed, and so how far to trust the number.</summary>
    public string ShotViabilityCoverageText => ShotViability is { } assessment
        ? Strings.Format("Planner_Viability_Coverage_Format", assessment.Coverage * 100,
            Strings.Get("Planning_Viability_Confidence_" + assessment.Confidence))
        : string.Empty;

    public string ShotViabilityPolicyText => Strings.Format("Planner_Viability_Policy_Format", ShotViabilityPolicy.Id);

    /// <summary>The indicator's tooltip: its title, then that the score is advisory.</summary>
    public string ShotViabilityToolTip => ShotViabilityTitle + System.Environment.NewLine + Strings.Get("Planner_Viability_Advisory");

    /// <summary>The indicator's name for assistive technology: the score, its rating and how many warnings.</summary>
    public string ShotViabilityAccessibleName
    {
        get
        {
            if (ShotViability is not { RoundedScore: int score } assessment) return Strings.Get("Planner_Viability_Unavailable_AccessibleName");
            // Critical issues and warnings are named apart: a reader hears "1 critical issue", not just "1 warning".
            var critical = assessment.Warnings.Count(metric => metric.Severity == ShotViabilitySeverity.Critical);
            var cautions = assessment.Warnings.Count - critical;
            string? Critical() => critical switch
            {
                0 => null, 1 => Strings.Get("Planner_Viability_Issues_Critical_One"),
                _ => Strings.Format("Planner_Viability_Issues_Critical_Many_Format", critical)
            };
            string? Cautions() => cautions switch
            {
                0 => null, 1 => Strings.Get("Planner_Viability_Issues_Warning_One"),
                _ => Strings.Format("Planner_Viability_Issues_Warning_Many_Format", cautions)
            };
            var issues = (Critical(), Cautions()) switch
            {
                (null, null) => Strings.Get("Planner_Viability_Issues_None"),
                ({ } c, null) => c,
                (null, { } w) => w,
                ({ } c, { } w) => Strings.Format("Planner_Viability_Issues_Join_Format", c, w)
            };
            return Strings.Format("Planner_Viability_AccessibleName_Format", score, RatingText(assessment.Rating), issues);
        }
    }

    /// <summary>Every metric, in presentation order, for the details popover.</summary>
    public IReadOnlyList<ShotViabilityRow> ShotViabilityRows
    {
        get
        {
            var assessment = ShotViability;
            if (!ReferenceEquals(assessment, _shotViabilityRowsSource))
            {
                _shotViabilityRowsSource = assessment;
                // Every metric that applies to this target; one that does not apply is not listed at all.
                _shotViabilityRows = assessment?.Metrics.Where(metric => metric.Status != ShotViabilityMetricStatus.NotApplicable)
                    .Select(RowOf).ToArray() ?? [];
            }
            return _shotViabilityRows;
        }
    }

    private static ShotViabilityRow RowOf(ShotViabilityMetricResult metric)
    {
        var name = Strings.Get("Planning_Viability_Metric_" + metric.Metric);
        var severity = metric.Status switch
        {
            ShotViabilityMetricStatus.Evaluated => Strings.Get("Planning_Viability_Severity_" + metric.Severity),
            var status => Strings.Get("Planning_Viability_Status_" + status)
        };
        var accessible = Strings.Format("Planner_Viability_Row_AccessibleName_Format", name, severity, metric.Explanation);
        return new ShotViabilityRow(metric.Id, name, metric.Explanation, KindOf(metric.Severity), severity, metric.IsEvaluated, accessible);
    }

    private static string RatingText(ShotViabilityRating rating) => Strings.Get("Planning_Viability_Rating_" + rating);

    private static StatusKind KindOf(ShotViabilitySeverity severity) => severity switch
    {
        ShotViabilitySeverity.Critical => StatusKind.Error,
        ShotViabilitySeverity.Caution => StatusKind.Warning,
        ShotViabilitySeverity.Good => StatusKind.Success,
        _ => StatusKind.Information
    };

    private ShotViabilityAssessment? _publishedShotViability;
    private bool _hasPublishedShotViability;

    /// <summary>
    /// Publishes the indicator's properties when the assessment has changed, and only then: the assessment is
    /// memoised on its inputs, so the same instance means nothing shown would change. Camera drags, imports and
    /// terrain updates notify often; most of them leave the assessment as it was.
    /// </summary>
    private void NotifyShotViability()
    {
        var current = ShotViability;
        if (_hasPublishedShotViability && ReferenceEquals(current, _publishedShotViability)) return;
        _hasPublishedShotViability = true;
        _publishedShotViability = current;
        OnPropertyChanged(nameof(ShotViability));
        OnPropertyChanged(nameof(ShotViabilityScoreText));
        OnPropertyChanged(nameof(ShotViabilityRatingText));
        OnPropertyChanged(nameof(ShotViabilityTitle));
        OnPropertyChanged(nameof(ShotViabilityToolTip));
        OnPropertyChanged(nameof(ShotViabilitySubject));
        OnPropertyChanged(nameof(ShotViabilityStatusKind));
        OnPropertyChanged(nameof(ShotViabilityWarningCount));
        OnPropertyChanged(nameof(ShotViabilityHasWarnings));
        OnPropertyChanged(nameof(ShotViabilityCoverageText));
        OnPropertyChanged(nameof(ShotViabilityAccessibleName));
        OnPropertyChanged(nameof(ShotViabilityRows));
    }

    // ---- Light pollution at the place ----------------------------------------------------------------

    private ILightPollutionDataProvider? _lightPollutionReader;
    private (double Latitude, double Longitude, long Generation)? _lightPollutionPlace;
    private ShotViabilityLightPollution? _lightPollutionAtPlace;
    private CancellationTokenSource? _lightPollutionRead;

    /// <summary>The atlas read in flight, if any; tests await it.</summary>
    internal Task LightPollutionReadForTesting { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// The reader of the installed light-pollution atlas the Planner map already uses: the score reads the same
    /// local files through the same decoded-tile cache. Nothing is downloaded for it.
    /// </summary>
    public void UseLightPollutionReader(ILightPollutionDataProvider reader)
    {
        _lightPollutionReader = reader;
        _lightPollutionPlace = null;
        RequestLightPollutionAtPlace();
        NotifyShotViability();
    }

    private long LightPollutionGeneration => LightPollutionInstallation?.Generation ?? 0;

    /// <summary>The atlas's value at <paramref name="observer"/>, or why it is not known; never a guess.</summary>
    private ShotViabilityLightPollution LightPollutionAt(GeoCoordinate observer)
    {
        if (_lightPollutionReader is null || LightPollutionInstallation is { IsAvailable: false, ReadError: null })
            return new(LightPollutionDataStatus.NotInstalled);
        if (LightPollutionInstallation?.ReadError is not null) return new(LightPollutionDataStatus.Unreadable);
        return _lightPollutionPlace == (observer.Latitude, observer.Longitude, LightPollutionGeneration) && _lightPollutionAtPlace is { } known
            ? known : new(LightPollutionDataStatus.Loading);
    }

    /// <summary>Reads the atlas at the current place when the place or the installed atlas has changed, and only then.</summary>
    private void RequestLightPollutionAtPlace()
    {
        if (_lightPollutionReader is not { } reader || CurrentSnapshot is not { } snapshot) return;
        var observer = snapshot.Session.Observer;
        var place = (observer.Latitude, observer.Longitude, LightPollutionGeneration);
        if (_lightPollutionPlace == place) return;
        _lightPollutionPlace = place;
        _lightPollutionAtPlace = null;
        _lightPollutionRead?.Cancel();
        _lightPollutionRead?.Dispose();
        _lightPollutionRead = new CancellationTokenSource();
        LightPollutionReadForTesting = ReadLightPollutionAsync(reader, place, _lightPollutionRead.Token);
    }

    private async Task ReadLightPollutionAsync(ILightPollutionDataProvider reader,
        (double Latitude, double Longitude, long Generation) place, CancellationToken cancellationToken)
    {
        ShotViabilityLightPollution result;
        try
        {
            var ratio = await reader.SampleAsync(place.Latitude, place.Longitude, bilinear: true, cancellationToken).ConfigureAwait(true);
            result = ratio is double value ? ShotViabilityLightPollution.Of(value) : new(LightPollutionDataStatus.OutsideCoverage);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The light-pollution atlas could not be read for the shot-viability score");
            result = new(LightPollutionDataStatus.Unreadable);
        }
        if (cancellationToken.IsCancellationRequested || _lightPollutionPlace != place) return;
        _lightPollutionAtPlace = result;
        NotifyShotViability();
    }

    /// <summary>
    /// Asks for the focused target's and the Sun's paths around the planned moment when the cached ones do not
    /// cover it: a new place, a new target, or a moment moved out of the span. Scrubbing within the span asks for
    /// nothing. Local astronomy only; the Sun and Moon have no useful window, so they ask for nothing either.
    /// </summary>
    private void RequestShotWindowPaths()
    {
        if (CurrentSnapshot is not { Session.IsTargetFocused: true } snapshot) return;
        var target = snapshot.Position.Target;
        if (target.IsSun || target.IsMoon) return;
        var session = snapshot.Session;
        var instant = snapshot.Position.Instant;
        if (_shotWindowPaths?.Covers(target.Id, session.Observer, instant) == true) return;
        var place = string.Join("|", target.Id, session.Observer.Latitude.ToString("R", CultureInfo.InvariantCulture),
            session.Observer.Longitude.ToString("R", CultureInfo.InvariantCulture), session.TimeZoneId);
        // A service that has no paths for this place and target is not asked again until one of them changes.
        if (place == _shotWindowUnavailablePlace) return;
        // One request is enough while the moment stays inside the span it will cover.
        if (_shotWindowRequest is { } pending && pending.Place == place &&
            instant >= pending.Start && instant + Duration.FromTimeSpan(ShotViabilityPolicy.UsefulWindowHorizon) <= pending.End)
            return;
        _shotWindowCalculation?.Cancel();
        _shotWindowCalculation?.Dispose();
        _shotWindowCalculation = new CancellationTokenSource();
        var request = new ShotWindowRequest(place, instant - Duration.FromTimeSpan(ShotViabilityPolicy.WindowPathsBefore),
            instant + Duration.FromTimeSpan(ShotViabilityPolicy.WindowPathsAfter));
        _shotWindowRequest = request;
        ShotWindowCalculationForTesting = CalculateShotWindowPathsAsync(session with { TargetId = target.Id }, request,
            _shotWindowCalculation.Token);
    }

    private sealed record ShotWindowRequest(string Place, Instant Start, Instant End);
    private ShotWindowRequest? _shotWindowRequest;
    private string? _shotWindowUnavailablePlace;

    private async Task CalculateShotWindowPathsAsync(PlanningSession session, ShotWindowRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var paths = await _planning.CalculateShotWindowPathsAsync(session, request.Start, request.End, cancellationToken)
                .ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(request, _shotWindowRequest)) return;
            _shotWindowRequest = null;
            if (paths is null) _shotWindowUnavailablePlace = request.Place;
            else _shotWindowPaths = paths;
            NotifyShotViability();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // The rest of the assessment does not depend on the window; it is reported as unavailable.
            _logger.LogWarning(ex, "Shot-viability window paths could not be calculated");
            if (ReferenceEquals(request, _shotWindowRequest))
            {
                _shotWindowRequest = null;
                _shotWindowUnavailablePlace = request.Place;
                NotifyShotViability();
            }
        }
    }
}
