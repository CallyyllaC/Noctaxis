using Noctaxis.Core.Domain;

namespace Noctaxis.Core.Tests;

public sealed class EquipmentAndObserverTests
{
    [Fact]
    public void ObserverState_UnresolvedZeroAndManualRemainDistinctDuringTerrainResolution()
    {
        var unresolved = new ObserverElevationState();
        Assert.Equal(TerrainElevationResolutionState.Unresolved, unresolved.ResolutionState);
        Assert.Null(unresolved.ResolvedGroundElevationAslMetres);
        var zero = unresolved.WithTerrainGroundElevation(0);
        Assert.Equal(TerrainElevationResolutionState.TerrainResolved, zero.ResolutionState);
        Assert.Equal(0, zero.ResolvedGroundElevationAslMetres);
        var manual = unresolved.WithManualOverride(-20);
        Assert.Equal(TerrainElevationResolutionState.ManualOverride, manual.ResolutionState);
        Assert.Equal(-20, manual.WithTerrainGroundElevation(120).ResolvedGroundElevationAslMetres);
        Assert.Equal(-20, manual.EffectiveObserverAltitudeAsl(999, 0));
        Assert.Equal(-17.5, manual.EffectiveObserverAltitudeAsl(999, 2.5));
        Assert.Null(manual.ResetManualOverride().ResolvedGroundElevationAslMetres);
    }

    [Fact]
    public void ObserverElevation_ResolvesTerrainOverrideResetAndEffectiveAltitude()
    {
        var automatic = new ObserverElevationState().WithTerrainGroundElevation(120);

        Assert.False(automatic.IsManualOverride);
        Assert.Equal(120, automatic.ResolveGroundElevationAsl(15));
        Assert.Equal(121.7, automatic.EffectiveObserverAltitudeAsl(15, 1.7), 10);

        var manual = automatic.WithManualOverride(250);
        Assert.True(manual.IsManualOverride);
        Assert.Equal(250, manual.ResolveGroundElevationAsl(15));
        Assert.Equal(251.7, manual.EffectiveObserverAltitudeAsl(15, 1.7), 10);

        var refreshed = manual.WithTerrainGroundElevation(135);
        Assert.Equal(250, refreshed.ResolveGroundElevationAsl(15));

        var reset = refreshed.ResetManualOverride();
        Assert.False(reset.IsManualOverride);
        Assert.Equal(135, reset.ResolveGroundElevationAsl(15));
        Assert.Equal(136.7, reset.EffectiveObserverAltitudeAsl(15, 1.7), 10);
    }

    [Fact]
    public void CameraProfiles_RequireNameAndPositiveFiniteSensorDimensions()
    {
        Assert.True(new CameraProfile("camera", "Full Frame", 36, 24).IsValid);
        Assert.False(new CameraProfile("camera", "", 36, 24).IsValid);
        Assert.False(new CameraProfile("camera", "Broken", 0, 24).IsValid);
        Assert.False(new CameraProfile("camera", "Broken", 36, double.NaN).IsValid);
    }

    [Fact]
    public void LensProfiles_ValidatePrimeAndZoomRangesAndClampFocalLength()
    {
        var prime = new LensProfile("prime", "24 mm", 24, 24);
        var zoom = new LensProfile("zoom", "70-200 mm", 70, 200);

        Assert.True(prime.IsValid);
        Assert.True(prime.IsPrime);
        Assert.Equal(24, prime.ClampFocalLength(85));
        Assert.True(zoom.IsValid);
        Assert.False(zoom.IsPrime);
        Assert.Equal(70, zoom.ClampFocalLength(24));
        Assert.Equal(135, zoom.ClampFocalLength(135));
        Assert.Equal(200, zoom.ClampFocalLength(300));
        Assert.False(new LensProfile("bad", "Bad", 0, 100).IsValid);
        Assert.False(new LensProfile("bad", "Bad", 200, 70).IsValid);
    }

    [Fact]
    public void EmptyEquipment_MigratesLegacySensorAndFocalLengthOnceWithStableIds()
    {
        var legacy = new LensConfiguration(SensorPreset.ApsC, 23.6, 15.7, 35);

        var first = new EquipmentSettings().EnsureUsable(legacy);
        var second = first.EnsureUsable(legacy);

        var camera = Assert.Single(first.Cameras!);
        var lens = Assert.Single(first.Lenses!);
        Assert.Equal(23.6, camera.SensorWidthMillimetres);
        Assert.Equal(15.7, camera.SensorHeightMillimetres);
        Assert.Equal(35, lens.MinimumFocalLengthMillimetres);
        Assert.Equal(35, lens.MaximumFocalLengthMillimetres);
        Assert.Single(second.Cameras!);
        Assert.Single(second.Lenses!);
        Assert.Equal(camera.Id, second.Cameras![0].Id);
        Assert.Equal(lens.Id, second.Lenses![0].Id);
    }
}
