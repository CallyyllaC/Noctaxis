namespace Noctaxis.Core.Export;

/// <summary>
/// A plan inside a PNG: one iTXt chunk named <see cref="MetadataKey"/> holding the plan's JSON
/// (<see cref="NoctaxisPlan"/>). Writing and reading go through here alone, whatever the image came from.
/// </summary>
public static class NoctaxisPlanImage
{
    public const string MetadataKey = "Noctaxis.Plan";
    /// <summary>
    /// The metadata the earlier scouting card wrote (its <c>NoctaxisExportMetadata</c>, schema 2). It is recognised
    /// so it can be named, but never read as a plan.
    /// </summary>
    public const string LegacyScoutingCardKey = "Noctaxis.ExportData";

    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>The PNG with the plan added; its pixels are untouched.</summary>
    public static byte[] Embed(byte[] png, NoctaxisPlan plan) => PngMetadataWriter.AddText(png, MetadataKey, plan.ToJson());

    public static bool IsPng(ReadOnlySpan<byte> data) => data.StartsWith(PngSignature);

    /// <summary>
    /// Reads the plan from an image's bytes. It never infers anything from the pixels: an image without the
    /// plan chunk has no plan.
    /// </summary>
    public static NoctaxisPlanReadResult Read(byte[]? data)
    {
        if (data is null || data.Length == 0) return NoctaxisPlanReadResult.Failed(NoctaxisPlanError.NotAnImage);
        if (!IsPng(data))
            return NoctaxisPlanReadResult.Failed(IsOtherImage(data) ? NoctaxisPlanError.UnsupportedImageFormat : NoctaxisPlanError.NotAnImage);
        string? json, legacy;
        try
        {
            // One bounded walk of the chunks; no pixel is decoded, whatever the image claims (NX-32).
            var texts = PngMetadataWriter.ReadTexts(data, MetadataKey, LegacyScoutingCardKey);
            json = texts[MetadataKey];
            legacy = string.IsNullOrWhiteSpace(json) ? texts[LegacyScoutingCardKey] : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            // A damaged or malformed PNG.
            return NoctaxisPlanReadResult.Failed(NoctaxisPlanError.NotAnImage);
        }
        if (!string.IsNullOrWhiteSpace(json)) return NoctaxisPlan.Parse(json);
        return NoctaxisPlanReadResult.Failed(string.IsNullOrWhiteSpace(legacy) ? NoctaxisPlanError.NoPlan : NoctaxisPlanError.LegacyScoutingCard);
    }

    private static bool IsOtherImage(ReadOnlySpan<byte> data) =>
        data.StartsWith((byte[])[0xFF, 0xD8, 0xFF]) ||                              // JPEG
        data.StartsWith("GIF8"u8) || data.StartsWith("BM"u8) ||                     // GIF, BMP
        (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8)) ||
        data.StartsWith((byte[])[0x49, 0x49, 0x2A, 0x00]) || data.StartsWith((byte[])[0x4D, 0x4D, 0x00, 0x2A]); // TIFF
}
