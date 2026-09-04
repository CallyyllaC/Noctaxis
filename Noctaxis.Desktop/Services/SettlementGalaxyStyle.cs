using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noctaxis.Desktop.Services;

public sealed record SettlementGalaxyStyle
{
    public const string EmbeddedResourceName = "Noctaxis.Desktop.Styles.noctaxis_galaxy_style_v1.json";
    public const string CanonicalV1Hash = "9206d937e60308b1bb280b24069bf2177e6741ef3dcda0f28d13476825fd106d";

    public string PresetName { get; init; } = "";
    public int StyleVersion { get; init; }
    public string RendererFamily { get; init; } = "";
    public DeterminismStyle Determinism { get; init; } = new();
    public StarStyle Stars { get; init; } = new();
    public SettlementDensityStyle Density { get; init; } = new();
    public GalaxyStyle Galaxy { get; init; } = new();
    public CloudStyle Clouds { get; init; } = new();
    public WispStyle Wisps { get; init; } = new();
    public SatelliteStyle Satellites { get; init; } = new();
    public BackgroundAmbienceStyle BackgroundAmbience { get; init; } = new();
    public MapIntegrationStyle MapIntegration { get; init; } = new();
    public OuterFalloffStyle OuterFalloff { get; init; } = new();
    public TonemappingStyle Tonemapping { get; init; } = new();
    public string[] LayerOrder { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static SettlementGalaxyStyle DefaultV1 { get; } = LoadDefault();
    private static readonly ConditionalWeakTable<SettlementGalaxyStyle, CachedStyleHash> Hashes = new();

    [JsonIgnore]
    public string SettingsHash => ReferenceEquals(this, DefaultV1) ? CanonicalV1Hash : Hashes.GetValue(this,
        static style => new CachedStyleHash(ComputeSettingsHash(style))).Value;

    private static string ComputeSettingsHash(SettlementGalaxyStyle style) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(style, JsonOptions)));

    private static SettlementGalaxyStyle LoadDefault()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded galaxy style '{EmbeddedResourceName}' was not found.");
        var value = JsonSerializer.Deserialize<SettlementGalaxyStyle>(stream, JsonOptions)
            ?? throw new InvalidDataException("The embedded settlement-galaxy style is empty.");
        if (value.StyleVersion != 1 || value.RendererFamily != "settlement-galaxy")
            throw new InvalidDataException("The embedded settlement-galaxy style identity is invalid.");
        ValidateContract(value);
        var actualHash = ComputeSettingsHash(value);
        if (!string.Equals(actualHash, CanonicalV1Hash, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The embedded settlement-galaxy style hash '{actualHash}' is not the locked V1 hash.");
        return value;
    }

    private static void ValidateContract(SettlementGalaxyStyle value)
    {
        if (string.IsNullOrWhiteSpace(value.Determinism.SeedNamespace) ||
            value.Determinism.SeedInputs.Length == 0 || !value.Determinism.RequireStableOrdering ||
            !value.Determinism.RequireStableHash || !value.Determinism.ForbidRuntimeRandom ||
            !value.Determinism.ForbidTimeBasedSeeds || !value.Determinism.ForbidProcessHashCodes)
            throw new InvalidDataException("The settlement-galaxy determinism contract is incomplete.");
        if (!value.Clouds.UnderlayEnabled || !value.Clouds.PreserveBrightStarsAbove ||
            !value.Wisps.Deterministic || !value.Wisps.BrightOnly ||
            !value.MapIntegration.RoadsAboveGalaxy || !value.MapIntegration.WaterAboveGalaxy ||
            !value.MapIntegration.PinAlwaysLast || !value.OuterFalloff.PositiveLightOnly)
            throw new InvalidDataException("The selected positive-light layer-order contract is invalid.");
        if (value.Density.WorkingScale != SettlementDensityBuilder.Supersampling ||
            value.Stars.ColourVariation.Families.Length == 0)
            throw new InvalidDataException("The selected density/star style is incomplete.");
        string[] selectedOrder = ["mapBackground", "galaxyBodyAndHierarchy", "galaxyColourZoning",
            "galaxyLuminance", "coreRadiance", "whirlpoolCloudUnderlay", "emissionWisps",
            "settlementStars", "starColourAura", "satelliteTreatment", "backgroundAmbience",
            "mapIntegration", "outerFalloff", "roads", "water", "finalTonemapping", "pin"];
        if (!value.LayerOrder.SequenceEqual(selectedOrder, StringComparer.Ordinal))
            throw new InvalidDataException("The embedded settlement-galaxy layer order is invalid.");
    }

    private sealed record CachedStyleHash(string Value);

}

public sealed record DeterminismStyle
{
    public string SeedNamespace { get; init; } = "";
    public string[] SeedInputs { get; init; } = [];
    public bool RequireStableOrdering { get; init; }
    public bool RequireStableHash { get; init; }
    public bool ForbidRuntimeRandom { get; init; }
    public bool ForbidTimeBasedSeeds { get; init; }
    public bool ForbidProcessHashCodes { get; init; }
}

public sealed record StarStyle
{
    public double SizeMinPercent { get; init; }
    public double SizeMaxPercent { get; init; }
    public double BaseRadius { get; init; }
    public double TargetSettlementStarDensity { get; init; }
    public int MaxSettlementStars { get; init; }
    public double CoreSigma { get; init; }
    public double NormalisePercentile { get; init; }
    public double MinimumImpulseRadius { get; init; }
    public int[] NeutralColour { get; init; } = [255,255,255];
    public StarClassThresholds ClassThresholds { get; init; } = new();
    public StarClassGains ClassGains { get; init; } = new();
    public double BrightBloomGain { get; init; }
    public double BrightBloomRadius { get; init; }
    public StarColourStyle ColourVariation { get; init; } = new();
}

public sealed record SettlementDensityStyle
{
    public int WorkingScale { get; init; }
    public double GaussianSigma { get; init; }
    public double NormalisePercentile { get; init; }
}

public sealed record StarClassThresholds { public double FaintMax { get; init; } public double CommonMax { get; init; } public double MediumMax { get; init; } }
public sealed record StarClassGains { public double Faint { get; init; } public double Common { get; init; } public double Medium { get; init; } public double Bright { get; init; } }
public sealed record StarColourStyle
{
    public double StarChroma { get; init; }
    public double BridgeChroma { get; init; }
    public double HazeChroma { get; init; }
    public double CoreSigma { get; init; }
    public double BridgeSigma { get; init; }
    public double HazeSigma { get; init; }
    public double DensityWeightFloor { get; init; }
    public double DensityWeightExponent { get; init; }
    public StarColourFamily[] Families { get; init; } = [];
}
public sealed record StarColourFamily { public string Name { get; init; } = ""; public double Ceiling { get; init; } public int[] Colour { get; init; } = [255,255,255]; }

public sealed record GalaxyStyle
{
    public GalaxyHierarchyStyle Hierarchy { get; init; } = new();
    public GalaxyColourZoningStyle ColourZoning { get; init; } = new();
    public GalaxyLuminanceStyle Luminance { get; init; } = new();
    public CoreRadianceStyle CoreRadiance { get; init; } = new();
}
public sealed record GalaxyHierarchyStyle
{
    public double HaloGain { get; init; } public double BodyGain { get; init; } public double CoreGain { get; init; }
    public double SubcoreGain { get; init; } public double CoreAxisRatioClamp { get; init; }
    public double BodyAxisRatioClamp { get; init; } public double HaloAxisRatioClamp { get; init; }
    public int MaxSubcores { get; init; }
    public double HaloRadiusScale { get; init; } public double BodyRadiusScale { get; init; }
    public double CoreRadiusScale { get; init; } public double BroadDensitySigma { get; init; }
    public double DensityGateExponent { get; init; } public double HaloDensityFloor { get; init; }
    public double HaloDensityWeight { get; init; } public double BodyDensityExponent { get; init; }
    public double CoreDensityExponent { get; init; } public double SubcoreMinimumStrengthFraction { get; init; }
    public double SubcoreAxisRatioClamp { get; init; } public double SubcoreRadiusScale { get; init; }
    public int[] HaloColour { get; init; } = [255,255,255];
    public int[] BodyColour { get; init; } = [255,255,255];
    public int[] CoreColour { get; init; } = [255,255,255];
}
public sealed record GalaxyColourZoningStyle
{
    public int[] Outer { get; init; } = [255,255,255]; public int[] Body { get; init; } = [255,255,255];
    public int[] Dense { get; init; } = [255,255,255]; public int[] Core { get; init; } = [255,255,255];
    public GalaxyColourZoneThresholds Thresholds { get; init; } = new();
    public double Strength { get; init; }
}
public sealed record GalaxyColourZoneThresholds
{
    public double OuterStart { get; init; } public double OuterFull { get; init; }
    public double OuterFadeStart { get; init; } public double OuterFadeEnd { get; init; }
    public double BodyStart { get; init; } public double BodyFull { get; init; }
    public double BodyFadeStart { get; init; } public double BodyFadeEnd { get; init; }
    public double DenseStart { get; init; } public double DenseFull { get; init; }
    public double DenseFadeStart { get; init; } public double DenseFadeEnd { get; init; }
    public double CoreStart { get; init; } public double CoreFull { get; init; }
}
public sealed record GalaxyLuminanceStyle
{
    public double BroadGamma { get; init; } public double BroadGain { get; init; }
    public double DenseGamma { get; init; } public double DenseGain { get; init; }
    public double KnotGamma { get; init; } public double KnotGain { get; init; }
    public double CoreThreshold { get; init; } public double CoreFull { get; init; }
    public double CoreGamma { get; init; }
    public double CoreGain { get; init; } public double BloomGain { get; init; }
    public double BloomRadius { get; init; } public double HotRadius { get; init; } public double SoftClip { get; init; }
    public double SoftClipSlope { get; init; }
    public int[] LavenderColour { get; init; } = [255,255,255];
    public int[] PaleColour { get; init; } = [255,255,255];
    public int[] WarmColour { get; init; } = [255,255,255];
}
public sealed record CoreRadianceStyle
{
    public double BloomGain { get; init; } public double AuraGain { get; init; } public double HotGain { get; init; }
    public int[] BloomColour { get; init; } = [255,255,255]; public int[] AuraColour { get; init; } = [255,255,255];
    public int[] HotColour { get; init; } = [255,255,255];
    public double MergeDistance { get; init; } public int PeakFilterSize { get; init; }
    public double PeakThreshold { get; init; } public int MaximumPeakCount { get; init; }
    public double PeakMinimumDistance { get; init; } public double BloomRadiusScale { get; init; }
    public double AuraRadiusScale { get; init; } public double HotRadiusScale { get; init; }
    public double BloomAxisRatioClamp { get; init; } public double AuraAxisRatioClamp { get; init; }
    public double HotAxisRatioClamp { get; init; } public double MinimumMajorSigma { get; init; }
    public double MinimumMinorSigma { get; init; }
}
public sealed record CloudStyle
{
    public string WhirlpoolHazePreset { get; init; } = ""; public bool UnderlayEnabled { get; init; }
    public bool PreserveBrightStarsAbove { get; init; } public double SpiralArms { get; init; }
    public double SpiralTwist { get; init; } public double RadialFrequency { get; init; }
    public double NoiseScale { get; init; } public double NoiseMix { get; init; }
    public double StructureFloor { get; init; } public double Gain { get; init; }
    public double SpiralPower { get; init; } public double RadialLogScale { get; init; }
    public double NoiseBlurSigma { get; init; } public double HazeSigma { get; init; }
    public int[] Colour { get; init; } = [255,255,255];
}
public sealed record WispStyle
{
    public string Preset { get; init; } = ""; public int Count { get; init; }
    public double MinLength { get; init; } public double MaxLength { get; init; }
    public double MinWidth { get; init; } public double MaxWidth { get; init; }
    public double MinGain { get; init; } public double MaxGain { get; init; }
    public double MajorAxisInfluence { get; init; } public double GradientTangentInfluence { get; init; }
    public int[] Colour { get; init; } = [255,255,255];
    public bool Deterministic { get; init; } public bool BrightOnly { get; init; }
    public double DensityWeightExponent { get; init; } public double MinimumDensity { get; init; }
    public double AngleJitterSigma { get; init; } public int RasterScale { get; init; }
    public double MaskBlurRadius { get; init; }
}
public sealed record SatelliteRankingStyle { public double IntegratedDensityExponent { get; init; } public double PeakDensityExponent { get; init; } }
public sealed record SatelliteStyle
{
    public double ComponentThreshold { get; init; } public int SatelliteCount { get; init; }
    public SatelliteRankingStyle Ranking { get; init; } = new(); public string Preset { get; init; } = "";
    public double BodyGain { get; init; } public double InnerGain { get; init; } public double CoreGain { get; init; }
    public double HaloGain { get; init; } public double BackgroundComponentGain { get; init; }
    public double PcaRadiusScale { get; init; } public double MinimumMeaningfulStrengthFraction { get; init; }
    public double AxisRatioClamp { get; init; } public double ShapedDensityFloor { get; init; }
    public double ShapedDensityWeight { get; init; } public double InnerEllipseExponent { get; init; }
    public double InnerDensityExponent { get; init; } public double CoreEllipseExponent { get; init; }
    public double CoreDensityExponent { get; init; } public double HaloEllipseExponent { get; init; }
    public double CorePeakThreshold { get; init; } public double StrengthExponent { get; init; }
    public double MinimumStrength { get; init; } public double MinorAxisRatioClamp { get; init; }
    public double MinorRadiusScale { get; init; } public double MinorStrengthExponent { get; init; }
    public double MinorMaximumStrength { get; init; }
    public int[] BodyColour { get; init; } = [255,255,255]; public int[] InnerColour { get; init; } = [255,255,255];
    public int[] CoreColour { get; init; } = [255,255,255]; public int[] HaloColour { get; init; } = [255,255,255];
}
public sealed record BackgroundAmbienceStyle
{
    public string Preset { get; init; } = ""; public double BackgroundLiftGain { get; init; }
    public double BroadHazeGain { get; init; } public int BackgroundStarCount { get; init; }
    public double BackgroundStarGain { get; init; } public int[] LiftColour { get; init; } = [255,255,255];
    public int[] HazeColour { get; init; } = [255,255,255];
    public double NoiseScale { get; init; }
    public double GalaxySuppressionExponent { get; init; } public double HazeBlurSigma { get; init; }
    public double StarAvoidDensity { get; init; } public double StarSigma { get; init; }
    public double RoadAvoidAlpha { get; init; } public double WaterAvoidAlpha { get; init; }
    public double PinAvoidAlpha { get; init; }
    public double StarIntensityMin { get; init; } public double StarIntensityMax { get; init; }
    public int StarAttemptMultiplier { get; init; } public int[] StarColour { get; init; } = [255,255,255];
}
public sealed record MapIntegrationStyle
{
    public string Preset { get; init; } = ""; public double DenseRoadRetention { get; init; }
    public double BodyRoadRetention { get; init; } public double DenseRiverRetention { get; init; }
    public double BackgroundMapContrast { get; init; } public double GalaxyLuminanceLift { get; init; }
    public double CoreLuminanceLift { get; init; } public bool RoadsAboveGalaxy { get; init; }
    public bool WaterAboveGalaxy { get; init; } public bool PinAlwaysLast { get; init; }
    public double BodyStart { get; init; } public double BodyFull { get; init; }
    public double DenseStart { get; init; } public double DenseFull { get; init; }
    public double LocalContrastSigma { get; init; } public double UnderColourSigma { get; init; }
}
public sealed record OuterFalloffStyle
{
    public string Preset { get; init; } = ""; public double OuterHaloRadius { get; init; }
    public double MinimumOpacity { get; init; } public double FalloffGamma { get; init; }
    public double Gain { get; init; } public bool PositiveLightOnly { get; init; }
    public double EnvelopeSigma { get; init; } public double NormalisePercentile { get; init; }
    public double InnerPresenceStart { get; init; } public double InnerPresenceFull { get; init; }
    public double OuterBaseWeight { get; init; } public double OuterAbsenceWeight { get; init; }
    public double MidDensityExponent { get; init; } public double MidGainFactor { get; init; }
    public int[] OuterColour { get; init; } = [255,255,255]; public int[] MidColour { get; init; } = [255,255,255];
}
public sealed record TonemappingStyle
{
    public string Preset { get; init; } = ""; public double HighlightThreshold { get; init; }
    public double HighlightCompression { get; init; } public double LocalPositiveLightContrast { get; init; }
    public double LocalRadius { get; init; } public double GlobalCurveStrength { get; init; }
    public double Saturation { get; init; }
    public double DetailSuppressionStart { get; init; } public double DetailSuppressionEnd { get; init; }
    public double DetailSuppressionAmount { get; init; } public double CurvePivot { get; init; }
    public double ToeStart { get; init; } public double ToeEnd { get; init; }
    public double SaturationStart { get; init; } public double SaturationFull { get; init; }
    public double SaturationFadeStart { get; init; } public double SaturationFadeEnd { get; init; }
}

public readonly record struct SettlementGalaxyRenderContext(Guid LocationId, WebMercatorViewport Viewport)
{
    public static SettlementGalaxyRenderContext Anonymous(WebMercatorViewport viewport) => new(Guid.Empty, viewport);
}

public static class SettlementGalaxyDeterminism
{
    public static ulong DeriveSeed(string stableObjectId, SettlementGalaxyRenderContext context,
        SettlementGalaxyStyle style)
    {
        var viewport = context.Viewport;
        var payload = string.Join('|', style.Determinism.SeedNamespace, stableObjectId,
            context.LocationId.ToString("D", CultureInfo.InvariantCulture),
            viewport.CentreLatitude.ToString("F8", CultureInfo.InvariantCulture),
            viewport.CentreLongitude.ToString("F8", CultureInfo.InvariantCulture),
            viewport.Zoom.ToString(CultureInfo.InvariantCulture),
            viewport.Width.ToString(CultureInfo.InvariantCulture),
            viewport.Height.ToString(CultureInfo.InvariantCulture),
            style.StyleVersion.ToString(CultureInfo.InvariantCulture), style.SettingsHash);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return BitConverter.ToUInt64(digest, 0);
    }

    public static double Unit(ulong seed, int lane = 0)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString("x16", CultureInfo.InvariantCulture) + ":" + lane));
        return BitConverter.ToUInt64(bytes, 0) / (double)ulong.MaxValue;
    }
}
