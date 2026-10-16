namespace Noctaxis.Desktop.Export;

/// <summary>
/// The file Copy PNG puts on the clipboard: the plan image's exact bytes, written to Noctaxis' own folder under the
/// system's temporary folder as <see cref="FileName"/>, the name Save PNG suggests. Each copy gets its own folder, so
/// a copy never rewrites a file another application may still be pasting; only the newest <see cref="Kept"/> copies
/// are kept, the rest removed whenever one is made.
/// </summary>
public static class ClipboardPlanFile
{
    public const string FileName = "noctaxis-plan.png";
    /// <summary>How many copied files are kept: the clipboard's own, and a couple a slow paste may still be reading.</summary>
    public const int Kept = 3;

    /// <summary>Where the copied files live: a folder of Noctaxis' own, never the user's.</summary>
    public static string DefaultRoot { get; } = Path.Combine(Path.GetTempPath(), "Noctaxis", "Clipboard");

    /// <summary>Writes <paramref name="png"/> as a new copied file and returns its path, removing all but the newest few.</summary>
    public static async Task<string> WriteAsync(byte[] png, string? root = null, CancellationToken cancellationToken = default)
    {
        root ??= DefaultRoot;
        // Sortable by when it was made, unique however quickly copies follow one another.
        var folder = Path.Combine(root, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        await File.WriteAllBytesAsync(path, png, cancellationToken);
        Prune(root, keep: folder);
        return path;
    }

    private static void Prune(string root, string keep)
    {
        var stale = Directory.EnumerateDirectories(root)
            .Where(folder => !string.Equals(folder, keep, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(folder => Path.GetFileName(folder), StringComparer.Ordinal)
            .Skip(Kept - 1);
        foreach (var folder in stale)
        {
            // A file still open elsewhere is left for the next copy to remove.
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
