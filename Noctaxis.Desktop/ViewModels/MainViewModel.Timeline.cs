using CommunityToolkit.Mvvm.ComponentModel;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;
using Noctaxis.Core.Planning;
using NodaTime;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>
/// The Timeline: one window of 24 hours, on from where it is anchored, across midnight if it falls there, and
/// one scrubber within it. It has no date or time of its own: the scrubber moves the Planner's one date and
/// time (<see cref="LocalDate"/>, <see cref="TimeText"/>), and the bar above shows the result.
/// </summary>
/// <remarks>
/// The window is anchored where the plan's moment is set directly — a date or time from the bar, its arrows,
/// its pickers, or Now — so the Timeline then shows the next 24 hours from it. Scrubbing moves the moment within
/// the window and never moves the window, during the drag or after it, so nothing slides under the pointer.
/// If the moment is ever set outside the window by any other route, the window follows it there.
/// </remarks>
public partial class MainViewModel
{
    private Instant _timelineStart = Instant.MinValue;
    private CancellationTokenSource? _timelineCalculation;
    private string? _timelineKey;
    /// <summary>What the paths and events now held were calculated for; they stay shown while newer ones are calculated.</summary>
    private string? _timelinePathsKey;

    /// <summary>The Timeline window's start: where it was last anchored.</summary>
    public Instant TimelineStart => _timelineStart;
    /// <summary>The Timeline window's end, 24 hours after its start.</summary>
    public Instant TimelineEnd => _timelineStart + IPlanningService.TimelineLength;
    /// <summary>The zone the Timeline's times and dates are read in: the plan's own.</summary>
    public DateTimeZone TimelineZone => _timeZones.Resolve(TimeZoneId);
    /// <summary>The real present moment, which the Timeline marks faintly when it falls inside the window.</summary>
    public Instant TimelineRealNow => _clock.GetCurrentInstant();
    /// <summary>The scrubber's whole range, in minutes.</summary>
    public double TimelineLengthMinutes => IPlanningService.TimelineLength.TotalMinutes;
    /// <summary>One keyboard step of the scrubber: the configured time snap, as the old slider used.</summary>
    public double TimelineStepMinutes => Math.Clamp(Settings.TimeSnapMinutes, 1, 30);

    /// <summary>Each target's path across the window, calculated when the window, place or targets change.</summary>
    [ObservableProperty] private IReadOnlyList<TimelinePath> _timelinePaths = [];

    /// <summary>
    /// The Sun's and Moon's events inside the window, calculated with its paths: once per window, never as the
    /// chart draws or as the moment is scrubbed.
    /// </summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TimelineChartDescription))]
    private TimelineEvents _timelineEvents = TimelineEvents.None;

    partial void OnTimelinePathsChanged(IReadOnlyList<TimelinePath> value) => OnPropertyChanged(nameof(TimelineChartDescription));

    /// <summary>
    /// The chart for assistive technology: the moment chosen, then each event cue it draws with its exact
    /// time ("Sunset 18:37, Astronomical darkness begins 20:34"). The darkness band itself adds nothing more.
    /// </summary>
    public string TimelineChartDescription
    {
        get
        {
            var zone = TimelineZone;
            var cues = TimelineEvents.Events
                .Where(item => item.Instant >= TimelineStart && item.Instant <= TimelineEnd &&
                    Controls.HorizonGraph.ShowsEvent(item.Kind, TimelinePaths, FocusedTargetId))
                .Select(item => Strings.Format("Planner_Timeline_EventSpoken_" + item.Kind + "_Format", Controls.HorizonGraph.Clock(item.Instant, zone)))
                .ToArray();
            return cues.Length == 0 ? TimelineSelectionText
                : Strings.Format("Planner_Timeline_ChartDescription_Format", TimelineSelectionText, string.Join(", ", cues));
        }
    }

    /// <summary>The scrubber: how far into the window the plan's moment is, in minutes.</summary>
    [ObservableProperty] private double _timelineOffsetMinutes;

    /// <summary>
    /// The moment chosen, as the bar's date and time say it now: during a scrub this moves with the thumb at
    /// once, before the plan's own recalculation lands, so the chart's cursor never lags the scrubber.
    /// </summary>
    public Instant TimelineSelectedInstant => LocalDate is { } date && LocalTimePatternHelper.TryParse(TimeText, out var time)
        ? ControlsInstant(NodaTime.LocalDate.FromDateTime(date.DateTime), time)
        : _session.Instant;

    /// <summary>The scrubber's value as a person reads it, its date and its time, for assistive technology.</summary>
    public string TimelineSelectionText
    {
        get
        {
            var local = TimelineSelectedInstant.InZone(TimelineZone).LocalDateTime;
            return Strings.Format("Planner_Timeline_Selection_Format", local.ToDateTimeUnspecified(), local.ToDateTimeUnspecified());
        }
    }

    partial void OnTimelineOffsetMinutesChanged(double value)
    {
        if (_suppressChanges || _timelineStart == Instant.MinValue) return;
        // Within the window, on the configured time snap of the clock (not of the offset), as the old slider was.
        var zone = TimelineZone;
        var snap = Math.Clamp(Settings.TimeSnapMinutes, 1, 30);
        var zoned = (_timelineStart + Duration.FromMinutes(Math.Clamp(value, 0, TimelineLengthMinutes))).InZone(zone);
        var raw = zoned.LocalDateTime;
        var minuteOfDay = raw.Hour * 60 + raw.Minute + raw.Second / 60d;
        var snapped = raw.Date.AtMidnight().PlusMinutes((long)(Math.Round(minuteOfDay / snap) * snap));
        // In an autumn fold the snapped clock time happens twice: keep the occurrence being scrubbed through.
        var mapping = zone.MapLocal(snapped);
        var instant = (mapping.Count == 2 && mapping.Last().Offset == zoned.Offset
            ? mapping.Last()
            : snapped.InZoneLeniently(zone)).ToInstant();
        if (instant < _timelineStart) instant = _timelineStart;
        if (instant > TimelineEnd) instant = TimelineEnd;
        var local = instant.InZone(zone).LocalDateTime;
        _controlsInstant = instant;
        _suppressChanges = true;
        LocalDate = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, TimeSpan.Zero);
        MinutesOfDay = local.Hour * 60 + local.Minute;
        TimeText = $"{local.Hour:00}:{local.Minute:00}";
        // The thumb shows the moment chosen, on its snap.
        TimelineOffsetMinutes = (instant - _timelineStart).TotalMinutes;
        _suppressChanges = false;
        PreviewTemporalPosition();
        ScheduleTemporalCommit();
        OnPropertyChanged(nameof(TimelineSelectionText));
    }

    /// <summary>The window starts at the plan's moment and runs 24 hours on from it.</summary>
    private void AnchorTimeline()
    {
        _timelineStart = _session.Instant;
        OnPropertyChanged(nameof(TimelineStart));
        OnPropertyChanged(nameof(TimelineEnd));
        OnPropertyChanged(nameof(TimelineZone));
        OnPropertyChanged(nameof(TimelineStepMinutes));
        SyncTimelineOffset();
        RequestTimelinePaths();
    }

    /// <summary>The window stays where it is unless the plan's moment has left it.</summary>
    private void KeepMomentInTimeline()
    {
        if (_session.Instant < _timelineStart || _session.Instant > TimelineEnd) AnchorTimeline();
        else SyncTimelineOffset();
    }

    private void SyncTimelineOffset()
    {
        var suppressed = _suppressChanges;
        _suppressChanges = true;
        TimelineOffsetMinutes = Math.Clamp((_session.Instant - _timelineStart).TotalMinutes, 0, TimelineLengthMinutes);
        _suppressChanges = suppressed;
        OnPropertyChanged(nameof(TimelineSelectionText));
        OnPropertyChanged(nameof(TimelineRealNow));
    }

    /// <summary>
    /// Calculates the window's paths when what they depend on has changed — the window, the place, the zone or
    /// the targets — and not otherwise: scrubbing within the window recalculates nothing here.
    /// </summary>
    private void RequestTimelinePaths()
    {
        if (_timelineStart == Instant.MinValue) return;
        var session = _session;
        var key = TimelineKey(session, _timelineStart);
        if (key == _timelineKey) return;
        _timelineKey = key;
        _timelineCalculation?.Cancel();
        _timelineCalculation?.Dispose();
        _timelineCalculation = new CancellationTokenSource();
        _ = CalculateTimelinePathsAsync(session, _timelineStart, key, _timelineCalculation.Token);
    }

    private static string TimelineKey(PlanningSession session, Instant start) =>
        string.Join("|", start.ToUnixTimeTicks(), session.Observer.Latitude, session.Observer.Longitude,
            session.TimeZoneId, session.TargetId, string.Join(",", session.EffectiveVisibleObjects
                .Where(item => item.IsVisible).OrderBy(item => item.Order).Select(item => item.TargetId)));

    /// <summary>
    /// Whether the Timeline's paths and events now held were calculated for <paramref name="session"/>'s place, zone and
    /// targets over the current window, rather than being the previous ones still shown while those are calculated.
    /// </summary>
    private bool TimelineHeldFor(PlanningSession session) =>
        _timelinePathsKey is { } held && held == TimelineKey(session, _timelineStart);

    private async Task CalculateTimelinePathsAsync(PlanningSession session, Instant start, string key, CancellationToken cancellationToken)
    {
        try
        {
            var pathsTask = _planning.CalculateTimelineAsync(session, start, cancellationToken);
            var eventsTask = _planning.CalculateTimelineEventsAsync(session, start, cancellationToken);
            var paths = await pathsTask.ConfigureAwait(true);
            var events = await eventsTask.ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || key != _timelineKey) return;
            _timelinePathsKey = key;
            TimelinePaths = paths;
            TimelineEvents = events;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // The rest of the Planner does not depend on the Timeline's paths; keep the last ones.
            Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(_logger, ex, "Timeline paths could not be calculated");
            if (key == _timelineKey) _timelineKey = null;
        }
    }
}
