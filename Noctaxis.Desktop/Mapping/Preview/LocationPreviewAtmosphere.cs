using SkiaSharp;

namespace Noctaxis.Desktop.Mapping.Preview;

/// <summary>A faint background star: position in scene pixels and brightness (0..1).</summary>
public readonly record struct PreviewStar(float X, float Y, float Brightness);

/// <summary>
/// Theme-independent atmosphere derived from a scene: nebular haze that gathers around the settlement
/// light, a seeded background starfield, and the site's meaningful off-site light concentrations
/// (<see cref="LocationPreviewLightSources"/>). Deterministic for a scene (seeded by its location), so every theme and export of a
/// location shows the same sky.
/// </summary>
public sealed class LocationPreviewAtmosphere
{
    public const int StarCount = 90;

    private LocationPreviewAtmosphere(byte[] nebulaPrimary, byte[] nebulaSecondary, IReadOnlyList<PreviewStar> stars,
        IReadOnlyList<LocationPreviewLightSource> lightSources)
    {
        NebulaPrimary = nebulaPrimary; NebulaSecondary = nebulaSecondary; Stars = stars; LightSources = lightSources;
    }

    /// <summary>Haze tinted by the theme's glow: follows the settlements. MaskWidth × MaskHeight, 0..255.</summary>
    public byte[] NebulaPrimary { get; }
    /// <summary>Wider haze tinted by the theme's instrument colour: the surrounding sky.</summary>
    public byte[] NebulaSecondary { get; }
    public IReadOnlyList<PreviewStar> Stars { get; }
    /// <summary>Up to three meaningful off-site light concentrations, strongest first (see <see cref="LocationPreviewLightSources"/>).</summary>
    public IReadOnlyList<LocationPreviewLightSource> LightSources { get; }

    public static LocationPreviewAtmosphere Create(LocationPreviewScene scene)
    {
        int width = scene.MaskWidth, height = scene.MaskHeight, count = width * height;
        var seed = BitConverter.ToInt32(scene.LocationId.ToByteArray(), 4);
        var bloom = scene.SettlementBloom;
        var primary = new byte[count];
        var secondary = new byte[count];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = y * width + x;
            float u = x / (float)height, v = y / (float)height;
            var cloud = Fbm(u * 2.2f, v * 2.2f, seed, 5);
            var wisp = Fbm(u * 5.5f + 17, v * 5.5f - 9, seed ^ 0x5bd1e995, 4);
            var light = bloom[i] / 255f;
            // Primary haze lives where the light is; the secondary is the open sky around it.
            var p = Math.Clamp(light * (.55f + .9f * cloud) + .25f * MathF.Max(0, wisp - .45f) * 2, 0, 1);
            var s = Math.Clamp(MathF.Pow(MathF.Max(0, cloud - .32f) / .68f, 1.6f) * (1 - .6f * light), 0, 1);
            primary[i] = (byte)Math.Round(p * 255);
            secondary[i] = (byte)Math.Round(s * 255);
        }

        var random = new Random(seed);
        var stars = new PreviewStar[StarCount];
        for (var n = 0; n < stars.Length; n++)
        {
            var brightness = (float)Math.Pow(random.NextDouble(), 2.4);
            stars[n] = new((float)(random.NextDouble() * scene.Width), (float)(random.NextDouble() * scene.Height), brightness);
        }

        return new(primary, secondary, stars, LocationPreviewLightSources.Find(scene));
    }

    // Value-noise fractal: smooth, seeded and platform-independent.
    private static float Fbm(float x, float y, int seed, int octaves)
    {
        float sum = 0, amplitude = .5f, frequency = 1, norm = 0;
        for (var o = 0; o < octaves; o++)
        {
            sum += amplitude * ValueNoise(x * frequency, y * frequency, seed + o * 1013);
            norm += amplitude; amplitude *= .5f; frequency *= 2.03f;
        }
        return sum / norm;
    }

    private static float ValueNoise(float x, float y, int seed)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        float sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
        float Lerp(float a, float b, float t) => a + (b - a) * t;
        return Lerp(Lerp(Hash(x0, y0, seed), Hash(x0 + 1, y0, seed), sx), Lerp(Hash(x0, y0 + 1, seed), Hash(x0 + 1, y0 + 1, seed), sx), sy);
    }

    private static float Hash(int x, int y, int seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + seed * 144269504);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }
}
