using SkiaSharp;

namespace Noctaxis.Desktop.Mapping.Preview;

/// <summary>
/// Paints a <see cref="LocationPreviewScene"/> with a <see cref="LocationPreviewStyle"/>: the saved
/// place seen through a Noctaxis navigation instrument. Pure, deterministic and UI-free, so the
/// Locations cards, tests and export or comparison tooling all render through here at any size.
/// <para>
/// Layers, bottom to top: ground (land and areas); atmosphere (nebular haze and starfield, not in
/// High Contrast); structure (waterways, roads as navigation traces, settlements as star clusters);
/// device falloff (the edge of the instrument's view); instrument (scan ring, ticks, north chevron,
/// light sectors with outward carets for up to three off-site light concentrations, and acquisition
/// brackets that mark the site).
/// </para>
/// </summary>
public static class LocationPreviewRenderer
{
    /// <summary>Line weights and symbols are designed at this output height and scale with it.</summary>
    public const float ReferenceHeight = 280;

    public static SKBitmap Render(LocationPreviewScene scene, LocationPreviewStyle style, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        Draw(canvas, scene, style, SKRect.Create(width, height));
        canvas.Flush();
        return bitmap;
    }

    public static byte[] RenderPng(LocationPreviewScene scene, LocationPreviewStyle style, int width, int height)
    {
        using var bitmap = Render(scene, style, width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Fills <paramref name="destination"/> (cover-fit, centred on the pin) with the themed preview.</summary>
    public static void Draw(SKCanvas canvas, LocationPreviewScene scene, LocationPreviewStyle style, SKRect destination)
    {
        using var restore = new SKAutoCanvasRestore(canvas);
        canvas.ClipRect(destination);
        var unit = destination.Height / ReferenceHeight;
        var scale = Math.Max(destination.Width / scene.Width, destination.Height / scene.Height);
        // Centre on the pin while keeping the scene covering the whole destination.
        var left = Math.Clamp(destination.MidX - scene.Pin.X * scale, destination.Right - scene.Width * scale, destination.Left);
        var top = Math.Clamp(destination.MidY - scene.Pin.Y * scale, destination.Bottom - scene.Height * scale, destination.Top);
        var sceneRect = SKRect.Create(left, top, scene.Width * scale, scene.Height * scale);
        SKPoint Map(SKPoint p) => new(left + p.X * scale, top + p.Y * scale);
        var view = new View(destination, sceneRect, Map(scene.Pin), unit, Map);

        DrawGround(canvas, scene, style, view);
        if (!style.IsHighContrast) DrawAtmosphere(canvas, scene, style, view);
        DrawStructure(canvas, scene, style, view);
        if (!style.IsHighContrast) DrawDeviceFalloff(canvas, style, view);
        DrawInstrument(canvas, scene, style, view);
    }

    private sealed record View(SKRect Destination, SKRect SceneRect, SKPoint Pin, float Unit, Func<SKPoint, SKPoint> Map)
    {
        /// <summary>Radius of the instrument's scan ring.</summary>
        public float ScanRadius => Destination.Height * .3f;
        /// <summary>Radius of the instrument's lens falloff.</summary>
        public float LensRadius => Math.Max(Destination.Width, Destination.Height) * .7f;
    }

    // ---- Ground -------------------------------------------------------------------------------

    private static void DrawGround(SKCanvas canvas, LocationPreviewScene scene, LocationPreviewStyle style, View view)
    {
        canvas.DrawColor(style.Land);
        // Areas in the Planner basemap roles, mixed at scene resolution and drawn once.
        using var ground = ComposeGround(scene, style);
        DrawScaled(canvas, ground, view.SceneRect, SKBlendMode.SrcOver);
    }

    // ---- Atmosphere ---------------------------------------------------------------------------

    private static void DrawAtmosphere(SKCanvas canvas, LocationPreviewScene scene, LocationPreviewStyle style, View view)
    {
        var atmosphere = scene.Atmosphere;
        if (style.IsLight)
        {
            // Pale grounds: faint tinted cloud rather than glow, so the sky reads without fogging the map.
            using var clouds = ComposeNebula(scene, atmosphere, view, style.Glow, .10f, style.Instrument, .07f);
            DrawScaled(canvas, clouds, view.SceneRect, SKBlendMode.SrcOver);
            return;
        }
        // Deep-space haze: the glow gathers around the settlements, the instrument colour fills the sky.
        using (var nebula = ComposeNebula(scene, atmosphere, view, style.Glow, .28f, style.Instrument, .15f))
            DrawScaled(canvas, nebula, view.SceneRect, SKBlendMode.Plus);
        using var star = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };
        var starColour = LocationPreviewStyle.Mix(style.Reticle, SKColors.White, .35);
        foreach (var s in atmosphere.Stars)
        {
            // The instrument reveals the sky: stars show within and just around its field and fade out by mid-lens,
            // so none sits in the outer field where card text may overlap.
            var p = view.Map(new SKPoint(s.X, s.Y));
            var d = SKPoint.Distance(p, view.Pin) / view.LensRadius;
            var visibility = Math.Clamp((.52f - d) / (.52f - .3f), 0, 1);
            if (visibility <= 0) continue;
            star.Color = starColour.WithAlpha((byte)Math.Round((55 + 170 * s.Brightness) * visibility));
            canvas.DrawCircle(p, (.35f + .7f * s.Brightness) * view.Unit, star);
        }
    }

    // ---- Structure ----------------------------------------------------------------------------

    private static void DrawStructure(SKCanvas canvas, LocationPreviewScene scene, LocationPreviewStyle style, View view)
    {
        var unit = view.Unit;
        var rivers = scene.Lines.Where(l => l.Class == PreviewLineClass.River).ToArray();
        var streams = scene.Lines.Where(l => l.Class == PreviewLineClass.Stream).ToArray();
        var minor = scene.Lines.Where(l => l.Class == PreviewLineClass.Minor).ToArray();
        var major = scene.Lines.Where(l => l.Class == PreviewLineClass.Major).ToArray();
        switch (style.Tone)
        {
            case LocationPreviewTone.Dark:
                // Waterways stay quiet; roads become navigation traces: a soft luminous track and a fine core.
                foreach (var line in streams) Stroke(canvas, line, view.Map, style.WaterLine, .8f * unit, .55f);
                foreach (var line in rivers) Stroke(canvas, line, view.Map, style.WaterLine, 1.5f * unit, .85f);
                var trace = LocationPreviewStyle.Mix(style.RoadMajor, style.Instrument, .4);
                foreach (var line in major) Stroke(canvas, line, view.Map, style.RoadMajor, 5.5f * unit, .22f, SKBlendMode.Plus);
                foreach (var line in minor) Stroke(canvas, line, view.Map, trace, 1f * unit, .5f);
                foreach (var line in major) Stroke(canvas, line, view.Map, trace, 1.6f * unit, .95f);
                using (var glow = ComposeGlow(scene, style.Glow, .46f, .3f)) DrawScaled(canvas, glow, view.SceneRect, SKBlendMode.Plus);
                DrawStarClusters(canvas, scene, view, LocationPreviewStyle.Mix(style.Glow, SKColors.White, .6), 1f, SKBlendMode.Plus, glints: true);
                break;
            case LocationPreviewTone.Light:
                foreach (var line in streams) Stroke(canvas, line, view.Map, style.WaterLine, .8f * unit, .6f);
                foreach (var line in rivers) Stroke(canvas, line, view.Map, style.WaterLine, 1.6f * unit, .9f);
                foreach (var line in minor) Stroke(canvas, line, view.Map, style.RoadMinor, 1.2f * unit, .9f);
                foreach (var line in major) Stroke(canvas, line, view.Map, style.RoadCasing, 3.4f * unit, .9f);
                foreach (var line in major) Stroke(canvas, line, view.Map, style.RoadMajor, 2f * unit, 1f);
                using (var wash = ComposeGlow(scene, style.Glow, .28f, .1f)) DrawScaled(canvas, wash, view.SceneRect, SKBlendMode.SrcOver);
                DrawStarClusters(canvas, scene, view, style.Glow, .8f, SKBlendMode.SrcOver, glints: false);
                break;
            default:
                // High Contrast: explicit structure, full-strength and cased, no glow.
                foreach (var line in streams) Stroke(canvas, line, view.Map, style.WaterLine, 1.1f * unit, 1f);
                foreach (var line in rivers) Stroke(canvas, line, view.Map, style.WaterLine, 1.9f * unit, 1f);
                foreach (var line in minor) Stroke(canvas, line, view.Map, style.RoadMinor, 1.4f * unit, 1f);
                foreach (var line in major) Stroke(canvas, line, view.Map, style.RoadCasing, 4.6f * unit, 1f);
                foreach (var line in major) Stroke(canvas, line, view.Map, style.RoadMajor, 2.8f * unit, 1f);
                using (var density = ComposeGlow(scene, style.Glow, 0, .28f)) DrawScaled(canvas, density, view.SceneRect, SKBlendMode.SrcOver);
                DrawStarClusters(canvas, scene, view, style.Glow, 1f, SKBlendMode.SrcOver, glints: false);
                break;
        }
    }

    /// <summary>Settlement lights as star clusters; the brightest carry a small four-point glint on dark grounds.</summary>
    private static void DrawStarClusters(SKCanvas canvas, LocationPreviewScene scene, View view, SKColor colour, float opacity,
        SKBlendMode blend, bool glints)
    {
        var unit = view.Unit;
        using var paint = new SKPaint { IsAntialias = true, BlendMode = blend };
        using var glint = new SKPaint { IsAntialias = true, BlendMode = blend, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(.6f, .5f * unit), StrokeCap = SKStrokeCap.Round };
        foreach (var light in scene.Lights)
        {
            var p = view.Map(new SKPoint(light.X, light.Y));
            paint.Color = colour.WithAlpha((byte)Math.Round(255 * opacity * (.45f + .55f * light.Intensity)));
            canvas.DrawCircle(p, (.5f + .85f * light.Intensity) * unit, paint);
            if (!glints || light.Intensity < .8f) continue;
            var length = (2 + 3 * (light.Intensity - .8f) / .2f) * unit;
            glint.Color = colour.WithAlpha((byte)Math.Round(150 * opacity));
            canvas.DrawLine(p.X - length, p.Y, p.X + length, p.Y, glint);
            canvas.DrawLine(p.X, p.Y - length, p.X, p.Y + length, glint);
        }
    }

    // ---- Device falloff -----------------------------------------------------------------------

    // The instrument's lens: clear to LensClear of its radius, LensMid of the edge haze by LensKnee, full at the rim.
    private const float LensClear = .38f, LensKnee = .75f, LensMid = .72f;

    /// <summary>Haze opacity (0..1, relative to the edge strength) that the lens applies at a point.</summary>
    private static float LensOpacity(View view, SKPoint p)
    {
        var d = SKPoint.Distance(p, view.Pin) / view.LensRadius;
        if (d <= LensClear) return 0;
        if (d <= LensKnee) return LensMid * (d - LensClear) / (LensKnee - LensClear);
        return Math.Min(1, LensMid + (1 - LensMid) * (d - LensKnee) / (1 - LensKnee));
    }

    private static void DrawDeviceFalloff(SKCanvas canvas, LocationPreviewStyle style, View view)
    {
        // The edge of the instrument's view: clear inside the scan field, falling off beyond it.
        var destination = view.Destination;
        var edge = style.Haze.WithAlpha((byte)(style.IsLight ? 120 : 215));
        using var lens = new SKPaint
        {
            Shader = SKShader.CreateRadialGradient(view.Pin, view.LensRadius,
                [style.Haze.WithAlpha(0), style.Haze.WithAlpha(0), style.Haze.WithAlpha((byte)(edge.Alpha * LensMid)), edge],
                [0f, LensClear, LensKnee, 1f], SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(destination, lens);
        using var sky = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, destination.Top), new SKPoint(0, destination.Top + destination.Height * .4f),
                [style.Haze.WithAlpha((byte)(style.IsLight ? 60 : 110)), style.Haze.WithAlpha(0)], SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(destination, sky);
    }

    // ---- Instrument ---------------------------------------------------------------------------

    private static void DrawInstrument(SKCanvas canvas, LocationPreviewScene scene, LocationPreviewStyle style, View view)
    {
        var unit = view.Unit;
        var pin = view.Pin;
        var radius = view.ScanRadius;
        var strong = style.IsHighContrast ? 230 : style.IsLight ? 190 : 170;
        var faint = style.IsHighContrast ? 160 : style.IsLight ? 110 : 80;
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Butt };
        var ring = SKRect.Create(pin.X - radius, pin.Y - radius, radius * 2, radius * 2);

        // Scan ring: four arcs broken at the cardinal points.
        stroke.Color = style.Instrument.WithAlpha((byte)strong);
        stroke.StrokeWidth = (style.IsHighContrast ? 1.6f : 1.1f) * unit;
        foreach (var start in new[] { -90f, 0f, 90f, 180f }) canvas.DrawArc(ring, start + 6, 78, false, stroke);

        // Inner acquisition ring, fine and dashed.
        stroke.Color = style.Instrument.WithAlpha((byte)faint);
        stroke.StrokeWidth = Math.Max(.7f, .7f * unit);
        using (var dash = SKPathEffect.CreateDash([2 * unit, 3 * unit], 0))
        {
            stroke.PathEffect = style.IsHighContrast ? null : dash;
            canvas.DrawCircle(pin, radius * .52f, stroke);
            stroke.PathEffect = null;
        }

        // Bearing scale: fine ticks every 10°, longer every 30°, inside the cardinal gaps a short crosshair.
        for (var degrees = 0; degrees < 360; degrees += 10)
        {
            if (degrees % 90 == 0) continue;
            var major = degrees % 30 == 0;
            stroke.Color = style.Instrument.WithAlpha((byte)(major ? strong : faint));
            stroke.StrokeWidth = (major ? .9f : .6f) * unit;
            var length = (major ? 5f : 2.5f) * unit;
            Radial(canvas, pin, degrees, radius + 2 * unit, radius + 2 * unit + length, stroke);
        }
        stroke.Color = style.Instrument.WithAlpha((byte)strong);
        stroke.StrokeWidth = 1f * unit;
        foreach (var degrees in new[] { 0f, 90f, 180f, 270f })
            Radial(canvas, pin, degrees, radius - 7 * unit, radius + 7 * unit, stroke);
        stroke.Color = style.Instrument.WithAlpha((byte)faint);
        stroke.StrokeWidth = Math.Max(.7f, .7f * unit);
        foreach (var degrees in new[] { 0f, 90f, 180f, 270f })
            Radial(canvas, pin, degrees, radius * .62f, radius * .86f, stroke);

        // North: a small filled chevron outside the ring.
        using (var north = new SKPath())
        using (var fill = new SKPaint { IsAntialias = true, Color = style.Instrument.WithAlpha((byte)strong) })
        {
            var tip = pin.Y - radius - 15 * unit;
            north.MoveTo(pin.X, tip);
            north.LineTo(pin.X + 4 * unit, tip + 6 * unit);
            north.LineTo(pin.X, tip + 4.5f * unit);
            north.LineTo(pin.X - 4 * unit, tip + 6 * unit);
            north.Close();
            canvas.DrawPath(north, fill);
        }

        // Light sectors: each meaningful off-site light concentration lights part of the scan ring. The
        // sector's span shows how broad the light is; its opacity and weight show how significant; a caret on
        // the ring points out towards it.
        var lightColour = style.IsHighContrast ? style.Instrument : style.Glow;
        foreach (var source in scene.Atmosphere.LightSources)
        {
            var sector = LightSector(pin, radius, source, unit);
            var ringRect = SKRect.Create(pin.X - sector.Radius, pin.Y - sector.Radius, sector.Radius * 2, sector.Radius * 2);
            var weight = style.IsHighContrast ? 1 : .55f + .45f * source.Strength;
            stroke.StrokeCap = SKStrokeCap.Round;
            if (style.Tone == LocationPreviewTone.Dark)
            {
                // A soft luminous track under the sector on dark grounds.
                using var track = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round,
                    StrokeWidth = 5 * unit, BlendMode = SKBlendMode.Plus, Color = lightColour.WithAlpha((byte)Math.Round(70 * source.Strength)) };
                canvas.DrawArc(ringRect, sector.StartDegrees - 90, sector.SweepDegrees, false, track);
            }
            stroke.Color = lightColour.WithAlpha((byte)Math.Round(strong * weight));
            stroke.StrokeWidth = (1.5f + .9f * source.Strength) * unit;
            // Skia measures arcs clockwise from east; compass bearings are clockwise from north.
            canvas.DrawArc(ringRect, sector.StartDegrees - 90, sector.SweepDegrees, false, stroke);
            using var caret = new SKPath();
            caret.MoveTo(sector.BaseLeft);
            caret.LineTo(sector.Apex);
            caret.LineTo(sector.BaseRight);
            stroke.StrokeWidth = 1.6f * unit;
            stroke.StrokeJoin = SKStrokeJoin.Round;
            stroke.Color = lightColour.WithAlpha((byte)strong);
            canvas.DrawPath(caret, stroke);
            stroke.StrokeCap = SKStrokeCap.Butt;
            stroke.StrokeJoin = SKStrokeJoin.Miter;
        }

        // Acquisition brackets: the lock itself marks the saved site; there is no map pin in the preview.
        stroke.Color = style.Instrument.WithAlpha((byte)strong);
        stroke.StrokeWidth = 1.3f * unit;
        var reach = 21 * unit;
        var arm = 6 * unit;
        foreach (var (dx, dy) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
        {
            var corner = new SKPoint(pin.X + dx * reach, pin.Y + dy * reach);
            canvas.DrawLine(corner.X, corner.Y, corner.X - dx * arm, corner.Y, stroke);
            canvas.DrawLine(corner.X, corner.Y, corner.X, corner.Y - dy * arm, stroke);
        }
    }

    /// <summary>Shortest and longest drawn sector, so compact lights stay visible and broad glows stay a sector.</summary>
    public const float MinimumSectorDegrees = 14, MaximumSectorDegrees = 80;

    /// <summary>
    /// Geometry of one light sector: an arc of the scan ring centred on the source bearing, spanning the
    /// source's angular spread (clamped), and a caret just outside the ring whose apex points outward along
    /// the bearing.
    /// </summary>
    internal readonly record struct LightSectorGeometry(float Radius, float StartDegrees, float SweepDegrees,
        SKPoint Apex, SKPoint BaseLeft, SKPoint BaseRight, SKPoint Outward);

    internal static LightSectorGeometry LightSector(SKPoint site, float scanRadius, LocationPreviewLightSource source, float unit)
    {
        var span = Math.Clamp(source.SpreadDegrees, MinimumSectorDegrees, MaximumSectorDegrees);
        var bearing = source.BearingDegrees;
        var radians = bearing * MathF.PI / 180;
        var outward = new SKPoint(MathF.Sin(radians), -MathF.Cos(radians));
        var apex = Polar(site, bearing, scanRadius + 10 * unit);
        var baseCentre = Polar(site, bearing, scanRadius + 5 * unit);
        var half = new SKPoint(MathF.Cos(radians) * 3.6f * unit, MathF.Sin(radians) * 3.6f * unit);
        return new(scanRadius, bearing - span / 2, span, apex, baseCentre + half, baseCentre - half, outward);
    }

    private static SKPoint Polar(SKPoint centre, float bearingDegrees, float distance)
    {
        var radians = bearingDegrees * MathF.PI / 180;
        return new(centre.X + MathF.Sin(radians) * distance, centre.Y - MathF.Cos(radians) * distance);
    }

    private static void Radial(SKCanvas canvas, SKPoint centre, float bearingDegrees, float from, float to, SKPaint paint)
    {
        var a = Polar(centre, bearingDegrees, from);
        var b = Polar(centre, bearingDegrees, to);
        canvas.DrawLine(a, b, paint);
    }

    // ---- Composition helpers ------------------------------------------------------------------

    private static SKImage ComposeGround(LocationPreviewScene scene, LocationPreviewStyle style)
    {
        var count = scene.MaskWidth * scene.MaskHeight;
        var pixels = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            float r = style.Land.Red, g = style.Land.Green, b = style.Land.Blue;
            void Over(SKColor c, byte coverage)
            {
                if (coverage == 0) return;
                var a = coverage / 255f;
                r += (c.Red - r) * a; g += (c.Green - g) * a; b += (c.Blue - b) * a;
            }
            Over(style.Vegetation, scene.Vegetation[i]);
            Over(style.BuiltUp, scene.BuiltUp[i]);
            Over(style.Water, scene.Water[i]);
            var o = i * 4;
            pixels[o] = (byte)b; pixels[o + 1] = (byte)g; pixels[o + 2] = (byte)r; pixels[o + 3] = 255;
        }
        return Image(scene, pixels);
    }

    /// <summary>Settlement light as one premultiplied layer: <paramref name="bloom"/> of the wide field plus <paramref name="core"/> of the tight one.</summary>
    private static SKImage ComposeGlow(LocationPreviewScene scene, SKColor colour, float bloom, float core)
    {
        var count = scene.MaskWidth * scene.MaskHeight;
        var wide = bloom > 0 ? scene.SettlementBloom : null;
        var pixels = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            var alpha = Math.Clamp(((wide?[i] ?? 0) * bloom + scene.Settlement[i] * core) / 255f, 0, 1);
            if (alpha <= 0) continue;
            Put(pixels, i * 4, colour.Red * alpha, colour.Green * alpha, colour.Blue * alpha, alpha);
        }
        return Image(scene, pixels);
    }

    /// <summary>Two-tone nebular haze as one premultiplied layer, seen through the instrument's lens.</summary>
    private static SKImage ComposeNebula(LocationPreviewScene scene, LocationPreviewAtmosphere atmosphere, View view,
        SKColor primary, float primaryStrength, SKColor secondary, float secondaryStrength)
    {
        var count = scene.MaskWidth * scene.MaskHeight;
        var pixels = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            // The haze thins towards the lens edge, so the outer field (where card text may sit) stays dark.
            var at = view.Map(new SKPoint((i % scene.MaskWidth + .5f) * LocationPreviewScene.MaskScale,
                (i / scene.MaskWidth + .5f) * LocationPreviewScene.MaskScale));
            var transmission = 1 - LensOpacity(view, at);
            transmission *= transmission;
            var a = atmosphere.NebulaPrimary[i] / 255f * primaryStrength * transmission;
            var b = atmosphere.NebulaSecondary[i] / 255f * secondaryStrength * transmission;
            var alpha = Math.Clamp(a + b, 0, 1);
            if (alpha <= 0) continue;
            Put(pixels, i * 4, primary.Red * a + secondary.Red * b, primary.Green * a + secondary.Green * b,
                primary.Blue * a + secondary.Blue * b, alpha);
        }
        return Image(scene, pixels);
    }

    private static void Put(byte[] pixels, int offset, float red, float green, float blue, float alpha)
    {
        var limit = alpha * 255;
        pixels[offset] = (byte)Math.Min(limit, blue);
        pixels[offset + 1] = (byte)Math.Min(limit, green);
        pixels[offset + 2] = (byte)Math.Min(limit, red);
        pixels[offset + 3] = (byte)Math.Round(limit);
    }

    private static SKImage Image(LocationPreviewScene scene, byte[] pixels) =>
        SKImage.FromPixelCopy(new SKImageInfo(scene.MaskWidth, scene.MaskHeight, SKColorType.Bgra8888, SKAlphaType.Premul), pixels, scene.MaskWidth * 4)!;

    private static void DrawScaled(SKCanvas canvas, SKImage image, SKRect destination, SKBlendMode blend)
    {
        using var paint = new SKPaint { BlendMode = blend };
        canvas.DrawImage(image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
    }

    private static void Stroke(SKCanvas canvas, PreviewLine line, Func<SKPoint, SKPoint> map, SKColor colour, float width, float opacity,
        SKBlendMode blend = SKBlendMode.SrcOver)
    {
        using var path = new SKPath();
        path.MoveTo(map(line.Points[0]));
        for (var i = 1; i < line.Points.Length; i++) path.LineTo(map(line.Points[i]));
        using var paint = new SKPaint
        {
            Color = colour.WithAlpha((byte)Math.Round(colour.Alpha * opacity)), StrokeWidth = width, Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true, BlendMode = blend
        };
        canvas.DrawPath(path, paint);
    }
}
