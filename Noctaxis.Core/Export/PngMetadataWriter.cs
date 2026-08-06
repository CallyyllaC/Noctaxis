using Hjg.Pngcs;
using Hjg.Pngcs.Chunks;

namespace Noctaxis.Core.Export;

public static class PngMetadataWriter
{
    public static byte[] AddText(byte[] png, string key, string value)
    {
        using var input = new MemoryStream(png, writable: false);
        using var output = new MemoryStream();
        var reader = new PngReader(input);
        var writer = new PngWriter(output, reader.ImgInfo) { ShouldCloseStream = false };
        writer.CopyChunksFirst(reader, ChunkCopyBehaviour.COPY_ALL_SAFE);
        writer.GetMetadata().SetText(key, value, false, false);
        for (var row = 0; row < reader.ImgInfo.Rows; row++)
            writer.WriteRow(reader.ReadRowInt(row), row);
        reader.End();
        writer.CopyChunksLast(reader, ChunkCopyBehaviour.COPY_ALL_SAFE);
        writer.End();
        return output.ToArray();
    }

    public static string? ReadText(byte[] png, string key)
    {
        using var input = new MemoryStream(png, writable: false);
        var reader = new PngReader(input);
        for (var row = 0; row < reader.ImgInfo.Rows; row++) reader.ReadRowInt(row);
        reader.End();
        return reader.GetMetadata().GetTxtForKey(key);
    }
}
