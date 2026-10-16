using CommunityToolkit.Mvvm.Input;
using Noctaxis.Core.Localisation;
using Noctaxis.Core.Time;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>
/// The Planner's time bar: the plan's date and local time, each stepped by its arrows or picked from its own
/// picker, Now, and the time zone they are in. All of it reads and writes the one date and time the Planner
/// already has (<see cref="LocalDate"/> and <see cref="TimeText"/>); there is no second time model.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The step of the time bar's arrows.</summary>
    public const int TimeStepMinutes = 30;

    /// <summary>The plan's date as the time bar shows it.</summary>
    public string PlannerDateText => LocalDate is { } date ? Strings.Format("Planner_Timeline_Date_Format", date.DateTime) : "—";

    /// <summary>
    /// The time zone the plan's date and time are in, by its short name at the plan's moment (GMT or BST, say),
    /// never by how it was chosen: a Settings choice of the system zone shows the system's zone.
    /// </summary>
    public string PlannerTimeZoneText => DescribePlannerTimeZone().ShortName;

    /// <summary>The time zone's identifier and offset, for the detail behind its short name.</summary>
    public string PlannerTimeZoneDetail
    {
        get
        {
            var zone = DescribePlannerTimeZone();
            return Strings.Format("Planner_TimeZone_Detail_Format", zone.Identifier, TimeZoneDisplay.FormatOffset(zone.Offset));
        }
    }

    private (string ShortName, string Identifier, NodaTime.Offset Offset) DescribePlannerTimeZone() =>
        TimeZoneDisplay.Describe(_timeZones.Resolve(TimeZoneId), _session.Instant);

    [RelayCommand]
    private void PreviousTimeStep() => StepTime(-TimeStepMinutes);

    [RelayCommand]
    private void NextTimeStep() => StepTime(TimeStepMinutes);

    /// <summary>
    /// Moves the plan's local time by <paramref name="minutes"/> on the clock, carrying into the day before or
    /// after across midnight, and applies date and time together as one change.
    /// </summary>
    public void StepTime(int minutes)
    {
        if (LocalDate is null || !LocalTimePatternHelper.TryParse(TimeText, out var time)) return;
        var local = NodaTime.LocalDate.FromDateTime(LocalDate.Value.DateTime).At(time).PlusMinutes(minutes);
        SetLocalDateTime(local);
    }

    /// <summary>The date picker's choice: the plan moves to that date at the same local time.</summary>
    public void SetPlannerDate(DateTime date) =>
        LocalDate = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The time picker's choice: the plan moves to that local time on the same date.</summary>
    public void SetPlannerTime(TimeSpan time) =>
        TimeText = $"{time.Hours:00}:{time.Minutes:00}";

    private void SetLocalDateTime(NodaTime.LocalDateTime local)
    {
        _suppressChanges = true;
        LocalDate = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, TimeSpan.Zero);
        MinutesOfDay = local.Hour * 60 + local.Minute;
        TimeText = $"{local.Hour:00}:{local.Minute:00}";
        _suppressChanges = false;
        ApplyLocalDateTime(true);
        // A time stepped from the bar: the Timeline now runs 24 hours on from it.
        AnchorTimeline();
        NotifyTimeBar();
    }

    /// <summary>
    /// Moves the plan to an exact moment, such as an imported plan's: the bar shows it in the plan's zone, and the
    /// moment keeps its seconds and, in an autumn fold, its own occurrence of the repeated local hour. The
    /// Timeline is anchored there as for any moment set from the bar. The caller schedules the recalculation.
    /// </summary>
    private void SetPlannerInstant(NodaTime.Instant instant)
    {
        _session = _session with { Instant = instant, TimeZoneId = TimeZoneId };
        LoadTimeControls();
        AnchorTimeline();
        NotifyTimeBar();
    }

    /// <summary>
    /// The planning location as the bar shows it, on one line: its name, the pin's coordinates and the
    /// camera's altitude above sea level, each as the Planner already words it ("Location 5 · 53.39276°,
    /// -0.47433° · 13.3 m ASL"). The coordinates follow the pin while it is dragged. This exact text is what
    /// copying the location copies.
    /// </summary>
    public string PlannerLocationText => string.Join(" · ", LocationName, PreviewObserver.ToString(), EffectiveCameraAltitudeShortText);

    /// <summary>
    /// What stands behind the location line, for its tooltip: what the altitude is made of, and the credit for
    /// the place name when a lookup supplied it.
    /// </summary>
    public string PlannerLocationDetail => string.Join(Environment.NewLine,
        new[] { EffectiveCameraAltitudeDetail, CurrentLocationAttribution }.Where(line => !string.IsNullOrWhiteSpace(line)));

    /// <summary>The location line was copied: said where the Planner says what it has just done.</summary>
    public void ReportLocationCopied() => StatusMessage = Strings.Get("Planner_Location_Copied_Status");

    private void NotifyTimeBar()
    {
        OnPropertyChanged(nameof(PlannerDateText));
        OnPropertyChanged(nameof(PlannerTimeZoneText));
        OnPropertyChanged(nameof(PlannerTimeZoneDetail));
    }
}
