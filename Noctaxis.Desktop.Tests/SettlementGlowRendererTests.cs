using Noctaxis.Desktop.Services;
using Noctaxis.Core.Environment;
using SkiaSharp;
using System.Runtime.InteropServices;

namespace Noctaxis.Desktop.Tests;

public sealed class SettlementGlowRendererTests
{
    [Fact]
    public void OpenCvNativeBackend_IsAvailableWithOptimizedCpuDispatch()
    {
        var status = new OpenCvMapImageAcceleration().Status;

        // The slim native package intentionally targets the application's primary Windows x64 build.
        // Other architectures remain supported by the deterministic managed fallback.
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        Assert.True(status.NativeAvailable, status.FailureReason);
        Assert.True(status.CpuOptimisationsEnabled);
        Assert.True(status.NativeThreadCount > 0);
        Assert.Contains("OpenCV native", status.Backend, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenCvKernels_MatchManagedReferenceWithinFloatingPointTolerance()
    {
        const int width = 96;
        const int height = 72;
        var source = new float[width * height];
        for (var index = 0; index < source.Length; index++)
            source[index] = (float)((index * 37 % 101) / 100d);
        var native = new OpenCvMapImageAcceleration();
        var managed = new ManagedMapImageAcceleration();

        var nativeBlur = native.GaussianBlur(source, width, height, 7.5);
        var managedBlur = managed.GaussianBlur(source, width, height, 7.5);
        var nativeMaximum = native.MaximumFilter(source, width, height, 31);
        var managedMaximum = managed.MaximumFilter(source, width, height, 31);

        Assert.Equal(source.Length, nativeBlur.Length);
        Assert.True(nativeBlur.Zip(managedBlur, (left, right) => Math.Abs(left - right)).Max() < 2e-6);
        Assert.Equal(managedMaximum, nativeMaximum);
    }

    [Fact]
    public void DisabledOrUnavailableNativeBackend_UsesDeterministicManagedFallback()
    {
        const int width = 32;
        const int height = 24;
        var source = Enumerable.Range(0, width * height).Select(index => (float)(index % 17) / 17).ToArray();
        var disabled = new OpenCvMapImageAcceleration(disableNative: true);
        var managed = new ManagedMapImageAcceleration();

        Assert.False(disabled.Status.NativeAvailable);
        Assert.Equal(managed.GaussianBlur(source, width, height, 3.25),
            disabled.GaussianBlur(source, width, height, 3.25));
        Assert.Equal(managed.MaximumFilter(source, width, height, 9),
            disabled.MaximumFilter(source, width, height, 9));
    }

    [Fact]
    public void WsfFractionDrivesDensityAndHeightInfluenceIsBounded()
    {
        var lowFraction = SettlementDensityBuilder.SettlementMass(.2f, 0);
        var highFraction = SettlementDensityBuilder.SettlementMass(.8f, 0);
        var tall = SettlementDensityBuilder.SettlementMass(.2f, 1_000);

        Assert.True(highFraction >= lowFraction * 3.9f);
        Assert.InRange(tall / lowFraction, 1, 1.3001f);
    }

    [Fact]
    public void WsfDensityAndSyntheticStars_AreDeterministic()
    {
        var settlement = WsfSettlement();
        var builder = new SettlementDensityBuilder();
        var firstDensity = builder.Build(settlement);
        var secondDensity = builder.Build(settlement);
        var stars = new SettlementStarGenerator();

        Assert.Equal(firstDensity.Density, secondDensity.Density);
        Assert.Equal(stars.Generate(settlement, 512, 280), stars.Generate(settlement, 512, 280));
        Assert.True(firstDensity.HasPrimaryComponent);
        Assert.NotEmpty(stars.Generate(settlement, 512, 280));
        Assert.True(stars.Generate(settlement, 512, 280).Count <= SettlementGalaxyStyle.DefaultV1.Stars.MaxSettlementStars);
    }

    [Fact]
    public void MissingWsfData_DegradesWithoutGlowFailure()
    {
        var processor = new SavedLocationMapImageProcessor();
        var rendered = processor.ProcessSettlement(DetailedSource(), null, null, Viewport(), out var diagnostics);
        Assert.NotEmpty(rendered);
        Assert.Null(diagnostics);
    }

    [Fact]
    public void SavedSettlementDerivative_RoundTripsAndRejectsCorruption()
    {
        var source = WsfSettlement();
        var bytes = SettlementRasterCodec.Encode(source);
        var decoded = SettlementRasterCodec.Decode(bytes);

        Assert.Equal(source.Grid, decoded.Grid);
        Assert.Equal(source.BuildingFraction, decoded.BuildingFraction);
        Assert.Equal(source.BuildingHeightMetres, decoded.BuildingHeightMetres);
        Assert.ThrowsAny<InvalidDataException>(() => SettlementRasterCodec.Decode([1, 2, 3]));
    }

    [Fact]
    public void SavedSettlementDerivative_RejectsLegacyRawHeightSchema()
    {
        var bytes = SettlementRasterCodec.Encode(WsfSettlement());
        using var encoded = new MemoryStream(bytes);
        using var gzip = new System.IO.Compression.GZipStream(encoded,
            System.IO.Compression.CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        gzip.CopyTo(decoded);
        var payload = decoded.ToArray();
        "NXWSF1"u8.CopyTo(payload);
        BitConverter.TryWriteBytes(payload.AsSpan(6, sizeof(int)), 1);
        using var legacy = new MemoryStream();
        using (var writer = new System.IO.Compression.GZipStream(legacy,
                   System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            writer.Write(payload);

        Assert.Throws<InvalidDataException>(() => SettlementRasterCodec.Decode(legacy.ToArray()));
    }

    [Fact]
    public void ComponentSelection_UsesOnlyFourConnectedComponentContainingPin()
    {
        const int width = 15;
        const int height = 9;
        var mask = new bool[width * height];
        mask[4 * width + 7] = true;
        mask[4 * width + 8] = true;
        mask[5 * width + 8] = true;
        mask[2 * width + 1] = true;
        mask[2 * width + 2] = true;

        var selected = SettlementDensityBuilder.SelectPinComponent(mask, width, height, out var count);

        Assert.Equal(3, count);
        Assert.True(selected[4 * width + 7]);
        Assert.True(selected[5 * width + 8]);
        Assert.False(selected[2 * width + 1]);
    }

    [Fact]
    public void EmptyAndSparseWsfData_DoNotFail()
    {
        var builder = new SettlementDensityBuilder();
        var emptySource = WsfSettlement() with
        {
            BuildingFraction = new float[128 * 96], BuildingHeightMetres = new float[128 * 96]
        };
        var sparseFractions = new float[128 * 96];
        sparseFractions[48 * 128 + 64] = .8f;
        var empty = builder.Build(emptySource);
        var sparse = builder.Build(emptySource with { BuildingFraction = sparseFractions });
        var calculator = new SettlementGlowGeometryCalculator();

        Assert.False(empty.HasPrimaryComponent);
        Assert.Null(calculator.Calculate(empty, 512, 280));
        Assert.True(sparse.HasPrimaryComponent);
        Assert.NotNull(calculator.Calculate(sparse, 512, 280));
    }

    [Fact]
    public void IdenticalInput_ProducesByteIdenticalThumbnail()
    {
        var viewport = Viewport();
        var settlement = WsfSettlement();
        var processor = new SavedLocationMapImageProcessor();
        var source = DetailedSource();

        var first = processor.ProcessSettlement(source, null, settlement, viewport, out var firstDiagnostics);
        var second = processor.ProcessSettlement(source, null, settlement, viewport, out var secondDiagnostics);

        Assert.Equal(firstDiagnostics, secondDiagnostics);
        Assert.Equal(first, second);
        Assert.NotNull(firstDiagnostics);
        Assert.True(firstDiagnostics.SettlementRendered);
        Assert.True(firstDiagnostics.ActiveSettlementCellCount > 0);
        Assert.True(firstDiagnostics.GeneratedStarCount > 0);
        Assert.True(firstDiagnostics.PrimaryComponentSelected);
        Assert.InRange(firstDiagnostics.SubCoreCount, 0, 5);
    }

    [Fact]
    public void DebugMode_WritesEverySelectedPassAndMathematicalDiagnostic_OnlyWhenRequested()
    {
        var requestedOutput = Environment.GetEnvironmentVariable("NOCTAXIS_GALAXY_DEBUG_DIR");
        var outputDirectory = string.IsNullOrWhiteSpace(requestedOutput)
            ? Path.Combine(Path.GetTempPath(), "noctaxis-galaxy-debug-" + Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(requestedOutput);
        try
        {
            var processor = new SavedLocationMapImageProcessor();
            _ = processor.ProcessSettlementDebug(DetailedSource(), null, WsfSettlement(), Viewport(),
                Guid.Parse("d8935e64-b696-4d06-a2a0-1c1b904e3502"), outputDirectory, out var diagnostics);

            Assert.True(diagnostics?.SettlementRendered == true);
            var expected = new[]
            {
                "01-hierarchy.png", "02-colour-zoning.png", "03-luminosity.png",
                "04-core-radiance.png", "05-clouds.png", "06-wisps.png", "07-stars.png",
                "08-star-chroma.png", "09-satellites.png", "10-ambience.png",
                "11-map-integration.png", "12-falloff.png", "13-tonemapping.png",
                "density.png", "broad-density.png", "component-map.png", "core-mask.png",
                "cloud-field.png", "star-impulses.png", "outer-falloff.png", "metrics.json"
            };
            Assert.All(expected, name => Assert.True(File.Exists(Path.Combine(outputDirectory, name)), name));
            var metrics = System.Text.Json.JsonSerializer.Deserialize<SettlementGalaxyPassMetrics[]>(
                File.ReadAllText(Path.Combine(outputDirectory, "metrics.json")));
            Assert.NotNull(metrics);
            Assert.Equal(13, metrics.Length);
            Assert.Equal("01-hierarchy.png", metrics[0].Pass);
            Assert.Equal("13-tonemapping.png", metrics[^1].Pass);
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(requestedOutput) && Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void GlowCompositor_IsLightOnlyAndPreservesAlpha()
    {
        using var bitmap = new SKBitmap(40, 24, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(new SKColor(37, 61, 89, 173));
        var before = Enumerable.Range(0, bitmap.Width * bitmap.Height)
            .Select(index => bitmap.GetPixel(index % bitmap.Width, index / bitmap.Width)).ToArray();
        var settlement = WsfSettlement();
        var style = SettlementGalaxyStyle.DefaultV1;
        var density = new SettlementDensityBuilder().Build(settlement, style);
        var geometry = new SettlementGlowGeometryCalculator().Calculate(density, bitmap.Width, bitmap.Height,
            style)!;
        var context = new SettlementGalaxyRenderContext(Guid.Empty, Viewport());
        var stars = new SettlementStarGenerator().Generate(settlement, bitmap.Width, bitmap.Height, context, style);

        new SettlementGlowCompositor().CompositeAstronomicalLayers(bitmap, density, geometry, stars, context, style);

        for (var index = 0; index < before.Length; index++)
        {
            var after = bitmap.GetPixel(index % bitmap.Width, index / bitmap.Width);
            Assert.True(after.Red >= before[index].Red);
            Assert.True(after.Green >= before[index].Green);
            Assert.True(after.Blue >= before[index].Blue);
            Assert.Equal(before[index].Alpha, after.Alpha);
        }
    }

    [Fact]
    public void RoadsAndWaterways_AreRedrawnAfterContinuousGlow_WithWaterLast()
    {
        var viewport = Viewport();
        var crossing = Line(viewport, 80, 105, viewport.Width - 80, 105);
        var features = Features(viewport,
            [new MapRoadFeature(1, "way", MapRoadClassification.ARoad, crossing,
                "primary", "A1", null, null, null, null)],
            [new MapWaterwayFeature(2, "way", MapWaterwayClassification.River, crossing,
                "river", null, null, null, null)]);
        var processor = new SavedLocationMapImageProcessor();

        using var rendered = SKBitmap.Decode(processor.ProcessSettlement(DetailedSource(), features,
            WsfSettlement(), viewport, out var diagnostics));

        Assert.NotNull(diagnostics);
        Assert.True(diagnostics.PrimaryComponentSelected);
        var sample = rendered.GetPixel(380, 58);
        Assert.True(sample.Blue > sample.Red && sample.Green > sample.Red,
            $"Expected last-drawn cyan water over the road/glow, got {sample}.");
        Assert.True(CountPixels(rendered, IsCyan) > 20);
    }

    [Fact]
    public void CurrentLocationPin_IsDrawnAfterGlowAndRemainsUnmodified()
    {
        var viewport = Viewport();
        var processor = new SavedLocationMapImageProcessor();
        var source = DetailedSource();
        using var withoutGlow = SKBitmap.Decode(processor.ProcessSettlement(source, null, null, viewport, out _));
        using var withGlow = SKBitmap.Decode(processor.ProcessSettlement(source, null,
            WsfSettlement(), viewport, out _));

        for (var y = 132; y <= 142; y++)
        for (var x = 251; x <= 261; x++)
            Assert.Equal(withoutGlow.GetPixel(x, y), withGlow.GetPixel(x, y));
    }

    [Fact]
    public void V1Preset_HasLockedReferenceHash_AndSettingChangeChangesIdentity()
    {
        var style = SettlementGalaxyStyle.DefaultV1;
        var changed = style with { OuterFalloff = style.OuterFalloff with { Gain = style.OuterFalloff.Gain + .001 } };

        Assert.Equal(1, style.StyleVersion);
        Assert.Equal("9206d937e60308b1bb280b24069bf2177e6741ef3dcda0f28d13476825fd106d",
            style.SettingsHash);
        Assert.Equal(SettlementGalaxyStyle.CanonicalV1Hash, style.SettingsHash);
        Assert.NotEqual(style.SettingsHash, changed.SettingsHash);
    }

    [Fact]
    public void FinalPassPreset_ExposesTheLockedPassesAndDoesNotUseLegacyGlowIdentity()
    {
        var style = SettlementGalaxyStyle.DefaultV1;

        Assert.Equal("settlement-galaxy-passes-1-14", SavedLocationMapImageProcessor.RendererId);
        Assert.Equal("B_underlay_balanced", style.Clouds.WhirlpoolHazePreset);
        Assert.Equal("B_obvious_fibres", style.Wisps.Preset);
        Assert.Equal(34, style.Wisps.Count);
        Assert.Equal(60, style.BackgroundAmbience.BackgroundStarCount);
        Assert.Equal([88, 112, 222], style.Galaxy.ColourZoning.Outer);
        Assert.Equal([255, 247, 236], style.Galaxy.ColourZoning.Core);
        Assert.Equal(.58, style.Stars.ColourVariation.StarChroma, 8);
        Assert.Equal(.49, style.Stars.ColourVariation.BridgeChroma, 8);
        Assert.Equal(.40, style.Stars.ColourVariation.HazeChroma, 8);
        Assert.Equal(1600, style.Stars.MaxSettlementStars);
        Assert.Equal(.1625, style.Stars.TargetSettlementStarDensity, 8);
        Assert.Equal(72, style.OuterFalloff.OuterHaloRadius, 8);
        Assert.Equal(.145, style.OuterFalloff.MinimumOpacity, 8);
        Assert.Equal(.40, style.OuterFalloff.FalloffGamma, 8);
        Assert.Equal(.205, style.OuterFalloff.Gain, 8);
    }

    [Fact]
    public void Sha256ObjectSeed_IsStableAndRespondsToViewportOrObjectIdentity()
    {
        var context = new SettlementGalaxyRenderContext(Guid.Parse("36b6db74-5af0-4cc0-b971-52501f804394"), Viewport());
        var style = SettlementGalaxyStyle.DefaultV1;
        var first = SettlementGalaxyDeterminism.DeriveSeed("wsf:v02:10:20", context, style);

        Assert.Equal(first, SettlementGalaxyDeterminism.DeriveSeed("wsf:v02:10:20", context, style));
        Assert.NotEqual(first, SettlementGalaxyDeterminism.DeriveSeed("wsf:v02:10:21", context, style));
        Assert.NotEqual(first, SettlementGalaxyDeterminism.DeriveSeed("wsf:v02:10:20",
            context with { Viewport = new WebMercatorViewport(14, 256, 896, 504, 53.61, -.43) }, style));
    }

    [Fact]
    public void ComponentRanking_AssignsExactlyThreeSatellites_AndKeepsMinorGroupsNonHeroic()
    {
        const int width = 220;
        const int height = 140;
        var fractions = new float[width * height];
        var heights = new float[fractions.Length];
        Fill(102, 62, 118, 78, .92f);
        Fill(15, 15, 32, 30, .72f);
        Fill(178, 14, 195, 31, .66f);
        Fill(18, 105, 34, 121, .60f);
        Fill(180, 104, 196, 120, .54f);
        var settlement = new SettlementRaster("test-wsf", "v02",
            new GeoRasterRequest(new GeoBounds(53.5, -.6, 53.7, -.2), width, height,
                GeoRasterProjection.WebMercator), fractions, heights);

        var density = new SettlementDensityBuilder().Build(settlement);
        var geometry = new SettlementGlowGeometryCalculator().Calculate(density, 512, 280)!;

        Assert.True(density.HasPrimaryComponent);
        Assert.Equal(3, density.SatelliteComponentLabels.Length);
        Assert.Equal(3, geometry.Satellites.Length);
        Assert.NotEmpty(density.MinorComponentLabels);
        Assert.NotEmpty(geometry.MinorComponents);
        Assert.DoesNotContain(geometry.MinorComponents, minor =>
            geometry.Satellites.Any(satellite => satellite.Label == minor.Label));
        return;

        void Fill(int left, int top, int right, int bottom, float value)
        {
            for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++) fractions[y * width + x] = value;
        }
    }

    [Fact]
    public void ComponentRanking_ExcludesMicroscopicComponentsBelowMeaningfulThreshold()
    {
        const int width = 240;
        const int height = 140;
        var fractions = new float[width * height];
        var heights = new float[fractions.Length];
        Fill(90, 45, 145, 95, .92f);
        Fill(20, 20, 42, 42, .72f);
        Fill(width - 5, height - 5, width - 3, height - 3, .07f);
        var style = SettlementGalaxyStyle.DefaultV1 with
        {
            Density = SettlementGalaxyStyle.DefaultV1.Density with { GaussianSigma = .01 },
            Satellites = SettlementGalaxyStyle.DefaultV1.Satellites with
            {
                ComponentThreshold = .06,
                MinimumMeaningfulStrengthFraction = .05
            }
        };
        var settlement = new SettlementRaster("test-wsf", "v02",
            new GeoRasterRequest(new GeoBounds(53.5, -.6, 53.7, -.2), width, height,
                GeoRasterProjection.WebMercator), fractions, heights);

        var density = new SettlementDensityBuilder(new ManagedMapImageAcceleration())
            .Build(settlement, width, height, style);
        var microscopic = density.Components
            .Where(component => component.Label != density.MainComponentLabel)
            .OrderBy(component => component.Strength)
            .First();

        Assert.DoesNotContain(microscopic.Label, density.SatelliteComponentLabels);
        Assert.DoesNotContain(microscopic.Label, density.MinorComponentLabels);
        return;

        void Fill(int left, int top, int right, int bottom, float value)
        {
            for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++) fractions[y * width + x] = value;
        }
    }

    [Fact]
    public void MainComponent_FallsBackToNearestSettlementWhenPinIsOutsideAllComponents()
    {
        const int width = 100;
        const int height = 70;
        var mask = new bool[width * height];
        for (var y = 20; y < 28; y++) for (var x = 60; x < 68; x++) mask[y * width + x] = true;
        for (var y = 3; y < 10; y++) for (var x = 3; x < 10; x++) mask[y * width + x] = true;
        var values = mask.Select(value => value ? 1f : 0f).ToArray();
        var (labels, components) = SettlementDensityBuilder.LabelComponents(mask, values, width, height,
            SettlementGalaxyStyle.DefaultV1.Satellites);
        var selected = SettlementDensityBuilder.SelectMainComponentLabel(labels, components, width, height);

        Assert.Equal(labels[23 * width + 63], selected);
    }

    [Fact]
    public void AstronomicalStagesPreserveOrAddLuminance_AndTonemappingPreservesAlpha()
    {
        var settlement = WsfSettlement();
        var style = SettlementGalaxyStyle.DefaultV1;
        var density = new SettlementDensityBuilder().Build(settlement, style);
        var geometry = new SettlementGlowGeometryCalculator().Calculate(density, 96, 54, style)!;
        var context = new SettlementGalaxyRenderContext(Guid.Empty, Viewport());
        var stars = new SettlementStarGenerator().Generate(settlement, 96, 54, context, style);
        using var bitmap = new SKBitmap(96, 54, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(new SKColor(29, 47, 71, 211));
        var before = Enumerable.Range(0, bitmap.Width * bitmap.Height)
            .Select(index => bitmap.GetPixel(index % bitmap.Width, index / bitmap.Width)).ToArray();
        var compositor = new SettlementGlowCompositor();

        compositor.CompositeAstronomicalLayers(bitmap, density, geometry, stars, context, style);
        for (var index = 0; index < before.Length; index++)
        {
            var afterGalaxy = bitmap.GetPixel(index % bitmap.Width, index / bitmap.Width);
            Assert.True(Luminance(afterGalaxy) + 1 >= Luminance(before[index]));
            Assert.Equal(before[index].Alpha, afterGalaxy.Alpha);
        }
        compositor.ApplyTonemapping(bitmap, style);

        for (var index = 0; index < before.Length; index++)
        {
            var after = bitmap.GetPixel(index % bitmap.Width, index / bitmap.Width);
            Assert.Equal(before[index].Alpha, after.Alpha);
        }
    }

    [Fact]
    public void Pass12OuterFalloff_HasContinuousPositiveDensityTail()
    {
        const int width = 160;
        const int height = 90;
        var density = new float[width * height];
        density[height / 2 * width + width / 2] = 1;

        var fields = new SettlementGlowCompositor(new ManagedMapImageAcceleration())
            .BuildOuterFalloffFields(density, width, height, SettlementGalaxyStyle.DefaultV1);

        var centre = fields.Falloff[height / 2 * width + width / 2];
        var near = fields.Falloff[height / 2 * width + width / 2 + 35];
        var far = fields.Falloff[height / 2 * width + width / 2 + 70];
        Assert.True(centre > 0 && near > 0 && far > 0);
        Assert.True(Math.Abs(near - far) < .25, "The broad tail must not end in a threshold cliff.");
        Assert.All(fields.Falloff, value => Assert.True(value >= 0));
    }

    [Fact]
    public void Pass13HighlightShoulderCompresses_AndLocalValleysAreNeverSubtracted()
    {
        var tone = SettlementGalaxyStyle.DefaultV1.Tonemapping;

        Assert.True(SettlementGlowCompositor.CompressHighlight(.90, tone) < .90);
        Assert.Equal(.35, SettlementGlowCompositor.ApplyPositiveLocalContrast(.35, .60, tone), 10);
        Assert.True(SettlementGlowCompositor.ApplyPositiveLocalContrast(.70, .50, tone) >= .70);
    }

    [Fact]
    public void ProductionLayerOrder_PutsRoadsThenWaterAboveGalaxy_AndPinAbsolutelyLast()
    {
        var order = SettlementGlowCompositor.ProductionLayerOrder;
        var stages = order.ToArray();
        var galaxy = Array.IndexOf(stages, SettlementGalaxyStage.SatelliteTreatment);
        var roads = Array.IndexOf(stages, SettlementGalaxyStage.Roads);
        var water = Array.IndexOf(stages, SettlementGalaxyStage.Water);
        var tone = Array.IndexOf(stages, SettlementGalaxyStage.Tonemapping);
        var pin = Array.IndexOf(stages, SettlementGalaxyStage.Pin);

        Assert.True(galaxy < roads && roads < water && water < tone && tone < pin);
    }

    [Fact]
    public void EmptySettlementAndEdgeComponents_RenderSafely()
    {
        var empty = WsfSettlement() with
        {
            BuildingFraction = new float[128 * 96], BuildingHeightMetres = new float[128 * 96]
        };
        var edge = (float[])empty.BuildingFraction.Clone();
        for (var y = 0; y < 12; y++) for (var x = 0; x < 18; x++) edge[y * 128 + x] = .85f;
        var processor = new SavedLocationMapImageProcessor();

        Assert.NotEmpty(processor.ProcessSettlement(DetailedSource(), null, empty, Viewport(), out var emptyDiagnostics));
        Assert.NotNull(emptyDiagnostics);
        Assert.False(emptyDiagnostics.SettlementRendered);
        Assert.False(emptyDiagnostics.PrimaryComponentSelected);
        Assert.NotEmpty(processor.ProcessSettlement(DetailedSource(), null,
            empty with { BuildingFraction = edge }, Viewport(), out _));
    }

    [Fact]
    public void PositivePercentileNormalisation_IgnoresEmptyRasterCells()
    {
        var source = new float[10_000];
        source[100] = .25f;
        source[200] = .50f;
        source[300] = 1f;

        var normalised = SettlementDensityBuilder.Normalise(source, 99.65);

        Assert.Equal(0, normalised[0]);
        Assert.InRange(normalised[100], .24f, .26f);
        Assert.InRange(normalised[200], .49f, .51f);
        Assert.Equal(1, normalised[300]);
    }

    [Fact]
    public void FeatureEnumerationOrder_DoesNotChangeRenderedPixels()
    {
        var viewport = Viewport();
        var roadA = new MapRoadFeature(22, "way", MapRoadClassification.ARoad,
            Line(viewport, 70, 90, 810, 120), "primary", "A22", null, null, null, null);
        var roadB = new MapRoadFeature(11, "way", MapRoadClassification.BRoad,
            Line(viewport, 90, 340, 790, 250), "secondary", "B11", null, null, null, null);
        var waterA = new MapWaterwayFeature(44, "way", MapWaterwayClassification.River,
            Line(viewport, 120, 70, 760, 380), "river", null, null, null, null);
        var waterB = new MapWaterwayFeature(33, "way", MapWaterwayClassification.Stream,
            Line(viewport, 100, 400, 800, 180), "stream", null, null, null, null);
        var processor = new SavedLocationMapImageProcessor();

        var first = processor.ProcessSettlement(DetailedSource(),
            Features(viewport, [roadA, roadB], [waterA, waterB]), WsfSettlement(), viewport, out _);
        var reversed = processor.ProcessSettlement(DetailedSource(),
            Features(viewport, [roadB, roadA], [waterB, waterA]), WsfSettlement(), viewport, out _);

        Assert.Equal(first, reversed);
    }

    private static WebMercatorViewport Viewport() => new(13, 256, 896, 504, 53.61, -0.43);

    private static SettlementRaster WsfSettlement()
    {
        const int width = 128;
        const int height = 96;
        var fractions = new float[width * height];
        var heights = new float[fractions.Length];
        for (var y = height / 2 - 14; y <= height / 2 + 14; y++)
        for (var x = width / 2 - 20; x <= width / 2 + 20; x++)
        {
            fractions[y * width + x] = .7f;
            heights[y * width + x] = 14;
        }
        return new SettlementRaster("test-wsf", "v02",
            new GeoRasterRequest(new GeoBounds(53.5, -.6, 53.7, -.2), width, height,
                GeoRasterProjection.WebMercator), fractions, heights);
    }

    private static MapFeatureDataDocument Features(WebMercatorViewport viewport,
        MapRoadFeature[] roads, MapWaterwayFeature[] waterways) => new(1, Guid.NewGuid(),
        new MapFeatureSourceMetadata("openstreetmap-overpass", "OpenStreetMap",
            "OpenStreetMap contributors", "https://www.openstreetmap.org/copyright", "ODbL",
            "https://opendatacommons.org/licenses/odbl/", "test", 1, DateTimeOffset.UnixEpoch,
            viewport.Bounds), roads, waterways);

    private static MapFeatureCoordinate[] Line(WebMercatorViewport viewport,
        double x1, double y1, double x2, double y2)
    {
        var first = viewport.Unproject(x1, y1);
        var second = viewport.Unproject(x2, y2);
        return [new(first.Latitude, first.Longitude), new(second.Latitude, second.Longitude)];
    }

    private static int CountPixels(SKBitmap bitmap, Func<SKColor, bool> predicate)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            if (predicate(bitmap.GetPixel(x, y))) count++;
        return count;
    }

    private static bool IsCyan(SKColor colour) => colour.Alpha > 45 && colour.Blue > 90 &&
        colour.Blue > colour.Red * 1.2 && colour.Green > colour.Red * 1.2;

    private static double Luminance(SKColor colour) =>
        .299 * colour.Red + .587 * colour.Green + .114 * colour.Blue;

    private static byte[] DetailedSource()
    {
        using var bitmap = new SKBitmap(896, 504);
        using var canvas = new SKCanvas(bitmap);
        using (var terrain = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(bitmap.Width, bitmap.Height),
                [SKColor.Parse("#C9D3BC"), SKColor.Parse("#E2D5BD"), SKColor.Parse("#B8C9B5")],
                [0, .52f, 1], SKShaderTileMode.Clamp)
        })
            canvas.DrawRect(SKRect.Create(bitmap.Width, bitmap.Height), terrain);
        using var grid = new SKPaint { Color = SKColor.Parse("#889887"), StrokeWidth = 2, IsAntialias = true };
        for (var x = 0; x < bitmap.Width; x += 28) canvas.DrawLine(x, 0, x + 180, bitmap.Height, grid);
        for (var y = 0; y < bitmap.Height; y += 32) canvas.DrawLine(0, y, bitmap.Width, y + 110, grid);
        for (var index = 0; index < 24; index++)
        {
            using var detail = new SKPaint
            {
                Color = new SKColor((byte)(70 + index * 6), (byte)(95 + index * 4),
                    (byte)(80 + index * 5)),
                IsAntialias = true
            };
            canvas.DrawCircle(18 + index * 35, 30 + index % 4 * 36, 8, detail);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
