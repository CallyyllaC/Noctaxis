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
    GeoCoordinate? LastCustomCoordinate = null)
{
    /// <summary>
    /// Set when the file could not be represented completely. The caller must then not save, since
    /// saving would replace data this build could not read.
    /// </summary>
    public PersistedStateWarning? PreservationWarning { get; init; }

    /// <summary>
    /// Set only when this state was read completely from an existing file: the time that file was last
    /// written. Only such a state may be taken as the full list of the user's locations (for example to
    /// remove assets of deleted ones). Defaults that stand in after a missing, corrupt, unreadable or
    /// partly understood file leave it null.
    /// </summary>
    public DateTime? AuthoritativeAsOfUtc { get; init; }
}

public interface IUserDataStore
{
    string StorageDirectory { get; }
    Task<PersistedState> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(PersistedState state, CancellationToken cancellationToken);
}

public interface IUserDataPathProvider
{
    /// <summary>The durable data folder: settings, saved locations, their map assets and user-installed datasets.</summary>
    string GetApplicationDataDirectory();

    /// <summary>
    /// Where regenerable caches live (terrain, land cover, settlement and basemap tiles). The data folder unless a
    /// provider says otherwise; the application's provider uses the platform cache folder (<see cref="CacheStorage"/>).
    /// </summary>
    string GetCacheDirectory() => GetApplicationDataDirectory();
}

/// <summary>
/// The saved state exists but could not be read, for example because another process holds it. Unlike
/// corrupt state this is not replaced by defaults: the caller must not save over a file it never read.
/// </summary>
public sealed class UserStateUnavailableException(string path, Exception innerException)
    : IOException($"Noctaxis state at '{path}' could not be read.", innerException)
{
    public string StatePath { get; } = path;
}

/// <summary>
/// A save was refused because the state file holds data this build cannot preserve: it was written by
/// a newer version, holds values this version does not know, or could not be read at all.
/// </summary>
public sealed class UserStateReadOnlyException(string path, string reason)
    : IOException($"Noctaxis state at '{path}' was not saved: {reason}")
{
    public string StatePath { get; } = path;
}

/// <summary>
/// The loaded state is not the whole of the file: it was written by a newer schema
/// (<see cref="IsNewerVersion"/>) or names values this version does not know. Defaults stand in where
/// needed, and the file must not be saved over.
/// </summary>
public sealed record PersistedStateWarning(int FileVersion, bool IsNewerVersion, IReadOnlyList<string> UnrecognisedValues);

/// <summary>
/// Where Noctaxis keeps its settings, logs and caches, in each platform's conventional place:
/// - Windows: <c>%APPDATA%\Noctaxis</c>;
/// - Linux: <c>$XDG_CONFIG_HOME/Noctaxis</c>, which is <c>~/.config/Noctaxis</c> unless set otherwise;
/// - macOS: <c>~/Library/Application Support/Noctaxis</c>.
/// </summary>
public sealed class PlatformUserDataPathProvider : IUserDataPathProvider
{
    private static string? _cacheDirectory;

    public string GetApplicationDataDirectory() =>
        Resolve(OperatingSystem.IsMacOS(), System.Environment.GetFolderPath);

    /// <summary>
    /// The cache folder the application prepared at startup (<see cref="UseCacheDirectory"/>); the data folder until
    /// then, so tools and tests that never prepare one keep the earlier single-folder layout.
    /// </summary>
    public string GetCacheDirectory() => Volatile.Read(ref _cacheDirectory) ?? GetApplicationDataDirectory();

    /// <summary>
    /// Called once by the application, after it owns the data folder and has moved earlier caches
    /// (<see cref="CacheStorage.Prepare"/>), with the folder its caches must use.
    /// </summary>
    public static void UseCacheDirectory(string directory) => Volatile.Write(ref _cacheDirectory, directory);

    /// <summary>
    /// The directory for a platform, from that platform's special folders. On macOS .NET's ApplicationData is
    /// <c>~/.config</c>, a Linux convention; its LocalApplicationData is the native Application Support folder.
    /// </summary>
    public static string Resolve(bool isMacOS, Func<System.Environment.SpecialFolder, string> folder)
    {
        var root = folder(isMacOS ? System.Environment.SpecialFolder.LocalApplicationData : System.Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = isMacOS
                ? Path.Combine(folder(System.Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Path.Combine(folder(System.Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(root, "Noctaxis");
    }
}

public sealed class JsonUserDataStore : IUserDataStore
{
    private readonly ILogger<JsonUserDataStore> _logger;
    private readonly Func<Instant> _now;
    private readonly JsonSerializerOptions _options;
    private readonly string _filePath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly IReadOnlyList<TimeSpan> _loadRetryDelays;
    private readonly IDataDirectoryOwnership? _ownership;

    /// <summary>Waits between attempts to read a state file that exists but cannot be opened.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultLoadRetryDelays =
        [TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1000)];

    /// <param name="ownership">
    /// When given (the application always gives it), every save first confirms that this process still owns the data
    /// folder, so no second writer of the same folder can replace the owner's state.
    /// </param>
    public JsonUserDataStore(IUserDataPathProvider paths, ILogger<JsonUserDataStore> logger, Func<Instant>? now = null,
        IReadOnlyList<TimeSpan>? loadRetryDelays = null, IDataDirectoryOwnership? ownership = null)
    {
        _ownership = ownership;
        _logger = logger;
        _now = now ?? (() => SystemClock.Instance.GetCurrentInstant());
        _loadRetryDelays = loadRetryDelays ?? DefaultLoadRetryDelays;
        StorageDirectory = paths.GetApplicationDataDirectory();
        _filePath = StateFilePath(paths);
        _options = CreateOptions(null);
    }

    /// <summary>
    /// The state schema this build writes and fully understands. Raise it whenever a release adds a
    /// persisted member or enum value, so earlier builds open that release's file read-only instead of
    /// saving over what they cannot represent.
    /// </summary>
    public const int CurrentSchemaVersion = 4;

    public string StorageDirectory { get; }

    /// <summary>Where the state is stored, for the one value read before the store exists: the language.</summary>
    public static string StateFilePath(IUserDataPathProvider paths) => Path.Combine(paths.GetApplicationDataDirectory(), "state.json");

    /// <summary>
    /// Why the file loaded last cannot be saved over, or null when saving is safe. Set by a load that
    /// could not read the file, or read it only in part (newer schema, values this build does not know).
    /// </summary>
    public string? SaveRefusal { get; private set; }

    private static JsonSerializerOptions CreateOptions(PersistedStateReadReport? report)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
        options.Converters.Add(new ExternalMapProviderJsonConverter(report));
        // Before the general enum converter, so a Planner layout naming a side or mode this version
        // does not know loses that panel's position rather than the whole file. This is the accepted,
        // documented cost for docking positions only, so it is not reported as unrecognised.
        options.Converters.Add(new TolerantEnumJsonConverter<PanelDockSide>(PanelDockSide.Right));
        options.Converters.Add(new TolerantEnumJsonConverter<PanelDockMode>(PanelDockMode.Docked));
        options.Converters.Add(new PersistedEnumConverterFactory(report));
        return options;
    }

    public async Task<PersistedState> LoadAsync(CancellationToken cancellationToken)
    {
        var machineZone = TimeZoneInfo.Local.Id;
        var fallback = new PersistedState(1, new AppSettings(), Array.Empty<SavedLocation>(), PlanningSession.Default(_now(), machineZone), null);
        SaveRefusal = null;
        if (!File.Exists(_filePath)) return fallback;
        byte[]? content;
        try { content = await ReadStateFileAsync(cancellationToken).ConfigureAwait(false); }
        catch (UserStateUnavailableException)
        {
            SaveRefusal = "The saved state could not be read.";
            throw;
        }
        if (content is null) return fallback;
        var read = Read(content);
        switch (read.Outcome)
        {
            case ReadOutcome.Complete:
                return read.State is { } complete
                    ? complete with { AuthoritativeAsOfUtc = FileWriteTimeUtc() }
                    : fallback;
            case ReadOutcome.Partial:
                SaveRefusal = "The saved state was read only in part.";
                _logger.LogWarning("Noctaxis state at {Path} (schema {Version}) was read only in part; it will not be saved over. Unrecognised: {Values}",
                    _filePath, read.Warning!.FileVersion, string.Join(", ", read.Warning.UnrecognisedValues));
                return (read.State ?? fallback) with { PreservationWarning = read.Warning };
            case ReadOutcome.Unreadable:
                // A newer build's file that this build cannot read is still that build's valid state:
                // it is neither quarantined nor replaced.
                SaveRefusal = "The saved state was written by a newer version.";
                _logger.LogWarning(read.Failure, "Noctaxis state at {Path} has schema {Version}, newer than {Current}, and could not be read; it will not be changed",
                    _filePath, read.Warning!.FileVersion, CurrentSchemaVersion);
                return fallback with { PreservationWarning = read.Warning };
            default:
                // Malformed or incomplete state of a version this build owns is quarantined and
                // defaults are used. Anything else is a programming failure and must reach the caller.
                _logger.LogWarning(read.Failure, "Could not parse Noctaxis state from {Path}; defaults will be used", _filePath);
                TryQuarantineCorruptFile();
                return fallback;
        }
    }

    private DateTime? FileWriteTimeUtc()
    {
        try { return File.GetLastWriteTimeUtc(_filePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private enum ReadOutcome { Complete, Partial, Unreadable, Corrupt }

    private sealed record ReadResult(ReadOutcome Outcome, PersistedState? State,
        PersistedStateWarning? Warning = null, Exception? Failure = null);

    /// <summary>
    /// Classifies one state document. Partial: it was read, but saving would lose part of it (a newer
    /// schema, or values this build does not know). Unreadable: a newer schema that could not be read
    /// at all. Corrupt: this build's own schema (or older), malformed.
    /// </summary>
    private ReadResult Read(byte[] content)
    {
        int version;
        try { version = SchemaVersion(content); }
        catch (JsonException ex) { return new(ReadOutcome.Corrupt, null, Failure: ex); }
        var report = new PersistedStateReadReport();
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            var state = JsonSerializer.Deserialize<PersistedStateDto>(stream, CreateOptions(report))?.ToDomain();
            if (version <= CurrentSchemaVersion && report.UnrecognisedValues.Count == 0)
                return new(ReadOutcome.Complete, state);
            return new(ReadOutcome.Partial, state,
                new PersistedStateWarning(version, version > CurrentSchemaVersion, report.UnrecognisedValues));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
        {
            return version > CurrentSchemaVersion
                ? new(ReadOutcome.Unreadable, null, new PersistedStateWarning(version, true, report.UnrecognisedValues), ex)
                : new(ReadOutcome.Corrupt, null, Failure: ex);
        }
    }

    /// <summary>The file's declared schema version (0 when absent); malformed JSON is a <see cref="JsonException"/>.</summary>
    private static int SchemaVersion(byte[] content)
    {
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Persisted state is not a JSON object.");
        foreach (var property in document.RootElement.EnumerateObject())
            if (string.Equals(property.Name, nameof(PersistedState.Version), StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var version)
                    ? version
                    : throw new JsonException("Persisted state has an invalid version.");
        return 0;
    }

    /// <summary>
    /// The state file's bytes, or null when it disappeared before it could be opened. A file that exists
    /// but cannot be read (held by another process, antivirus, sync software, permissions) is not corrupt:
    /// the read is retried briefly and then reported as <see cref="UserStateUnavailableException"/>, so
    /// the caller neither replaces the user's state with defaults nor quarantines it.
    /// </summary>
    private async Task<byte[]?> ReadStateFileAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= _loadRetryDelays.Count)
                {
                    _logger.LogError(ex, "Noctaxis state at {Path} could not be read after {Attempts} attempts",
                        _filePath, attempt + 1);
                    throw new UserStateUnavailableException(_filePath, ex);
                }
                _logger.LogWarning(ex, "Noctaxis state at {Path} could not be read; retrying", _filePath);
                await Task.Delay(_loadRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task SaveAsync(PersistedState state, CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Defence in depth: the application never enables saving after such a load, but the file
            // must survive any path that tries anyway.
            if (SaveRefusal is { } refusal) throw new UserStateReadOnlyException(_filePath, refusal);
            if (_ownership is not null && !_ownership.Owns(StorageDirectory))
                throw new UserStateReadOnlyException(_filePath, "This Noctaxis does not own the data folder.");
            Directory.CreateDirectory(StorageDirectory);
            var temporary = _filePath + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, PersistedStateDto.FromDomain(state), _options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            BackUpCurrentState();
            File.Move(temporary, _filePath, true);
        }
        finally { _saveGate.Release(); }
    }

    /// <summary>
    /// Keeps the state being replaced as <c>state.json.bak</c>, one generation deep, and only when that
    /// state reads completely: a corrupt or partly understood file never replaces the last good backup.
    /// A file that has meanwhile become a newer build's state (another instance) is not replaced at all.
    /// Otherwise best effort: a backup that cannot be written must not block the save it protects.
    /// </summary>
    private void BackUpCurrentState()
    {
        byte[] current;
        try { current = File.ReadAllBytes(_filePath); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read Noctaxis state at {Path} to back it up", _filePath);
            return;
        }
        var read = Read(current);
        if (read.Outcome is ReadOutcome.Partial or ReadOutcome.Unreadable)
        {
            SaveRefusal = "The saved state was replaced by a newer version.";
            throw new UserStateReadOnlyException(_filePath, SaveRefusal);
        }
        if (read.Outcome != ReadOutcome.Complete)
        {
            _logger.LogWarning("Noctaxis state at {Path} is not valid; the existing backup is kept", _filePath);
            return;
        }
        var temporary = BackupFilePath + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, current);
            File.Move(temporary, BackupFilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not back up Noctaxis state to {Path}", BackupFilePath);
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }

    private string BackupFilePath => _filePath + ".bak";

    private void TryQuarantineCorruptFile()
    {
        try
        {
            if (File.Exists(_filePath)) File.Move(_filePath, _filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record PersistedStateDto(int Version, AppSettings Settings, IReadOnlyList<SavedLocation> Locations, SessionDto Session, Guid? MostRecentLocationId, GeoCoordinate? LastCustomCoordinate = null)
    {
        public PersistedState ToDomain()
        {
            // Required members may be absent from syntactically valid JSON (hand edits, older or
            // partial files). Treat that as corrupt state rather than dereferencing null below.
            if (Session is null) throw new InvalidDataException("Persisted state has no planning session.");
            if (Session.Lens is null)
                throw new InvalidDataException("Persisted planning session has no lens configuration.");
            ValidateStructure();
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
            // The saved focus, or for files from before it was saved with the objects, the session's target.
            // A focus that is missing or hidden is no focus, never a stand-in.
            var requested = settings.CelestialObjects is { } objects ? objects.DefaultPrimaryTargetId : session.TargetId;
            var (focus, anchor) = CelestialVisibilityPolicy.FocusAndAnchor(normalised, requested);
            settings = settings with { CelestialObjects = new CelestialObjectSettings(normalised, focus) };
            session = session with { VisibleObjects = normalised, TargetId = anchor, IsTargetFocused = focus is not null };
            return new(Version, settings, locations, session, MostRecentLocationId, LastCustomCoordinate);
        }
        /// <summary>
        /// Rejects a null where a record, or a record's required text, must be: an empty saved location, equipment
        /// profile or celestial selection, or a location without a name or time zone. Such a file is valid JSON but
        /// not a state any build wrote, so it is classified here, before migration or use dereferences it, and goes
        /// through the same policy as malformed JSON (quarantined unchanged; a newer schema's file is left alone).
        /// Values the loader already repairs, such as an unknown session zone, are not rejected.
        /// </summary>
        private void ValidateStructure()
        {
            if (Locations?.Any(location => location is null || location.Name is null || location.TimeZoneId is null) == true)
                throw new InvalidDataException("Persisted state has an incomplete saved location.");
            if (Settings?.Equipment is { } equipment &&
                (equipment.Cameras?.Any(profile => profile is null) == true ||
                 equipment.Lenses?.Any(profile => profile is null) == true))
                throw new InvalidDataException("Persisted equipment has an empty profile.");
            if (Settings?.CelestialObjects?.ConfiguredObjects?.Any(item => item?.TargetId is null) == true ||
                Session.VisibleObjects?.Any(item => item?.TargetId is null) == true)
                throw new InvalidDataException("Persisted celestial objects have an empty selection.");
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
