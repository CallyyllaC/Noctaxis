using CosineKitty;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Time;
using NodaTime;
using AstronomyEngine = CosineKitty.Astronomy;

namespace Noctaxis.Core.Astronomy;

public interface IAstronomyService
{
    TargetPosition Calculate(AstralTarget target, GeoCoordinate observer, Instant instant, LocalDate localDate, string timeZoneId);
    Task<TargetPosition> CalculateCatalogueAsync(AstralTarget target, GeoCoordinate observer, Instant instant,
        LocalDate localDate, string timeZoneId, CancellationToken cancellationToken) =>
        Task.Run(() => Calculate(target, observer, instant, localDate, timeZoneId), cancellationToken);
    Task<AstralPath> CalculatePathAsync(AstralTarget target, GeoCoordinate observer, LocalDate localDate, string timeZoneId, Instant selectedInstant, Duration interval, CancellationToken cancellationToken);
}

public sealed class AstronomyEngineService(ITimeZoneResolver timeZones) : IAstronomyService
{
    public Task<TargetPosition> CalculateCatalogueAsync(AstralTarget target, GeoCoordinate observer, Instant instant,
        LocalDate localDate, string timeZoneId, CancellationToken cancellationToken)
    {
        if (target.IsSun || target.IsMoon || !target.HasEquatorialCoordinates ||
            !target.CoordinateEpoch.Equals("J2000", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A catalogue calculation requires a J2000 RA/Dec target.", nameof(target));
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Calculate(target, observer, instant, localDate, timeZoneId);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken);
    }

    public TargetPosition Calculate(AstralTarget target, GeoCoordinate observer, Instant instant, LocalDate localDate, string timeZoneId)
    {
        var horizontal = Horizontal(target, observer, instant);
        var events = CalculateEvents(target, observer, localDate, timeZoneId);
        var astroTime = ToAstroTime(instant);
        double? illumination = target.IsMoon ? AstronomyEngine.Illumination(Body.Moon, astroTime).phase_fraction : null;
        double? phase = target.IsMoon ? AstronomyEngine.MoonPhase(astroTime) : null;
        var twilight = target.IsSun ? CalculateTwilight(observer, localDate, timeZoneId) : null;
        return new TargetPosition(target, instant, horizontal, events, illumination, phase, twilight);
    }

    public async Task<AstralPath> CalculatePathAsync(AstralTarget target, GeoCoordinate observer, LocalDate localDate, string timeZoneId, Instant selectedInstant, Duration interval, CancellationToken cancellationToken)
    {
        if (interval <= Duration.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        var bounds = timeZones.GetLocalDay(localDate, timeZoneId);
        return await Task.Run(() =>
        {
            var samples = new List<AstralPathSample>();
            for (var cursor = bounds.Start; cursor <= bounds.End; cursor += interval)
            {
                cancellationToken.ThrowIfCancellationRequested();
                samples.Add(new AstralPathSample(cursor, Horizontal(target, observer, cursor)));
            }
            if (samples.Count == 0 || samples[^1].Instant < bounds.End)
                samples.Add(new AstralPathSample(bounds.End, Horizontal(target, observer, bounds.End)));

            var events = target.HasEquatorialCoordinates
                ? DeriveEventsFromSamples(samples)
                : CalculateEvents(target, observer, localDate, timeZoneId);
            return new AstralPath(localDate, timeZoneId, interval, samples, events, selectedInstant);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static HorizontalCoordinate Horizontal(AstralTarget target, GeoCoordinate location, Instant instant)
    {
        var time = ToAstroTime(instant);
        var observer = ToObserver(location);
        double ra;
        double dec;

        if (target.IsSun || target.IsMoon)
        {
            var equatorial = AstronomyEngine.Equator(target.IsSun ? Body.Sun : Body.Moon, time, observer, EquatorEpoch.OfDate, Aberration.Corrected);
            ra = equatorial.ra;
            dec = equatorial.dec;
        }
        else if (target.HasEquatorialCoordinates && target.CoordinateEpoch.Equals("J2000", StringComparison.OrdinalIgnoreCase))
        {
            var j2000Sphere = new Spherical(target.DeclinationDegrees!.Value, target.RightAscensionHours!.Value * 15d, 1d);
            var j2000Vector = AstronomyEngine.VectorFromSphere(j2000Sphere, time);
            var ofDateVector = AstronomyEngine.RotateVector(AstronomyEngine.Rotation_EQJ_EQD(time), j2000Vector);
            var equatorial = AstronomyEngine.EquatorFromVector(ofDateVector);
            ra = equatorial.ra;
            dec = equatorial.dec;
        }
        else
        {
            throw new InvalidOperationException($"Target '{target.DisplayName}' has no calculable coordinates.");
        }

        var horizon = AstronomyEngine.Horizon(time, observer, ra, dec, Refraction.Normal);
        return new HorizontalCoordinate(Angles.NormaliseDegrees(horizon.azimuth), horizon.altitude);
    }

    private TargetEvents CalculateEvents(AstralTarget target, GeoCoordinate location, LocalDate localDate, string timeZoneId)
    {
        var bounds = timeZones.GetLocalDay(localDate, timeZoneId);
        if (target.HasEquatorialCoordinates)
        {
            var samples = new List<AstralPathSample>();
            for (var cursor = bounds.Start; cursor <= bounds.End; cursor += Duration.FromMinutes(2))
                samples.Add(new AstralPathSample(cursor, Horizontal(target, location, cursor)));
            return DeriveEventsFromSamples(samples);
        }

        var body = target.IsSun ? Body.Sun : Body.Moon;
        var observer = ToObserver(location);
        var start = ToAstroTime(bounds.Start);
        var spanDays = (bounds.End - bounds.Start).TotalDays + 0.01;
        var rise = AstronomyEngine.SearchRiseSet(body, observer, Direction.Rise, start, spanDays, 0);
        var set = AstronomyEngine.SearchRiseSet(body, observer, Direction.Set, start, spanDays, 0);
        var transit = AstronomyEngine.SearchHourAngle(body, observer, 0, start, +1);
        return new TargetEvents(
            InBounds(rise, bounds),
            InBounds(transit.time, bounds),
            InBounds(set, bounds));
    }

    private TwilightEvents CalculateTwilight(GeoCoordinate location, LocalDate date, string timeZoneId)
    {
        var bounds = timeZones.GetLocalDay(date, timeZoneId);
        var observer = ToObserver(location);
        var start = ToAstroTime(bounds.Start);
        var days = (bounds.End - bounds.Start).TotalDays + 0.01;
        Instant? Search(Direction direction, double altitude) => InBounds(
            AstronomyEngine.SearchAltitude(Body.Sun, observer, direction, start, days, altitude), bounds);

        return new TwilightEvents(
            InBounds(AstronomyEngine.SearchRiseSet(Body.Sun, observer, Direction.Rise, start, days, 0), bounds),
            InBounds(AstronomyEngine.SearchRiseSet(Body.Sun, observer, Direction.Set, start, days, 0), bounds),
            Search(Direction.Rise, -6), Search(Direction.Set, -6),
            Search(Direction.Rise, -12), Search(Direction.Set, -12),
            Search(Direction.Rise, -18), Search(Direction.Set, -18));
    }

    private static TargetEvents DeriveEventsFromSamples(IReadOnlyList<AstralPathSample> samples)
    {
        Instant? rise = null;
        Instant? set = null;
        if (samples.Count == 0) return new TargetEvents(null, null, null);
        var transit = samples.MaxBy(x => x.Horizontal.AltitudeDegrees)!.Instant;
        for (var i = 1; i < samples.Count; i++)
        {
            var previous = samples[i - 1];
            var current = samples[i];
            if (previous.Horizontal.AltitudeDegrees < 0 && current.Horizontal.AltitudeDegrees >= 0 && rise is null)
                rise = InterpolateCrossing(previous, current);
            if (previous.Horizontal.AltitudeDegrees >= 0 && current.Horizontal.AltitudeDegrees < 0)
                set = InterpolateCrossing(previous, current);
        }
        return new TargetEvents(rise, transit, set);
    }

    private static Instant InterpolateCrossing(AstralPathSample a, AstralPathSample b)
    {
        var denominator = Math.Abs(a.Horizontal.AltitudeDegrees) + Math.Abs(b.Horizontal.AltitudeDegrees);
        var fraction = denominator <= double.Epsilon ? 0.5 : Math.Abs(a.Horizontal.AltitudeDegrees) / denominator;
        return a.Instant + (b.Instant - a.Instant) * fraction;
    }

    private static Observer ToObserver(GeoCoordinate location) => new(location.Latitude, location.Longitude, location.ElevationMetres);
    private static AstroTime ToAstroTime(Instant instant) => new(instant.ToDateTimeUtc());
    private static Instant? InBounds(AstroTime? time, (Instant Start, Instant End) bounds)
    {
        if (time is null) return null;
        var instant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(time.ToUtcDateTime(), DateTimeKind.Utc));
        return instant >= bounds.Start && instant < bounds.End ? instant : null;
    }
}
