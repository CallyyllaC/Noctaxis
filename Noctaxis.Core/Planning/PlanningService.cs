using Noctaxis.Core.Localisation;
using Noctaxis.Core.Astronomy;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Terrain;
using Noctaxis.Core.Time;
using Noctaxis.Core.Weather;
using NodaTime;

namespace Noctaxis.Core.Planning;

public interface IPlanningService
{
    TargetPosition CalculateCurrent(PlanningSession session);
    /// <summary>
    /// The Timeline's paths: each of the session's targets (the same set the snapshot plans, in the same
    /// order) sampled from <paramref name="start"/> for 24 hours, across midnight if it falls there. Only the
    /// span differs from the snapshot's own paths, which stay on the local day. Without an implementation,
    /// there is nothing to draw.
    /// </summary>
    Task<IReadOnlyList<TimelinePath>> CalculateTimelineAsync(PlanningSession session, Instant start, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TimelinePath>>([]);

    /// <summary>
    /// The Sun's rise, set and astronomical-darkness boundaries and the Moon's rise and set inside the Timeline
    /// window from <paramref name="start"/>, from the same calculations as the plan's own day. Without an
    /// implementation, there are none.
    /// </summary>
    Task<TimelineEvents> CalculateTimelineEventsAsync(PlanningSession session, Instant start, CancellationToken cancellationToken) =>
        Task.FromResult(TimelineEvents.None);

    /// <summary>
    /// The focused target's and the Sun's paths from <paramref name="start"/> to <paramref name="end"/>, sampled
    /// at the same instants, for the shot-viability useful window. Local astronomy only: nothing is fetched.
    /// Without an implementation there are none, and the useful window is reported as unavailable.
    /// </summary>
    Task<Viability.ShotWindowPaths?> CalculateShotWindowPathsAsync(PlanningSession session, Instant start, Instant end,
        CancellationToken cancellationToken) => Task.FromResult<Viability.ShotWindowPaths?>(null);

    /// <summary>How long the Timeline runs on from where it is anchored.</summary>
    public static readonly Duration TimelineLength = Duration.FromHours(24);

    Task<PlanningSnapshot> CalculateCoreSnapshotAsync(PlanningSession session,
        CancellationToken cancellationToken);
    Task<PlannerEnvironmentSnapshot> LoadEnvironmentAsync(PlanningSession session,
        CancellationToken cancellationToken);
    Task<PlannerEnvironmentSnapshot> LoadEnvironmentAsync(PlanningSession session,
        double cameraHeightAboveGroundMetres, CancellationToken cancellationToken) =>
        LoadEnvironmentAsync(session, cancellationToken);
    async Task<TerrainHorizonProfile> PrioritiseTerrainAsync(PlanningSession session,
        IReadOnlyList<double> bearings, CancellationToken cancellationToken) =>
        (await LoadEnvironmentAsync(session, cancellationToken).ConfigureAwait(false)).HorizonProfile;
    Task<TerrainHorizonProfile> PrioritiseTerrainAsync(PlanningSession session,
        double cameraHeightAboveGroundMetres, IReadOnlyList<double> bearings,
        CancellationToken cancellationToken) => PrioritiseTerrainAsync(session, bearings, cancellationToken);
    Task<WeatherResult> LoadWeatherAsync(PlanningSession session, WeatherSettings weatherSettings,
        CancellationToken cancellationToken);
    PlanningRefreshWork StartRefresh(PlanningSession session, WeatherSettings weatherSettings,
        CancellationToken cancellationToken);
    PlanningRefreshWork StartRefresh(PlanningSession session, WeatherSettings weatherSettings,
        double cameraHeightAboveGroundMetres, CancellationToken cancellationToken) =>
        StartRefresh(session, weatherSettings, cancellationToken);
    Task<PlanningSnapshot> CalculateSnapshotAsync(PlanningSession session, WeatherSettings weatherSettings, CancellationToken cancellationToken);
    Task<WeatherResult> RefreshWeatherAsync(PlanningSession session, WeatherSettings weatherSettings, CancellationToken cancellationToken);
}

/// <summary>
/// The independently useful pieces of a Planner refresh. The UI may commit the core astronomy
/// snapshot before optional static-environment and weather enrichment have finished.
/// </summary>
public sealed record PlanningRefreshWork(
    Task<PlanningSnapshot> Core,
    Task<PlannerEnvironmentSnapshot> Environment,
    Task<WeatherResult> Weather,
    Func<IReadOnlyList<double>, CancellationToken, Task<TerrainHorizonProfile>>? PriorityTerrain = null);

public sealed class PlanningService(
    ITargetCatalogue catalogue,
    IAstronomyService astronomy,
    ILensCalculator lenses,
    IPlannerEnvironmentService environment,
    IWeatherProvider weather,
    ITimeZoneResolver timeZones) : IPlanningService
{
    public TargetPosition CalculateCurrent(PlanningSession session)
    {
        var target = catalogue.Get(session.TargetId);
        var localDate = timeZones.InZone(session.Instant, session.TimeZoneId).Date;
        return astronomy.Calculate(target, session.Observer, session.Instant, localDate, session.TimeZoneId);
    }

    public async Task<PlanningSnapshot> CalculateSnapshotAsync(
        PlanningSession session,
        WeatherSettings weatherSettings,
        CancellationToken cancellationToken)
    {
        var work = StartRefresh(session, weatherSettings, cancellationToken);
        await Task.WhenAll(work.Core, work.Environment, work.Weather).ConfigureAwait(false);
        var core = await work.Core.ConfigureAwait(false);
        var environmentSnapshot = await work.Environment.ConfigureAwait(false);
        var weatherResult = await work.Weather.ConfigureAwait(false);
        var horizon = environmentSnapshot.HorizonProfile;
        return core with
        {
            Terrain = horizon,
            TerrainCrossings = TerrainCrossingCalculator.Calculate(core.Path, horizon),
            Weather = weatherResult,
            Environment = environmentSnapshot
        };
    }

    public PlanningRefreshWork StartRefresh(
        PlanningSession session,
        WeatherSettings weatherSettings,
        CancellationToken cancellationToken) =>
        StartRefresh(session, weatherSettings, AppSettings.DefaultCameraHeightAboveGroundMetres,
            cancellationToken);

    public PlanningRefreshWork StartRefresh(
        PlanningSession session,
        WeatherSettings weatherSettings,
        double cameraHeightAboveGroundMetres,
        CancellationToken cancellationToken) => new(
        CalculateCoreSnapshotAsync(session, cancellationToken),
        LoadEnvironmentAsync(session, cameraHeightAboveGroundMetres, cancellationToken),
        LoadWeatherAsync(session, weatherSettings, cancellationToken),
        session.EnableTerrainCalculations ? (bearings, token) => environment.GetPriorityHorizonAsync(session.Observer,
            CreateTerrainRequest(session, cameraHeightAboveGroundMetres), bearings, token) : null);

    // The targets a plan draws: the shown ones, once each, in the user's order. Being focused (or the plan's
    // anchor) never adds a target: a hidden target is not drawn, whatever else it is.
    private AstralTarget[] Targets(PlanningSession session) => session.EffectiveVisibleObjects
        .Where(item => item.IsVisible)
        .OrderBy(item => item.Order)
        .Select(item => catalogue.ResolveId(item.TargetId))
        .Where(id => id is not null)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(id => catalogue.Get(id!))
        .ToArray();

    public async Task<IReadOnlyList<TimelinePath>> CalculateTimelineAsync(PlanningSession session, Instant start,
        CancellationToken cancellationToken)
    {
        var end = start + IPlanningService.TimelineLength;
        return await Task.WhenAll(Targets(session).Select(async target => new TimelinePath(target, await astronomy.CalculatePathAsync(
            target, session.Observer, start, end, session.TimeZoneId, session.Instant, Duration.FromMinutes(10), cancellationToken)
            .ConfigureAwait(false)))).ConfigureAwait(false);
    }

    public async Task<Viability.ShotWindowPaths?> CalculateShotWindowPathsAsync(PlanningSession session, Instant start, Instant end,
        CancellationToken cancellationToken)
    {
        var target = catalogue.Get(session.TargetId);
        var sun = catalogue.Get("sun");
        var interval = Duration.FromTimeSpan(Viability.ShotViabilityPolicy.WindowPathsInterval);
        var targetPath = astronomy.CalculatePathAsync(target, session.Observer, start, end, session.TimeZoneId,
            session.Instant, interval, cancellationToken);
        var sunPath = target.IsSun ? targetPath : astronomy.CalculatePathAsync(sun, session.Observer, start, end,
            session.TimeZoneId, session.Instant, interval, cancellationToken);
        var paths = await Task.WhenAll(targetPath, sunPath).ConfigureAwait(false);
        return new Viability.ShotWindowPaths(target.Id, session.Observer, start, end, paths[0].Samples, paths[1].Samples);
    }

    public Task<TimelineEvents> CalculateTimelineEventsAsync(PlanningSession session, Instant start, CancellationToken cancellationToken)
    {
        var end = start + IPlanningService.TimelineLength;
        return Task.Run(() =>
        {
            var sun = catalogue.Get("sun");
            var moon = catalogue.Get("moon");
            var events = new List<TimelineEvent>();
            void Add(TimelineEventKind kind, Instant? instant)
            {
                if (instant is { } at && at >= start && at <= end && !events.Any(other => other.Kind == kind && other.Instant == at))
                    events.Add(new TimelineEvent(kind, at));
            }
            // Each local day the window touches, with the plan's own per-day Sun and Moon calculations.
            var firstDay = timeZones.InZone(start, session.TimeZoneId).Date;
            var lastDay = timeZones.InZone(end, session.TimeZoneId).Date;
            var darkAtStart = false;
            for (var day = firstDay; day <= lastDay; day = day.PlusDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var position = astronomy.Calculate(sun, session.Observer, start, day, session.TimeZoneId);
                // Its position is the Sun's at the window's start, whichever day it was calculated with.
                if (day == firstDay) darkAtStart = position.Horizontal.AltitudeDegrees < -18;
                var twilight = position.Twilight;
                Add(TimelineEventKind.Sunrise, twilight?.Sunrise);
                Add(TimelineEventKind.Sunset, twilight?.Sunset);
                Add(TimelineEventKind.AstronomicalDarknessBegins, twilight?.AstronomicalDusk);
                Add(TimelineEventKind.AstronomicalDarknessEnds, twilight?.AstronomicalDawn);
                var moonEvents = astronomy.Calculate(moon, session.Observer, start, day, session.TimeZoneId).Events;
                Add(TimelineEventKind.Moonrise, moonEvents.Rise);
                Add(TimelineEventKind.Moonset, moonEvents.Set);
            }
            return new TimelineEvents(events.OrderBy(item => item.Instant).ToArray(), darkAtStart);
        }, cancellationToken);
    }

    public async Task<PlanningSnapshot> CalculateCoreSnapshotAsync(
        PlanningSession session,
        CancellationToken cancellationToken)
    {
        var localDate = timeZones.InZone(session.Instant, session.TimeZoneId).Date;
        var primaryTarget = catalogue.Get(session.TargetId);
        var targets = Targets(session);
        // The Sun's and Moon's rise/set and twilight searches run off the caller's thread like everything
        // else here: the Planner starts this on its UI thread (PF-04).
        var positionTasks = targets.ToDictionary(target => target.Id,
            target => target.HasEquatorialCoordinates
                ? astronomy.CalculateCatalogueAsync(target, session.Observer, session.Instant, localDate, session.TimeZoneId, cancellationToken)
                : Task.Run(() => astronomy.Calculate(target, session.Observer, session.Instant, localDate, session.TimeZoneId), cancellationToken),
            StringComparer.OrdinalIgnoreCase);
        var pathTasks = targets.ToDictionary(target => target.Id,
            target => astronomy.CalculatePathAsync(target, session.Observer, localDate, session.TimeZoneId,
                session.Instant, Duration.FromMinutes(10), cancellationToken), StringComparer.OrdinalIgnoreCase);
        await Task.WhenAll(positionTasks.Values.Cast<Task>().Concat(pathTasks.Values)).ConfigureAwait(false);
        var positions = positionTasks.ToDictionary(pair => pair.Key, pair => pair.Value.Result, StringComparer.OrdinalIgnoreCase);
        // The anchor is normally one of the shown targets; when none is shown it is calculated on its own, and
        // is not among the plans drawn.
        var position = positions.GetValueOrDefault(primaryTarget.Id) ?? (primaryTarget.HasEquatorialCoordinates
            ? await astronomy.CalculateCatalogueAsync(primaryTarget, session.Observer, session.Instant, localDate, session.TimeZoneId, cancellationToken).ConfigureAwait(false)
            : astronomy.Calculate(primaryTarget, session.Observer, session.Instant, localDate, session.TimeZoneId));
        var path = pathTasks.TryGetValue(primaryTarget.Id, out var primaryPath) ? await primaryPath.ConfigureAwait(false)
            : await astronomy.CalculatePathAsync(primaryTarget, session.Observer, localDate, session.TimeZoneId,
                session.Instant, Duration.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        var sun = positions.GetValueOrDefault("sun") ?? astronomy.Calculate(catalogue.Get("sun"), session.Observer, session.Instant, localDate, session.TimeZoneId);
        var moon = positions.GetValueOrDefault("moon") ?? astronomy.Calculate(catalogue.Get("moon"), session.Observer, session.Instant, localDate, session.TimeZoneId);
        var objectPlans = new List<CelestialObjectPlan>(targets.Length);
        foreach (var target in targets)
            objectPlans.Add(new CelestialObjectPlan(positions[target.Id], await pathTasks[target.Id].ConfigureAwait(false)));
        var pendingHorizon = session.EnableTerrainCalculations
            ? new TerrainHorizonProfile(session.Observer, [], false, Strings.Get("Terrain_Status_HorizonLoading"), session.Instant)
            : TerrainHorizonProfile.Disabled(session.Observer, session.Instant, 0,
                session.EffectiveObserverElevation.ManualGroundElevationOverrideAslMetres);
        return new PlanningSnapshot(session, position, path, lenses.Calculate(session.Lens), pendingHorizon,
            new TerrainCrossings(null, null), new WeatherResult(DataState.Loading, null, Strings.Get("Weather_Loading")),
            new AstronomyContext(sun, moon), objectPlans);
    }

    public Task<PlannerEnvironmentSnapshot> LoadEnvironmentAsync(PlanningSession session,
        CancellationToken cancellationToken) => LoadEnvironmentAsync(session,
        AppSettings.DefaultCameraHeightAboveGroundMetres, cancellationToken);

    public Task<PlannerEnvironmentSnapshot> LoadEnvironmentAsync(PlanningSession session,
        double cameraHeightAboveGroundMetres, CancellationToken cancellationToken) =>
        environment.GetSnapshotAsync(session.Observer,
            CreateTerrainRequest(session, cameraHeightAboveGroundMetres), cancellationToken);

    public Task<TerrainHorizonProfile> PrioritiseTerrainAsync(PlanningSession session,
        IReadOnlyList<double> bearings, CancellationToken cancellationToken) =>
        PrioritiseTerrainAsync(session, AppSettings.DefaultCameraHeightAboveGroundMetres, bearings,
            cancellationToken);

    public Task<TerrainHorizonProfile> PrioritiseTerrainAsync(PlanningSession session,
        double cameraHeightAboveGroundMetres, IReadOnlyList<double> bearings,
        CancellationToken cancellationToken) =>
        environment.GetPriorityHorizonAsync(session.Observer,
            CreateTerrainRequest(session, cameraHeightAboveGroundMetres), bearings, cancellationToken);

    public Task<WeatherResult> LoadWeatherAsync(PlanningSession session, WeatherSettings weatherSettings,
        CancellationToken cancellationToken) =>
        weather.GetWeatherAsync(CreateWeatherRequest(session, weatherSettings, false), cancellationToken);

    public Task<WeatherResult> RefreshWeatherAsync(
        PlanningSession session,
        WeatherSettings weatherSettings,
        CancellationToken cancellationToken) =>
        weather.GetWeatherAsync(CreateWeatherRequest(session, weatherSettings, true), cancellationToken);

    private static WeatherRequest CreateWeatherRequest(PlanningSession session, WeatherSettings settings, bool force) =>
        new(session.Observer, session.Instant, settings.EffectiveFields,
            Math.Clamp(settings.CacheDistanceKilometres, 0, 100), force);

    private static TerrainProfileRequest CreateTerrainRequest(PlanningSession session,
        double cameraHeightAboveGroundMetres) => PlannerEnvironmentService.DefaultHorizonRequest with
    {
        ObserverHeightAboveGroundMetres = AppSettings.NormaliseCameraHeight(cameraHeightAboveGroundMetres),
        ManualGroundElevationOverrideMetres =
            session.EffectiveObserverElevation.ManualGroundElevationOverrideAslMetres,
        EnableTerrainCalculations = session.EnableTerrainCalculations,
        // The datum as supplied, not the one a previous build resolved: the resolved value flows
        // back into the session observer, and using it here would make every identical request miss.
        FallbackObserverGroundElevationMetres =
            session.EffectiveObserverElevation.SuppliedGroundElevationAslMetres ??
            session.Observer.ElevationMetres
    };
}
