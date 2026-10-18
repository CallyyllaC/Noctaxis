using System.Diagnostics;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Services;

namespace Noctaxis.Desktop.Tests;

// S-V01: a deleted saved location's stored map assets, which include its exact coordinates, are removed.
public sealed partial class LocationMapThumbnailServiceTests
{
    private static string AssetDirectory(TestDirectory files, Guid id) =>
        System.IO.Path.Combine(files.Path, "SavedLocationMaps", id.ToString("N"));
    private static string LegacyDirectory(TestDirectory files, Guid id) =>
        System.IO.Path.Combine(files.Path, "SavedLocationThumbnails", id.ToString("N"));

    private static string CreateFolder(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(System.IO.Path.Combine(directory, "metadata.json"), "{\"Latitude\":51.5,\"Longitude\":-0.1}");
        return directory;
    }

    [Fact]
    public async Task DeleteLocationAssets_RemovesCurrentAndLegacyAssetsOfThatLocationOnly()
    {
        using var files = new TestDirectory();
        using var service = CreateService(files, new TileHandler(CreateTile()));
        var deleted = Location(Guid.NewGuid(), 51.8903, -2.6004, "Private site");
        var kept = Location(Guid.NewGuid(), 52.1, -2.2, "Kept");
        Assert.NotNull(await service.Value.GetThumbnailAsync(deleted, SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None));
        Assert.NotNull(await service.Value.GetThumbnailAsync(kept, SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None));
        CreateFolder(LegacyDirectory(files, deleted.Id));
        Assert.True(File.Exists(System.IO.Path.Combine(AssetDirectory(files, deleted.Id), "metadata.json")));

        await service.Value.DeleteLocationAssetsAsync(deleted.Id, CancellationToken.None);

        Assert.False(Directory.Exists(AssetDirectory(files, deleted.Id)));
        Assert.False(Directory.Exists(LegacyDirectory(files, deleted.Id)));
        Assert.True(File.Exists(System.IO.Path.Combine(AssetDirectory(files, kept.Id), SourceFile)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.Value.DeleteLocationAssetsAsync(Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteLocationAssets_GenerationInFlightOrQueuedCannotRecreateThem()
    {
        using var files = new TestDirectory();
        var handler = new GatedTileHandler(CreateTile());
        using var service = CreateService(files, handler);
        var location = Location(Guid.NewGuid(), 51.8903, -2.6004, "Private site");

        // One generation is downloading; a second is queued behind it for the same location.
        var running = service.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = service.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None);
        var deletion = service.Value.DeleteLocationAssetsAsync(location.Id, CancellationToken.None);
        Assert.False(deletion.IsCompleted);

        handler.Release.TrySetResult();
        await deletion.WaitAsync(TimeSpan.FromSeconds(30));
        await running.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(await queued.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(Directory.Exists(AssetDirectory(files, location.Id)));

        // A request made after the deletion (the location imported again) works as normal.
        Assert.NotNull(await service.Value.GetThumbnailAsync(location, SavedLocationMapRefreshMode.RefreshSource, CancellationToken.None));
        Assert.True(Directory.Exists(AssetDirectory(files, location.Id)));
    }

    [Fact]
    public async Task OrphanSweep_RemovesOnlyOldUnreferencedLocationFolders()
    {
        using var files = new TestDirectory();
        using var service = CreateService(files, new TileHandler(CreateTile()));
        var cutoff = DateTime.UtcNow.AddHours(-1);
        var saved = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var legacyOrphan = Guid.NewGuid();
        var recent = Guid.NewGuid();
        var old = cutoff.AddDays(-30);
        foreach (var directory in new[]
                 {
                     CreateFolder(AssetDirectory(files, saved)), CreateFolder(AssetDirectory(files, orphan)),
                     CreateFolder(LegacyDirectory(files, legacyOrphan))
                 })
            Directory.SetLastWriteTimeUtc(directory, old);
        CreateFolder(AssetDirectory(files, recent));
        var unmanaged = System.IO.Path.Combine(files.Path, "SavedLocationMaps", "not-a-location");
        CreateFolder(unmanaged);
        Directory.SetLastWriteTimeUtc(unmanaged, old);

        var removed = await service.Value.RemoveOrphanedAssetsAsync(new HashSet<Guid> { saved }, cutoff, CancellationToken.None);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(AssetDirectory(files, orphan)));
        Assert.False(Directory.Exists(LegacyDirectory(files, legacyOrphan)));
        Assert.True(Directory.Exists(AssetDirectory(files, saved)));
        // Possibly a location saved since the state was read (for example by another instance).
        Assert.True(Directory.Exists(AssetDirectory(files, recent)));
        Assert.True(Directory.Exists(unmanaged));
    }

    [Fact]
    public async Task Deletion_RemovesLinksWithoutFollowingThemOutOfTheManagedFolder()
    {
        using var files = new TestDirectory();
        using var outside = new TestDirectory();
        var precious = System.IO.Path.Combine(outside.Path, "precious.txt");
        File.WriteAllText(precious, "not Noctaxis data");
        using var service = CreateService(files, new TileHandler(CreateTile()));
        var linked = Guid.NewGuid();
        var nested = Guid.NewGuid();
        Directory.CreateDirectory(System.IO.Path.Combine(files.Path, "SavedLocationMaps"));
        // The location folder itself is a link, and another location folder holds a link inside it.
        if (!TryCreateDirectoryLink(AssetDirectory(files, linked), outside.Path))
        {
            // Junctions need no privilege, so on Windows the link must exist; elsewhere a sandbox may forbid links.
            Assert.False(OperatingSystem.IsWindows(), "Could not create a directory junction for the test.");
            return;
        }
        var nestedDirectory = CreateFolder(AssetDirectory(files, nested));
        Assert.True(TryCreateDirectoryLink(System.IO.Path.Combine(nestedDirectory, "elsewhere"), outside.Path));

        await service.Value.DeleteLocationAssetsAsync(linked, CancellationToken.None);
        await service.Value.DeleteLocationAssetsAsync(nested, CancellationToken.None);

        Assert.False(Directory.Exists(AssetDirectory(files, linked)));
        Assert.False(Directory.Exists(nestedDirectory));
        Assert.True(File.Exists(precious));
    }

    /// <summary>A symbolic link, or on Windows without that privilege a junction, which needs none.</summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows()) return false;
        }
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        mklink.WaitForExit();
        return mklink.ExitCode == 0 && Directory.Exists(link);
    }

    private sealed class GatedTileHandler(byte[] tile) : Noctaxis.Desktop.Mapping.Vector.IVectorTileFetcher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<byte[]?> FetchAsync(int zoom, int column, int row, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return tile;
        }
    }
}
