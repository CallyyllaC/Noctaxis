using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using NodaTime;
using Noctaxis.Core.Time;
using Noctaxis.Core.Measurements;

namespace Noctaxis.Core.Persistence;

public sealed record PersistedState(
    int Version,
    AppSettings Settings,
    IReadOnlyList<SavedLocation> Locations,
    PlanningSession Session,
    Guid? MostRecentLocationId,
    GeoCoordinate? LastCustomCoordinate = null);

public interface IUserDataStore
{
    string StorageDirectory { get; }
    Task<PersistedState> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(PersistedState state, CancellationToken cancellationToken);
}

public interface IUserDataPathProvider { string GetApplicationDataDirectory(); }

public sealed class PlatformUserDataPathProvider : IUserDataPathProvider
{
    public string GetApplicationDataDirectory()
    {
        var root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(root, "Noctaxis");
    }
}

public sealed class JsonUserDataStore : IUserDataStore
{
    private readonly ILogger<JsonUserDataStore> _logger;
    private readonly Func<Instant> _now;
    private readonly JsonSerializerOptions _options;
    private readonly string _filePath;

    public JsonUserDataStore(IUserDataPathProvider paths, ILogger<JsonUserDataStore> logger, Func<Instant>? now = null)
    {
        _logger = logger;
        _now = now ?? (() => SystemClock.Instance.GetCurrentInstant());
        StorageDirectory = paths.GetApplicationDataDirectory();
        _filePath = Path.Combine(StorageDirectory, "state.json");
        _options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        _options.Converters.Add(new JsonStringEnumConverter());
    }

    public string StorageDirectory { get; }

    public async Task<PersistedState> LoadAsync(CancellationToken cancellationToken)
    {
        var machineZone = TimeZoneInfo.Local.Id;
        var fallback = new PersistedState(1, new AppSettings(), Array.Empty<SavedLocation>(), PlanningSession.Default(_now(), machineZone), null);
        if (!File.Exists(_filePath)) return fallback;
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
            var dto = await JsonSerializer.DeserializeAsync<PersistedStateDto>(stream, _options, cancellationToken).ConfigureAwait(false);
            return dto?.ToDomain() ?? fallback;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogWarning(ex, "Could not read Noctaxis state from {Path}; defaults will be used", _filePath);
            TryQuarantineCorruptFile();
            return fallback;
        }
    }

    public async Task SaveAsync(PersistedState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(StorageDirectory);
        var temporary = _filePath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, PersistedStateDto.FromDomain(state), _options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, _filePath, true);
    }

    private void TryQuarantineCorruptFile()
    {
        try
        {
            if (File.Exists(_filePath)) File.Move(_filePath, _filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record PersistedStateDto(int Version, AppSettings Settings, IReadOnlyList<SavedLocation> Locations, SessionDto Session, Guid? MostRecentLocationId, GeoCoordinate? LastCustomCoordinate = null)
    {
        public PersistedState ToDomain()
        {
            var resolver = new TimeZoneResolver();
            var settings = Settings ?? new();
            if (settings.SelectedTimeZoneId == AppSettings.UseSystemTimeZoneId &&
                settings.LegacyData?.TryGetValue("TimeZoneOverride", out var oldZone) == true &&
                oldZone.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(oldZone.GetString()))
                settings = settings with { SelectedTimeZoneId = oldZone.GetString()! };
            var selected = settings.SelectedTimeZoneId;
            if (!string.Equals(selected, AppSettings.UseSystemTimeZoneId, StringComparison.OrdinalIgnoreCase) &&
                resolver.GetEffectiveId(selected) == resolver.MachineTimeZoneId &&
                !string.Equals(selected, resolver.MachineTimeZoneId, StringComparison.OrdinalIgnoreCase))
                settings = settings with { SelectedTimeZoneId = AppSettings.UseSystemTimeZoneId };
            var session = Session.ToDomain();
            session = session with { TimeZoneId = resolver.GetEffectiveId(session.TimeZoneId) };
            var locations = Locations ?? Array.Empty<SavedLocation>();
            if (Version < 4)
            {
                if (session.ObserverElevation is null)
                    session = session with
                    {
                        ObserverElevation = new ObserverElevationState(
                            ManualGroundElevationOverrideAslMetres: session.Observer.ElevationMetres)
                    };
                locations = locations.Select(location => location.ObserverElevation is null
                    ? location with
                    {
                        ObserverElevation = new ObserverElevationState(
                            ManualGroundElevationOverrideAslMetres: location.Coordinate.ElevationMetres)
                    }
                    : location).ToArray();
            }
            var equipment = settings.EffectiveEquipment(session.Lens);
            var cameras = equipment.Cameras ?? throw new InvalidDataException("Equipment migration did not create a camera.");
            var lenses = equipment.Lenses ?? throw new InvalidDataException("Equipment migration did not create a lens.");
            var cameraId = cameras.Any(item => item.Id.Equals(session.CameraProfileId,
                StringComparison.OrdinalIgnoreCase))
                ? session.CameraProfileId!
                : cameras[0].Id;
            var lensId = lenses.Any(item => item.Id.Equals(session.LensProfileId,
                StringComparison.OrdinalIgnoreCase))
                ? session.LensProfileId!
                : lenses[0].Id;
            var camera = cameras.First(item => item.Id.Equals(cameraId,
                StringComparison.OrdinalIgnoreCase));
            var lens = lenses.First(item => item.Id.Equals(lensId,
                StringComparison.OrdinalIgnoreCase));
            session = session with
            {
                CameraProfileId = cameraId,
                LensProfileId = lensId,
                Lens = session.Lens with
                {
                    Preset = SensorPreset.Custom,
                    SensorWidthMillimetres = camera.SensorWidthMillimetres,
                    SensorHeightMillimetres = camera.SensorHeightMillimetres,
                    FocalLengthMillimetres = lens.ClampFocalLength(session.Lens.FocalLengthMillimetres)
                }
            };
            settings = settings with
            {
                Units = MeasurementUnits.NormaliseId(settings.Units),
                CameraHeightAboveGroundMetres = settings.EffectiveCameraHeightAboveGroundMetres,
                Equipment = equipment,
                LegacyData = null
            };
            var configured = settings.CelestialObjects?.ConfiguredObjects ?? session.VisibleObjects ?? CelestialObjectSettings.Defaults;
            var normalised = CelestialVisibilityPolicy.Normalise(configured);
            var primary = settings.CelestialObjects?.DefaultPrimaryTargetId ?? session.TargetId;
            var visiblePrimary = normalised.FirstOrDefault(item => item.IsVisible && item.TargetId.Equals(primary, StringComparison.OrdinalIgnoreCase));
            if (visiblePrimary is null)
                primary = normalised.FirstOrDefault(item => item.IsVisible)?.TargetId ?? "sun";
            settings = settings with { CelestialObjects = new CelestialObjectSettings(normalised, primary) };
            session = session with { VisibleObjects = normalised, TargetId = primary };
            return new(Version, settings, locations, session, MostRecentLocationId, LastCustomCoordinate);
        }
        public static PersistedStateDto FromDomain(PersistedState state) => new(state.Version, state.Settings, state.Locations, SessionDto.FromDomain(state.Session), state.MostRecentLocationId, state.LastCustomCoordinate);
    }

    private sealed record SessionDto(GeoCoordinate Observer, DateTime InstantUtc, string TimeZoneId,
        string TargetId, LensConfiguration Lens, Guid? SavedLocationId,
        IReadOnlyList<CelestialObjectSelection>? VisibleObjects = null,
        string? CameraProfileId = null, string? LensProfileId = null,
        ObserverElevationState? ObserverElevation = null)
    {
        public PlanningSession ToDomain() => new(Observer,
            Instant.FromDateTimeUtc(DateTime.SpecifyKind(InstantUtc, DateTimeKind.Utc)), TimeZoneId,
            TargetId, Lens, SavedLocationId, VisibleObjects, CameraProfileId, LensProfileId,
            ObserverElevation);
        public static SessionDto FromDomain(PlanningSession session) => new(session.Observer,
            session.Instant.ToDateTimeUtc(), session.TimeZoneId, session.TargetId, session.Lens,
            session.SavedLocationId, session.VisibleObjects, session.CameraProfileId,
            session.LensProfileId, session.ObserverElevation);
    }
}
