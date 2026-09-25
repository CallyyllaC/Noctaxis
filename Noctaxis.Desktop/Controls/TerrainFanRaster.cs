using Avalonia.Media;
using Noctaxis.Core.Domain;
using SkiaSharp;

namespace Noctaxis.Desktop.Controls;

/// <summary>
/// A renderer-owned, immutable presentation table for one fan. A pixel selects one
/// angular interval and one radial band, then composites that band's existing tone
/// once. No terrain calculation, resampling, or change to patch endpoints occurs.
/// </summary>
internal sealed class TerrainFanRaster : IDisposable
{
    private const int TextureWidth = 1024;
    private readonly SKBitmap _bitmap;
    private readonly SKImage _image;
    private readonly SKShader _table;
    private readonly SKRuntimeEffect _effect;
    private readonly SKRuntimeEffectUniforms _uniforms;
    private readonly SKRuntimeEffectChildren _children;
    private readonly SKPaint _paint = new();
    private readonly Group[] _groups;
    private readonly PlanTerrainFan _fan;
    private readonly record struct Group(double Left, double Right, int First, int Count);

    internal TerrainFanRaster(PlanTerrainFan fan, IReadOnlyList<double> tones)
    {
        _fan = fan;
        var groups = new List<Group>();
        for (var i = 0; i < fan.Patches.Length;)
        {
            var first = i;
            var patch = fan.Patches[i++];
            while (i < fan.Patches.Length && fan.Patches[i].LeftOffset == patch.LeftOffset &&
                   fan.Patches[i].RightOffset == patch.RightOffset) i++;
            groups.Add(new(patch.LeftOffset, patch.RightOffset, first, i - first));
        }
        _groups = groups.ToArray();
        _bitmap = new SKBitmap(new SKImageInfo(TextureWidth,
            Math.Max(1, ((_groups.Length + fan.Patches.Length * 2) * 4 + TextureWidth - 1) / TextureWidth),
            SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var pixels = _bitmap.GetPixelSpan();
        for (var i = 0; i < _groups.Length; i++)
        {
            var group = _groups[i];
            Write(pixels, i, group.Left * 1e7, group.Right * 1e7, group.First, group.Count);
        }
        for (var i = 0; i < fan.Patches.Length; i++)
        {
            var p = fan.Patches[i];
            Write(pixels, _groups.Length + i * 2, p.LeftStart * 1024, p.RightStart * 1024, p.LeftEnd * 1024, p.RightEnd * 1024);
            Write(pixels, _groups.Length + i * 2 + 1, tones[i] * 1e9, 0, 0, 0);
        }
        _bitmap.NotifyPixelsChanged();
        _image = SKImage.FromBitmap(_bitmap);
        _table = _image.ToRawShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp,
            new SKSamplingOptions(SKFilterMode.Nearest));
        _effect = SKRuntimeEffect.CreateShader(Source, out var errors) ?? throw new InvalidOperationException(errors);
        _uniforms = new(_effect);
        _children = new(_effect);
    }

    private static void Write(Span<byte> data, int index, double a, double b, double c, double d)
    {
        WriteScalar(data, index * 4, a); WriteScalar(data, index * 4 + 1, b);
        WriteScalar(data, index * 4 + 2, c); WriteScalar(data, index * 4 + 3, d);
    }

    private static void WriteScalar(Span<byte> data, int index, double value)
    {
        // A child shader's return type is half4 on native backends. Store fixed-point
        // integer bytes in normalised channels and reconstruct in float arithmetic, so the
        // native half boundary cannot overflow 500 km distances or round indices.
        var bits = checked((uint)Math.Round(value));
        data[index * 4] = (byte)bits; data[index * 4 + 1] = (byte)(bits >> 8);
        data[index * 4 + 2] = (byte)(bits >> 16); data[index * 4 + 3] = (byte)(bits >> 24);
    }

    // Same half-open ownership rule as the GPU lookup. Exposed internally so the
    // artifact harness can compare the table to the original polar Contains oracle.
    internal int Owner(double offset, double distance)
    {
        var lo = 0; var hi = _groups.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_groups[mid].Right <= offset) lo = mid + 1; else hi = mid;
        }
        if (lo == _groups.Length && lo > 0 && offset == _groups[^1].Right) lo--;
        if (lo == _groups.Length || offset < _groups[lo].Left || offset > _groups[lo].Right) return -1;
        var g = _groups[lo];
        var t = (offset - g.Left) / (g.Right - g.Left);
        lo = g.First; hi = lo + g.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            var p = _fan.Patches[mid];
            if (distance >= p.LeftEnd + (p.RightEnd - p.LeftEnd) * t) lo = mid + 1; else hi = mid;
        }
        return lo < g.First + g.Count && _fan.Patches[lo].Contains(offset, distance) ? lo : -1;
    }

    internal void Draw(SKCanvas canvas, EnvironmentalOverlayFrame frame, GeoCoordinate observer,
        double bearing, double fov, double maximumDistance)
    {
        if (_groups.Length == 0) return;
        var parameters = frame.RenderKey.Parameters;
        var tint = Color.FromUInt32(parameters.TerrainColourArgb);
        var screen = EnvironmentalOverlayMath.GeographicToScreen(frame, observer);
        _uniforms.Reset();
        _uniforms.Add("worldStep", new[] { frame.WorldStepXX, frame.WorldStepXY, frame.WorldStepYX, frame.WorldStepYY });
        _uniforms.Add("observerScreen", new[] { (float)screen.X, (float)screen.Y });
        var latitude = observer.Latitude * Angles.DegreesToRadians;
        _uniforms.Add("observerSinCos", new[] { (float)Math.Sin(latitude), (float)Math.Cos(latitude) });
        _uniforms.Add("centreBearing", (float)(bearing * Angles.DegreesToRadians));
        _uniforms.Add("halfFov", (float)(fov * Angles.DegreesToRadians / 2));
        _uniforms.Add("maximumDistance", (float)maximumDistance);
        _uniforms.Add("groupCount", (float)_groups.Length);
        _uniforms.Add("tint", new[] { (float)tint.R, tint.G, tint.B });
        _uniforms.Add("opacity", (float)(Math.Round(255 * Math.Clamp(parameters.TerrainTintOpacity + .25f, 0, .85f)) / 255));
        _children.Reset(); _children.Add("table", _table);
        using var shader = _effect.ToShader(_uniforms, _children);
        _paint.Shader = shader;
        canvas.DrawRect(0, 0, frame.Width, frame.Height, _paint);
        _paint.Shader = null;
    }

    public void Dispose()
    {
        _paint.Dispose(); _children.Dispose(); _uniforms.Dispose(); _effect.Dispose();
        _table.Dispose(); _image.Dispose(); _bitmap.Dispose();
    }

    internal const string Source = """
        uniform float4 worldStep;
        uniform float2 observerScreen;
        uniform float2 observerSinCos;
        uniform float centreBearing;
        uniform float halfFov;
        uniform float maximumDistance;
        uniform float groupCount;
        uniform float3 tint;
        uniform float opacity;
        uniform shader table;
        float scalar(int index) {
            float row = floor(float(index) / 1024.0);
            float4 bytes = floor(float4(table.eval(float2(float(index) - row * 1024.0 + 0.5, row + 0.5))) * 255.0 + 0.5);
            return bytes.x + bytes.y * 256.0 + bytes.z * 65536.0 + bytes.w * 16777216.0;
        }
        float4 item(int index) {
            float4 value = float4(scalar(index * 4), scalar(index * 4 + 1), scalar(index * 4 + 2), scalar(index * 4 + 3));
            if (float(index) < groupCount) return value / float4(1e7, 1e7, 1.0, 1.0);
            float entry = float(index) - groupCount;
            return value / (entry - floor(entry * 0.5) * 2.0 < 0.5 ? 1024.0 : 1e9);
        }
        float bearingAngle(float y, float x) {
            float ax = abs(x), ay = abs(y);
            float ratio = min(ax, ay) / max(max(ax, ay), 1e-30);
            bool reduce = ratio > 0.41421356237;
            float z = reduce ? (ratio - 1.0) / (ratio + 1.0) : ratio;
            float q = z * z;
            float angle = z * (1.0 + q * (-1.0/3.0 + q * (1.0/5.0 + q * (-1.0/7.0 +
                q * (1.0/9.0 + q * (-1.0/11.0 + q * (1.0/13.0 + q * (-1.0/15.0 + q/17.0))))))));
            if (reduce) angle += 0.7853981633974483;
            if (ay > ax) angle = 1.5707963267948966 - angle;
            if (x < 0.0) angle = 3.141592653589793 - angle;
            return y < 0.0 ? -angle : angle;
        }
        float sphericalSin(float z) {
            if (z > 1.5707963267948966) z = 3.141592653589793 - z;
            if (z < -1.5707963267948966) z = -3.141592653589793 - z;
            float q = z * z;
            return z * (1.0 + q * (-1.0/6.0 + q * (1.0/120.0 + q * (-1.0/5040.0 +
                q * (1.0/362880.0 + q * (-1.0/39916800.0 + q/6227020800.0))))));
        }
        half4 main(float2 pixel) {
            float2 screen = pixel - observerScreen;
            float2 world = float2(screen.x * worldStep.x + screen.y * worldStep.z,
                                 screen.x * worldStep.y + screen.y * worldStep.w);
            float lon = world.x / 6378137.0;
            float delta = world.y / 6378137.0;
            float sinhDelta;
            float coshMinusOne;
            if (abs(delta) < 0.5) {
                float d2 = delta * delta;
                sinhDelta = delta * (1.0 + d2 * (1.0/6.0 + d2 * (1.0/120.0 + d2 *
                    (1.0/5040.0 + d2 * (1.0/362880.0 + d2/39916800.0)))));
                coshMinusOne = d2 * (0.5 + d2 * (1.0/24.0 + d2 * (1.0/720.0 + d2 *
                    (1.0/40320.0 + d2/3628800.0))));
            } else {
                float e = exp(delta);
                sinhDelta = (e - 1.0 / e) * 0.5;
                coshMinusOne = (e + 1.0 / e) * 0.5 - 1.0;
            }
            float so = observerSinCos.x;
            float co = observerSinCos.y;
            float denominator = 1.0 + coshMinusOne + so * sinhDelta;
            float sl = (so * (1.0 + coshMinusOne) + sinhDelta) / denominator;
            float cl = co / denominator;
            // Stable near-field spherical terms: avoid subtracting cos(lon) from
            // cosh(delta), which loses the tiny radial component at high zoom.
            float halfSin = sphericalSin(lon * 0.5);
            float bx = co / denominator * (sinhDelta + so * (coshMinusOne + 2.0 * halfSin * halfSin));
            float by = sphericalSin(lon) * cl;
            float sineDistance = length(float2(bx, by));
            float centralCosine = clamp(so * sl + co * cl * (1.0 - 2.0 * halfSin * halfSin), -1.0, 1.0);
            float sd2 = sineDistance * sineDistance;
            // Skia's portable atan approximation has a nonzero error near zero.
            // The asin series avoids a hundreds-of-metres range bias there; at the
            // 500 km fan edge its omitted term is below a millimetre.
            float centralAngle = sineDistance < 0.1 && centralCosine > 0.0
                ? sineDistance * (1.0 + sd2 * (1.0/6.0 + sd2 * (3.0/40.0 + sd2 * (5.0/112.0 + sd2 * 35.0/1152.0))))
                : bearingAngle(sineDistance, centralCosine);
            float distance = 6371008.8 * centralAngle;
            if (distance > maximumDistance) return half4(0.0);
            float bearing = bearingAngle(by, bx);
            float signedOffset = bearing - centreBearing;
            if (signedOffset < -3.141592653589793) signedOffset += 6.283185307179586;
            if (signedOffset > 3.141592653589793) signedOffset -= 6.283185307179586;
            if (abs(signedOffset) > halfFov) return half4(0.0);
            float offset = (signedOffset + halfFov) * 57.29577951308232;
            int lo = 0;
            int hi = int(groupCount);
            for (int search = 0; search < 24; search++) {
                if (lo >= hi) break;
                int mid = (lo + hi) / 2;
                if (item(mid).y <= offset) lo = mid + 1; else hi = mid;
            }
            if (lo == int(groupCount)) lo--;
            float4 group = item(lo);
            if (offset < group.x || offset > group.y) return half4(0.0);
            float t = (offset - group.x) / (group.y - group.x);
            lo = int(group.z); hi = lo + int(group.w);
            int end = hi;
            for (int search = 0; search < 24; search++) {
                if (lo >= hi) break;
                int mid = (lo + hi) / 2;
                float4 band = item(int(groupCount) + mid * 2);
                if (distance >= mix(band.z, band.w, t)) lo = mid + 1; else hi = mid;
            }
            if (lo == end) return half4(0.0);
            float4 band = item(int(groupCount) + lo * 2);
            if (distance < mix(band.x, band.y, t)) return half4(0.0);
            float tone = item(int(groupCount) + lo * 2 + 1).x;
            float shade = 0.85 - 0.67 * tone;
            float3 colour = floor(clamp(255.0 * shade * 0.8 + tint * 0.2, 0.0, 255.0) + 0.5) / 255.0;
            return half4(colour * opacity, opacity);
        }
        """;
}
