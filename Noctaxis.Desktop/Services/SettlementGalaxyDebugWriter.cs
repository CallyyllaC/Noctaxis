using SkiaSharp;
using System.Text.Json;

namespace Noctaxis.Desktop.Services;

/// <summary>Opt-in development artifact writer; normal saved-location generation never constructs it.</summary>
public sealed class SettlementGalaxyDebugWriter
{
    private readonly SortedDictionary<string, SettlementGalaxyPassMetrics> _metrics =
        new(StringComparer.Ordinal);
    private SKBitmap? _baseline;
    private SKBitmap? _previousPass;
    private float[]? _density;
    private int _densityWidth;
    private int _densityHeight;
    public string OutputDirectory { get; }

    public SettlementGalaxyDebugWriter(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        OutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(OutputDirectory);
    }

    internal void BeginRun(SKBitmap baseline, SettlementDensityModel density)
    {
        _baseline?.Dispose();
        _previousPass?.Dispose();
        _baseline = baseline.Copy();
        _previousPass = null;
        _density = density.Density;
        _densityWidth = density.Width;
        _densityHeight = density.Height;
        _metrics.Clear();
    }

    internal void WriteColour(string fileName, SettlementGlowCompositor.FloatImage image)
    {
        using var bitmap = image.ToBitmap();
        WriteBitmap(fileName, bitmap);
        RecordMetrics(fileName, bitmap);
    }

    public void WriteColour(string fileName, SKBitmap bitmap)
    {
        WriteBitmap(fileName, bitmap);
        RecordMetrics(fileName, bitmap);
    }

    public void WriteGray(string fileName, float[] field, int width, int height)
    {
        if (field.Length != checked(width * height))
            throw new ArgumentException("Diagnostic field dimensions do not match its samples.", nameof(field));
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var pixels = new SkiaBitmapPixelBuffer(bitmap);
        var maximum = field.Where(float.IsFinite).DefaultIfEmpty().Max();
        if (maximum <= 1e-12f) maximum = 1;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var value = (byte)Math.Clamp((int)Math.Round(field[y * width + x] / maximum * 255), 0, 255);
            pixels.Write(x, y, value, value, value, 255);
        }
        WriteBitmap(fileName, bitmap);
    }

    public void WriteLabels(string fileName, int[] labels, int sourceWidth, int sourceHeight,
        int outputWidth, int outputHeight)
    {
        var field = new float[outputWidth * outputHeight];
        var maximum = Math.Max(1, labels.DefaultIfEmpty().Max());
        for (var y = 0; y < outputHeight; y++)
        for (var x = 0; x < outputWidth; x++)
        {
            var sx = Math.Clamp((int)Math.Round(x * (sourceWidth - 1d) / Math.Max(1, outputWidth - 1)),
                0, sourceWidth - 1);
            var sy = Math.Clamp((int)Math.Round(y * (sourceHeight - 1d) / Math.Max(1, outputHeight - 1)),
                0, sourceHeight - 1);
            field[y * outputWidth + x] = labels[sy * sourceWidth + sx] / (float)maximum;
        }
        WriteGray(fileName, field, outputWidth, outputHeight);
    }

    private void WriteBitmap(string fileName, SKBitmap bitmap)
    {
        var safeName = Path.GetFileName(fileName);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new FileStream(Path.Combine(OutputDirectory, safeName), FileMode.Create,
            FileAccess.Write, FileShare.Read);
        data.SaveTo(stream);
    }

    private void RecordMetrics(string fileName, SKBitmap bitmap)
    {
        if (_baseline is null || !IsPassImage(fileName)) return;
        _metrics[fileName] = SettlementGalaxyCalibrationMetrics.Analyse(fileName, bitmap, _baseline,
            _density, _densityWidth, _densityHeight,
            fileName.StartsWith("07-", StringComparison.Ordinal) ? _previousPass : null);
        _previousPass?.Dispose();
        _previousPass = bitmap.Copy();
        var json = JsonSerializer.Serialize(_metrics.Values, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(OutputDirectory, "metrics.json"), json);
    }

    private static bool IsPassImage(string fileName) => fileName.Length >= 3 &&
        char.IsAsciiDigit(fileName[0]) && char.IsAsciiDigit(fileName[1]) && fileName[2] == '-';
}
