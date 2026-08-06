using System.Text.Json;
using System.Text.Json.Serialization;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Time;
using NodaTime;
using SkiaSharp;
using Noctaxis.Core.Measurements;

namespace Noctaxis.Core.Export;

public sealed record ScoutingCardExportContext(
    string LocationName,
    SavedLocation? SavedLocation,
    WeatherSettings WeatherSettings,
    string Units);

public interface IScoutingCardExporter
{
    Task<byte[]> RenderPngAsync(PlanningSnapshot snapshot, ScoutingCardExportContext context, CancellationToken cancellationToken);
}

public sealed class ScoutingCardExporter(ITimeZoneResolver timeZones, IClock clock) : IScoutingCardExporter
{
    public const string MetadataKey = "Noctaxis.ExportData";
    public const int ExportSchemaVersion = 2;

    public Task<byte[]> RenderPngAsync(PlanningSnapshot snapshot, ScoutingCardExportContext context, CancellationToken cancellationToken) =>
        Task.Run(() => Render(snapshot, context, cancellationToken), cancellationToken);

    private byte[] Render(PlanningSnapshot snapshot, ScoutingCardExportContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const int width = 1080;
        const int height = 1440;
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColor.Parse("#0B0F17"));
        using var title = Text(42, "#F1F4FA", true);
        using var heading = Text(24, "#AAB4C3", true);
        using var body = Text(25, "#E5E9F0");
        using var muted = Text(20, "#8792A3");
        using var accent = new SKPaint { Color = SKColor.Parse(snapshot.Position.Target.IsSun ? "#F3B34C" : snapshot.Position.Target.IsMoon ? "#79B8FF" : "#B790FF"), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4 };

        var local = timeZones.InZone(snapshot.Session.Instant, snapshot.Session.TimeZoneId);
        Draw(canvas, "NOCTAXIS  ·  SCOUTING CARD", 64, 78, title);
        Draw(canvas, context.LocationName, 64, 132, heading);
        Draw(canvas, $"{snapshot.Session.Observer.Latitude:F5}°, {snapshot.Session.Observer.Longitude:F5}°  ·  {snapshot.Session.TimeZoneId}", 64, 170, muted);
        Draw(canvas, $"{local:ddd, dd MMM yyyy  HH:mm}  ·  {snapshot.Position.Target.DisplayName}", 64, 222, body);
        Draw(canvas, "TARGET", 64, 285, heading);
        Draw(canvas, $"Azimuth  {snapshot.Position.Horizontal.AzimuthDegrees:F1}°     Altitude  {snapshot.Position.Horizontal.AltitudeDegrees:+0.0;-0.0;0.0}°", 64, 330, body);
        Draw(canvas, $"Rise {Format(snapshot.Position.Events.Rise, snapshot.Session.TimeZoneId)}   Terrain clear {Format(snapshot.TerrainCrossings.ClearsTerrain, snapshot.Session.TimeZoneId)}", 64, 370, body);
        Draw(canvas, $"Transit {Format(snapshot.Position.Events.Transit, snapshot.Session.TimeZoneId)}   Set {Format(snapshot.Position.Events.Set, snapshot.Session.TimeZoneId)}", 64, 410, body);
        DrawBearingSchematic(canvas, snapshot, 64, 455, 440, 420, accent, body, muted);
        DrawHorizon(canvas, snapshot, 548, 455, 468, 420, accent, muted);
        Draw(canvas, "CAMERA", 64, 930, heading);
        Draw(canvas, $"{snapshot.Session.Lens.Preset} · {snapshot.Session.Lens.FocalLengthMillimetres:F0} mm · {snapshot.Session.Lens.Orientation}", 64, 974, body);
        Draw(canvas, $"FOV  H {snapshot.FieldOfView.HorizontalDegrees:F1}°  V {snapshot.FieldOfView.VerticalDegrees:F1}°  D {snapshot.FieldOfView.DiagonalDegrees:F1}°", 64, 1014, body);
        Draw(canvas, "WEATHER  ·  OPEN-METEO", 64, 1076, heading);
        var weather = snapshot.Weather.Conditions;
        var measurementSystem = MeasurementUnits.Parse(context.Units);
        Draw(canvas, weather is null ? snapshot.Weather.Message : $"{weather.Summary} · cloud {Value(weather.CloudCoverPercent, "%")} · precipitation {Value(weather.PrecipitationProbabilityPercent, "%")}", 64, 1120, body);
        if (weather is not null)
            Draw(canvas, $"{MeasurementUnits.FormatTemperature(weather.TemperatureCelsius, measurementSystem)} · humidity {Value(weather.HumidityPercent, "%")} · wind {MeasurementUnits.FormatWindSpeed(weather.WindSpeedMetresPerSecond, measurementSystem)} gust {MeasurementUnits.FormatWindSpeed(weather.WindGustMetresPerSecond, measurementSystem)}", 64, 1160, body);
        Draw(canvas, "Map panel is a bearing/FOV schematic; not ground framing.", 64, 1290, muted);
        Draw(canvas, MapProvider.Attribution, 64, 1324, muted);
        Draw(canvas, "Terrain crossings are sampled estimates. Weather may differ from conditions on site.", 64, 1360, muted);

        using var skiaImage = SKImage.FromBitmap(bitmap);
        using var encoded = skiaImage.Encode(SKEncodedImageFormat.Png, 100);
        var metadata = CreateMetadata(snapshot, context);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var json = JsonSerializer.Serialize(metadata, jsonOptions);
        return PngMetadataWriter.AddText(encoded.ToArray(), MetadataKey, json);
    }

    private NoctaxisExportMetadata CreateMetadata(PlanningSnapshot s, ScoutingCardExportContext context)
    {
        string? Iso(Instant? value) => value?.ToDateTimeOffset().ToString("O");
        var enabled = context.WeatherSettings.EffectiveFields;
        return new NoctaxisExportMetadata(
            typeof(ScoutingCardExporter).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            ExportSchemaVersion,
            clock.GetCurrentInstant().ToDateTimeOffset(),
            context.LocationName,
            s.Session.Observer,
            s.Session.Instant.ToDateTimeOffset(),
            timeZones.InZone(s.Session.Instant, s.Session.TimeZoneId).ToString("yyyy-MM-dd'T'HH:mm:ss z", null),
            s.Session.TimeZoneId,
            s.Position.Target,
            s.Session.EffectiveVisibleObjects,
            new ExportPosition(s.Position.Horizontal, Iso(s.Position.Events.Rise), Iso(s.Position.Events.Transit), Iso(s.Position.Events.Set)),
            ExportSunMoon.From(s.Astronomy, enabled, Iso),
            s.Session.Lens,
            s.FieldOfView,
            s.Position.Horizontal.AzimuthDegrees,
            CardWeather.From(s.Weather.Conditions, enabled),
            s.Weather.Conditions?.RetrievedAt.ToDateTimeOffset(),
            enabled,
            context.SavedLocation,
            new ExportDisplaySettings(MeasurementUnits.NormaliseId(context.Units), MapProvider.Attribution, "Bearing and field-of-view schematic"),
            s.Path.Samples.Select(x => new CardPathSample(x.Instant.ToDateTimeOffset(), x.Horizontal)).ToArray(),
            s.Terrain.Samples);
    }

    private void DrawBearingSchematic(SKCanvas canvas, PlanningSnapshot s, float x, float y, float width, float height, SKPaint accent, TextStyle body, TextStyle muted)
    {
        using var panel = new SKPaint { Color = SKColor.Parse("#141B27"), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(x, y, x + width, y + height), 16, 16, panel);
        var cx = x + width / 2; var cy = y + height / 2 + 12; var radius = Math.Min(width, height) * .36f;
        using var grid = new SKPaint { Color = SKColor.Parse("#354052"), StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };
        canvas.DrawCircle(cx, cy, radius, grid); Draw(canvas, "N", cx - 8, cy - radius - 14, body);
        var bearing = s.Position.Horizontal.AzimuthDegrees * Angles.DegreesToRadians;
        var half = s.FieldOfView.HorizontalDegrees / 2 * Angles.DegreesToRadians;
        using var wedge = new SKPath(); wedge.MoveTo(cx, cy);
        wedge.LineTo(cx + radius * (float)Math.Sin(bearing - half), cy - radius * (float)Math.Cos(bearing - half));
        wedge.LineTo(cx + radius * (float)Math.Sin(bearing + half), cy - radius * (float)Math.Cos(bearing + half)); wedge.Close();
        using var fill = new SKPaint { Color = accent.Color.WithAlpha(45), IsAntialias = true };
        canvas.DrawPath(wedge, fill); canvas.DrawLine(cx, cy, cx + radius * (float)Math.Sin(bearing), cy - radius * (float)Math.Cos(bearing), accent);
        Draw(canvas, "Bearing / field of view", x + 20, y + 34, muted);
    }

    private static void DrawHorizon(SKCanvas canvas, PlanningSnapshot s, float x, float y, float width, float height, SKPaint accent, TextStyle muted)
    {
        using var panel = new SKPaint { Color = SKColor.Parse("#141B27"), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(x, y, x + width, y + height), 16, 16, panel);
        float Y(double altitude) => y + height - 38 - (float)Math.Clamp((altitude + 10) / 100, 0, 1) * (height - 72);
        using var terrainPaint = new SKPaint { Color = SKColor.Parse("#465243"), StrokeWidth = 3, IsAntialias = true };
        using var pathPaint = new SKPaint { Color = accent.Color, StrokeWidth = 3, IsAntialias = true };
        for (var i = 1; i < s.Path.Samples.Count; i++)
            canvas.DrawLine(x + (i - 1) * width / (s.Path.Samples.Count - 1), Y(s.Terrain.AltitudeAt(s.Path.Samples[i - 1].Horizontal.AzimuthDegrees)), x + i * width / (s.Path.Samples.Count - 1), Y(s.Terrain.AltitudeAt(s.Path.Samples[i].Horizontal.AzimuthDegrees)), terrainPaint);
        for (var i = 1; i < s.Path.Samples.Count; i++)
            canvas.DrawLine(x + (i - 1) * width / (s.Path.Samples.Count - 1), Y(s.Path.Samples[i - 1].Horizontal.AltitudeDegrees), x + i * width / (s.Path.Samples.Count - 1), Y(s.Path.Samples[i].Horizontal.AltitudeDegrees), pathPaint);
        Draw(canvas, "Daily altitude / terrain", x + 20, y + 34, muted);
    }

    private string Format(Instant? instant, string zone) => instant is null ? "—" : timeZones.InZone(instant.Value, zone).ToString("HH:mm", null);
    private static string Value(double? value, string suffix) => value.HasValue ? $"{value:F0}{suffix}" : "—";
    private static TextStyle Text(float size, string colour, bool bold = false) => new(size, colour, bold);
    private static void Draw(SKCanvas canvas, string text, float x, float y, TextStyle style) => canvas.DrawText(text, x, y, SKTextAlign.Left, style.Font, style.Paint);

    private sealed class TextStyle : IDisposable
    {
        public TextStyle(float size, string colour, bool bold)
        {
            Paint = new SKPaint { Color = SKColor.Parse(colour), IsAntialias = true };
            Font = new SKFont(SKTypeface.FromFamilyName("Arial", bold ? SKFontStyle.Bold : SKFontStyle.Normal), size);
        }
        public SKPaint Paint { get; }
        public SKFont Font { get; }
        public void Dispose() { Font.Dispose(); Paint.Dispose(); }
    }
}

public sealed record NoctaxisExportMetadata(
    string ApplicationVersion, int ExportSchemaVersion, DateTimeOffset ExportedAtUtc,
    string LocationName, GeoCoordinate Coordinate, DateTimeOffset SelectedInstantUtc,
    string SelectedLocalDateTime, string TimeZoneId, AstralTarget SelectedTarget,
    IReadOnlyList<CelestialObjectSelection> VisibleObjects, ExportPosition TargetPosition, ExportSunMoon SunAndMoon, LensConfiguration Lens,
    FieldOfView FieldOfView, double MapOrCameraDirectionDegrees, CardWeather? Weather,
    DateTimeOffset? WeatherRetrievedAtUtc, IReadOnlyList<WeatherField> EnabledWeatherFields,
    SavedLocation? SavedLocation, ExportDisplaySettings DisplaySettings,
    IReadOnlyList<CardPathSample> Path, IReadOnlyList<TerrainHorizonSample> Terrain);

public sealed record ExportPosition(HorizontalCoordinate Horizontal, string? RiseUtc, string? TransitUtc, string? SetUtc);
public sealed record ExportDisplaySettings(string Units, string MapAttribution, string MapRepresentation);
public sealed record CardPathSample(DateTimeOffset InstantUtc, HorizontalCoordinate Horizontal);

public sealed record ExportSunMoon(
    string? SunriseUtc, string? SunsetUtc, string? CivilDawnUtc, string? CivilDuskUtc,
    string? NauticalDawnUtc, string? NauticalDuskUtc, string? AstronomicalDawnUtc,
    string? AstronomicalDuskUtc, string? AstronomicalDarkness,
    string? MoonriseUtc, string? MoonsetUtc, double? MoonPhaseAngleDegrees, double? MoonIlluminatedFraction)
{
    public static ExportSunMoon From(AstronomyContext value, IReadOnlyList<WeatherField> enabled, Func<Instant?, string?> iso)
    {
        var t = value.Sun.Twilight;
        bool Has(WeatherField field) => enabled.Contains(field);
        return new(
            Has(WeatherField.Sunrise) ? iso(t?.Sunrise) : null, Has(WeatherField.Sunset) ? iso(t?.Sunset) : null,
            Has(WeatherField.CivilTwilight) ? iso(t?.CivilDawn) : null, Has(WeatherField.CivilTwilight) ? iso(t?.CivilDusk) : null,
            Has(WeatherField.NauticalTwilight) ? iso(t?.NauticalDawn) : null, Has(WeatherField.NauticalTwilight) ? iso(t?.NauticalDusk) : null,
            Has(WeatherField.AstronomicalTwilight) ? iso(t?.AstronomicalDawn) : null, Has(WeatherField.AstronomicalTwilight) ? iso(t?.AstronomicalDusk) : null,
            Has(WeatherField.AstronomicalDarkness) ? $"before {iso(t?.AstronomicalDawn)}; after {iso(t?.AstronomicalDusk)}" : null,
            Has(WeatherField.Moonrise) ? iso(value.Moon.Events.Rise) : null, Has(WeatherField.Moonset) ? iso(value.Moon.Events.Set) : null,
            Has(WeatherField.MoonPhase) ? value.Moon.MoonPhaseAngleDegrees : null,
            Has(WeatherField.MoonIllumination) ? value.Moon.MoonIlluminatedFraction : null);
    }
}

public sealed record CardWeather(
    DateTimeOffset ForecastInstantUtc, double? CloudCoverPercent, double? LowCloudPercent,
    double? MediumCloudPercent, double? HighCloudPercent, double? PrecipitationProbabilityPercent,
    double? PrecipitationMillimetres, string? PrecipitationType, double? WindSpeedMetresPerSecond,
    double? WindDirectionDegrees, double? WindGustMetresPerSecond, double? TemperatureCelsius,
    double? HumidityPercent, double? DewPointCelsius, double? VisibilityKilometres, string Summary,
    DateTimeOffset RetrievedAtUtc)
{
    public static CardWeather? From(WeatherConditions? value, IReadOnlyList<WeatherField> fields)
    {
        if (value is null) return null;
        bool Has(WeatherField field) => fields.Contains(field);
        return new(value.ForecastInstant.ToDateTimeOffset(),
            Has(WeatherField.TotalCloudCover) ? value.CloudCoverPercent : null,
            Has(WeatherField.LowCloudCover) ? value.LowCloudPercent : null,
            Has(WeatherField.MediumCloudCover) ? value.MediumCloudPercent : null,
            Has(WeatherField.HighCloudCover) ? value.HighCloudPercent : null,
            Has(WeatherField.PrecipitationProbability) ? value.PrecipitationProbabilityPercent : null,
            Has(WeatherField.PrecipitationAmount) ? value.PrecipitationMillimetres : null,
            Has(WeatherField.PrecipitationType) ? value.PrecipitationType : null,
            Has(WeatherField.WindSpeed) ? value.WindSpeedMetresPerSecond : null,
            Has(WeatherField.WindDirection) ? value.WindDirectionDegrees : null,
            Has(WeatherField.WindGusts) ? value.WindGustMetresPerSecond : null,
            Has(WeatherField.Temperature) ? value.TemperatureCelsius : null,
            Has(WeatherField.RelativeHumidity) ? value.HumidityPercent : null,
            Has(WeatherField.DewPoint) ? value.DewPointCelsius : null,
            Has(WeatherField.Visibility) ? value.VisibilityKilometres : null,
            value.Summary, value.RetrievedAt.ToDateTimeOffset());
    }
}
