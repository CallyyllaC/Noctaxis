using OpenCvSharp;

namespace Noctaxis.Desktop.Services;

public sealed record MapImageAccelerationStatus(
    bool NativeAvailable,
    bool CpuOptimisationsEnabled,
    int NativeThreadCount,
    string Backend,
    string? FailureReason = null);

public enum GaussianBorderMode { Reflect, ConstantZero }

public interface IMapImageAcceleration
{
    MapImageAccelerationStatus Status { get; }
    float[] GaussianBlur(float[] source, int width, int height, double sigma,
        GaussianBorderMode borderMode = GaussianBorderMode.Reflect);
    float[] MaximumFilter(float[] source, int width, int height, int size);
}

/// <summary>
/// Routes large raster kernels through native OpenCV. OpenCV selects its optimized CPU dispatch
/// (SSE/AVX where supported) and native worker pool; a deterministic managed implementation remains
/// available if the native runtime cannot load.
/// </summary>
public sealed class OpenCvMapImageAcceleration : IMapImageAcceleration
{
    private readonly ManagedMapImageAcceleration _fallback = new();
    private readonly object _initializationGate = new();
    private volatile MapImageAccelerationStatus? _status;

    public OpenCvMapImageAcceleration(bool disableNative = false)
    {
        if (disableNative)
            _status = new MapImageAccelerationStatus(false, false, 1,
                "Managed deterministic fallback", "Native acceleration was disabled explicitly.");
    }

    public static OpenCvMapImageAcceleration Shared { get; } = new();

    public MapImageAccelerationStatus Status => EnsureStatus();

    public float[] GaussianBlur(float[] source, int width, int height, double sigma,
        GaussianBorderMode borderMode = GaussianBorderMode.Reflect)
    {
        Validate(source, width, height);
        if (sigma <= 0) return (float[])source.Clone();
        if (!EnsureStatus().NativeAvailable)
            return _fallback.GaussianBlur(source, width, height, sigma, borderMode);
        try
        {
            var radius = (int)(4 * sigma + .5);
            var kernelSize = radius * 2 + 1;
            using var sourceMat = Mat.FromPixelData(height, width, MatType.CV_32FC1, source);
            using var destination = new Mat();
            Cv2.GaussianBlur(sourceMat, destination, new Size(kernelSize, kernelSize), sigma, sigma,
                borderMode == GaussianBorderMode.Reflect ? BorderTypes.Reflect : BorderTypes.Constant);
            destination.GetArray(out float[] output);
            return output;
        }
        catch (Exception exception) when (IsNativeFailure(exception))
        {
            DisableNative(exception);
            return _fallback.GaussianBlur(source, width, height, sigma, borderMode);
        }
    }

    public float[] MaximumFilter(float[] source, int width, int height, int size)
    {
        Validate(source, width, height);
        if (size < 1 || size % 2 == 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (!EnsureStatus().NativeAvailable) return _fallback.MaximumFilter(source, width, height, size);
        try
        {
            using var sourceMat = Mat.FromPixelData(height, width, MatType.CV_32FC1, source);
            using var destination = new Mat();
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(size, size));
            Cv2.Dilate(sourceMat, destination, kernel, borderType: BorderTypes.Constant,
                borderValue: Scalar.All(0));
            destination.GetArray(out float[] output);
            return output;
        }
        catch (Exception exception) when (IsNativeFailure(exception))
        {
            DisableNative(exception);
            return _fallback.MaximumFilter(source, width, height, size);
        }
    }

    private MapImageAccelerationStatus EnsureStatus()
    {
        if (_status is not null) return _status;
        lock (_initializationGate)
        {
            if (_status is not null) return _status;
            try
            {
                Cv2.SetUseOptimized(true);
                _ = Cv2.GetBuildInformation();
                _status = new MapImageAccelerationStatus(true, Cv2.UseOptimized(),
                    Math.Max(1, Cv2.GetNumThreads()),
                    Cv2.UseOptimized() ? "OpenCV native optimized CPU" : "OpenCV native CPU");
            }
            catch (Exception exception) when (IsNativeFailure(exception))
            {
                _status = FailedStatus(exception);
            }
            return _status;
        }
    }

    private void DisableNative(Exception exception)
    {
        lock (_initializationGate) _status = FailedStatus(exception);
    }

    private static MapImageAccelerationStatus FailedStatus(Exception exception) => new(false, false, 1,
        "Managed deterministic fallback", exception.GetBaseException().Message);

    private static bool IsNativeFailure(Exception exception) => exception is OpenCVException or
        DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException;

    private static void Validate(float[] source, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (source.Length != checked(width * height))
            throw new ArgumentException("Raster length does not match its dimensions.", nameof(source));
    }
}

public sealed class ManagedMapImageAcceleration : IMapImageAcceleration
{
    public MapImageAccelerationStatus Status { get; } = new(false, false, 1,
        "Managed deterministic fallback");

    public float[] GaussianBlur(float[] source, int width, int height, double sigma,
        GaussianBorderMode borderMode = GaussianBorderMode.Reflect)
    {
        if (sigma <= 0) return (float[])source.Clone();
        var radius = (int)(4 * sigma + .5);
        var kernel = new double[radius * 2 + 1];
        double total = 0;
        for (var offset = -radius; offset <= radius; offset++)
        {
            var value = Math.Exp(-.5 * offset * offset / (sigma * sigma));
            kernel[offset + radius] = value;
            total += value;
        }
        for (var index = 0; index < kernel.Length; index++) kernel[index] /= total;
        var horizontal = new float[source.Length];
        Parallel.For(0, height, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                double sum = 0;
                for (var offset = -radius; offset <= radius; offset++)
                {
                    var sampleX = x + offset;
                    if (borderMode == GaussianBorderMode.ConstantZero && (sampleX < 0 || sampleX >= width))
                        continue;
                    sampleX = Reflect(sampleX, width);
                    sum += source[row + sampleX] * kernel[offset + radius];
                }
                horizontal[row + x] = (float)sum;
            }
        });
        var output = new float[source.Length];
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                double sum = 0;
                for (var offset = -radius; offset <= radius; offset++)
                {
                    var sampleY = y + offset;
                    if (borderMode == GaussianBorderMode.ConstantZero && (sampleY < 0 || sampleY >= height))
                        continue;
                    sampleY = Reflect(sampleY, height);
                    sum += horizontal[sampleY * width + x] * kernel[offset + radius];
                }
                output[y * width + x] = (float)sum;
            }
        });
        return output;
    }

    private static int Reflect(int index, int length)
    {
        if (length <= 1) return 0;
        while (index < 0 || index >= length)
            index = index < 0 ? -index - 1 : 2 * length - index - 1;
        return index;
    }

    public float[] MaximumFilter(float[] source, int width, int height, int size)
    {
        if (size < 1 || size % 2 == 0) throw new ArgumentOutOfRangeException(nameof(size));
        var radius = size / 2;
        var horizontal = new float[source.Length];
        Parallel.For(0, height, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var value = 0f;
                for (var sampleX = Math.Max(0, x - radius); sampleX <= Math.Min(width - 1, x + radius); sampleX++)
                    value = Math.Max(value, source[row + sampleX]);
                horizontal[row + x] = value;
            }
        });
        var output = new float[source.Length];
        Parallel.For(0, height, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var value = 0f;
                for (var sampleY = Math.Max(0, y - radius); sampleY <= Math.Min(height - 1, y + radius); sampleY++)
                    value = Math.Max(value, horizontal[sampleY * width + x]);
                output[y * width + x] = value;
            }
        });
        return output;
    }
}
