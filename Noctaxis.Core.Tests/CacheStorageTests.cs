using Noctaxis.Core.Persistence;

namespace Noctaxis.Core.Tests;

/// <summary>
/// NX-38: regenerable caches live in the platform cache folder, and the caches earlier versions kept in the data folder
/// move there by rename only - never copied, never deleted, resumable after an interruption. Durable data (state, saved
/// locations, the user-installed light-pollution atlas, unknown folders) never moves.
/// </summary>
public sealed class CacheStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctaxis-cache-storage-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_root, "Roaming", "Noctaxis");
    private string Cache => Path.Combine(_root, "Local", "Noctaxis", "Cache");

    private string Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void AFreshInstallUsesTheCacheFolderAndCreatesNothingInTheDataFolder()
    {
        Assert.Equal(Cache, CacheStorage.Prepare(Data, Cache));
        Assert.True(Directory.Exists(Cache));
        Assert.False(Directory.Exists(Data));
    }

    [Fact]
    public void EarlierCachesMoveByRenameAndDurableDataStays()
    {
        Write(Data, "EnvironmentalData/mapzen-terrarium/1/12/1/1.png", "terrain");
        Write(Data, "EnvironmentalData/esa-worldcover/v200/N51W003.tif", "landcover");
        Write(Data, "EnvironmentalData/wsf-3d/2/fraction/chunk.tif", "settlement");
        Write(Data, "BasemapCache/openfreemap/v1/14/1/1.pbf", "basemap");
        var atlas = Write(Data, "EnvironmentalData/LightPollution/Lorenz/2025/current", "generation");
        var state = Write(Data, "state.json", "{}");
        var assets = Write(Data, "SavedLocationMaps/abc/source.png", "map");
        var obsolete = Write(Data, "EnvironmentalData/mapzen-skadi/N51/N51W003.hgt.gz", "old dem");

        Assert.Equal(Cache, CacheStorage.Prepare(Data, Cache, sameVolume: (_, _) => true));

        Assert.Equal("terrain", File.ReadAllText(Path.Combine(Cache, "EnvironmentalData/mapzen-terrarium/1/12/1/1.png")));
        Assert.Equal("landcover", File.ReadAllText(Path.Combine(Cache, "EnvironmentalData/esa-worldcover/v200/N51W003.tif")));
        Assert.Equal("settlement", File.ReadAllText(Path.Combine(Cache, "EnvironmentalData/wsf-3d/2/fraction/chunk.tif")));
        Assert.Equal("basemap", File.ReadAllText(Path.Combine(Cache, "BasemapCache/openfreemap/v1/14/1/1.pbf")));
        foreach (var entry in CacheStorage.MigratedEntries) Assert.False(Directory.Exists(Path.Combine(Data, entry)));
        // The user-installed atlas, the state, saved-location assets and unidentified folders are not caches to move.
        Assert.Equal("generation", File.ReadAllText(atlas));
        Assert.Equal("{}", File.ReadAllText(state));
        Assert.Equal("map", File.ReadAllText(assets));
        Assert.Equal("old dem", File.ReadAllText(obsolete));
        Assert.False(Directory.Exists(Path.Combine(Cache, "EnvironmentalData", "LightPollution")));
    }

    [Fact]
    public void AnInterruptedMoveIsCompletedLaterWithoutLosingEitherCopy()
    {
        // A previous start moved part of the terrain cache, or an earlier version wrote new tiles afterwards.
        Write(Cache, "EnvironmentalData/mapzen-terrarium/1/12/1/1.png", "already moved");
        Write(Data, "EnvironmentalData/mapzen-terrarium/1/12/1/1.png", "older duplicate");
        Write(Data, "EnvironmentalData/mapzen-terrarium/1/12/2/2.png", "still to move");

        Assert.Equal(Cache, CacheStorage.Prepare(Data, Cache, sameVolume: (_, _) => true));

        Assert.Equal("already moved", File.ReadAllText(Path.Combine(Cache, "EnvironmentalData/mapzen-terrarium/1/12/1/1.png")));
        Assert.Equal("still to move", File.ReadAllText(Path.Combine(Cache, "EnvironmentalData/mapzen-terrarium/1/12/2/2.png")));
        // The duplicate is never deleted: it stays where it was, and only emptied folders are removed.
        Assert.Equal("older duplicate", File.ReadAllText(Path.Combine(Data, "EnvironmentalData/mapzen-terrarium/1/12/1/1.png")));
        Assert.False(Directory.Exists(Path.Combine(Data, "EnvironmentalData/mapzen-terrarium/1/12/2")));
    }

    [Fact]
    public void ALockedCacheFileStaysInPlaceAndTheRestMoves()
    {
        var locked = Write(Data, "EnvironmentalData/esa-worldcover/v200/locked.tif", "in use");
        Write(Data, "EnvironmentalData/esa-worldcover/v200/free.tif", "free");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(Cache, CacheStorage.Prepare(Data, Cache, sameVolume: (_, _) => true));

        Assert.Equal("free", File.ReadAllText(Path.Combine(Cache, "EnvironmentalData/esa-worldcover/v200/free.tif")));
        Assert.Equal("in use", File.ReadAllText(OperatingSystem.IsWindows()
            ? locked
            : Path.Combine(Cache, "EnvironmentalData/esa-worldcover/v200/locked.tif")));

        // The next start finishes the move.
        if (OperatingSystem.IsWindows())
        {
            CacheStorage.Prepare(Data, Cache, sameVolume: (_, _) => true);
            Assert.Equal("in use", File.ReadAllText(Path.Combine(Cache, "EnvironmentalData/esa-worldcover/v200/locked.tif")));
            Assert.False(Directory.Exists(Path.Combine(Data, "EnvironmentalData/esa-worldcover")));
        }
    }

    [Fact]
    public void CachesOnAnotherVolumeAreNeverCopiedAndKeepBeingUsedWhereTheyAre()
    {
        var tile = Write(Data, "EnvironmentalData/mapzen-terrarium/1/12/1/1.png", "terrain");

        Assert.Equal(Data, CacheStorage.Prepare(Data, Cache, sameVolume: (_, _) => false));

        Assert.Equal("terrain", File.ReadAllText(tile));
        Assert.False(Directory.Exists(Path.Combine(Cache, "EnvironmentalData")));
    }

    [Fact]
    public void AnUnusableCacheFolderLeavesEverythingInTheDataFolder()
    {
        var tile = Write(Data, "BasemapCache/openfreemap/v1/1.pbf", "basemap");
        var blocker = Write(_root, "not-a-folder", "a file where the cache folder would go");

        Assert.Equal(Data, CacheStorage.Prepare(Data, Path.Combine(blocker, "Cache"), sameVolume: (_, _) => true));
        Assert.Equal("basemap", File.ReadAllText(tile));
    }

    [Fact]
    public void ThePlatformCacheFolderFollowsEachPlatformsConvention()
    {
        string Folder(System.Environment.SpecialFolder folder) => folder switch
        {
            System.Environment.SpecialFolder.LocalApplicationData => @"C:\Users\someone\AppData\Local",
            System.Environment.SpecialFolder.UserProfile => "/home/someone",
            _ => ""
        };
        Assert.Equal(Path.Combine(@"C:\Users\someone\AppData\Local", "Noctaxis", "Cache"),
            CacheStorage.Resolve(isWindows: true, isMacOS: false, Folder, _ => null));
        Assert.Equal(Path.Combine("/home/someone", ".cache", "Noctaxis"),
            CacheStorage.Resolve(isWindows: false, isMacOS: false, Folder, _ => null));
        Assert.Equal(Path.Combine("/var/cache/someone", "Noctaxis"),
            CacheStorage.Resolve(isWindows: false, isMacOS: false, Folder, name => name == "XDG_CACHE_HOME" ? "/var/cache/someone" : null));
        // A relative XDG_CACHE_HOME is invalid by the specification and ignored.
        Assert.Equal(Path.Combine("/home/someone", ".cache", "Noctaxis"),
            CacheStorage.Resolve(isWindows: false, isMacOS: false, Folder, name => name == "XDG_CACHE_HOME" ? "relative" : null));
        Assert.Equal(Path.Combine("/home/someone", "Library", "Caches", "Noctaxis"),
            CacheStorage.Resolve(isWindows: false, isMacOS: true, Folder, _ => null));
    }

    [Fact]
    public void CacheConsumersUseTheCacheFolderAndTheAtlasStaysDurable()
    {
        var paths = new SplitPaths(Data, Cache);
        Assert.Equal(Path.Combine(Cache, "EnvironmentalData"),
            new Noctaxis.Core.Environment.TerrainDiskCache(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<Noctaxis.Core.Environment.TerrainDiskCache>.Instance).RootDirectory);
        Assert.Equal(Path.Combine(Cache, "EnvironmentalData"),
            new Noctaxis.Core.Environment.EnvironmentalTileCache(paths, Microsoft.Extensions.Logging.Abstractions.NullLogger<Noctaxis.Core.Environment.EnvironmentalTileCache>.Instance).RootDirectory);
        // A provider that names no cache folder keeps the single-folder layout of earlier versions.
        Assert.Equal(Data, ((IUserDataPathProvider)new DataOnly(Data)).GetCacheDirectory());
    }

    private sealed record SplitPaths(string Data, string Cache) : IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => Data;
        public string GetCacheDirectory() => Cache;
    }

    private sealed record DataOnly(string Data) : IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => Data;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
