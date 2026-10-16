using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Noctaxis.Desktop.Export;

/// <summary>Why a source gave no image bytes to import. <see cref="None"/>: it did.</summary>
public enum PlanImageSourceProblem
{
    None,
    /// <summary>No image or file was offered (an empty clipboard, text, nothing dropped).</summary>
    NothingToImport,
    /// <summary>The clipboard holds an image, but only as decoded pixels: whatever was in the file is gone.</summary>
    ClipboardImageWithoutPlan,
    /// <summary>More than one file: a plan is imported from exactly one image.</summary>
    MultipleFiles,
    /// <summary>More than one file on the clipboard: none is chosen for the user.</summary>
    MultipleClipboardFiles,
    /// <summary>A file that could not be read, or a folder.</summary>
    UnreadableFile
}

/// <summary>The bytes of the one image to import, or why there are none.</summary>
public sealed record PlanImageSource(byte[]? Bytes, PlanImageSourceProblem Problem = PlanImageSourceProblem.None)
{
    public static PlanImageSource Of(byte[] bytes) => new(bytes);
    public static PlanImageSource Failed(PlanImageSourceProblem problem) => new(null, problem);
}

/// <summary>
/// Where a plan image can come from — a picked file, the clipboard, a drop — reduced to the bytes of one image.
/// None of them parses anything: every source hands its bytes to the one importer
/// (<see cref="ViewModels.MainViewModel.ImportPlanImage"/>). Copy PNG puts the plan image on the clipboard as the
/// PNG file itself — the bytes Save PNG writes — with its raw PNG bytes and its picture beside it, so the plan inside
/// survives a paste back into Noctaxis and a paste into anything that takes files.
/// </summary>
public static class PlanImageSources
{
    /// <summary>The largest file read: far more than any plan image, so nothing arbitrary is pulled into memory.</summary>
    public const long MaximumBytes = 64 * 1024 * 1024;

    /// <summary>
    /// The platform's raw-PNG clipboard format, which keeps the file's chunks (and so the plan): "PNG" on Windows,
    /// "public.png" on macOS and "image/png" elsewhere.
    /// </summary>
    public static DataFormat<byte[]> PngFormat { get; } = DataFormat.CreateBytesPlatformFormat(
        OperatingSystem.IsWindows() ? "PNG" : OperatingSystem.IsMacOS() ? "public.png" : "image/png");

    /// <summary>The one picked or dropped item, read whole; several items are refused rather than one chosen.</summary>
    public static async Task<PlanImageSource> FromItemsAsync(IReadOnlyList<IStorageItem>? items)
    {
        if (items is null || items.Count == 0) return PlanImageSource.Failed(PlanImageSourceProblem.NothingToImport);
        if (items.Count > 1) return PlanImageSource.Failed(PlanImageSourceProblem.MultipleFiles);
        if (items[0] is not IStorageFile file) return PlanImageSource.Failed(PlanImageSourceProblem.UnreadableFile);
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > MaximumBytes) return PlanImageSource.Failed(PlanImageSourceProblem.UnreadableFile);
                buffer.Write(chunk, 0, read);
            }
            return PlanImageSource.Of(buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return PlanImageSource.Failed(PlanImageSourceProblem.UnreadableFile);
        }
    }

    /// <summary>
    /// The clipboard's plan image: first a copied file (Copy PNG's own, or a PNG copied in a file manager), exactly
    /// one; then raw PNG bytes where the platform offers them. An image offered only as a decoded bitmap is reported
    /// as such — nothing is read from its pixels, and no text is read as a plan.
    /// </summary>
    public static async Task<PlanImageSource> FromClipboardAsync(IAsyncDataTransfer? data)
    {
        if (data is null) return PlanImageSource.Failed(PlanImageSourceProblem.NothingToImport);
        if (data.Contains(DataFormat.File) && await data.TryGetFilesAsync() is { Length: > 0 } files)
            return files.Length > 1
                ? PlanImageSource.Failed(PlanImageSourceProblem.MultipleClipboardFiles)
                : await FromItemsAsync(files);
        if (data.Contains(PngFormat) && await data.TryGetValueAsync(PngFormat) is { Length: > 0 } png)
            // The same limit as a file: raw clipboard bytes are no more trusted (NX-32).
            return png.Length > MaximumBytes ? PlanImageSource.Failed(PlanImageSourceProblem.UnreadableFile) : PlanImageSource.Of(png);
        return PlanImageSource.Failed(data.Contains(DataFormat.Bitmap)
            ? PlanImageSourceProblem.ClipboardImageWithoutPlan : PlanImageSourceProblem.NothingToImport);
    }

    /// <summary>A drop: its files, exactly one of them an image to import.</summary>
    public static Task<PlanImageSource> FromDropAsync(IDataTransfer data) =>
        FromItemsAsync(data.Contains(DataFormat.File) ? data.TryGetFiles() : null);

    /// <summary>Whether a drag carries files at all, for the drop highlight. Which file, and whether it holds a plan, is the import's to say.</summary>
    public static bool OffersFiles(IDataTransfer data) => data.Contains(DataFormat.File);

    /// <summary>
    /// The clipboard payload for a plan image: <paramref name="file"/>, the PNG file holding exactly
    /// <paramref name="png"/> (<see cref="ClipboardPlanFile"/>), for Noctaxis and anything that takes files; the same
    /// bytes in the raw-PNG format; and the image as a picture for applications that take only pictures. A picture
    /// alone would lose the plan.
    /// </summary>
    public static DataTransfer ForClipboard(byte[] png, IStorageFile? file)
    {
        var item = file is null ? new DataTransferItem() : DataTransferItem.CreateFile(file);
        item.Set(PngFormat, png);
        using (var stream = new MemoryStream(png, writable: false)) item.SetBitmap(new Bitmap(stream));
        var transfer = new DataTransfer();
        transfer.Add(item);
        return transfer;
    }
}
