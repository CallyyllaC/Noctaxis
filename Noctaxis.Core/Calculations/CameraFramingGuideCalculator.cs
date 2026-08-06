using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Calculations;

public interface ICameraFramingGuideCalculator
{
    CameraFramingGuide Calculate(FieldOfView fieldOfView, double? primaryTargetAzimuthDegrees,
        CameraFramingSettings settings);
}

/// <summary>
/// Resolves the angular camera frame independently of any map projection. A primary target takes
/// precedence; the stored manual bearing is the fallback and is ready for future direct aiming UI.
/// </summary>
public sealed class CameraFramingGuideCalculator : ICameraFramingGuideCalculator
{
    public CameraFramingGuide Calculate(FieldOfView fieldOfView, double? primaryTargetAzimuthDegrees,
        CameraFramingSettings settings)
    {
        if (!double.IsFinite(fieldOfView.HorizontalDegrees) || fieldOfView.HorizontalDegrees is <= 0 or >= 180)
            throw new ArgumentOutOfRangeException(nameof(fieldOfView), "Horizontal field of view must be between 0 and 180 degrees.");

        var hasTarget = primaryTargetAzimuthDegrees is double target && double.IsFinite(target);
        var sourceBearing = hasTarget ? primaryTargetAzimuthDegrees!.Value : settings.ManualBearingDegrees;
        if (!double.IsFinite(sourceBearing) || !double.IsFinite(settings.CompositionOffsetDegrees))
            throw new ArgumentOutOfRangeException(nameof(settings), "Camera bearings must be finite.");

        var centre = Angles.NormaliseDegrees(sourceBearing + settings.CompositionOffsetDegrees);
        var halfField = fieldOfView.HorizontalDegrees / 2;
        return new CameraFramingGuide(
            centre,
            fieldOfView.HorizontalDegrees,
            Angles.NormaliseDegrees(centre - halfField),
            Angles.NormaliseDegrees(centre + halfField),
            hasTarget ? CameraFramingDirectionSource.PrimaryTarget : CameraFramingDirectionSource.ManualBearing);
    }
}
