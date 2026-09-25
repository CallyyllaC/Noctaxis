using System.Collections.Immutable;
using Mapsui;
using Mapsui.Extensions;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Controls;

public enum EnvironmentalPixelClassification
{
    OutsideCone,
    Clear,
    TerrainObstructed,
    GroundFacing,
    BeyondVisibility,
    TerrainObstructedAndBeyondVisibility
}

public readonly record struct EnvironmentalTerrainSample(
    double BearingDegrees,
    double UnwrappedBearingDegrees,
    double OffsetDegrees,
    bool IsObstructed,
    double? ObstructionDistanceMetres, double EffectiveCoverage = 1);

public readonly record struct EnvironmentalProfileTexel(
    float ObstructionDistanceMetres,
    bool IsObstructed, float EffectiveCoverage = 1);

public readonly record struct EnvironmentalProfileKey(
    double ObserverLatitude,
    double ObserverLongitude,
    double ObserverHeightAboveGroundMetres,
    double TerrainAngularDetailDegrees,
    long TerrainGeneratedAtTicks,
    int CompletedBearingCount,
    int ProviderStateFingerprint);

public readonly record struct EnvironmentalTerrainTextureKey(
    EnvironmentalProfileKey Profile,
    double CentreBearingDegrees,
    double HorizontalFovDegrees,
    double MaximumDistanceMetres,
    int TerrainSampleFingerprint);

public readonly record struct EnvironmentalOverlayKey(
    EnvironmentalTerrainTextureKey TerrainTexture,
    GeoCoordinate Observer,
    double? WeatherVisibilityDistanceMetres);

public readonly record struct EnvironmentalRenderParameters(
    float ConeOpacity,
    float WeatherOpacityScale,
    uint TerrainColourArgb,
    float TerrainTintOpacity = .40f)
{
    public static EnvironmentalRenderParameters Default { get; } = new(.10f, 1f, 0xff9a6f9e);

    public EnvironmentalRenderParameters Normalised() => new(
        Math.Clamp(float.IsFinite(ConeOpacity) ? ConeOpacity : .10f, 0, .5f),
        Math.Clamp(float.IsFinite(WeatherOpacityScale) ? WeatherOpacityScale : 1f, 0, 1),
        TerrainColourArgb | 0xff000000,
        Math.Clamp(float.IsFinite(TerrainTintOpacity) ? TerrainTintOpacity : .40f, .15f, .55f));
}

public readonly record struct EnvironmentalRenderKey(
    double Width,
    double Height,
    double WorldOriginX,
    double WorldOriginY,
    double WorldStepXX,
    double WorldStepXY,
    double WorldStepYX,
    double WorldStepYY,
    EnvironmentalRenderParameters Parameters);

public sealed record EnvironmentalOverlayState(
    GeoCoordinate Observer,
    double CentreBearingDegrees,
    double HorizontalFovDegrees,
    double MaximumDistanceMetres,
    double? WeatherVisibilityDistanceMetres,
    ImmutableArray<EnvironmentalTerrainSample> SourceSamples,
    ImmutableArray<EnvironmentalProfileTexel> ProfileTexels,
    EnvironmentalProfileKey ProfileKey,
    EnvironmentalOverlayKey OverlayKey,
    long ProfileRevision,
    long TerrainTextureRevision,
    long OverlayRevision)
{
    public PlanTerrainFan? TerrainFan { get; init; }
}

public readonly record struct EnvironmentalOverlayFrame(
    float Width,
    float Height,
    float WorldOriginX,
    float WorldOriginY,
    float WorldStepXX,
    float WorldStepXY,
    float WorldStepYX,
    float WorldStepYY,
    EnvironmentalRenderKey RenderKey);

public sealed class EnvironmentalOverlayDiagnostics
{
    private long _profileStateChanges;
    private long _overlayStateRebuilds;
    private long _renderInvalidations;
    private long _profileUploads;
    private long _drawCalls;
    private long _shaderCompilations;

    public long ProfileStateChanges => Interlocked.Read(ref _profileStateChanges);
    public long OverlayStateRebuilds => Interlocked.Read(ref _overlayStateRebuilds);
    public long RenderInvalidations => Interlocked.Read(ref _renderInvalidations);
    public long ProfileUploads => Interlocked.Read(ref _profileUploads);
    public long DrawCalls => Interlocked.Read(ref _drawCalls);
    public long ShaderCompilations => Interlocked.Read(ref _shaderCompilations);

    internal void ProfileChanged() => Interlocked.Increment(ref _profileStateChanges);
    internal void OverlayRebuilt() => Interlocked.Increment(ref _overlayStateRebuilds);
    internal void RenderInvalidated() => Interlocked.Increment(ref _renderInvalidations);
    internal void ProfileUploaded() => Interlocked.Increment(ref _profileUploads);
    internal void Drawn() => Interlocked.Increment(ref _drawCalls);
    internal void ShaderCompiled() => Interlocked.Increment(ref _shaderCompilations);
}

public sealed class EnvironmentalOverlayStateCoordinator(
    EnvironmentalOverlayDiagnostics? diagnostics = null,
    int profileTextureWidth = EnvironmentalOverlayStateFactory.DefaultProfileTextureWidth)
{
    private readonly EnvironmentalOverlayDiagnostics _diagnostics = diagnostics ?? new EnvironmentalOverlayDiagnostics();
    private EnvironmentalProfileKey? _profileKey;
    private EnvironmentalOverlayKey? _overlayKey;
    private EnvironmentalTerrainTextureKey? _terrainTextureKey;
    private EnvironmentalRenderKey? _renderKey;
    private EnvironmentalOverlayState? _state;
    private ImmutableArray<EnvironmentalProfileTexel> _profileTexels = [];
    private long _profileRevision;
    private long _terrainTextureRevision;
    private long _overlayRevision;
    private TerrainHorizonProfile? _terrain;
    private CameraTerrainDepth? _cameraDepth;
    public long PlanSampleEvaluations { get; private set; }

    public EnvironmentalOverlayDiagnostics Diagnostics => _diagnostics;

    public EnvironmentalOverlayState Update(
        GeoCoordinate observer,
        GeoSector sector,
        FramingVisibilityAssessment? visibility,
        EnvironmentalProfileKey profileKey,
        TerrainHorizonProfile? terrain = null)
    {
        var depth = terrain is null ? null : visibility?.CameraDepth;
        var sameDepth = ReferenceEquals(depth, _cameraDepth) || depth is not null && _cameraDepth is not null &&
            depth.LowerAltitude == _cameraDepth.LowerAltitude && depth.UpperAltitude == _cameraDepth.UpperAltitude &&
            depth.Bearings.AsSpan().SequenceEqual(_cameraDepth.Bearings.AsSpan()) &&
            depth.DistancesMetres.AsSpan().SequenceEqual(_cameraDepth.DistancesMetres.AsSpan());
        var reuse = terrain is not null && ReferenceEquals(_terrain, terrain) &&
                    sameDepth &&
                    _state is not null && _state.ProfileKey == profileKey &&
                    _state.CentreBearingDegrees == sector.CentreBearingDegrees &&
                    _state.HorizontalFovDegrees == sector.HorizontalFovDegrees &&
                    _state.MaximumDistanceMetres == sector.DistanceMetres;
        var fan = reuse ? _state!.TerrainFan : terrain is not null
            ? depth is not null ? PlanTerrainFan.Build(sector, terrain, depth) : new PlanTerrainFan([], false, [])
            : null;
        var source = reuse ? _state!.SourceSamples : fan is not null
            ? fan.Rays.Select(ray => new EnvironmentalTerrainSample(ray.BearingDegrees,
                sector.LeftBearingDegrees + Angles.NormaliseDegrees(ray.BearingDegrees - sector.LeftBearingDegrees),
                Angles.NormaliseDegrees(ray.BearingDegrees - sector.LeftBearingDegrees),
                !ray.Bands.IsEmpty, ray.Bands.IsEmpty ? null : ray.Bands[0].StartDistanceMetres,
                ray.Bands.IsEmpty ? 0 : 1)).ToImmutableArray()
            : EnvironmentalOverlayStateFactory.OrderSamples(sector, visibility);
        if (terrain is not null && !reuse) PlanSampleEvaluations += source.Length;
        _terrain = terrain;
        _cameraDepth = depth;
        var terrainTextureKey = EnvironmentalOverlayStateFactory.CreateTerrainTextureKey(profileKey, sector, source);
        if (fan is not null)
        {
            var hash = new HashCode();
            hash.Add(terrainTextureKey.TerrainSampleFingerprint); hash.Add(fan.GroundFacing);
            foreach (var ray in fan.Rays)
                foreach (var band in ray.Bands)
                { hash.Add(band.StartDistanceMetres); hash.Add(band.EndDistanceMetres); hash.Add(band.Strength);
                    hash.Add(band.ApparentAltitudeDegrees); }
            terrainTextureKey = terrainTextureKey with { TerrainSampleFingerprint = hash.ToHashCode() };
        }
        var overlayKey = EnvironmentalOverlayStateFactory.CreateOverlayKey(
            terrainTextureKey, observer, visibility, sector.DistanceMetres);
        if (_profileKey != profileKey)
        {
            _profileKey = profileKey;
            _profileRevision++;
            _diagnostics.ProfileChanged();
        }
        if (_state is not null && _overlayKey == overlayKey) return _state;

        if (_terrainTextureKey != terrainTextureKey)
        {
            _terrainTextureKey = terrainTextureKey;
            _terrainTextureRevision++;
            _profileTexels = fan is null
                ? EnvironmentalOverlayStateFactory.Resample(source, sector.HorizontalFovDegrees, profileTextureWidth)
                : [default, default]; // Base/weather shader only; solid bands own production terrain.
        }

        _overlayKey = overlayKey;
        _overlayRevision++;
        _diagnostics.OverlayRebuilt();
        _state = new EnvironmentalOverlayState(
            observer,
            sector.CentreBearingDegrees,
            sector.HorizontalFovDegrees,
            sector.DistanceMetres,
            overlayKey.WeatherVisibilityDistanceMetres,
            source,
            _profileTexels,
            profileKey,
            overlayKey,
            _profileRevision,
            _terrainTextureRevision,
            _overlayRevision) { TerrainFan = fan };
        return _state;
    }

    public bool UpdateRender(EnvironmentalRenderKey key)
    {
        if (_renderKey == key) return false;
        _renderKey = key;
        _diagnostics.RenderInvalidated();
        return true;
    }
}

public static class EnvironmentalOverlayStateFactory
{
    public const int DefaultProfileTextureWidth = 512;

    // No positive-angle deadband: retain shallow resolved terrain. The texture's existing
    // square-root presentation maps this bounded weight to 45–100% of the tint setting.
    public const double PlanFullStrengthAngleDegrees = 5;
    public const double PlanMinimumStrength = .45;

    public static ImmutableArray<EnvironmentalTerrainSample> PlanSamples(
        GeoSector sector, TerrainHorizonProfile terrain)
    {
        if (!terrain.TerrainCalculationsEnabled || !terrain.HasTerrainCoverage || terrain.Samples.Count == 0)
            return [];
        var offsets = terrain.Samples.Select(s => Angles.NormaliseDegrees(s.AzimuthDegrees - sector.LeftBearingDegrees))
            .Where(o => o > 1e-7 && o < sector.HorizontalFovDegrees - 1e-7)
            .Append(0).Append(sector.HorizontalFovDegrees).Order().ToArray();
        var result = ImmutableArray.CreateBuilder<EnvironmentalTerrainSample>(offsets.Length);
        foreach (var offset in offsets)
        {
            var bearing = Angles.NormaliseDegrees(sector.LeftBearingDegrees + offset);
            var angle = terrain.TerrainAltitudeAt(bearing);
            var distance = angle is > 0 && double.IsFinite(angle.Value)
                ? ValidDistance(terrain.TerrainObstructionAt(bearing).EffectiveFirstObstructionDistanceMetres, sector.DistanceMetres)
                : null;
            var strength = distance.HasValue
                ? PlanMinimumStrength + (1 - PlanMinimumStrength) * Math.Sqrt(Math.Clamp(angle!.Value / PlanFullStrengthAngleDegrees, 0, 1))
                : 0;
            result.Add(new(bearing, sector.LeftBearingDegrees + offset, offset, distance.HasValue,
                distance, strength * strength));
        }
        return result.MoveToImmutable();
    }

    public static EnvironmentalProfileKey CreateProfileKey(
        TerrainHorizonProfile terrain,
        double terrainAngularDetailDegrees)
    {
        var providerFingerprint = HashCode.Combine(
            terrain.HasTerrainCoverage,
            terrain.GroundHorizonState,
            terrain.IsComplete,
            terrain.Status);
        return new EnvironmentalProfileKey(
            terrain.Observer.Latitude,
            terrain.Observer.Longitude,
            terrain.ObserverHeightAboveGroundMetres,
            terrainAngularDetailDegrees,
            terrain.GeneratedAt.ToUnixTimeTicks(),
            terrain.EffectiveCompletedBearingCount,
            providerFingerprint);
    }

    public static EnvironmentalTerrainTextureKey CreateTerrainTextureKey(
        EnvironmentalProfileKey profileKey,
        GeoSector sector,
        ImmutableArray<EnvironmentalTerrainSample> samples)
    {
        var sampleHash = new HashCode();
        foreach (var sample in samples)
        {
            sampleHash.Add(sample.BearingDegrees);
            sampleHash.Add(sample.IsObstructed);
            sampleHash.Add(sample.ObstructionDistanceMetres);
            sampleHash.Add(sample.EffectiveCoverage);
        }
        return new EnvironmentalTerrainTextureKey(
            profileKey,
            Angles.NormaliseDegrees(sector.CentreBearingDegrees),
            sector.HorizontalFovDegrees,
            sector.DistanceMetres,
            sampleHash.ToHashCode());
    }

    public static EnvironmentalOverlayKey CreateOverlayKey(
        EnvironmentalTerrainTextureKey terrainTextureKey,
        GeoCoordinate observer,
        FramingVisibilityAssessment? visibility,
        double maximumDistanceMetres) => new(
        terrainTextureKey,
        observer.Normalised(),
        ValidDistance(visibility?.WeatherVisibilityDistanceMetres, maximumDistanceMetres));

    public static ImmutableArray<EnvironmentalTerrainSample> OrderSamples(
        GeoSector sector,
        FramingVisibilityAssessment? visibility)
    {
        if (visibility is null || visibility.EffectiveTerrainObstructions.Count == 0)
            return [];
        var samples = new List<EnvironmentalTerrainSample>(visibility.EffectiveTerrainObstructions.Count);
        foreach (var source in visibility.EffectiveTerrainObstructions)
        {
            var offset = Angles.NormaliseDegrees(source.BearingDegrees - sector.LeftBearingDegrees);
            if (offset > sector.HorizontalFovDegrees + 1e-7) continue;
            var distance = ValidDistance(source.FirstObstructionDistanceMetres, sector.DistanceMetres);
            var obstructed = source.IsObstructed && distance.HasValue;
            samples.Add(new EnvironmentalTerrainSample(
                Angles.NormaliseDegrees(source.BearingDegrees),
                sector.LeftBearingDegrees + offset,
                offset,
                obstructed,
                obstructed ? distance : null, source.EffectiveCoverage));
        }
        samples.Sort(static (left, right) => left.OffsetDegrees.CompareTo(right.OffsetDegrees));
        for (var index = samples.Count - 1; index > 0; index--)
        {
            if (Math.Abs(samples[index].OffsetDegrees - samples[index - 1].OffsetDegrees) > 1e-7) continue;
            if (!samples[index].IsObstructed || samples[index - 1].IsObstructed)
                samples.RemoveAt(index);
            else
                samples.RemoveAt(index - 1);
        }
        return samples.ToImmutableArray();
    }

    public static ImmutableArray<EnvironmentalProfileTexel> Resample(
        ImmutableArray<EnvironmentalTerrainSample> samples,
        double horizontalFovDegrees,
        int width = DefaultProfileTextureWidth)
    {
        if (width < 2) throw new ArgumentOutOfRangeException(nameof(width));
        var builder = ImmutableArray.CreateBuilder<EnvironmentalProfileTexel>(width);
        for (var index = 0; index < width; index++)
        {
            var offset = horizontalFovDegrees * index / (width - 1d);
            var sample = SampleAtOffset(samples, offset);
            builder.Add(sample.IsObstructed && sample.ObstructionDistanceMetres is double distance
                ? new EnvironmentalProfileTexel((float)distance, true, (float)sample.EffectiveCoverage)
                : default);
        }
        return builder.MoveToImmutable();
    }

    public static EnvironmentalTerrainSample SampleAtOffset(
        ImmutableArray<EnvironmentalTerrainSample> samples,
        double offsetDegrees)
    {
        if (samples.IsDefaultOrEmpty) return default;
        if (offsetDegrees <= samples[0].OffsetDegrees) return samples[0];
        if (offsetDegrees >= samples[^1].OffsetDegrees) return samples[^1];
        var low = 0;
        var high = samples.Length - 1;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (samples[middle].OffsetDegrees <= offsetDegrees) low = middle;
            else high = middle;
        }
        var left = samples[low];
        var right = samples[high];
        if (Math.Abs(offsetDegrees - left.OffsetDegrees) <= 1e-9) return left;
        if (Math.Abs(offsetDegrees - right.OffsetDegrees) <= 1e-9) return right;
        if (left.IsObstructed != right.IsObstructed)
        {
            var transition = (left.OffsetDegrees + right.OffsetDegrees) / 2;
            return offsetDegrees < transition ? left : right;
        }
        if (!left.IsObstructed) return left with
        {
            BearingDegrees = Angles.NormaliseDegrees(left.BearingDegrees + offsetDegrees - left.OffsetDegrees),
            UnwrappedBearingDegrees = left.UnwrappedBearingDegrees + offsetDegrees - left.OffsetDegrees,
            OffsetDegrees = offsetDegrees
        };
        var fraction = (offsetDegrees - left.OffsetDegrees) /
                       (right.OffsetDegrees - left.OffsetDegrees);
        var distance = left.ObstructionDistanceMetres!.Value +
                       (right.ObstructionDistanceMetres!.Value - left.ObstructionDistanceMetres.Value) * fraction;
        return new EnvironmentalTerrainSample(
            Angles.NormaliseDegrees(left.BearingDegrees + offsetDegrees - left.OffsetDegrees),
            left.UnwrappedBearingDegrees + offsetDegrees - left.OffsetDegrees,
            offsetDegrees,
            true,
            distance, left.EffectiveCoverage + (right.EffectiveCoverage - left.EffectiveCoverage) * fraction);
    }

    private static double? ValidDistance(double? distanceMetres, double maximumDistanceMetres) =>
        distanceMetres is double distance && double.IsFinite(distance) && distance > 0 && distance < maximumDistanceMetres
            ? distance
            : null;
}

public static class EnvironmentalOverlayMath
{
    public static bool IsInsideCone(double bearingDegrees, double centreBearingDegrees, double horizontalFovDegrees)
    {
        var separation = Math.Abs(Angles.NormaliseSignedDegrees(bearingDegrees - centreBearingDegrees));
        return separation <= horizontalFovDegrees / 2 + 1e-9;
    }

    public static EnvironmentalPixelClassification Classify(
        EnvironmentalOverlayState state,
        double bearingDegrees,
        double distanceMetres)
    {
        if (!IsInsideCone(bearingDegrees, state.CentreBearingDegrees, state.HorizontalFovDegrees) ||
            distanceMetres < 0 || distanceMetres > state.MaximumDistanceMetres)
            return EnvironmentalPixelClassification.OutsideCone;
        var signed = Angles.NormaliseSignedDegrees(bearingDegrees - state.CentreBearingDegrees);
        var offset = signed + state.HorizontalFovDegrees / 2;
        if (state.TerrainFan is { } fan)
        {
            if (fan.GroundFacing) return EnvironmentalPixelClassification.GroundFacing;
            var visible = fan.ContainsTerrain(offset, distanceMetres);
            var beyond = state.WeatherVisibilityDistanceMetres is double limit && distanceMetres >= limit;
            return (visible, beyond) switch
            {
                (true, true) => EnvironmentalPixelClassification.TerrainObstructedAndBeyondVisibility,
                (true, false) => EnvironmentalPixelClassification.TerrainObstructed,
                (false, true) => EnvironmentalPixelClassification.BeyondVisibility,
                _ => EnvironmentalPixelClassification.Clear
            };
        }
        var texelIndex = (int)Math.Round(Math.Clamp(offset / state.HorizontalFovDegrees, 0, 1) *
                                         (state.ProfileTexels.Length - 1));
        var terrain = !state.ProfileTexels.IsDefaultOrEmpty &&
                      state.ProfileTexels[texelIndex] is { IsObstructed: true } texel &&
                      distanceMetres >= texel.ObstructionDistanceMetres;
        var weather = state.WeatherVisibilityDistanceMetres is double visibility &&
                      distanceMetres >= visibility;
        return (terrain, weather) switch
        {
            (true, true) => EnvironmentalPixelClassification.TerrainObstructedAndBeyondVisibility,
            (true, false) => EnvironmentalPixelClassification.TerrainObstructed,
            (false, true) => EnvironmentalPixelClassification.BeyondVisibility,
            _ => EnvironmentalPixelClassification.Clear
        };
    }

    public static EnvironmentalOverlayFrame CreateFrame(
        Viewport viewport,
        double width,
        double height,
        EnvironmentalRenderParameters parameters)
    {
        var origin = viewport.ScreenToWorld(0, 0);
        var x = viewport.ScreenToWorld(1, 0);
        var y = viewport.ScreenToWorld(0, 1);
        var normalised = parameters.Normalised();
        var key = new EnvironmentalRenderKey(
            width,
            height,
            origin.X,
            origin.Y,
            x.X - origin.X,
            x.Y - origin.Y,
            y.X - origin.X,
            y.Y - origin.Y,
            normalised);
        return new EnvironmentalOverlayFrame(
            (float)width,
            (float)height,
            (float)origin.X,
            (float)origin.Y,
            (float)(x.X - origin.X),
            (float)(x.Y - origin.Y),
            (float)(y.X - origin.X),
            (float)(y.Y - origin.Y),
            key);
    }

    public static GeoCoordinate ScreenToGeographic(
        EnvironmentalOverlayFrame frame,
        double screenX,
        double screenY)
    {
        var worldX = frame.WorldOriginX + screenX * frame.WorldStepXX + screenY * frame.WorldStepYX;
        var worldY = frame.WorldOriginY + screenX * frame.WorldStepXY + screenY * frame.WorldStepYY;
        return WebMercator.ToWgs84(worldX, worldY);
    }

    public static (double X, double Y) GeographicToScreen(
        EnvironmentalOverlayFrame frame,
        GeoCoordinate coordinate)
    {
        var key = frame.RenderKey;
        var world = WebMercator.FromWgs84(coordinate);
        var worldX = WebMercator.WrapXNear(world.X, key.WorldOriginX);
        var deltaX = worldX - key.WorldOriginX;
        var deltaY = world.Y - key.WorldOriginY;
        var determinant = key.WorldStepXX * key.WorldStepYY - key.WorldStepYX * key.WorldStepXY;
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-20)
            return (double.NaN, double.NaN);
        return (
            (deltaX * key.WorldStepYY - deltaY * key.WorldStepYX) / determinant,
            (key.WorldStepXX * deltaY - key.WorldStepXY * deltaX) / determinant);
    }
}
