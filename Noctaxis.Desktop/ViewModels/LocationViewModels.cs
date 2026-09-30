using Noctaxis.Core.Localisation;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Locations;
using Noctaxis.Desktop.Services;
using Avalonia.Media.Imaging;

namespace Noctaxis.Desktop.ViewModels;

public sealed record SavedLocationEdit(string Name, string? Description);

public interface IPlannerDialogService
{
    Task<bool> ConfirmClearTerrainCacheAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    /// <param name="initialQuery">What the shared search field already held, so the user does not retype it.</param>
    Task<LocationSearchResult?> ShowLocationSearchAsync(string? initialQuery = null, CancellationToken cancellationToken = default);
    Task<SavedLocationEdit?> ShowSavedLocationEditAsync(SavedLocation location, bool isCreateMode = false,
        CancellationToken cancellationToken = default);
    Task<bool> ConfirmDeleteSavedLocationAsync(SavedLocation location, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
    Task<bool> ConfirmRefreshSavedLocationThumbnailsAsync(int locationCount, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
    Task<bool> ConfirmRefreshSavedLocationSettlementCachesAsync(int locationCount,
        CancellationToken cancellationToken = default) => Task.FromResult(true);
    /// <summary>
    /// Asks before permanently removing something the user created or that is costly to recreate. Without a
    /// dialog the answer is no, so nothing is ever removed unconfirmed.
    /// </summary>
    Task<bool> ConfirmDestructiveAsync(DestructiveConfirmation request, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

/// <summary>A destructive confirmation: a question, what will be lost, and the verb on the confirming button.</summary>
public sealed record DestructiveConfirmation(string Title, string Message, string ConfirmLabel);

public partial class SavedLocationEditorViewModel : ObservableObject
{
    private readonly SavedLocation _original;
    public SavedLocationEditorViewModel(SavedLocation location)
    {
        _original = location;
        _name = location.Name;
        _description = location.Notes ?? string.Empty;
    }
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string? _validationMessage;
    public string Coordinates => $"{_original.Coordinate.Latitude:F5}°, {_original.Coordinate.Longitude:F5}°";
    public SavedLocationEdit? ValidateAndCreateResult()
    {
        var name = Name.Trim();
        if (name.Length == 0) { ValidationMessage = Strings.Get("Locations_Edit_NameRequired"); return null; }
        ValidationMessage = null;
        return new SavedLocationEdit(name, string.IsNullOrWhiteSpace(Description) ? null : Description.Trim());
    }
}

public partial class LocationSearchViewModel(ILocationSearchProvider provider, ILogger<LocationSearchViewModel> logger) : ObservableObject
{
    private CancellationTokenSource? _searchCancellation;
    private Task _activeSearch = Task.CompletedTask;
    internal Task WaitForSearchAsync() => _activeSearch;
    public ObservableCollection<LocationSearchResult> Results { get; } = [];
    public string Attribution => provider.Attribution;
    [ObservableProperty] private string _query = string.Empty;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _errorMessage;

    partial void OnQueryChanged(string value)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        _activeSearch = SearchDebouncedAsync(value, _searchCancellation.Token);
    }

    public void Reset()
    {
        _searchCancellation?.Cancel();
        Query = string.Empty;
        Results.Clear();
        ErrorMessage = null;
    }

    private async Task SearchDebouncedAsync(string query, CancellationToken cancellationToken)
    {
        Results.Clear(); ErrorMessage = null; IsSearching = false;
        if (query.Trim().Length < 2) return;
        try
        {
            await Task.Delay(350, cancellationToken);
            IsSearching = true;
            var results = await provider.SearchAsync(query, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var result in results) Results.Add(result);
            if (results.Count == 0) ErrorMessage = Strings.Get("Locations_Search_NoMatches");
        }
        catch (OperationCanceledException) { logger.LogDebug("Obsolete location search cancelled"); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        { ErrorMessage = ex.Message; logger.LogWarning(ex, "Location search failed"); }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
        finally { if (!cancellationToken.IsCancellationRequested) IsSearching = false; }
    }
}

public interface ILocationGridItem;

/// <summary>
/// The tile that saves the Planner's current pin. It is the only place on the page that stands for
/// that pin, so it also shows where the pin is: the action first, the coordinates as quiet metadata.
/// </summary>
public sealed partial class AddLocationCardViewModel(Func<Task> addLocation) : ObservableObject, ILocationGridItem
{
    public IAsyncRelayCommand AddCommand { get; } = new AsyncRelayCommand(addLocation);

    /// <summary>The active pin, written the way a saved card writes its own coordinates.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoordinates)), NotifyPropertyChangedFor(nameof(AccessibleName))]
    private string? _coordinates;

    public bool HasCoordinates => !string.IsNullOrWhiteSpace(Coordinates);

    /// <summary>Everything the tile shows, in one name, so the coordinates are not hover-only.</summary>
    public string AccessibleName => HasCoordinates
        ? Strings.Format("Locations_AddLocationAt_AccessibleName_Format", Coordinates)
        : Strings.Get("Locations_AddLocation_AccessibleName");
}

public partial class LocationCardViewModel : ObservableObject, ILocationGridItem, IDisposable
{
    private readonly Func<LocationCardViewModel, Task> _open;
    private readonly Func<LocationCardViewModel, Task> _edit;
    private readonly Func<LocationCardViewModel, Task> _delete;
    private readonly Func<LocationCardViewModel, Task> _toggleFavourite;
    private readonly ILocationMapThumbnailService _mapThumbnails;
    private readonly Func<DateTimeOffset> _now;
    private CancellationTokenSource? _thumbnailCancellation;
    private bool _disposed;

    public LocationCardViewModel(SavedLocation location, bool isSelected,
        ILocationMapThumbnailService mapThumbnails, Func<DateTimeOffset> now,
        Func<LocationCardViewModel, Task> open,
        Func<LocationCardViewModel, Task> edit, Func<LocationCardViewModel, Task> delete,
        Func<LocationCardViewModel, Task> toggleFavourite)
    {
        Location = location; _isFavourite = location.IsFavourite; _isSelected = isSelected;
        _mapThumbnails = mapThumbnails; _now = now;
        _open = open; _edit = edit; _delete = delete; _toggleFavourite = toggleFavourite;
        _ = LoadMapThumbnailAsync();
    }

    public SavedLocation Location { get; private set; }
    public Guid Id => Location.Id;
    public string Name => Location.Name;
    public string Coordinates => FormatCoordinates(Location.Coordinate);

    /// <summary>
    /// How a coordinate is written on this page, in one place: the saved cards and the tile that saves
    /// the current pin read the same, and the numbers follow the user's regional format.
    /// </summary>
    public static string FormatCoordinates(GeoCoordinate coordinate) =>
        Strings.Format("Locations_Card_Coordinates_Format", coordinate.Latitude, coordinate.Longitude);
    public string? RegionDescription => Location.RegionDescription;
    public bool HasRegionDescription => !string.IsNullOrWhiteSpace(Location.RegionDescription);
    public string? UserDescription => Location.Notes;
    public bool HasUserDescription => !string.IsNullOrWhiteSpace(Location.Notes);
    /// <summary>State badges are shown as text and icons, so current and favourite never rest on colour.</summary>
    public bool HasStateBadges => IsFavourite || IsSelected;
    public string Eyebrow => IsFavourite ? Strings.Get("Locations_Card_Eyebrow_Favourite") : Strings.Get("Locations_Card_Eyebrow_Saved");
    /// <summary>The card's open action, named with its state for assistive technology.</summary>
    public string OpenAccessibleName => Strings.Format((IsFavourite, IsSelected) switch
    {
        (true, true) => "Locations_Card_OpenFavouriteCurrent_AccessibleName",
        (true, false) => "Locations_Card_OpenFavourite_AccessibleName",
        (false, true) => "Locations_Card_OpenCurrent_AccessibleName",
        _ => "Locations_Card_Open_AccessibleName"
    }, Name);
    public bool IsNotFavourite => !IsFavourite;
    public string FavouriteAccessibleName => IsFavourite ? Strings.Get("Locations_Card_RemoveFavourite_AccessibleName") : Strings.Get("Locations_Card_AddFavourite_AccessibleName");
    public string? LastUsedText => FormatLastUsed(Location.LastUsedUtc, _now());
    public bool HasLastUsed => LastUsedText is not null;
    [ObservableProperty] private bool _isFavourite;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private Bitmap? _mapThumbnail;
    /// <summary>Theme-independent preview content; the card renders it with the current theme.</summary>
    [ObservableProperty] private Mapping.Preview.LocationPreviewScene? _previewScene;
    [ObservableProperty] private bool _isMapThumbnailLoading;
    [ObservableProperty] private SavedLocationThumbnailMetadata? _thumbnailMetadata;
    [ObservableProperty] private SavedLocationThumbnailResult? _lastThumbnailResult;
    public string FavouriteLabel => IsFavourite ? Strings.Get("Locations_Card_Unfavourite_Label") : Strings.Get("Locations_Card_Favourite_Label");
    public bool HasSemanticWarning => ThumbnailMetadata?.FeatureFetchStatus is
        nameof(MapFeatureFetchStatus.Unavailable) or nameof(MapFeatureFetchStatus.CachedPrevious);
    public string SemanticStatusSummary
    {
        get
        {
            if (ThumbnailMetadata is null) return Strings.Get("Locations_Card_Semantic_StatusUnavailable");
            var reason = ThumbnailMetadata.FeatureFailureReason;
            return ThumbnailMetadata.FeatureFetchStatus switch
            {
                nameof(MapFeatureFetchStatus.CachedPrevious) =>
                    string.IsNullOrWhiteSpace(reason) ? Strings.Get("Locations_Card_Semantic_Retained") : Strings.Format("Locations_Card_Semantic_Retained_Format", reason),
                nameof(MapFeatureFetchStatus.Unavailable) =>
                    string.IsNullOrWhiteSpace(reason) ? Strings.Get("Locations_Card_Semantic_Unavailable") : Strings.Format("Locations_Card_Semantic_Unavailable_Format", reason),
                _ => Strings.Get("Locations_Card_Semantic_Available")
            };
        }
    }
    public bool HasSettlementWarning => ThumbnailMetadata?.SettlementStatus is
        nameof(EnvironmentalDataState.Partial) or nameof(EnvironmentalDataState.Unavailable) or
        nameof(EnvironmentalDataState.InvalidData) or nameof(EnvironmentalDataState.TileAbsent) or
        nameof(EnvironmentalDataState.SourceUnavailable) or nameof(EnvironmentalDataState.InvalidRaster);
    public string SettlementStatusSummary
    {
        get
        {
            if (ThumbnailMetadata is null) return Strings.Get("Locations_Card_Settlement_StatusUnavailable");
            return ThumbnailMetadata.SettlementStatus switch
            {
                nameof(EnvironmentalDataState.Available) or nameof(EnvironmentalDataState.Cached) =>
                    Strings.Format("Locations_Card_Settlement_Available_Format", ThumbnailMetadata.SettlementCellCount ?? 0),
                nameof(EnvironmentalDataState.Empty) =>
                    Strings.Get("Locations_Card_Settlement_Empty"),
                nameof(EnvironmentalDataState.Partial) => Strings.Get("Locations_Card_Settlement_Partial"),
                nameof(EnvironmentalDataState.TileAbsent) =>
                    Strings.Get("Locations_Card_Settlement_TileAbsent"),
                nameof(EnvironmentalDataState.InvalidRaster) =>
                    Strings.Get("Locations_Card_Settlement_InvalidRaster"),
                nameof(EnvironmentalDataState.SourceUnavailable) =>
                    Strings.Get("Locations_Card_Settlement_SourceUnavailable"),
                _ => string.IsNullOrWhiteSpace(ThumbnailMetadata.SettlementStatusMessage)
                    ? Strings.Get("Locations_Card_Settlement_Unavailable")
                    : ThumbnailMetadata.SettlementStatusMessage
            };
        }
    }

    partial void OnThumbnailMetadataChanged(SavedLocationThumbnailMetadata? value)
    {
        OnPropertyChanged(nameof(HasSemanticWarning));
        OnPropertyChanged(nameof(SemanticStatusSummary));
        OnPropertyChanged(nameof(HasSettlementWarning));
        OnPropertyChanged(nameof(SettlementStatusSummary));
    }

    public void Update(SavedLocation location)
    {
        var coordinateChanged = location.Coordinate != Location.Coordinate;
        Location = location; IsFavourite = location.IsFavourite;
        OnPropertyChanged(nameof(Name)); OnPropertyChanged(nameof(Coordinates));
        OnPropertyChanged(nameof(RegionDescription)); OnPropertyChanged(nameof(HasRegionDescription));
        OnPropertyChanged(nameof(UserDescription)); OnPropertyChanged(nameof(HasUserDescription));
        OnPropertyChanged(nameof(FavouriteLabel));
        OnPropertyChanged(nameof(OpenAccessibleName)); OnPropertyChanged(nameof(IsNotFavourite));
        OnPropertyChanged(nameof(FavouriteAccessibleName)); OnPropertyChanged(nameof(LastUsedText));
        OnPropertyChanged(nameof(HasLastUsed));
        if (coordinateChanged) _ = LoadMapThumbnailAsync();
    }

    [RelayCommand] private Task Open() => _open(this);
    [RelayCommand] private Task Edit() => _edit(this);
    [RelayCommand] private Task Delete() => _delete(this);
    [RelayCommand] private Task ToggleFavourite() => _toggleFavourite(this);
    partial void OnIsFavouriteChanged(bool value)
    {
        OnPropertyChanged(nameof(FavouriteLabel)); OnPropertyChanged(nameof(HasStateBadges)); OnPropertyChanged(nameof(Eyebrow));
        OnPropertyChanged(nameof(IsNotFavourite)); OnPropertyChanged(nameof(FavouriteAccessibleName));
        OnPropertyChanged(nameof(OpenAccessibleName));
    }
    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(HasStateBadges)); OnPropertyChanged(nameof(OpenAccessibleName));
    }

    [RelayCommand]
    private Task RefreshMapThumbnail() => LoadMapThumbnailAsync(SavedLocationMapRefreshMode.RefreshSource);

    [RelayCommand]
    private Task RefreshSettlementCache() => LoadMapThumbnailAsync(SavedLocationMapRefreshMode.RefreshSettlement);

    public Task<bool> RefreshMapThumbnailAsync(bool forceRefresh) => LoadMapThumbnailAsync(
        forceRefresh ? SavedLocationMapRefreshMode.RefreshSource : SavedLocationMapRefreshMode.UseCache);
    public Task<bool> RefreshMapThumbnailAsync(SavedLocationMapRefreshMode mode) => LoadMapThumbnailAsync(mode);
    public async Task<SavedLocationThumbnailResult?> RefreshMapThumbnailWithResultAsync(
        SavedLocationMapRefreshMode mode)
    {
        await LoadMapThumbnailAsync(mode);
        return LastThumbnailResult;
    }

    private Task<bool> LoadMapThumbnailAsync() => LoadMapThumbnailAsync(SavedLocationMapRefreshMode.UseCache);

    private async Task<bool> LoadMapThumbnailAsync(SavedLocationMapRefreshMode mode)
    {
        if (_disposed) return false;
        LastThumbnailResult = null;
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation?.Dispose();
        _thumbnailCancellation = new CancellationTokenSource();
        var cancellationToken = _thumbnailCancellation.Token;
        var requestedLocation = Location;
        IsMapThumbnailLoading = true;
        try
        {
            var result = await _mapThumbnails.GetThumbnailAsync(requestedLocation, mode, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result is null) return false;
            if (Location.Id != result.Metadata.LocationId ||
                BitConverter.DoubleToInt64Bits(Location.Coordinate.Latitude) != BitConverter.DoubleToInt64Bits(result.Metadata.Latitude) ||
                BitConverter.DoubleToInt64Bits(Location.Coordinate.Longitude) != BitConverter.DoubleToInt64Bits(result.Metadata.Longitude))
                return false;
            LastThumbnailResult = result;
            ThumbnailMetadata = result.Metadata;
            // The same cached assets feed the themed preview; building it never touches the network.
            if (Path.GetDirectoryName(result.ImagePath) is { Length: > 0 } directory)
            {
                var scene = await Mapping.Preview.LocationPreviewSceneLoader.LoadAsync(directory, result.Metadata,
                    requestedLocation.Coordinate, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                PreviewScene = scene;
            }
            if (result.Metadata.LegacyArtworkSkipped)
            {
                // No legacy artwork for this frame; an earlier bitmap no longer matches the saved data.
                var stale = MapThumbnail;
                MapThumbnail = null;
                stale?.Dispose();
            }
            else try
            {
                var bitmap = new Bitmap(result.ImagePath);
                var previous = MapThumbnail;
                MapThumbnail = bitmap;
                previous?.Dispose();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The generated disk asset remains valid even when this UI process cannot decode it yet.
                // Preserve the currently displayed bitmap; a later page load can retry the disk asset.
            }
            return result.RefreshSucceeded;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return false; }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsMapThumbnailLoading = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation?.Dispose();
        _thumbnailCancellation = null;
        var bitmap = MapThumbnail;
        MapThumbnail = null;
        bitmap?.Dispose();
        IsMapThumbnailLoading = false;
    }

    internal static string? FormatLastUsed(DateTimeOffset? value, DateTimeOffset now)
    {
        if (!value.HasValue) return null;
        var elapsed = now - value.Value;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed < TimeSpan.FromMinutes(2)) return Strings.Get("Locations_Card_LastUsed_JustNow");
        if (elapsed < TimeSpan.FromHours(1)) return Strings.Format("Locations_Card_LastUsed_Minutes_Format", (int)elapsed.TotalMinutes);
        if (elapsed < TimeSpan.FromHours(24)) return Strings.Format("Locations_Card_LastUsed_Hours_Format", (int)elapsed.TotalHours);
        if (elapsed < TimeSpan.FromDays(14)) return Strings.Format("Locations_Card_LastUsed_Days_Format", (int)elapsed.TotalDays);
        return Strings.Format("Locations_Card_LastUsed_Date_Format", value.Value.ToLocalTime());
    }
}

public enum LocationSortMode { RecentlyUsed, Name, DateAdded }
public sealed record LocationSortOption(LocationSortMode Mode, string Label);

public partial class LocationsViewModel : ObservableObject
{
    private readonly ILocationResolver _resolver;
    private readonly IDeviceLocationAvailabilityService _deviceAvailability;
    private readonly Func<LocationResolution, Task> _openCustom;
    private readonly Func<LocationSearchResult, bool, Task> _useSearchResult;
    private readonly Func<string?, Task<LocationSearchResult?>> _showSearch;
    private readonly Func<LocationCardViewModel, Task> _openSaved;
    private readonly Func<LocationCardViewModel, Task> _edit;
    private readonly Func<LocationCardViewModel, Task> _delete;
    private readonly Func<LocationCardViewModel, Task> _toggleFavourite;
    private readonly ILocationMapThumbnailService _mapThumbnails;
    private readonly Func<DateTimeOffset> _now;
    private GeoCoordinate? _lastCustomCoordinate;
    private readonly AddLocationCardViewModel _addLocationCard;

    public LocationsViewModel(ILocationResolver resolver, IDeviceLocationAvailabilityService deviceAvailability,
        Func<LocationResolution, Task> openCustom, Func<LocationSearchResult, bool, Task> useSearchResult,
        Func<string?, Task<LocationSearchResult?>> showSearch,
        Func<LocationCardViewModel, Task> openSaved, Func<LocationCardViewModel, Task> edit,
        Func<LocationCardViewModel, Task> delete, Func<LocationCardViewModel, Task> toggleFavourite,
        Func<Task> addLocation, ILocationMapThumbnailService mapThumbnails, Func<DateTimeOffset> now)
    {
        _resolver = resolver; _deviceAvailability = deviceAvailability; _openCustom = openCustom;
        _useSearchResult = useSearchResult; _showSearch = showSearch;
        _openSaved = openSaved; _edit = edit; _delete = delete; _toggleFavourite = toggleFavourite;
        _mapThumbnails = mapThumbnails; _now = now;
        _addLocationCard = new AddLocationCardViewModel(addLocation);
        _selectedSortOption = SortOptions[0];
        GridItems.Add(_addLocationCard);
    }

    public ObservableCollection<LocationCardViewModel> Saved { get; } = [];
    public ObservableCollection<ILocationGridItem> GridItems { get; } = [];
    public IReadOnlyList<LocationSortOption> SortOptions { get; } =
    [
        new(LocationSortMode.RecentlyUsed, Strings.Get("Locations_Sort_RecentlyUsed")),
        new(LocationSortMode.Name, Strings.Get("Locations_Sort_Name")),
        new(LocationSortMode.DateAdded, Strings.Get("Locations_Sort_DateAdded"))
    ];
    public bool HasSavedLocations => Saved.Count > 0;
    public bool IsFirstRun => Saved.Count == 0;
    public string SavedSummary => Saved.Count == 1 ? Strings.Get("Locations_Summary_One") : Strings.Format("Locations_Summary_Many_Format", Saved.Count);

    // ---- Filtering -------------------------------------------------------------------------------
    // A filter is a view of the saved locations, never a change to them: Saved keeps every location in
    // its sorted order and only the grid is rebuilt, so clearing the filter restores the list exactly.

    private string _searchText = string.Empty;

    /// <summary>The text the shared search field holds while the Locations page is open.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            var text = value ?? string.Empty;
            if (string.Equals(_searchText, text, StringComparison.Ordinal)) return;
            _searchText = text;
            OnPropertyChanged();
            RebuildGridItems();
            OnPropertyChanged(nameof(IsFiltered));
            OnPropertyChanged(nameof(HasNoMatches));
            OnPropertyChanged(nameof(NoMatchesMessage));
        }
    }

    public bool IsFiltered => _searchText.Trim().Length > 0;

    /// <summary>The filter matched nothing. The page says so rather than showing an empty grid.</summary>
    public bool HasNoMatches => IsFiltered && HasSavedLocations && !GridItems.OfType<LocationCardViewModel>().Any();

    public string NoMatchesMessage => Strings.Format("Locations_Search_NoMatches_Format", _searchText.Trim());

    /// <summary>
    /// Whether a card answers the current filter. It looks at what identifies a location to its owner:
    /// the name, their own description, and the region the place resolved to. Comparison is
    /// case-insensitive and ignores accents, in the culture the interface is running in, so searching
    /// "kielder" finds "Kielder" and "durrenstein" finds "Dürrenstein".
    /// </summary>
    private bool Matches(LocationCardViewModel card)
    {
        var term = _searchText.Trim();
        if (term.Length == 0) return true;
        var compare = System.Globalization.CultureInfo.CurrentCulture.CompareInfo;
        bool Contains(string? value) => !string.IsNullOrEmpty(value) && compare.IndexOf(value, term,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;
        return Contains(card.Name) || Contains(card.UserDescription) || Contains(card.RegionDescription);
    }
    [ObservableProperty] private LocationSortOption _selectedSortOption;
    [ObservableProperty] private string? _resolutionMessage;
    [ObservableProperty] private bool _canUseDeviceLocation;
    [ObservableProperty] private string? _deviceLocationUnavailableReason;
    [ObservableProperty] private string? _thumbnailAttributionSummary;
    public bool HasThumbnailAttributions => !string.IsNullOrWhiteSpace(ThumbnailAttributionSummary);

    public void Load(IEnumerable<SavedLocation> locations, GeoCoordinate? lastCustomCoordinate, Guid? selectedLocationId = null)
    {
        _lastCustomCoordinate = lastCustomCoordinate;
        var previous = Saved.ToDictionary(card => card.Id);
        var incoming = locations.ToArray();
        Saved.Clear();
        foreach (var location in incoming)
        {
            if (previous.Remove(location.Id, out var card))
            {
                card.Update(location);
                card.IsSelected = location.Id == selectedLocationId;
            }
            else card = CreateCard(location, location.Id == selectedLocationId);
            Saved.Add(card);
        }
        foreach (var removed in previous.Values)
        {
            removed.PropertyChanged -= OnCardPropertyChanged;
            removed.Dispose();
        }
        ApplySort();
        RefreshThumbnailAttributions();
        OnPropertyChanged(nameof(HasSavedLocations)); OnPropertyChanged(nameof(IsFirstRun)); OnPropertyChanged(nameof(SavedSummary));
    }

    public async Task RefreshDeviceAvailabilityAsync(CancellationToken cancellationToken)
    {
        var availability = await _deviceAvailability.GetAvailabilityAsync(cancellationToken);
        CanUseDeviceLocation = availability.CanRequest;
        DeviceLocationUnavailableReason = availability.CanRequest ? null : availability.Reason ?? Strings.Get("Locations_Device_Unavailable");
        UseDeviceLocationCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUseDeviceLocation))]
    private async Task UseDeviceLocation()
    {
        var result = await _resolver.ResolveDeviceOrFallbackAsync(_lastCustomCoordinate, CancellationToken.None);
        if (result.Source != LocationResolutionSource.OperatingSystemLocation)
        {
            ResolutionMessage = Strings.Get("Locations_Device_NotResolved");
            return;
        }
        _lastCustomCoordinate = result.Coordinate;
        ResolutionMessage = Strings.Get("Locations_Device_Selected");
        await _openCustom(result);
    }

    [RelayCommand]
    private Task SearchForPlace() => SearchForPlaceAsync(null);

    /// <summary>
    /// The one place search: the shared search field hands it what was typed, and it opens the existing
    /// search surface with that text already in it. There is no second geocoding path.
    /// </summary>
    public async Task SearchForPlaceAsync(string? initialQuery)
    {
        var result = await _showSearch(initialQuery);
        if (result is null) return;
        _lastCustomCoordinate = result.Coordinate;
        await _useSearchResult(result, false);
    }

    private LocationCardViewModel CreateCard(SavedLocation location, bool isSelected)
    {
        var card = new LocationCardViewModel(location, isSelected, _mapThumbnails, _now,
            _openSaved, _edit, _delete, _toggleFavourite);
        card.PropertyChanged += OnCardPropertyChanged;
        return card;
    }

    private void OnCardPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(LocationCardViewModel.ThumbnailMetadata)) RefreshThumbnailAttributions();
    }

    public void RefreshThumbnailAttributions()
    {
        var values = Saved.SelectMany(card => AttributionEntries(card.ThumbnailMetadata))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Text))
            // By the credit itself: the map raster and the road/water features are both OpenStreetMap and
            // carry the same wording, and the same sentence twice credits nobody further.
            .DistinctBy(entry => entry.Text.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Text)
            .OrderBy(value => value, StringComparer.CurrentCultureIgnoreCase);
        // One quiet line, the same separator the rest of the UI uses between related fragments.
        ThumbnailAttributionSummary = string.Join(" · ", values);
        OnPropertyChanged(nameof(HasThumbnailAttributions));
    }

    private static IEnumerable<(string Provider, string? Url, string Text)> AttributionEntries(
        SavedLocationThumbnailMetadata? metadata)
    {
        if (metadata is null) yield break;
        if (!string.IsNullOrWhiteSpace(metadata.AttributionText))
            yield return (metadata.ProviderId, metadata.AttributionUrl, metadata.AttributionText);
        if (!string.IsNullOrWhiteSpace(metadata.FeatureAttributionText))
            yield return (metadata.FeatureProviderId ?? "openstreetmap-overpass",
                metadata.FeatureAttributionUrl, metadata.FeatureAttributionText);
        if (string.Equals(metadata.SettlementProviderId, WsfSettlementDataProvider.SourceId,
                StringComparison.OrdinalIgnoreCase))
            yield return (metadata.SettlementProviderId!, "https://geoservice.dlr.de/web/datasets/wsf_3d",
                "World Settlement Footprint 3D (DLR)");
    }

    partial void OnSelectedSortOptionChanged(LocationSortOption value) => ApplySort();

    private void ApplySort()
    {
        if (Saved.Count < 2)
        {
            RebuildGridItems();
            return;
        }
        var mode = SelectedSortOption?.Mode ?? LocationSortMode.RecentlyUsed;
        IOrderedEnumerable<LocationCardViewModel> ordered = Saved.OrderByDescending(card => card.IsFavourite);
        ordered = mode switch
        {
            LocationSortMode.Name => ordered.ThenBy(card => card.Name, StringComparer.CurrentCultureIgnoreCase),
            LocationSortMode.DateAdded => ordered
                .ThenByDescending(card => card.Location.DateAddedUtc ?? DateTimeOffset.MinValue)
                .ThenByDescending(card => card.Location.SortOrder),
            _ => ordered.ThenByDescending(card => card.Location.LastUsedUtc ?? DateTimeOffset.MinValue)
        };
        Reorder(ordered.ThenBy(card => card.Name, StringComparer.CurrentCultureIgnoreCase));
    }
    private void Reorder(IEnumerable<LocationCardViewModel> ordered)
    {
        var values = ordered.ToArray(); Saved.Clear(); foreach (var value in values) Saved.Add(value);
        RebuildGridItems();
    }

    private void RebuildGridItems()
    {
        GridItems.Clear();
        foreach (var card in Saved.Where(Matches)) GridItems.Add(card);
        // The way to save the current pin is always on the page, filtered or not.
        GridItems.Add(_addLocationCard);
        OnPropertyChanged(nameof(HasNoMatches));
    }

    /// <summary>The Planner's active pin, shown on the tile that saves it.</summary>
    public void SetCurrentPin(GeoCoordinate coordinate) =>
        _addLocationCard.Coordinates = LocationCardViewModel.FormatCoordinates(coordinate);
}
