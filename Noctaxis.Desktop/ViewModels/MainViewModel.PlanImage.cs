using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Export;
using Noctaxis.Core.Localisation;
using Noctaxis.Core.Measurements;
using Noctaxis.Desktop.Export;
using NodaTime;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>The clean map for a plan image, centred on a location and sized in sheet pixels; null when there is none.</summary>
public delegate Task<ExportMapImage?> ExportMapSource(GeoCoordinate centre, int width, int height, CancellationToken cancellationToken);

/// <summary>What an import did: whether the plan was applied, and the message shown for it.</summary>
public sealed record PlanImportOutcome(bool Imported, string Message);

/// <summary>
/// Plan images: export draws one sheet from the plan on screen (<see cref="CreateExportPngAsync"/>), and every
/// import — Open plan image…, Load from clipboard, a dropped file — goes through <see cref="ImportPlanImage"/>.
/// A plan image carries only the minimal plan (<see cref="NoctaxisPlan"/>); everything else is recalculated and
/// fetched fresh when it is opened.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The weather panel's rows: one every three hours across the 24-hour window.</summary>
    internal const int ExportWeatherRows = 8;

    /// <summary>
    /// How long export waits for weather: once for the fresh refresh and once for the panel's rows. Weather is never a
    /// reason not to have a plan image, so a slow provider costs at most this, then the sheet says what it has.
    /// </summary>
    internal TimeSpan ExportWeatherTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The minimal plan the Planner shows now, or for <paramref name="session"/>'s place and moment.</summary>
    internal NoctaxisPlan CurrentPlan(PlanningSession? session = null)
    {
        var source = session ?? _session;
        var focusedId = source.IsTargetFocused ? source.TargetId : null;
        return new NoctaxisPlan(source.Instant, source.Observer.Latitude, source.Observer.Longitude,
            CameraBearingDegrees, CameraPitchDegrees,
            string.IsNullOrWhiteSpace(focusedId) ? null : new NoctaxisPlanFocus(focusedId, FollowFocusedTarget));
    }

    /// <summary>
    /// The plan image: the canonical sheet (<see cref="IPlannerExportRenderer"/>) with the plan embedded. Save PNG
    /// writes these bytes and Copy PNG puts these same bytes on the clipboard. Everything the sheet shows is taken
    /// from the plan the export started from; its weather is refreshed for that plan's own place and moment, so the
    /// sheet never pairs one plan with weather fetched for another.
    /// </summary>
    /// <remarks>
    /// Weather is never required. A fresh forecast is used when the refresh succeeds. When it fails or times out,
    /// the sheet uses the forecast already cached for the plan and says when that was retrieved; with none, its
    /// weather panel says the weather is unavailable and why, and shows no values. Missing weather is never drawn
    /// as conditions. Only cancellation, a missing focused target or a plan not yet calculated stop an export.
    /// </remarks>
    public async Task<byte[]> CreateExportPngAsync(CancellationToken cancellationToken, ExportMapSource? map = null)
    {
        if (Snapshot is not { } exported)
        {
            StatusMessage = Strings.Get("Planner_Export_NotReady");
            throw new ExportNotPossibleException(StatusMessage);
        }
        var target = ExportTarget(exported) ?? throw new ExportNotPossibleException(StatusMessage);
        var exportedWeather = Settings.EffectiveWeather;
        var plan = CurrentPlan(exported.Session);
        var zone = TimelineZone;
        var (start, end, events) = (TimelineStart, TimelineEnd, TimelineEvents);
        var header = (LocationName, Coordinates: exported.Session.Observer.ToString(),
            Subject: Strings.Format("Export_Sheet_Subject_Format", target.Name, start.InZone(zone).ToDateTimeUnspecified(),
                end.InZone(zone).ToDateTimeUnspecified(), Noctaxis.Core.Time.TimeZoneDisplay.Describe(zone, start).ShortName),
            Equipment: Strings.Format("Export_Sheet_Equipment_Format", SelectedCamera?.DisplayName ?? "—", SelectedLens?.DisplayName ?? "—",
                exported.Session.Lens.FocalLengthMillimetres, exported.FieldOfView.HorizontalDegrees, exported.FieldOfView.VerticalDegrees));
        var theme = ExportTheme.Current();
        StatusMessage = Strings.Get("Planner_Export_RefreshingWeather");
        _logger.LogInformation("Starting export weather refresh");
        try
        {
            var result = await RefreshExportWeatherAsync(exported.Session, exportedWeather, cancellationToken);
            var fresh = result.State == DataState.Ready && result.Conditions is not null;
            // Without a fresh forecast the sheet keeps the plan's own weather, whatever it is; its panel says what it shows.
            var rendered = fresh ? exported with { Weather = result } : exported;
            if (fresh && IsCurrentWeatherRequest(exported.Session, exportedWeather) && Snapshot is { } current)
            {
                // The plan on screen would still make this same request, so it takes the result too.
                Snapshot = ReferenceEquals(current, exported) ? rendered : current with { Weather = result };
                NotifySnapshotProperties();
            }
            else if (fresh) _logger.LogInformation("The plan changed during export; the sheet keeps the plan it started from");
            else _logger.LogWarning("Fresh weather was unavailable for export ({Reason}); the sheet uses what is cached", result.Message);
            var (hours, note) = await ExportWeatherAsync(exported.Session, exportedWeather, start, end, zone,
                fresh ? null : result.Message, cancellationToken);
            var mapImage = map is null ? null : await ExportMapAsync(map, exported.Session.Observer, cancellationToken);
            var snapshot = new PlannerExportSnapshot(plan, rendered, header.LocationName, header.Coordinates, header.Subject,
                header.Equipment, target, events, start, end, zone, hours, note, mapImage,
                Strings.Format("Export_Sheet_Footer_Format", typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "",
                    Strings.Get("Export_Sheet_WeatherCredit")),
                theme);
            var png = NoctaxisPlanImage.Embed(_exporter.RenderPng(snapshot), plan);
            StatusMessage = Strings.Get(fresh ? "Planner_Export_Ready" : "Planner_Export_ReadyWithoutFreshWeather");
            return png;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The details go to the log; the status line says so in the user's language (NX-04).
            _logger.LogWarning(ex, "Plan image export failed");
            StatusMessage = Strings.Get("Planner_Export_FailedUnexpectedly");
            throw;
        }
    }

    /// <summary>
    /// The sheet's one target: the focused target, as the Timeline calculated it. With nothing focused there is no
    /// sheet — the export never guesses one or draws every shown target — and the reason is the status message.
    /// </summary>
    private ExportTargetSeries? ExportTarget(PlanningSnapshot exported)
    {
        if (!exported.Session.IsTargetFocused || FocusedTargetId is not { } focused ||
            !focused.Equals(exported.Session.TargetId, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = Strings.Get("Planner_Export_FocusRequired");
            return null;
        }
        // The paths and events must be the ones for this plan's place and window, not the previous ones the Timeline
        // still shows while it calculates new ones: a sheet never pairs one plan with another's target path.
        if (!TimelineHeldFor(exported.Session) ||
            TimelinePaths.FirstOrDefault(path => path.Target.Id.Equals(focused, StringComparison.OrdinalIgnoreCase)) is not { } path)
        {
            StatusMessage = Strings.Get("Planner_Export_TargetPending");
            return null;
        }
        return ExportTargetSeries.From(path, exported.Terrain, Controls.CelestialPalette.Role(exported, path.Target));
    }

    /// <summary>
    /// The fresh weather for the exported plan, or an unavailable result saying why: a provider error, no answer within
    /// <see cref="ExportWeatherTimeout"/> (even from a provider that ignores cancellation), or a failure of the request
    /// itself. Cancelling the export still cancels it.
    /// </summary>
    private async Task<WeatherResult> RefreshExportWeatherAsync(PlanningSession session, WeatherSettings settings,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExportWeatherTimeout);
        // WaitAsync bounds the wait even for a provider that does not observe the token (NX-40).
        try { return await _planning.RefreshWeatherAsync(session, settings, timeout.Token).WaitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WeatherResult(DataState.Error, null, Strings.Get("Planner_Export_WeatherTimedOut"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Export weather refresh failed");
            return new WeatherResult(DataState.Error, null, ex.Message);
        }
    }

    /// <summary>
    /// The weather across the window, a row every few hours, in the user's units, and the note that goes with it:
    /// none for a fresh forecast; when the refresh failed (<paramref name="refreshProblem"/>), that the rows are a
    /// cached forecast and when it was retrieved; and with no rows, that the weather is unavailable and why.
    /// </summary>
    private async Task<(IReadOnlyList<ExportWeatherHour> Hours, string? Note)> ExportWeatherAsync(PlanningSession session,
        WeatherSettings settings, Instant start, Instant end, DateTimeZone zone, string? refreshProblem, CancellationToken cancellationToken)
    {
        var units = Settings.EffectiveMeasurementSystem;
        var step = Duration.FromHours(Math.Max(1, Math.Round((end - start).TotalHours / ExportWeatherRows)));
        var hours = new List<ExportWeatherHour>();
        string? problem = refreshProblem;
        Instant? oldest = null;
        var stale = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ExportWeatherTimeout);
        for (var time = start; time < end && hours.Count < ExportWeatherRows; time += step)
        {
            WeatherResult result;
            try { result = await _planning.LoadWeatherAsync(session with { Instant = time }, settings, timeout.Token).WaitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                problem ??= Strings.Get("Planner_Export_WeatherTimedOut");
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Export weather for {Time} failed", time);
                problem ??= ex.Message;
                continue;
            }
            if (result.State != DataState.Ready || result.Conditions is not { } w) { problem ??= result.Message; continue; }
            if (oldest is null || w.RetrievedAt < oldest) oldest = w.RetrievedAt;
            stale |= w.IsStale;
            hours.Add(new ExportWeatherHour(time, Controls.HorizonGraph.Clock(time, zone),
                Percent(w.CloudCoverPercent), Percent(w.PrecipitationProbabilityPercent),
                MeasurementUnits.FormatWindSpeed(w.WindSpeedMetresPerSecond, units),
                MeasurementUnits.FormatTemperature(w.TemperatureCelsius, units)));
        }
        if (hours.Count == 0)
            return (hours, problem is { Length: > 0 }
                ? Strings.Format("Export_Sheet_WeatherUnavailable_Format", problem)
                : Strings.Get("Export_Sheet_WeatherUnavailable"));
        // Rows that did not come from a fresh refresh are a cached forecast: say so, and how old.
        if ((refreshProblem is not null || stale) && oldest is { } retrieved)
            return (hours, Strings.Format("Export_Sheet_WeatherCached_Format", retrieved.InZone(zone).ToDateTimeUnspecified()));
        return (hours, null);
    }

    private static string Percent(double? value) => value is { } v ? $"{v:F0}%" : "—";

    /// <summary>The clean map, or none: a sheet is still worth having when the map cannot be drawn.</summary>
    private async Task<ExportMapImage?> ExportMapAsync(ExportMapSource map, GeoCoordinate centre, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { return await map(centre, PlannerExportRenderer.MapWidth, PlannerExportRenderer.MapHeight, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Plan image map timed out; the sheet is drawn without it");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Plan image map could not be drawn; the sheet is drawn without it");
            return null;
        }
    }

    public void ReportExportDestinationFailure(Exception exception)
    {
        StatusMessage = Strings.Format("Planner_Export_Failed_Format", exception.Message);
        _logger.LogWarning(exception, "Plan image destination failed");
    }

    /// <summary>
    /// The one import of a plan image, whatever it came from. It reads the image, validates the plan and resolves
    /// its target before anything changes; only a valid plan is applied, and then only its place, moment, camera
    /// direction and focus — terrain, elevation, astronomy and weather are recalculated and fetched as for any
    /// other change. A target no longer in the list leaves the plan without focus and with Follow off.
    /// </summary>
    public PlanImportOutcome ImportPlanImage(PlanImageSource source)
    {
        if (!_initialised) return Report(false, Strings.Get("Planner_Import_NotReady"));
        if (source.Problem != PlanImageSourceProblem.None) return Report(false, Message(source.Problem));
        var read = NoctaxisPlanImage.Read(source.Bytes);
        if (read.Plan is not { } plan) return Report(false, Message(read.Error));
        var target = plan.Focus is { } focus
            ? CelestialObjects.FirstOrDefault(item => item.TargetId.Equals(focus.TargetId, StringComparison.OrdinalIgnoreCase))
            : null;
        // Everything the plan needs is worked out before anything of the Planner changes.
        ApplyPlan(plan, target);
        _logger.LogInformation("Plan image imported");
        return Report(true, plan.Focus is not null && target is null
            ? Strings.Format("Planner_Import_TargetMissing_Format", plan.Focus.TargetId)
            : Strings.Get("Planner_Import_Done"));
    }

    private PlanImportOutcome Report(bool imported, string message)
    {
        StatusMessage = message;
        if (!imported) _logger.LogInformation("Plan image not imported: {Reason}", message);
        return new(imported, message);
    }

    private void ApplyPlan(NoctaxisPlan plan, CelestialObjectItemViewModel? target)
    {
        // Nothing is aimed while the plan is applied; Follow is restored last, and only with its target.
        FollowFocusedTarget = false;
        CommitObserverLocation(new GeoCoordinate(plan.Latitude, plan.Longitude));
        // The plan's moment is exact: it is applied as that instant, never rebuilt from the local clock.
        SetPlannerInstant(plan.TimeUtc);
        if (target is not null)
        {
            if (!target.IsVisible) target.IsVisible = true;
            SetPrimaryTarget(target);
        }
        else SetPrimaryTarget(null);
        SaveCelestialSelections();
        ScheduleAstronomyRefresh(40);
        CameraBearingDegrees = plan.BearingDegrees;
        CameraPitchDegrees = plan.PitchDegrees;
        FollowFocusedTarget = target is not null && plan.Focus!.Follow;
        SelectedPageIndex = 1;
    }

    private static string Message(PlanImageSourceProblem problem) => Strings.Get("Planner_Import_" + problem);
    private static string Message(NoctaxisPlanError error) => Strings.Get("Planner_Import_" + error);
}

/// <summary>
/// An export that cannot be made now for an expected reason the status line already states: the plan is not calculated
/// yet, nothing is focused, or the focused target's path is still being calculated. The export handlers treat only
/// this as quiet; any other failure is reported (NX-08).
/// </summary>
public sealed class ExportNotPossibleException(string message) : InvalidOperationException(message);
