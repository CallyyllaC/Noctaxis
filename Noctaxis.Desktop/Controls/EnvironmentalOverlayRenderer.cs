using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Noctaxis.Core.Domain;
using SkiaSharp;
using System.Diagnostics;
using Noctaxis.Desktop.Diagnostics;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// Owns the single custom draw operation used for continuous environmental effects. Terrain and
/// weather calculations are deliberately absent: the renderer only consumes immutable state.
/// </summary>
public sealed class EnvironmentalOverlayRenderer : IDisposable
{
    public const int DrawOperationsPerFrame = 1;
    private readonly object _gate = new();
    private readonly EnvironmentalOverlayDiagnostics _diagnostics;
    private SkiaEnvironmentalOverlayResources? _resources;
    private bool _disposed;

    public EnvironmentalOverlayRenderer(EnvironmentalOverlayDiagnostics? diagnostics = null)
    {
        _diagnostics = diagnostics ?? new EnvironmentalOverlayDiagnostics();
    }

    public EnvironmentalOverlayDiagnostics Diagnostics => _diagnostics;

    public void Draw(
        DrawingContext context,
        Rect bounds,
        EnvironmentalOverlayState state,
        EnvironmentalOverlayFrame frame,
        Color coneColour)
    {
        if (_disposed || bounds.Width <= 0 || bounds.Height <= 0) return;
        context.Custom(new DrawOperation(this, bounds, state, frame, coneColour));
    }

    internal void Render(
        ImmediateDrawingContext context,
        EnvironmentalOverlayState state,
        EnvironmentalOverlayFrame frame,
        Color coneColour)
    {
        if (_disposed) return;
        var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (leaseFeature is null) return;
        using var lease = leaseFeature.Lease();
        lock (_gate)
        {
            if (_disposed) return;
            _resources ??= new SkiaEnvironmentalOverlayResources(_diagnostics);
            Action? synchronize = ViewportRenderProbe.Sink is not null && ViewportRenderProbe.SynchronizeGpu && lease.GrContext is not null
                ? () => { lease.SkSurface?.Flush(); lease.GrContext.Flush(); lease.GrContext.Submit(true); } : null;
            var priorStarted = Stopwatch.GetTimestamp();
            synchronize?.Invoke();
            var priorMs = synchronize is null ? 0 : Stopwatch.GetElapsedTime(priorStarted).TotalMilliseconds;
            _resources.Draw(lease.SkCanvas, state, frame, coneColour, lease.GrContext is not null, synchronize, priorMs);
            if (lease.GrContext is { } gpuContext) ViewportRenderProbe.GpuValidation?.Invoke(gpuContext, state);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _resources?.Dispose();
            _resources = null;
        }
    }

    public static string? ValidateShaderSource()
    {
        using var effect = SKRuntimeEffect.CreateShader(EnvironmentalOverlayShader.Source, out var errors);
        return effect is null ? errors : null;
    }

    private sealed class DrawOperation : ICustomDrawOperation
    {
        private readonly EnvironmentalOverlayRenderer _owner;
        private readonly EnvironmentalOverlayState _state;
        private readonly EnvironmentalOverlayFrame _frame;
        private readonly Color _coneColour;

        public DrawOperation(EnvironmentalOverlayRenderer owner, Rect bounds,
            EnvironmentalOverlayState state, EnvironmentalOverlayFrame frame, Color coneColour)
        {
            _owner = owner;
            Bounds = bounds;
            _state = state;
            _frame = frame;
            _coneColour = coneColour;
        }

        public Rect Bounds { get; }
        public bool HitTest(Point point) => false;
        public void Render(ImmediateDrawingContext context) => _owner.Render(context, _state, _frame, _coneColour);
        public void Dispose() { }

        public bool Equals(ICustomDrawOperation? other) => other is DrawOperation operation &&
            ReferenceEquals(operation._owner, _owner) &&
            operation.Bounds == Bounds &&
            operation._state.OverlayRevision == _state.OverlayRevision &&
            operation._frame.RenderKey == _frame.RenderKey &&
            operation._coneColour == _coneColour;
    }
}

public sealed class EnvironmentalOverlayResourceCache<T> : IDisposable where T : class, IDisposable
{
    private long _revision = long.MinValue;
    private T? _resource;
    private bool _disposed;

    public int CreationCount { get; private set; }

    public T GetOrCreate(long revision, Func<T> factory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_resource is not null && revision == _revision) return _resource;
        var replacement = factory();
        _resource?.Dispose();
        _resource = replacement;
        _revision = revision;
        CreationCount++;
        return replacement;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _resource?.Dispose();
        _resource = null;
    }
}

internal sealed class SkiaEnvironmentalOverlayResources : IDisposable
{
    private readonly EnvironmentalOverlayDiagnostics _diagnostics;
    private readonly SKRuntimeEffect _effect;
    private readonly SKRuntimeEffectUniforms _uniforms;
    private readonly SKRuntimeEffectChildren _children;
    private readonly SKPaint _paint = new() { IsAntialias = false };
    private readonly EnvironmentalOverlayResourceCache<ProfileTexture> _profiles = new();
    private bool _disposed;

    private readonly Func<PlanTerrainFan, int, PlanTerrainPatch, double>? _terrainStrength;
    private PlanTerrainFan? _toneFan;
    private double[] _terrainTones = [];
    private PlanTerrainFan? _rasterFan;
    private TerrainFanRaster? _terrainRaster;
    internal int TonePreparationCount { get; private set; }
    internal bool HasPreparedTerrain => _rasterFan is not null;

    public SkiaEnvironmentalOverlayResources(EnvironmentalOverlayDiagnostics diagnostics)
        : this(diagnostics, null) { }

    // Allows deterministic palette comparisons over the very same production mesh.
    internal SkiaEnvironmentalOverlayResources(EnvironmentalOverlayDiagnostics diagnostics,
        Func<PlanTerrainFan, int, PlanTerrainPatch, double>? terrainStrength)
    {
        _terrainStrength = terrainStrength;
        _diagnostics = diagnostics;
        _effect = SKRuntimeEffect.CreateShader(EnvironmentalOverlayShader.Source, out var errors) ??
                  throw new InvalidOperationException($"Environmental overlay shader compilation failed: {errors}");
        _uniforms = new SKRuntimeEffectUniforms(_effect);
        _children = new SKRuntimeEffectChildren(_effect);
        _diagnostics.ShaderCompiled();
    }

    public void Draw(SKCanvas canvas, EnvironmentalOverlayState state, EnvironmentalOverlayFrame frame, Color colour, bool? gpu = null,
        Action? synchronize = null, double priorGpuMs = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var probe = ViewportRenderProbe.Sink;
        var started = probe is null ? 0 : Stopwatch.GetTimestamp();
        var allocated = probe is null ? 0 : GC.GetAllocatedBytesForCurrentThread();
        var preparations = TonePreparationCount;
        _projectionTicks = _fillTicks = 0;
        _pathBuildTicks = _gpuCompletionTicks = _tonePreparationTicks = 0;
        var profile = _profiles.GetOrCreate(state.TerrainTextureRevision, () =>
        {
            _diagnostics.ProfileUploaded();
            return ProfileTexture.Create(state.ProfileTexels, state.MaximumDistanceMetres);
        });

        var parameters = frame.RenderKey.Parameters;
        var observerScreen = EnvironmentalOverlayMath.GeographicToScreen(frame, state.Observer);
        _uniforms.Reset();
        _uniforms.Add("worldStepXX", frame.WorldStepXX);
        _uniforms.Add("worldStepXY", frame.WorldStepXY);
        _uniforms.Add("worldStepYX", frame.WorldStepYX);
        _uniforms.Add("worldStepYY", frame.WorldStepYY);
        _uniforms.Add("observerScreenX", (float)observerScreen.X);
        _uniforms.Add("observerScreenY", (float)observerScreen.Y);
        _uniforms.Add("observerLatitude", (float)(state.Observer.Latitude * Angles.DegreesToRadians));
        _uniforms.Add("centreBearing", (float)(state.CentreBearingDegrees * Angles.DegreesToRadians));
        _uniforms.Add("halfFov", (float)(state.HorizontalFovDegrees * Angles.DegreesToRadians / 2));
        _uniforms.Add("maximumDistance", (float)state.MaximumDistanceMetres);
        _uniforms.Add("visibilityDistance", (float)(state.WeatherVisibilityDistanceMetres ?? -1));
        _uniforms.Add("profileWidth", (float)state.ProfileTexels.Length);
        _uniforms.Add("coneColour", ToColor(colour));
        _uniforms.Add("coneOpacity", parameters.ConeOpacity);
        _uniforms.Add("weatherOpacityScale", parameters.WeatherOpacityScale);
        _uniforms.Add("terrainColour", ToColor(Color.FromUInt32(parameters.TerrainColourArgb)));
        _uniforms.Add("terrainTintOpacity", parameters.TerrainTintOpacity);

        _children.Reset();
        _children.Add("terrainProfile", profile.Shader);
        using var shader = _effect.ToShader(_uniforms, _children);
        _paint.Shader = shader;
        canvas.DrawRect(0, 0, frame.Width, frame.Height, _paint);
        _paint.Shader = null;
        synchronize?.Invoke();
        var baseDone = probe is null ? 0 : Stopwatch.GetTimestamp();
        if (state.TerrainFan is { } fan) DrawTerrainFan(canvas, fan, frame, parameters, state);
        else ClearTerrainPreparation();
        var completionStarted = synchronize is null ? 0 : Stopwatch.GetTimestamp();
        synchronize?.Invoke();
        if (synchronize is not null) _gpuCompletionTicks = Stopwatch.GetTimestamp() - completionStarted;
        _diagnostics.Drawn();
        if (probe is not null) probe(new(gpu, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Stopwatch.GetElapsedTime(started, baseDone).TotalMilliseconds,
            Stopwatch.GetElapsedTime(baseDone).TotalMilliseconds,
            _tonePreparationTicks * 1000d / Stopwatch.Frequency,
            _projectionTicks * 1000d / Stopwatch.Frequency, _pathBuildTicks * 1000d / Stopwatch.Frequency,
            _fillTicks * 1000d / Stopwatch.Frequency, _gpuCompletionTicks * 1000d / Stopwatch.Frequency,
            GC.GetAllocatedBytesForCurrentThread() - allocated, state.TerrainFan?.Patches.Length ?? 0,
            TonePreparationCount - preparations) { PriorGpuMs = priorGpuMs });
    }

    private long _projectionTicks, _pathBuildTicks, _fillTicks, _gpuCompletionTicks, _tonePreparationTicks;

    private void DrawTerrainFan(SKCanvas canvas, PlanTerrainFan fan,
        EnvironmentalOverlayFrame frame, EnvironmentalRenderParameters parameters, EnvironmentalOverlayState state)
    {
        using var restore = new SKAutoCanvasRestore(canvas);
        canvas.ClipRect(new SKRect(0, 0, frame.Width, frame.Height));
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false, Style = SKPaintStyle.Fill };
        void Fill(IReadOnlyList<GeoCoordinate> coordinates, SKColor colour)
        {
            if (coordinates.Count < 3) return;
            var measure = ViewportRenderProbe.Sink is not null;
            var started = measure ? Stopwatch.GetTimestamp() : 0;
            long projectionTicks = 0;
            using var path = new SKPath();
            for (var index = 0; index < coordinates.Count; index++)
            {
                var projectionStarted = measure ? Stopwatch.GetTimestamp() : 0;
                var p = EnvironmentalOverlayMath.GeographicToScreen(frame, coordinates[index]);
                if (measure) projectionTicks += Stopwatch.GetTimestamp() - projectionStarted;
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return;
                if (index == 0) path.MoveTo((float)p.X, (float)p.Y); else path.LineTo((float)p.X, (float)p.Y);
            }
            path.Close(); paint.Color = colour;
            var projected = measure ? Stopwatch.GetTimestamp() : 0;
            if (measure) { _projectionTicks += projectionTicks; _pathBuildTicks += projected - started - projectionTicks; }
            canvas.DrawPath(path, paint);
            if (measure) { _fillTicks += Stopwatch.GetTimestamp() - projected; }
        }
        if (fan.GroundFacing)
        {
            ClearTerrainPreparation();
            Fill(fan.GroundOutline, new SKColor(153, 105, 53, 75));
            return;
        }
        if (_terrainStrength is null && !ReferenceEquals(_toneFan, fan))
        {
            var toneStarted = Stopwatch.GetTimestamp();
            _terrainTones = TerrainFanLocalContrast.Prepare(fan, out _);
            if (ViewportRenderProbe.Sink is not null) _tonePreparationTicks = Stopwatch.GetTimestamp() - toneStarted;
            _toneFan = fan;
            TonePreparationCount++;
        }
        if (!ReferenceEquals(_rasterFan, fan))
        {
            var started = Stopwatch.GetTimestamp();
            var tones = _terrainStrength is null ? _terrainTones : fan.Patches.Select((p, i) => _terrainStrength(fan, i, p)).ToArray();
            var replacement = new TerrainFanRaster(fan, tones);
            _terrainRaster?.Dispose();
            _terrainRaster = replacement;
            _rasterFan = fan;
            if (ViewportRenderProbe.Sink is not null) _pathBuildTicks += Stopwatch.GetTimestamp() - started;
        }
        var fillStarted = Stopwatch.GetTimestamp();
        _terrainRaster!.Draw(canvas, frame, state.Observer, state.CentreBearingDegrees, state.HorizontalFovDegrees, state.MaximumDistanceMetres);
        if (ViewportRenderProbe.Sink is not null) _fillTicks += Stopwatch.GetTimestamp() - fillStarted;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearTerrainPreparation();
        _profiles.Dispose();
        _paint.Dispose();
        _children.Dispose();
        _uniforms.Dispose();
        _effect.Dispose();
    }

    private void ClearTerrainPreparation()
    {
        _toneFan = null;
        _terrainTones = [];
        _rasterFan = null;
        _terrainRaster?.Dispose();
        _terrainRaster = null;
    }

    private static SKColorF ToColor(Color colour) => new(
        colour.R / 255f,
        colour.G / 255f,
        colour.B / 255f,
        colour.A / 255f);

    private sealed class ProfileTexture(SKBitmap bitmap, SKImage image, SKShader shader) : IDisposable
    {
        public SKShader Shader { get; } = shader;

        public static ProfileTexture Create(
            IReadOnlyList<EnvironmentalProfileTexel> texels,
            double maximumDistanceMetres)
        {
            var width = Math.Max(2, texels.Count);
            var bitmap = new SKBitmap(new SKImageInfo(width, 1, SKColorType.RgbaF32, SKAlphaType.Unpremul));
            var pixels = MemoryMarshal.Cast<byte, float>(bitmap.GetPixelSpan());
            for (var index = 0; index < width; index++)
            {
                var texel = index < texels.Count ? texels[index] : default;
                var pixel = index * 4;
                pixels[pixel] = texel.IsObstructed
                    ? Math.Clamp((float)(texel.ObstructionDistanceMetres / maximumDistanceMetres), 0, 1)
                    : 0;
                pixels[pixel + 1] = texel.IsObstructed ? 1 : 0;
                pixels[pixel + 2] = texel.IsObstructed ? Math.Clamp(texel.EffectiveCoverage, 0, 1) : 0;
                pixels[pixel + 3] = 1;
            }
            bitmap.NotifyPixelsChanged();
            var image = SKImage.FromBitmap(bitmap);
            var shader = image.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKFilterMode.Nearest));
            return new ProfileTexture(bitmap, image, shader);
        }

        public void Dispose()
        {
            Shader.Dispose();
            image.Dispose();
            bitmap.Dispose();
        }
    }
}

public static class EnvironmentalOverlayShader
{
    public const string Source = """
        uniform float worldStepXX;
        uniform float worldStepXY;
        uniform float worldStepYX;
        uniform float worldStepYY;
        uniform float observerScreenX;
        uniform float observerScreenY;
        uniform float observerLatitude;
        uniform float centreBearing;
        uniform float halfFov;
        uniform float maximumDistance;
        uniform float visibilityDistance;
        uniform float profileWidth;
        uniform half4 coneColour;
        uniform float coneOpacity;
        uniform float weatherOpacityScale;
        uniform half4 terrainColour;
        uniform float terrainTintOpacity;
        uniform shader terrainProfile;

        const float TWO_PI = 6.28318530717958647692;
        const float MERCATOR_RADIUS = 6378137.0;
        const float EARTH_RADIUS = 6371008.8;
        const half TERRAIN_FRONTIER_OPACITY = 0.60;

        half4 premul(half3 rgb, half alpha) {
            return half4(rgb * alpha, alpha);
        }

        half4 over(half4 foreground, half4 background) {
            return foreground + background * (1.0 - foreground.a);
        }

        half4 main(float2 p) {
            float2 screenOffset = p - float2(observerScreenX, observerScreenY);
            float2 worldOffset = float2(
                screenOffset.x * worldStepXX + screenOffset.y * worldStepYX,
                screenOffset.x * worldStepXY + screenOffset.y * worldStepYY);
            float sinObserverLatitude = sin(observerLatitude);
            float cosObserverLatitude = cos(observerLatitude);
            float mercatorDelta = worldOffset.y / MERCATOR_RADIUS;
            float sinhMercatorDelta;
            float coshMercatorDelta;
            if (abs(mercatorDelta) < 0.01) {
                float deltaSquared = mercatorDelta * mercatorDelta;
                sinhMercatorDelta = mercatorDelta *
                    (1.0 + deltaSquared / 6.0 + deltaSquared * deltaSquared / 120.0);
                coshMercatorDelta = 1.0 + deltaSquared / 2.0 +
                    deltaSquared * deltaSquared / 24.0;
            } else {
                float expMercatorDelta = exp(mercatorDelta);
                float inverseExpMercatorDelta = 1.0 / expMercatorDelta;
                sinhMercatorDelta = 0.5 * (expMercatorDelta - inverseExpMercatorDelta);
                coshMercatorDelta = 0.5 * (expMercatorDelta + inverseExpMercatorDelta);
            }
            float latitudeDenominator = coshMercatorDelta +
                                        sinObserverLatitude * sinhMercatorDelta;
            float sinLatitude = (sinObserverLatitude * coshMercatorDelta +
                                 sinhMercatorDelta) / latitudeDenominator;
            float cosLatitude = cosObserverLatitude / latitudeDenominator;
            float deltaLongitude = worldOffset.x / MERCATOR_RADIUS;
            float sinDeltaLongitude = sin(deltaLongitude);
            float cosDeltaLongitude = cos(deltaLongitude);
            float bearingY = sinDeltaLongitude * cosLatitude;
            float bearingX = cosObserverLatitude / latitudeDenominator *
                (sinhMercatorDelta + sinObserverLatitude *
                 (coshMercatorDelta - cosDeltaLongitude));
            float centralCosine = sinObserverLatitude * sinLatitude +
                                  cosObserverLatitude * cosLatitude * cosDeltaLongitude;
            float centralAngle = atan(length(float2(bearingX, bearingY)),
                                      clamp(centralCosine, -1.0, 1.0));
            float distance = EARTH_RADIUS * centralAngle;
            float bearing = centreBearing;
            if (length(screenOffset) >= 1.0) {
                bearing = atan(bearingY, bearingX);
                if (bearing < 0.0) bearing += TWO_PI;
            }

            // Single-precision spherical terms lose bearing stability in the first few hundred
            // metres. Web Mercator is locally conformal, so use its observer-relative tangent
            // scale nearby; retain the spherical solution for the long-range cone.
            if (length(worldOffset) < 20000.0) {
                float2 localOffset = worldOffset * cosObserverLatitude;
                distance = length(localOffset);
                if (length(localOffset) >= 0.01) {
                    bearing = atan(localOffset.x, localOffset.y);
                    if (bearing < 0.0) bearing += TWO_PI;
                }
            }
            if (distance > maximumDistance) return half4(0.0);
            bool weatherReached = visibilityDistance > 0.0 && distance >= visibilityDistance;

            float signedOffset = atan(sin(bearing - centreBearing), cos(bearing - centreBearing));
            if (abs(signedOffset) > halfFov) return half4(0.0);

            float u = clamp((signedOffset + halfFov) / (2.0 * halfFov), 0.0, 1.0);
            half4 terrain = terrainProfile.eval(float2(u * (profileWidth - 1.0) + 0.5, 0.5));
            bool hasTerrain = terrain.g > 0.5;
            float terrainDistance = float(terrain.r) * maximumDistance;
            bool terrainReached = hasTerrain && distance >= terrainDistance;

            half luminance = dot(coneColour.rgb, half3(0.2126, 0.7152, 0.0722));
            half4 cone = weatherReached
                ? premul(half3(luminance * 0.55), half(min(0.7, coneOpacity * 1.6 * weatherOpacityScale)))
                : premul(coneColour.rgb, half(min(0.7, coneOpacity * 1.8)));
            // Presentation only: preserve the Pass C physical coverage and sqrt curve.
            half displayStrength = sqrt(clamp(terrain.b, half(0.0), half(1.0)));
            half4 result = terrainReached
                ? over(premul(terrainColour.rgb, half(terrainTintOpacity) * displayStrength), cone) : cone;
            // A one-pixel radial frontier with a soft edge, in screen-relative ground metres.
            // The profile distance is the meaningful camera-frame frontier, not a new raycast.
            float groundPixel = max(0.001, length(float2(worldStepXX, worldStepXY)) * cosObserverLatitude);
            half frontier = half(clamp(1.0 - abs(distance - terrainDistance) / groundPixel, 0.0, 1.0));
            return hasTerrain && displayStrength > 0.0
                ? over(premul(terrainColour.rgb, TERRAIN_FRONTIER_OPACITY * frontier), result) : result;
        }
        """;
}
