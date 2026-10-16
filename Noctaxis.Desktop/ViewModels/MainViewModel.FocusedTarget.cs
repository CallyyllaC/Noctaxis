using Noctaxis.Core.Domain;
using Noctaxis.Core.Localisation;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>
/// The focused target — internally still the primary target — and the camera aimed at it. Focus aims the
/// camera once; Follow focused target keeps it aimed as the time, date, place or focused target changes.
/// Both use the target position the Planner already shows, and write the camera's own bearing and pitch.
/// </summary>
public partial class MainViewModel
{
    // Set by Focus; the aim waits for the new target's position when it has not been calculated yet.
    private bool _aimAtFocusedTarget;

    public string FocusedTargetName => CelestialObjects.FirstOrDefault(item => item.IsPrimary)?.DisplayName ?? "";
    public bool HasFocusedTarget => CelestialObjects.Any(item => item.IsPrimary);
    /// <summary>The focused target's position has been calculated, so its details can be shown.</summary>
    public bool HasFocusedTargetDetails => HasFocusedTarget && Snapshot is not null;

    /// <summary>
    /// Whether the camera follows the focused target. Saved with the camera's other framing settings, so
    /// it comes back as it was left.
    /// </summary>
    public bool FollowFocusedTarget
    {
        get => Settings.EffectiveCameraFraming.FollowFocusedTarget;
        set
        {
            if (value == FollowFocusedTarget) return;
            Settings = Settings with { CameraFraming = Settings.EffectiveCameraFraming with { FollowFocusedTarget = value } };
            NotifyCameraFrameProperties();
            QueueCameraFramingSave();
            // On: aim now. Off: leave the camera exactly where following left it.
            if (value) AimAtFocusedTarget();
        }
    }

    /// <summary>Following is on and there is a target to follow, so the camera's direction is not the user's to set.</summary>
    public bool IsFollowingFocusedTarget => FollowFocusedTarget && HasFocusedTarget;
    public bool IsCameraDirectionManual => !IsFollowingFocusedTarget;

    /// <summary>
    /// Focus: this target becomes the focused one. The camera is the camera's: it turns to the target only when
    /// Follow with camera is on, through the camera's own bearing and pitch; with Follow off it stays exactly
    /// where it is.
    /// </summary>
    private void FocusCelestialObject(CelestialObjectItemViewModel item)
    {
        MakePrimaryTarget(item);
        // Focusing a hidden target is refused; then nothing is aimed either.
        if (!item.IsPrimary || !IsFollowingFocusedTarget) return;
        _aimAtFocusedTarget = true;
        AimAtFocusedTarget();
    }

    /// <summary>
    /// Points the camera at the focused target: bearing to its azimuth, pitch to its altitude within the
    /// camera's limits. Nothing happens until the position on hand is the focused target's own.
    /// </summary>
    private void AimAtFocusedTarget()
    {
        // Only a focused target is aimed at: the plan's anchor alone is nothing to aim at.
        if (!HasFocusedTarget || CurrentSnapshot is not { } snapshot || !snapshot.Session.IsTargetFocused ||
            !snapshot.Position.Target.Id.Equals(_session.TargetId, StringComparison.OrdinalIgnoreCase)) return;
        _aimAtFocusedTarget = false;
        var position = snapshot.Position.Horizontal;
        // Aiming writes the camera's own direction; from then on the camera holds it, whatever the target does.
        var framing = (Settings.EffectiveCameraFraming with
        {
            ManualBearingDegrees = Angles.NormaliseDegrees(position.AzimuthDegrees),
            CompositionOffsetDegrees = 0,
            CameraPitchDegrees = position.AltitudeDegrees
        }).Normalised();
        if (framing == Settings.EffectiveCameraFraming) return;
        Settings = Settings with { CameraFraming = framing };
        NotifyCameraFrameProperties();
        QueueCameraFramingSave();
    }

    /// <summary>A new position: aim if Focus is waiting for it, or if the camera is following.</summary>
    private void AimAtNewPosition()
    {
        if (_aimAtFocusedTarget || IsFollowingFocusedTarget) AimAtFocusedTarget();
    }

    private void NotifyFocusedTarget()
    {
        OnPropertyChanged(nameof(FocusedTargetName));
        OnPropertyChanged(nameof(HasFocusedTarget));
        OnPropertyChanged(nameof(HasFocusedTargetDetails));
        OnPropertyChanged(nameof(IsFollowingFocusedTarget));
        OnPropertyChanged(nameof(IsCameraDirectionManual));
    }

    /// <summary>
    /// A rise, transit or set time, or why there is none. Without a position there is nothing to say
    /// ("—"); otherwise a missing event is either outside this date, or does not happen at all because
    /// the target stays on one side of the horizon all day.
    /// </summary>
    private string EventText(Func<TargetEvents, NodaTime.Instant?> select, bool? isRise)
    {
        if (Snapshot?.Position.Events is not { } events) return FormatTime(null);
        if (select(events) is { } time) return FormatTime(time);
        // isRise: true for the rise, false for the set, null for the transit, which a horizon does not prevent.
        return (isRise, events.Horizon) switch
        {
            (true, DayHorizon.AboveAllDay) => Strings.Get("Planner_Target_Events_UpAllDay"),
            (false, DayHorizon.AboveAllDay) => Strings.Get("Planner_Target_Events_DoesNotSet"),
            (true, DayHorizon.BelowAllDay) => Strings.Get("Planner_Target_Events_DoesNotRise"),
            (false, DayHorizon.BelowAllDay) => Strings.Get("Planner_Target_Events_DownAllDay"),
            _ => Strings.Get("Planner_Target_Events_NotOnThisDate")
        };
    }
}
