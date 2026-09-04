using System.Text.Json;
using SkiaSharp;

namespace Noctaxis.Desktop.Services;

public sealed record SettlementGalaxyCalibrationRun(
    string LocationDirectory,
    string ReferenceDirectory,
    string OutputDirectory,
    string SelectedStyleHash,
    IReadOnlyList<SettlementGalaxyCalibrationRanking> Rankings);

public sealed record SettlementGalaxyCalibrationRanking(
    string Group,
    string Candidate,
    double Score,
    string StyleHash,
    string PassImage,
    SettlementGalaxyPassMetrics Metrics,
    bool Selected);

/// <summary>
/// Offline, deterministic development harness for bounded settlement-galaxy style calibration.
/// It consumes already-persisted map/WSF assets and never performs acquisition or modifies them.
/// </summary>
public sealed class SettlementGalaxyCalibrationHarness
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public SettlementGalaxyCalibrationRun Run(string locationDirectory, string referenceDirectory,
        string outputDirectory)
    {
        locationDirectory = Path.GetFullPath(locationDirectory);
        referenceDirectory = Path.GetFullPath(referenceDirectory);
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var input = LoadInput(locationDirectory);
        var references = LoadReferences(referenceDirectory, outputDirectory);
        var selected = SettlementGalaxyStyle.DefaultV1;
        var allRankings = new List<SettlementGalaxyCalibrationRanking>();

        selected = RunGroup("01-passes-1-3", "06-wisps.png", BodyCandidates(selected),
            references.BroadBody, input, outputDirectory, ScoreBroadBody, allRankings);
        selected = RunGroup("02-passes-4-6", "06-wisps.png", CloudCandidates(selected),
            references.BroadBody, input, outputDirectory, ScoreBroadBody, allRankings);
        selected = RunGroup("03-pass-7", "07-stars.png", StarCandidates(selected),
            references.StarHierarchy, input, outputDirectory, ScoreStars, allRankings);
        selected = RunGroup("04-pass-8", "08-star-chroma.png", ChromaCandidates(selected),
            references.StarHierarchy, input, outputDirectory, ScoreChroma, allRankings);
        selected = RunGroup("05-passes-9-12", "12-falloff.png", EnvelopeCandidates(selected),
            references.FinalHierarchy, input, outputDirectory, ScoreEnvelope, allRankings);
        selected = RunGroup("06-pass-13", "13-tonemapping.png", ToneCandidates(selected),
            references.FinalHierarchy, input, outputDirectory, ScoreTone, allRankings);

        var selectedDirectory = Path.Combine(outputDirectory, "selected");
        Render(input, selected, selectedDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "selected-style.json"),
            JsonSerializer.Serialize(selected, Json));
        File.WriteAllText(Path.Combine(outputDirectory, "ranking.json"),
            JsonSerializer.Serialize(allRankings, Json));
        return new SettlementGalaxyCalibrationRun(locationDirectory, referenceDirectory, outputDirectory,
            selected.SettingsHash, allRankings);
    }

    public string RenderSelected(string locationDirectory, string outputDirectory)
    {
        var style = SettlementGalaxyStyle.DefaultV1;
        outputDirectory = Path.GetFullPath(outputDirectory);
        Render(LoadInput(Path.GetFullPath(locationDirectory)), style, outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "selected-style.json"),
            JsonSerializer.Serialize(style, Json));
        return style.SettingsHash;
    }

    private static SettlementGalaxyStyle RunGroup(string group, string stage,
        IReadOnlyList<(string Name, SettlementGalaxyStyle Style)> candidates,
        IReadOnlyList<SettlementGalaxyPassMetrics> references, CalibrationInput input,
        string outputRoot,
        Func<SettlementGalaxyPassMetrics, SettlementGalaxyPassMetrics, double> score,
        List<SettlementGalaxyCalibrationRanking> allRankings)
    {
        var ranked = new List<(string Name, SettlementGalaxyStyle Style, string Directory,
            SettlementGalaxyPassMetrics Metrics, double Score)>();
        foreach (var candidate in candidates)
        {
            var directory = Path.Combine(outputRoot, "candidates", group, candidate.Name);
            Render(input, candidate.Style, directory);
            using var bitmap = EnsureBgra(SKBitmap.Decode(Path.Combine(directory, stage))
                ?? throw new InvalidDataException($"Candidate pass '{stage}' is not readable."));
            using var black = Black(bitmap.Width, bitmap.Height);
            var metrics = SettlementGalaxyCalibrationMetrics.Analyse(stage, bitmap, black);
            var candidateScore = references.Average(reference => score(metrics, reference));
            ranked.Add((candidate.Name, candidate.Style, directory, metrics, candidateScore));
        }
        var winner = ranked.OrderBy(value => value.Score).ThenBy(value => value.Name, StringComparer.Ordinal).First();
        foreach (var value in ranked.OrderBy(value => value.Score))
            allRankings.Add(new SettlementGalaxyCalibrationRanking(group, value.Name, value.Score,
                value.Style.SettingsHash, Path.Combine(value.Directory, stage), value.Metrics,
                value.Name == winner.Name));
        File.WriteAllText(Path.Combine(outputRoot, "ranking.json"), JsonSerializer.Serialize(allRankings, Json));
        return winner.Style;
    }

    private static void Render(CalibrationInput input, SettlementGalaxyStyle style, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var processor = new SavedLocationMapImageProcessor(new SettlementDensityBuilder(),
            new SettlementGlowGeometryCalculator(), new SettlementGlowCompositor(),
            new SettlementStarGenerator(), style);
        var bytes = processor.ProcessSettlementDebug(input.Source, input.Features, input.Settlement,
            input.Viewport, input.LocationId, outputDirectory, out var diagnostics);
        File.WriteAllBytes(Path.Combine(outputDirectory, "final.png"), bytes);
        File.WriteAllText(Path.Combine(outputDirectory, "render-diagnostics.json"),
            JsonSerializer.Serialize(diagnostics, Json));
    }

    private static CalibrationInput LoadInput(string directory)
    {
        string Required(string name)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) throw new FileNotFoundException($"Calibration input '{name}' is missing.", path);
            return path;
        }
        using var metadata = JsonDocument.Parse(File.ReadAllBytes(Required("metadata.json")));
        var root = metadata.RootElement;
        var id = root.GetProperty("locationId").GetGuid();
        var viewport = new WebMercatorViewport(root.GetProperty("sourceZoom").GetInt32(), 256,
            root.GetProperty("sourceImageWidth").GetInt32(), root.GetProperty("sourceImageHeight").GetInt32(),
            root.GetProperty("sourceCentreLatitude").GetDouble(),
            root.GetProperty("sourceCentreLongitude").GetDouble());
        var features = JsonSerializer.Deserialize<MapFeatureDataDocument>(
            File.ReadAllBytes(Required("features.json")), Json)
            ?? throw new InvalidDataException("Calibration features are empty.");
        return new CalibrationInput(id, viewport, File.ReadAllBytes(Required("source.png")), features,
            SettlementRasterCodec.Decode(File.ReadAllBytes(Required("settlement-field.bin.gz"))));
    }

    private static CalibrationReferences LoadReferences(string directory, string outputDirectory)
    {
        SettlementGalaxyPassMetrics Read(string fileName, SKRectI? crop = null)
        {
            using var decoded = SKBitmap.Decode(Path.Combine(directory, fileName))
                ?? throw new InvalidDataException($"Visual reference '{fileName}' is not readable.");
            using var source = EnsureBgra(decoded);
            using var bitmap = crop is null ? source.Copy() : Crop(source, crop.Value);
            using var black = Black(bitmap.Width, bitmap.Height);
            return SettlementGalaxyCalibrationMetrics.Analyse(fileName, bitmap, black);
        }
        var broad = new[]
        {
            Read("04_broad_cloud_frame_1000117328.png"),
            Read("05_broad_cloud_variant_1000117335.png")
        };
        var star = new[]
        {
            Read("02_original_frame_1000117322.png"),
            Read("03_positive_glow_frame_1000117326.png")
        };
        using var comparison = SKBitmap.Decode(Path.Combine(directory,
            "06_main_galaxy_B_vs_C_comparison_C_selected.png"))
            ?? throw new InvalidDataException("Selected C comparison reference is not readable.");
        var selectedCrop = new SKRectI(comparison.Width / 2, 0, comparison.Width, comparison.Height);
        var final = new[] { Read("06_main_galaxy_B_vs_C_comparison_C_selected.png", selectedCrop), star[1] };
        var references = new CalibrationReferences(broad, star, final);
        File.WriteAllText(Path.Combine(outputDirectory, "reference-metrics.json"),
            JsonSerializer.Serialize(references, Json));
        return references;
    }

    private static IReadOnlyList<(string, SettlementGalaxyStyle)> BodyCandidates(SettlementGalaxyStyle s) =>
    [
        ("selected", s),
        ("balanced-lift", WithBodyGains(s, 1.28, 1.35, 1.20, 1.35, 1.28, 1.10)),
        ("broad-lift", WithBodyGains(s, 1.50, 1.42, 1.12, 1.50, 1.35, 1.05)),
        ("substantial-body", WithBodyGains(s, 1.38, 1.55, 1.20, 1.58, 1.42, 1.10)),
        ("hero-envelope", WithBodyGains(s, 2.25, 1.65, .90, 2.00, 1.50, .85)),
        ("diffuse-envelope", WithBodyGains(s, 2.00, 1.45, .85, 2.20, 1.35, .75))
    ];

    private static SettlementGalaxyStyle WithBodyGains(SettlementGalaxyStyle s,
        double halo, double body, double core, double broad, double dense, double knot) => s with
    {
        Galaxy = s.Galaxy with
        {
            Hierarchy = s.Galaxy.Hierarchy with
            {
                HaloGain = s.Galaxy.Hierarchy.HaloGain * halo,
                BodyGain = s.Galaxy.Hierarchy.BodyGain * body,
                CoreGain = s.Galaxy.Hierarchy.CoreGain * core
            },
            Luminance = s.Galaxy.Luminance with
            {
                BroadGain = s.Galaxy.Luminance.BroadGain * broad,
                DenseGain = s.Galaxy.Luminance.DenseGain * dense,
                KnotGain = s.Galaxy.Luminance.KnotGain * knot
            }
        }
    };

    private static IReadOnlyList<(string, SettlementGalaxyStyle)> CloudCandidates(SettlementGalaxyStyle s) =>
    [
        ("selected", s),
        ("core-cloud-lift", WithCloudGains(s, 1.25, 1.30, 1.35, 1.05)),
        ("broad-cloud", WithCloudGains(s, 1.12, 1.25, 1.50, .90)),
        ("radiant-fibres", WithCloudGains(s, 1.38, 1.38, 1.25, 1.12)),
        ("diffuse-cloud", WithCloudGains(s, .90, 1.20, 2.00, .80))
    ];

    private static SettlementGalaxyStyle WithCloudGains(SettlementGalaxyStyle s,
        double bloom, double aura, double cloud, double wisps) => s with
    {
        Galaxy = s.Galaxy with { CoreRadiance = s.Galaxy.CoreRadiance with
        {
            BloomGain = s.Galaxy.CoreRadiance.BloomGain * bloom,
            AuraGain = s.Galaxy.CoreRadiance.AuraGain * aura
        } },
        Clouds = s.Clouds with { Gain = s.Clouds.Gain * cloud },
        Wisps = s.Wisps with { MinGain = s.Wisps.MinGain * wisps, MaxGain = s.Wisps.MaxGain * wisps }
    };

    private static IReadOnlyList<(string, SettlementGalaxyStyle)> StarCandidates(SettlementGalaxyStyle s) =>
    [
        ("selected", s),
        ("balanced-texture", WithStars(s, .68, 4300, .76, .82, .90, .86, 99.75, .54)),
        ("sparse-hierarchy", WithStars(s, .52, 3400, .64, .72, .83, .76, 99.80, .52)),
        ("deep-texture", WithStars(s, .74, 4700, .66, .78, .88, .80, 99.78, .54)),
        ("restrained-texture", WithStars(s, .40, 2600, .60, .70, .82, .70, 99.85, .50)),
        ("stellar-detail", WithStars(s, .32, 2100, .55, .65, .78, .65, 99.90, .48)),
        ("rare-stars", WithStars(s, .25, 1600, .50, .60, .75, .60, 99.93, .46))
    ];

    private static SettlementGalaxyStyle WithStars(SettlementGalaxyStyle s, double density, int maximum,
        double faint, double common, double medium, double bloom, double percentile = 99.7,
        double coreSigma = .55) => s with { Stars = s.Stars with
    {
        TargetSettlementStarDensity = s.Stars.TargetSettlementStarDensity * density,
        MaxSettlementStars = maximum,
        NormalisePercentile = percentile,
        CoreSigma = coreSigma,
        ClassGains = s.Stars.ClassGains with
        {
            Faint = s.Stars.ClassGains.Faint * faint,
            Common = s.Stars.ClassGains.Common * common,
            Medium = s.Stars.ClassGains.Medium * medium
        },
        BrightBloomGain = s.Stars.BrightBloomGain * bloom
    } };

    private static IReadOnlyList<(string, SettlementGalaxyStyle)> ChromaCandidates(SettlementGalaxyStyle s) =>
    [
        ("selected", s),
        ("overlap", WithChromaSigmas(s, 1.05, 1.10, 1.15)),
        ("compact", WithChromaSigmas(s, .90, .90, .90)),
        ("broad-haze", WithChromaSigmas(s, 1.00, 1.05, 1.20))
    ];

    private static SettlementGalaxyStyle WithChromaSigmas(SettlementGalaxyStyle s,
        double core, double bridge, double haze) => s with { Stars = s.Stars with
    {
        ColourVariation = s.Stars.ColourVariation with
        {
            CoreSigma = s.Stars.ColourVariation.CoreSigma * core,
            BridgeSigma = s.Stars.ColourVariation.BridgeSigma * bridge,
            HazeSigma = s.Stars.ColourVariation.HazeSigma * haze
        }
    } };

    private static IReadOnlyList<(string, SettlementGalaxyStyle)> EnvelopeCandidates(SettlementGalaxyStyle s) =>
    [
        ("selected", s),
        ("retreating-ambience", WithEnvelope(s, .90, .80, 72, .205)),
        ("long-envelope", WithEnvelope(s, .90, .82, 80, .225)),
        ("subordinate-components", WithEnvelope(s, .85, .75, 68, .190))
    ];

    private static SettlementGalaxyStyle WithEnvelope(SettlementGalaxyStyle s,
        double satellites, double ambience, double radius, double gain) => s with
    {
        Satellites = s.Satellites with
        {
            BodyGain = s.Satellites.BodyGain * satellites,
            InnerGain = s.Satellites.InnerGain * satellites,
            CoreGain = s.Satellites.CoreGain * satellites,
            HaloGain = s.Satellites.HaloGain * satellites
        },
        BackgroundAmbience = s.BackgroundAmbience with
        {
            BackgroundLiftGain = s.BackgroundAmbience.BackgroundLiftGain * ambience,
            BroadHazeGain = s.BackgroundAmbience.BroadHazeGain * ambience
        },
        OuterFalloff = s.OuterFalloff with { OuterHaloRadius = radius, Gain = gain }
    };

    private static IReadOnlyList<(string, SettlementGalaxyStyle)> ToneCandidates(SettlementGalaxyStyle s) =>
    [
        ("selected", s),
        ("gentle", WithTone(s, .69, .25, .27, .095, 1.08)),
        ("balanced", WithTone(s, .67, .30, .32, .125, 1.12)),
        ("compressed", WithTone(s, .65, .32, .30, .135, 1.10))
    ];

    private static SettlementGalaxyStyle WithTone(SettlementGalaxyStyle s, double threshold,
        double compression, double local, double curve, double saturation) => s with
    {
        Tonemapping = s.Tonemapping with
        {
            HighlightThreshold = threshold, HighlightCompression = compression,
            LocalPositiveLightContrast = local, GlobalCurveStrength = curve, Saturation = saturation
        }
    };

    private static double ScoreBroadBody(SettlementGalaxyPassMetrics a, SettlementGalaxyPassMetrics b) =>
        Distance(a.BlurredLuminanceMean, b.BlurredLuminanceMean) * 2 +
        Distance(a.BlurredLuminancePeak, b.BlurredLuminancePeak) +
        Distance(a.MainBodyMeanLuminance, b.MainBodyMeanLuminance) * 2 +
        Distance(a.CoreBodyLuminanceRatio, b.CoreBodyLuminanceRatio) +
        Distance(a.OuterBodyLuminanceRatio, b.OuterBodyLuminanceRatio) +
        Distance(a.LavenderHueFraction, b.LavenderHueFraction) +
        Distance(a.CyanHueFraction, b.CyanHueFraction);

    private static double ScoreStars(SettlementGalaxyPassMetrics a, SettlementGalaxyPassMetrics b)
    {
        var rejection = a.LargestNearWhiteRegion > Math.Max(64, b.LargestNearWhiteRegion * 1.75) ? 100 : 0;
        return rejection + Distance(a.HighLuminance80Fraction, b.HighLuminance80Fraction) * 2 +
            Distance(a.HighLuminance90Fraction, b.HighLuminance90Fraction) * 2 +
            Distance(a.LuminanceP95, b.LuminanceP95) + Distance(a.LuminanceP99, b.LuminanceP99) +
            Distance(a.LuminanceP997, b.LuminanceP997) +
            Distance(a.RareBrightStarCount, b.RareBrightStarCount) +
            Distance(a.MedianBrightFootprint, b.MedianBrightFootprint);
    }

    private static double ScoreChroma(SettlementGalaxyPassMetrics a, SettlementGalaxyPassMetrics b) =>
        Distance(a.BlurredChromaMean, b.BlurredChromaMean) * 2 +
        Distance(a.LavenderHueFraction, b.LavenderHueFraction) * 2 +
        Distance(a.CyanHueFraction, b.CyanHueFraction) * 2;

    private static double ScoreEnvelope(SettlementGalaxyPassMetrics a, SettlementGalaxyPassMetrics b) =>
        ScoreBroadBody(a, b) + Distance(a.PositiveLightCoverage, b.PositiveLightCoverage) * 2 +
        Distance(a.HighLuminance90Fraction, b.HighLuminance90Fraction);

    private static double ScoreTone(SettlementGalaxyPassMetrics a, SettlementGalaxyPassMetrics b) =>
        Distance(a.LuminanceP95, b.LuminanceP95) + Distance(a.LuminanceP99, b.LuminanceP99) * 2 +
        Distance(a.LuminanceP997, b.LuminanceP997) * 2 +
        Distance(a.HighLuminance90Fraction, b.HighLuminance90Fraction) +
        Distance(a.BlurredChromaMean, b.BlurredChromaMean);

    private static double Distance(double actual, double target) =>
        Math.Abs(Math.Log((Math.Max(0, actual) + 1e-4) / (Math.Max(0, target) + 1e-4)));

    private static SKBitmap EnsureBgra(SKBitmap source)
    {
        if (source.ColorType == SKColorType.Bgra8888) return source.Copy();
        var output = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        canvas.DrawBitmap(source, 0, 0);
        return output;
    }

    private static SKBitmap Crop(SKBitmap source, SKRectI crop)
    {
        var output = new SKBitmap(crop.Width, crop.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        canvas.DrawBitmap(source, crop, new SKRect(0, 0, crop.Width, crop.Height));
        return output;
    }

    private static SKBitmap Black(int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.Black);
        return bitmap;
    }

    private sealed record CalibrationInput(Guid LocationId, WebMercatorViewport Viewport, byte[] Source,
        MapFeatureDataDocument Features, Noctaxis.Core.Environment.SettlementRaster Settlement);
    private sealed record CalibrationReferences(SettlementGalaxyPassMetrics[] BroadBody,
        SettlementGalaxyPassMetrics[] StarHierarchy, SettlementGalaxyPassMetrics[] FinalHierarchy);
}
