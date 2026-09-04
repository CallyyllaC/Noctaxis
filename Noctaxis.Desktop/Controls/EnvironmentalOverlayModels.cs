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
    BeyondVisibility,
    TerrainObstructedAndBeyondVisibility
}

public readonly record struct EnvironmentalTerrainSample(
    double BearingDegrees,
    double UnwrappedBearingDegrees,
    double OffsetDegrees,
    bool IsObstructed,
    double? ObstructionDistanceMetres);

public readonly record struct EnvironmentalProfileTexel(
    float ObstructionDistanceMetres,
    bool IsObstructed);

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
    float HatchOpacity,
    float HatchSpacingPixels,
    float HatchThicknessPixels,
    float HatchHighlightOffsetPixels)
{
    public static EnvironmentalRenderParameters Default { get; } = new(.10f, .72f, .92f, 7, 2.2f, 3.2f);

    public EnvironmentalRenderParameters Normalised() => new(
        Math.Clamp(float.IsFinite(ConeOpacity) ? ConeOpacity : .10f, 0, .5f),
        Math.Clamp(float.IsFinite(WeatherOpacityScale) ? WeatherOpacityScale : .72f, 0, 1),
        Math.Clamp(float.IsFinite(HatchOpacity) ? HatchOpacity : .92f, 0, 1),
        Math.Clamp(float.IsFinite(HatchSpacingPixels) ? HatchSpacingPixels : 7, 3, 32),
        Math.Clamp(float.IsFinite(HatchThicknessPixels) ? HatchThicknessPixels : 2.2f, .5f, 8),
        Math.Clamp(float.IsFinite(HatchHighlightOffsetPixels) ? HatchHighlightOffsetPixels : 3.2f, 0, 16));
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
    long OverlayRevision);

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
    public int LegacyHatchPrimitiveCount => 0;

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

    public EnvironmentalOverlayDiagnostics Diagnostics => _diagnostics;

    public EnvironmentalOverlayState Update(
        GeoCoordinate observer,
        GeoSector sector,
        FramingVisibilityAssessment? visibility,
        EnvironmentalProfileKey profileKey)
    {
        var source = EnvironmentalOverlayStateFactory.OrderSamples(sector, visibility);
        var terrainTextureKey = EnvironmentalOverlayStateFactory.CreateTerrainTextureKey(profileKey, sector, source);
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
            _profileTexels = EnvironmentalOverlayStateFactory.Resample(
                source, sector.HorizontalFovDegrees, profileTextureWidth);
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
            _overlayRevision);
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
                obstructed ? distance : null));
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
                ? new EnvironmentalProfileTexel((float)distance, true)
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
            distance);
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
