using Microsoft.Extensions.Logging;

namespace Noctaxis.Core.Persistence;

/// <summary>
/// Where Noctaxis keeps regenerable caches (terrain, land cover, settlement and basemap tiles), apart from the durable
/// data folder that holds settings, saved locations, their map assets and user-installed datasets (NX-38):
/// <list type="bullet">
/// <item>Windows: <c>%LOCALAPPDATA%\Noctaxis\Cache</c> (local, never roamed);</item>
/// <item>Linux: <c>$XDG_CACHE_HOME/Noctaxis</c>, which is <c>~/.cache/Noctaxis</c> unless set otherwise;</item>
/// <item>macOS: <c>~/Library/Caches/Noctaxis</c>.</item>
/// </list>
/// </summary>
public static class CacheStorage
{
    /// <summary>
    /// The cache folders this version uses, relative to the data folder where earlier versions kept them. Only these
    /// are moved: anything else in the data folder (the light-pollution atlas the user installed, saved-location
    /// assets, obsolete folders of earlier versions) stays exactly where it is.
    /// </summary>
    public static readonly IReadOnlyList<string> MigratedEntries =
    [
        Path.Combine("EnvironmentalData", "mapzen-terrarium"),
        Path.Combine("EnvironmentalData", "esa-worldcover"),
        Path.Combine("EnvironmentalData", "wsf-3d"),
        "BasemapCache"
    ];

    /// <summary>The platform's cache folder for Noctaxis, from that platform's conventions.</summary>
    public static string Resolve(bool isWindows, bool isMacOS, Func<System.Environment.SpecialFolder, string> folder,
        Func<string, string?> environment)
    {
        if (isWindows)
        {
            var local = folder(System.Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(string.IsNullOrWhiteSpace(local)
                ? Path.Combine(folder(System.Environment.SpecialFolder.UserProfile), "AppData", "Local")
                : local, "Noctaxis", "Cache");
        }
        var home = folder(System.Environment.SpecialFolder.UserProfile);
        if (isMacOS) return Path.Combine(home, "Library", "Caches", "Noctaxis");
        // XDG: an unset, empty or relative XDG_CACHE_HOME is ignored.
        var xdg = environment("XDG_CACHE_HOME");
        return Path.Combine(!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg) ? xdg : Path.Combine(home, ".cache"), "Noctaxis");
    }

    public static string ResolveForCurrentPlatform() =>
        Resolve(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), System.Environment.GetFolderPath,
            System.Environment.GetEnvironmentVariable);

    /// <summary>
    /// Makes <paramref name="cacheDirectory"/> the cache folder, moving the caches earlier versions kept in
    /// <paramref name="dataDirectory"/> into it, and returns the folder the caches must use. Run once at startup by the
    /// instance that owns the data folder, before any cache is opened.
    /// <list type="bullet">
    /// <item>Nothing is copied: a cache moves only by a rename on the same volume. When the folders are on different
    /// volumes (a redirected roaming profile, say) and earlier caches exist, they stay where they are and keep being
    /// used, rather than gigabytes being copied.</item>
    /// <item>Nothing is deleted: when a cache already exists at the destination (a move interrupted earlier, or an
    /// earlier version run again), files are moved across one by one; a file that exists on both sides stays in the
    /// old place, and only emptied folders are removed. The next start continues where this one stopped.</item>
    /// <item>A cache folder that cannot be created leaves everything as it was, in the data folder.</item>
    /// </list>
    /// </summary>
    public static string Prepare(string dataDirectory, string cacheDirectory, ILogger? logger = null,
        Func<string, string, bool>? sameVolume = null)
    {
        sameVolume ??= SameVolume;
        try { Directory.CreateDirectory(cacheDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logger?.LogWarning(ex, "Cache folder {Cache} is unusable; caches stay in {Data}", cacheDirectory, dataDirectory);
            return dataDirectory;
        }
        var legacy = MigratedEntries.Where(entry => Directory.Exists(Path.Combine(dataDirectory, entry))).ToArray();
        if (legacy.Length == 0) return cacheDirectory;
        if (!sameVolume(dataDirectory, cacheDirectory))
        {
            logger?.LogWarning("Caches in {Data} are on another volume than {Cache}; they stay where they are", dataDirectory, cacheDirectory);
            return dataDirectory;
        }
        foreach (var entry in legacy)
            MoveEntry(Path.Combine(dataDirectory, entry), Path.Combine(cacheDirectory, entry), logger);
        return cacheDirectory;
    }

    private static void MoveEntry(string source, string destination, ILogger? logger)
    {
        try
        {
            if (!Directory.Exists(destination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(source, destination);
                logger?.LogInformation("Moved cache {Source} to {Destination}", source, destination);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Could not move cache {Source} as a whole; moving its files instead", source);
        }
        Merge(source, destination, logger);
    }

    private static void Merge(string source, string destination, ILogger? logger)
    {
        foreach (var file in SafeFiles(source))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (File.Exists(target)) continue; // Both sides have it: the old copy stays, nothing is lost.
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(file, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "Cache file {File} stays in place for now", file);
            }
        }
        RemoveEmptyDirectories(source);
    }

    /// <summary>Every file under <paramref name="root"/>, never following links out of it.</summary>
    private static IEnumerable<string> SafeFiles(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files, directories;
            try
            {
                files = Directory.GetFiles(directory);
                directories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
            foreach (var child in directories)
                if (!new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push(child);
        }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        try
        {
            foreach (var child in Directory.GetDirectories(root))
                if (!new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint)) RemoveEmptyDirectories(child);
            if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Whether both folders are on one mounted volume, judged by the longest mount point containing each (on Unix every
    /// path shares the root "/", so the path root alone cannot tell two mounts apart). Network paths never count as the
    /// same volume. Moving across volumes would copy, which this migration never does.
    /// </summary>
    private static bool SameVolume(string first, string second)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var mounts = DriveInfo.GetDrives().Select(drive => drive.RootDirectory.FullName).ToArray();
            string? MountOf(string path)
            {
                var full = Path.GetFullPath(path);
                if (full.StartsWith(@"\\", StringComparison.Ordinal)) return null;
                return mounts.Where(root => full.StartsWith(root, comparison)).MaxBy(root => root.Length);
            }
            var (a, b) = (MountOf(first), MountOf(second));
            return a is not null && string.Equals(a, b, comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
