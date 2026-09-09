using CommunityToolkit.Mvvm.ComponentModel;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Planning;
using Noctaxis.Desktop.Controls;
using Microsoft.Extensions.Logging;

namespace Noctaxis.Desktop.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private double _settingsMinimumTerrainFrameCoveragePercent = 5;
    public double CameraPitchDegrees
    {
        get => Settings.EffectiveCameraFraming.CameraPitchDegrees;
        set
        {
            var framing = (Settings.EffectiveCameraFraming with { CameraPitchDegrees = value }).Normalised();
            if (framing == Settings.EffectiveCameraFraming) return;
            Settings = Settings with { CameraFraming = framing };
            NotifyCameraFrameProperties();
            QueueCameraFramingSave();
        }
    }
    public double CameraBearingDegrees
    {
        get => CurrentSnapshot is { } snapshot ? _cameraFramingGuideCalculator.Calculate(snapshot.FieldOfView,
            snapshot.Position.Horizontal.AzimuthDegrees, Settings.EffectiveCameraFraming).CentreBearingDegrees
            : Settings.EffectiveCameraFraming.ManualBearingDegrees;
        set
        {
            if (!double.IsFinite(value)) return;
            var bearing = Angles.NormaliseDegrees(value);
            if (Math.Abs(Angles.NormaliseSignedDegrees(bearing - CameraBearingDegrees)) < 1e-10) return;
            var target = CurrentSnapshot?.Position.Horizontal.AzimuthDegrees;
            Settings = Settings with { CameraFraming = Settings.EffectiveCameraFraming with
            {
                ManualBearingDegrees = bearing,
                CompositionOffsetDegrees = target.HasValue ? Angles.NormaliseSignedDegrees(bearing - target.Value) : 0
            }};
            NotifyCameraFrameProperties();
            QueueCameraFramingSave();
        }
    }

    public CameraTerrainDepth? TerrainFrameDepth => Settings.EnableTerrainCalculations
        ? DerivedCameraFraming?.CameraDepth : null;
    public double TerrainFrameThreshold => Settings.EffectiveCameraFraming.MinimumTerrainFrameCoveragePercent / 100;
    public string TerrainFrameStatus => !Settings.EnableTerrainCalculations ? "Terrain calculations disabled"
        : TerrainFrameDepth is not null ? string.Empty
        : PlannerRefresh.GroundTerrainState is PlannerRefreshWorkState.Running or PlannerRefreshWorkState.Pending
            ? "Resolving terrain…" : "Terrain unavailable";
    public string CameraFrameDiagnostics => !ShowTerrainDebugOverlay ? string.Empty :
        CameraFramingVisibility?.EffectiveTerrainObstructions.MinBy(s => Math.Abs(Angles.NormaliseSignedDegrees(s.BearingDegrees - TerrainDebugBearing))) is { } sample
        ? $"Camera pitch: {CameraPitchDegrees:0.0}°\nVertical FoV: {CurrentSnapshot?.FieldOfView.VerticalDegrees:0.0}°\n" +
          $"Frame bounds: {TerrainFrameDepth?.LowerAltitude:0.0}° to {TerrainFrameDepth?.UpperAltitude:0.0}°\n" +
          $"Terrain horizon: {sample.FrameTerrainHorizonDegrees:0.0}°\nRaw coverage: {sample.RawCoverage:P1}\n" +
          $"Minimum coverage: {TerrainFrameThreshold:P0}\nEffective coverage: {sample.EffectiveCoverage:P1}\n" +
          $"Display strength: {TerrainTintPresentation.DisplayStrength(sample.EffectiveCoverage):P1}\n" +
          $"Threshold altitude: {sample.ThresholdAltitudeDegrees:0.0}°\nMeaningful frontier: {sample.FirstObstructionDistanceMetres:0} m"
        : TerrainFrameStatus;

    private void NotifyCameraFrameProperties()
    {
        OnPropertyChanged(nameof(CameraPitchDegrees)); OnPropertyChanged(nameof(CameraBearingDegrees));
        OnPropertyChanged(nameof(CameraFramingGuide)); OnPropertyChanged(nameof(CameraFramingVisibility));
        OnPropertyChanged(nameof(FramingVisibilityStatus)); OnPropertyChanged(nameof(TerrainFrameDepth));
        OnPropertyChanged(nameof(TerrainFrameStatus)); OnPropertyChanged(nameof(TerrainFrameThreshold));
        OnPropertyChanged(nameof(CameraFrameDiagnostics)); OnPropertyChanged(nameof(TerrainDebugBearing));
    }

    private bool _cameraSavePending;
    private bool _cameraSaveRunning;
    private Task _cameraSaveTask = Task.CompletedTask;
    internal Task WaitForCameraFramingPersistenceAsync() => _cameraSaveTask;

    private void QueueCameraFramingSave()
    {
        _cameraSavePending = true;
        if (!_cameraSaveRunning) _cameraSaveTask = SaveLatestCameraFramingAsync();
    }

    private async Task SaveLatestCameraFramingAsync()
    {
        _cameraSaveRunning = true;
        try
        {
            while (_cameraSavePending)
            {
                _cameraSavePending = false;
                await PersistAsync(CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _logger.LogWarning(ex, "Could not persist camera framing"); }
        finally { _cameraSaveRunning = false; }
    }
}
