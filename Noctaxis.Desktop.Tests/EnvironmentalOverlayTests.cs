using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Mapsui;
using Mapsui.Extensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Noctaxis.Core.Calculations;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Terrain;
using Noctaxis.Desktop.Controls;
using NodaTime;
using SkiaSharp;

namespace Noctaxis.Desktop.Tests;

public sealed class EnvironmentalOverlayTests
{
    private static readonly GeoCoordinate Observer = new(53.61, -0.43);

    [Fact]
    public void PanZoomResizeAndRenderingParametersOnlyInvalidateRenderState()
    {
        var diagnostics = new EnvironmentalOverlayDiagnostics();
        var coordinator = new EnvironmentalOverlayStateCoordinator(diagnostics, 32);
        var sector = Sector(0, 40);
        _ = coordinator.Update(Observer, sector, Visibility(sector, null, (0, 100), (40, 200)), ProfileKey());

        var initial = RenderKey();
        Assert.True(coordinator.UpdateRender(initial));
        Assert.True(coordinator.UpdateRender(initial with { WorldOriginX = 500 })); // pan
        Assert.True(coordinator.UpdateRender(initial with { WorldStepXX = .5 })); // zoom
        Assert.True(coordinator.UpdateRender(initial with { Width = 1_600 })); // resize
        Assert.True(coordinator.UpdateRender(initial with
        {
            Parameters = initial.Parameters with { HatchOpacity = .5f }
        }));
        Assert.True(coordinator.UpdateRender(initial with
        {
            Parameters = initial.Parameters with { HatchSpacingPixels = 12 }
        }));
        Assert.True(coordinator.UpdateRender(initial with
        {
            Parameters = initial.Parameters with { HatchThicknessPixels = 4 }
        }));

        Assert.Equal(1, diagnostics.ProfileStateChanges);
        Assert.Equal(1, diagnostics.OverlayStateRebuilds);
        Assert.Equal(7, diagnostics.RenderInvalidations);
    }

    [Fact]
    public void EnvironmentalIdentityChangesInvalidateProfileAndOverlay()
    {
        EnvironmentalProfileKey[] keys =
        [
            ProfileKey(),
            ProfileKey() with { ObserverLatitude = 54 },
            ProfileKey() with { ObserverHeightAboveGroundMetres = 4 },
            ProfileKey() with { TerrainAngularDetailDegrees = 5 },
            ProfileKey() with { TerrainGeneratedAtTicks = 20 },
            ProfileKey() with { ProviderStateFingerprint = 99 }
        ];
        var diagnostics = new EnvironmentalOverlayDiagnostics();
        var coordinator = new EnvironmentalOverlayStateCoordinator(diagnostics, 16);
        var sector = Sector(0, 40);
        foreach (var key in keys)
            _ = coordinator.Update(Observer, sector, Visibility(sector, null, (0, 100), (40, 200)), key);

        Assert.Equal(keys.Length, diagnostics.ProfileStateChanges);
        Assert.Equal(keys.Length, diagnostics.OverlayStateRebuilds);
    }

    [Fact]
    public void HeadingFovAndWeatherRebuildOverlayWithoutChangingTerrainProfileRevision()
    {
        var diagnostics = new EnvironmentalOverlayDiagnostics();
        var coordinator = new EnvironmentalOverlayStateCoordinator(diagnostics, 32);
        var key = ProfileKey();
        var first = Sector(0, 40);
        var second = Sector(45, 40);
        var third = Sector(45, 60);

        _ = coordinator.Update(Observer, first, Visibility(first, null, (0, 100), (40, 200)), key);
        _ = coordinator.Update(Observer, second, Visibility(second, null, (0, 100), (40, 200)), key);
        _ = coordinator.Update(Observer, third, Visibility(third, null, (0, 100), (60, 200)), key);
        _ = coordinator.Update(Observer, third, Visibility(third, 15_000, (0, 100), (60, 200)), key);

        Assert.Equal(1, diagnostics.ProfileStateChanges);
        Assert.Equal(4, diagnostics.OverlayStateRebuilds);
    }

    [Fact]
    public void ObserverChangesRebuildOverlayWithoutReuploadingTerrainTexture()
    {
        var diagnostics = new EnvironmentalOverlayDiagnostics();
        var coordinator = new EnvironmentalOverlayStateCoordinator(diagnostics, 32);
        var key = ProfileKey();
        var initialSector = Sector(0, 40);
        var initial = coordinator.Update(Observer, initialSector,
            Visibility(initialSector, null, (0, 100), (40, 200)), key);

        var elevatedObserver = Observer with { ElevationMetres = 125 };
        var elevatedSector = new GeoSector(elevatedObserver, 0, 40, MapOverlayGeometry.MaximumRangeMetres);
        var elevated = coordinator.Update(elevatedObserver, elevatedSector,
            Visibility(elevatedSector, null, (0, 100), (40, 200)), key);

        var movedObserver = elevatedObserver with { Latitude = elevatedObserver.Latitude + .002 };
        var movedSector = new GeoSector(movedObserver, 0, 40, MapOverlayGeometry.MaximumRangeMetres);
        var moved = coordinator.Update(movedObserver, movedSector,
            Visibility(movedSector, null, (0, 100), (40, 200)), key);

        Assert.Equal(elevatedObserver, elevated.Observer);
        Assert.Equal(movedObserver, moved.Observer);
        Assert.Equal(3, diagnostics.OverlayStateRebuilds);
        Assert.Equal(1, diagnostics.ProfileStateChanges);
        Assert.Equal(initial.TerrainTextureRevision, elevated.TerrainTextureRevision);
        Assert.Equal(initial.TerrainTextureRevision, moved.TerrainTextureRevision);
    }

    [Fact]
    public void OrderedProfileInterpolationHandlesExactMidpointSparseAndNorthWrap()
    {
        var sector = Sector(0, 20); // 350 -> 0 -> 10
        var ordered = EnvironmentalOverlayStateFactory.OrderSamples(sector,
            Visibility(sector, null, (0, 100), (10, 200), (20, 300)));

        Assert.Equal([350d, 360d, 370d], ordered.Select(sample => sample.UnwrappedBearingDegrees).ToArray());
        Assert.Equal(100, EnvironmentalOverlayStateFactory.SampleAtOffset(ordered, 0)
            .ObstructionDistanceMetres);
        Assert.Equal(150, EnvironmentalOverlayStateFactory.SampleAtOffset(ordered, 5)
            .ObstructionDistanceMetres);
        Assert.Equal(200, EnvironmentalOverlayStateFactory.SampleAtOffset(ordered, 10)
            .ObstructionDistanceMetres);
        Assert.Equal(300, EnvironmentalOverlayStateFactory.SampleAtOffset(ordered, 20)
            .ObstructionDistanceMetres);
    }

    [Fact]
    public void ClearStateIsNeverInterpolatedAsMaximumDistance()
    {
        var sector = Sector(0, 20);
        var ordered = EnvironmentalOverlayStateFactory.OrderSamples(sector,
            Visibility(sector, null, (0, 80), (10, null), (20, 120)));

        var clear = EnvironmentalOverlayStateFactory.SampleAtOffset(ordered, 10);
        Assert.False(clear.IsObstructed);
        Assert.Null(clear.ObstructionDistanceMetres);
        Assert.DoesNotContain(EnvironmentalOverlayStateFactory.Resample(ordered, 20, 64),
            texel => texel.IsObstructed && texel.ObstructionDistanceMetres >= MapOverlayGeometry.MaximumRangeMetres);
    }

    [Theory]
    [InlineData(0, 60, true)]
    [InlineData(30, 60, true)]
    [InlineData(-30, 60, true)]
    [InlineData(30.0001, 60, false)]
    [InlineData(-30.0001, 60, false)]
    [InlineData(359, 4, true)]
    [InlineData(1, 4, true)]
    [InlineData(2.0001, 4, false)]
    [InlineData(89.5, 179, true)]
    public void ConeMembershipIncludesBoundariesAndHandlesNorth(double bearing, double fov, bool expected)
    {
        Assert.Equal(expected, EnvironmentalOverlayMath.IsInsideCone(bearing, 0, fov));
    }

    [Fact]
    public void PixelClassificationCoversAllEnvironmentalCombinations()
    {
        var sector = Sector(0, 20);
        var coordinator = new EnvironmentalOverlayStateCoordinator(profileTextureWidth: 32);
        var state = coordinator.Update(Observer, sector,
            Visibility(sector, 200, (0, 100), (20, 100)), ProfileKey());

        Assert.Equal(EnvironmentalPixelClassification.OutsideCone,
            EnvironmentalOverlayMath.Classify(state, 20, 50));
        Assert.Equal(EnvironmentalPixelClassification.Clear,
            EnvironmentalOverlayMath.Classify(state, 0, 50));
        Assert.Equal(EnvironmentalPixelClassification.TerrainObstructed,
            EnvironmentalOverlayMath.Classify(state, 0, 150));
        Assert.Equal(EnvironmentalPixelClassification.TerrainObstructedAndBeyondVisibility,
            EnvironmentalOverlayMath.Classify(state, 0, 250));

        var weatherOnly = coordinator.Update(Observer, sector,
            Visibility(sector, 200, (0, null), (20, null)), ProfileKey());
        Assert.Equal(EnvironmentalPixelClassification.BeyondVisibility,
            EnvironmentalOverlayMath.Classify(weatherOnly, 0, 250));
    }

    [Fact]
    public void WeatherAndTerrainBoundariesComposeWithoutMovingEachOther()
    {
        var sector = Sector(0, 20);
        var coordinator = new EnvironmentalOverlayStateCoordinator(profileTextureWidth: 32);
        var terrainBeforeWeather = coordinator.Update(Observer, sector,
            Visibility(sector, 1_000, (0, 500), (20, 500)), ProfileKey());

        Assert.Equal(EnvironmentalPixelClassification.Clear,
            EnvironmentalOverlayMath.Classify(terrainBeforeWeather, 0, 250));
        Assert.Equal(EnvironmentalPixelClassification.TerrainObstructed,
            EnvironmentalOverlayMath.Classify(terrainBeforeWeather, 0, 750));
        Assert.Equal(EnvironmentalPixelClassification.TerrainObstructedAndBeyondVisibility,
            EnvironmentalOverlayMath.Classify(terrainBeforeWeather, 0, 1_250));

        var weatherBeforeTerrain = coordinator.Update(Observer, sector,
            Visibility(sector, 500, (0, 1_000), (20, 1_000)), ProfileKey());
        Assert.Equal(EnvironmentalPixelClassification.Clear,
            EnvironmentalOverlayMath.Classify(weatherBeforeTerrain, 0, 250));
        Assert.Equal(EnvironmentalPixelClassification.BeyondVisibility,
            EnvironmentalOverlayMath.Classify(weatherBeforeTerrain, 0, 750));
        Assert.Equal(EnvironmentalPixelClassification.TerrainObstructedAndBeyondVisibility,
            EnvironmentalOverlayMath.Classify(weatherBeforeTerrain, 0, 1_250));

        var equal = coordinator.Update(Observer, sector,
            Visibility(sector, 1_000, (0, 1_000), (20, 1_000)), ProfileKey());
        Assert.Equal(EnvironmentalPixelClassification.Clear,
            EnvironmentalOverlayMath.Classify(equal, 0, 999));
        Assert.Equal(EnvironmentalPixelClassification.TerrainObstructedAndBeyondVisibility,
            EnvironmentalOverlayMath.Classify(equal, 0, 1_000));
    }

    [Fact]
    public void ViewportRenderKeysDoNotParticipateInTerrainProfileIdentity()
    {
        var profile = ProfileKey();
        var zoomOne = RenderKey() with { WorldStepXX = 10, WorldStepYY = -10 };
        var zoomTwo = RenderKey() with { WorldStepXX = 2, WorldStepYY = -2 };

        Assert.Equal(profile, profile);
        Assert.NotEqual(zoomOne, zoomTwo);
        Assert.NotEqual(profile, profile with { ObserverHeightAboveGroundMetres = 2.5 });
    }

    [Theory]
    [InlineData(500, 0)]
    [InlineData(5_000, 27)]
    public void FrameUsesMapsuiScreenToWorldTransformWithoutProjectionApproximation(
        double resolution,
        double rotation)
    {
        var observerWorld = WebMercator.FromWgs84(Observer);
        var viewport = new Viewport(observerWorld.X, observerWorld.Y, resolution, rotation, 1_200, 800);
        var frame = EnvironmentalOverlayMath.CreateFrame(
            viewport, 1_200, 800, EnvironmentalRenderParameters.Default);

        foreach (var point in new[] { (X: 0d, Y: 0d), (X: 600d, Y: 400d), (X: 1_199d, Y: 799d) })
        {
            var world = viewport.ScreenToWorld(point.X, point.Y);
            var expected = WebMercator.ToWgs84(world.X, world.Y);
            var actual = EnvironmentalOverlayMath.ScreenToGeographic(frame, point.X, point.Y);
            Assert.Equal(expected.Latitude, actual.Latitude, 4);
            Assert.Equal(expected.Longitude, actual.Longitude, 4);
        }
    }

    [Fact]
    public void RuntimeEffectShaderCompilesWithoutGpuContext()
    {
        Assert.Null(EnvironmentalOverlayRenderer.ValidateShaderSource());
    }

    [AvaloniaTheory]
    [InlineData(50)]
    [InlineData(100)]
    [InlineData(500)]
    public void TerrainHatchPipelineKeepsObserverSideClearAndDrawsBeyondFirstHit(double hitDistance)
    {
        const int logicalWidth = 928;
        const int logicalHeight = 640;
        var resolution = hitDistance / 100;
        var observer = Observer;
        var sector = new GeoSector(observer, 105, 40, MapOverlayGeometry.MaximumRangeMetres);
        var coordinator = new EnvironmentalOverlayStateCoordinator(profileTextureWidth: 32);
        var state = coordinator.Update(observer, sector,
            Visibility(sector, null, (0, hitDistance), (40, hitDistance)), ProfileKey());
        Assert.All(state.ProfileTexels, texel => Assert.True(texel.IsObstructed));
        var world = WebMercator.FromWgs84(observer);
        var viewport = new Viewport(
            world.X + (logicalWidth / 2d - 273) * resolution,
            world.Y + (255 - logicalHeight / 2d) * resolution,
            resolution, 0, logicalWidth, logicalHeight);
        var parameters = EnvironmentalRenderParameters.Default with
        {
            HatchSpacingPixels = 3,
            HatchThicknessPixels = 8
        };
        var frame = EnvironmentalOverlayMath.CreateFrame(
            viewport, logicalWidth, logicalHeight, parameters);
        using var host = new EnvironmentalOverlayTestControl(state, frame, Colors.DeepPink)
        {
            Width = logicalWidth,
            Height = logicalHeight
        };
        host.Measure(new Size(logicalWidth, logicalHeight));
        host.Arrange(new Rect(0, 0, logicalWidth, logicalHeight));
        using var rendered = new RenderTargetBitmap(
            new PixelSize(logicalWidth, logicalHeight), new Vector(96, 96));
        rendered.Render(host);
        using var pixels = new WriteableBitmap(
            new PixelSize(logicalWidth, logicalHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = pixels.Lock();
        rendered.CopyPixels(framebuffer);

        var pin = viewport.WorldToScreen(world.X, world.Y);
        Assert.Equal(273, pin.X, 8);
        Assert.Equal(255, pin.Y, 8);
        var shaderPin = EnvironmentalOverlayMath.GeographicToScreen(frame, observer);
        Assert.Equal(pin.X, shaderPin.X, 8);
        Assert.Equal(pin.Y, shaderPin.Y, 8);
        var renderedPin = new SKPoint((float)pin.X, (float)pin.Y);
        var pixelOffset = (int)Math.Floor(renderedPin.Y) * framebuffer.RowBytes +
                          (int)Math.Floor(renderedPin.X) * 4;
        var observerHatchAlpha = System.Runtime.InteropServices.Marshal.ReadByte(
            framebuffer.Address, pixelOffset + 3);
        var nearest = (X: -1, Y: -1, Distance: double.PositiveInfinity);
        for (var y = 0; y < framebuffer.Size.Height; y++)
        for (var x = 0; x < framebuffer.Size.Width; x++)
        {
            var alpha = System.Runtime.InteropServices.Marshal.ReadByte(
                framebuffer.Address, y * framebuffer.RowBytes + x * 4 + 3);
            if (alpha <= 150) continue;
            var distance = Math.Sqrt(Math.Pow(x - renderedPin.X, 2) + Math.Pow(y - renderedPin.Y, 2));
            if (distance < nearest.Distance) nearest = (x, y, distance);
        }
        Assert.True(observerHatchAlpha < 100,
            $"Observer-side foreground must contain only the base cone; alpha was {observerHatchAlpha}.");
        var nearestCoordinate = EnvironmentalOverlayMath.ScreenToGeographic(frame, nearest.X, nearest.Y);
        var nearestGroundDistance = Angles.GreatCircleDistanceMetres(observer, nearestCoordinate);
        Assert.InRange(nearestGroundDistance, hitDistance * .9, hitDistance * 1.2);
    }

    [AvaloniaTheory]
    [InlineData(5000d, 15000d, 2500d, false, false)]
    [InlineData(5000d, 15000d, 10000d, false, true)]
    [InlineData(5000d, 15000d, 25000d, true, true)]
    [InlineData(15000d, 5000d, 10000d, true, false)]
    [InlineData(15000d, 5000d, 25000d, true, true)]
    [InlineData(15000d, 15000d, 10000d, false, false)]
    [InlineData(15000d, 15000d, 25000d, true, true)]
    [InlineData(null, 20000d, 50000d, true, false)]
    [InlineData(null, null, 250000d, false, false)]
    [InlineData(null, 600000d, 490000d, false, false)]
    public void ShaderInteriorPixels_ComposeWeatherAndTerrainIndependently(double? hit,
        double? weather, double distance, bool grayscale, bool hatched)
    {
        var sector = new GeoSector(Observer, 90, 40, 500000);
        var state = new EnvironmentalOverlayStateCoordinator(profileTextureWidth: 32)
            .Update(Observer, sector, Visibility(sector, weather, (0, hit), (40, hit)), ProfileKey());
        var sample = WebMercator.FromWgs84(Angles.Destination(Observer, 90, distance));
        var viewport = new Viewport(sample.X, sample.Y, 1, 0, 64, 64);
        var parameters = EnvironmentalRenderParameters.Default with
        { HatchSpacingPixels = 3, HatchThicknessPixels = 8 };
        var frame = EnvironmentalOverlayMath.CreateFrame(viewport, 64, 64, parameters);
        using var host = new EnvironmentalOverlayTestControl(state, frame, Colors.DeepPink)
            { Width = 64, Height = 64 };
        host.Measure(new Size(64, 64));
        host.Arrange(new Rect(0, 0, 64, 64));
        using var rendered = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
        rendered.Render(host);
        using var pixels = new WriteableBitmap(new PixelSize(64, 64), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = pixels.Lock();
        rendered.CopyPixels(buffer);
        var offset = 32 * buffer.RowBytes + 32 * 4;
        int Channel(int channel) => System.Runtime.InteropServices.Marshal.ReadByte(buffer.Address, offset + channel);
        Assert.True(Channel(3) > 0);
        Assert.Equal(hatched, Channel(3) > 150);
        // Solid hatch deliberately covers the base. Check base colour separately in clear regions.
        if (!hatched)
        {
            if (grayscale) Assert.InRange(Math.Abs(Channel(2) - Channel(1)), 0, 1);
            else Assert.True(Channel(2) > Channel(1) + 10);
        }
    }

    private sealed class EnvironmentalOverlayTestControl(
        EnvironmentalOverlayState state,
        EnvironmentalOverlayFrame frame,
        Color colour) : Control, IDisposable
    {
        private readonly EnvironmentalOverlayRenderer _renderer = new();

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            _renderer.Draw(context, new Rect(Bounds.Size), state, frame, colour);
        }

        public void Dispose() => _renderer.Dispose();
    }

    [AvaloniaFact]
    public void MapOverlayPipelineKeepsTerrainMaskCentredOnDisplayedPlanningPin()
    {
        const int width = 928;
        const int height = 640;
        const double resolution = 5;
        var observer = Observer;
        var guide = new CameraFramingGuide(105, 70, 70, 140, CameraFramingDirectionSource.PrimaryTarget);
        var sector = new GeoSector(observer, guide.CentreBearingDegrees,
            guide.HorizontalFieldOfViewDegrees, MapOverlayGeometry.MaximumRangeMetres);
        var visibility = Visibility(sector, null, (0, 500), (70, 500));
        var view = new NoctaxisMapView
        {
            Width = width,
            Height = height,
            Observer = observer,
            Snapshot = Snapshot(observer),
            FramingGuide = guide,
            FramingVisibility = visibility,
            FramingSettings = new CameraFramingSettings(
                ShadingOpacityPercent: 10, LineThickness: 1.25, TerrainCastAngularDetailDegrees: 1),
            ShowCameraOverlay = true
        };
        view.MapControlForTesting.Map!.Layers.Clear();
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var observerWorld = WebMercator.FromWgs84(observer);
        var centre = new MPoint(
            observerWorld.X + (width / 2d - 273) * resolution,
            observerWorld.Y + (255 - height / 2d) * resolution);
        view.MapControlForTesting.Map.Navigator.CenterOnAndZoomTo(centre, resolution, 0);
        var viewport = view.MapControlForTesting.Map.Navigator.Viewport;
        var pin = viewport.WorldToScreen(observerWorld.X, observerWorld.Y);
        Assert.Equal(273, pin.X, 1);
        Assert.Equal(255, pin.Y, 1);

        using var rendered = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        rendered.Render(view.OverlayForTesting);
        using var pixels = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = pixels.Lock();
        rendered.CopyPixels(framebuffer);

        var foregroundRadians = (guide.CentreBearingDegrees + 5) * Math.PI / 180;
        var foregroundX = (int)Math.Round(pin.X + 80 * Math.Sin(foregroundRadians));
        var foregroundY = (int)Math.Round(pin.Y - 80 * Math.Cos(foregroundRadians));
        var foregroundAlpha = System.Runtime.InteropServices.Marshal.ReadByte(framebuffer.Address,
            foregroundY * framebuffer.RowBytes + foregroundX * 4 + 3);
        Assert.True(foregroundAlpha < 100,
            $"Observer-side foreground must be unhatched; alpha was {foregroundAlpha}.");

        var coveredBearings = new List<int>();
        for (var bearing = 0; bearing < 360; bearing++)
        {
            var hatchPixels = 0;
            for (var radius = 190; radius <= 240; radius++)
            {
                var radians = bearing * Math.PI / 180;
                var x = (int)Math.Round(pin.X + radius * Math.Sin(radians));
                var y = (int)Math.Round(pin.Y - radius * Math.Cos(radians));
                var offset = y * framebuffer.RowBytes + x * 4;
                var alpha = System.Runtime.InteropServices.Marshal.ReadByte(framebuffer.Address, offset + 3);
                if (alpha > 100) hatchPixels++;
            }
            if (hatchPixels >= 4) coveredBearings.Add(bearing);
        }

        Assert.Contains((int)guide.CentreBearingDegrees, coveredBearings);
        Assert.InRange(coveredBearings.Min(), 68, 72);
        Assert.InRange(coveredBearings.Max(), 138, 142);
        window.Close();
    }

    [AvaloniaFact]
    public void TerrainDebugMapVisualMarksProductionWinningSample()
    {
        const int width = 800;
        const int height = 600;
        const double resolution = 5;
        var observer = Observer;
        var baseSnapshot = Snapshot(observer);
        var sightline = new[]
        {
            TerrainSightlineSample.FromSlope(500, 120, 0, .2)
        };
        var terrain = new TerrainHorizonProfile(observer,
            Enumerable.Range(0, 8).Select(index => new TerrainHorizonSample(index * 45,
                11.309932, 500, Sightline: sightline)).ToArray(),
            true, "Synthetic debug", baseSnapshot.Session.Instant,
            ChosenObserverGroundElevationMetres: 20,
            ObserverAbsoluteElevationMetres: 21.7,
            ObserverHeightAboveGroundMetres: 1.7);
        var snapshot = baseSnapshot with { Terrain = terrain };
        var view = new NoctaxisMapView
        {
            Width = width,
            Height = height,
            Observer = observer,
            Snapshot = snapshot,
            FramingGuide = new CameraFramingGuide(90, 40, 70, 110,
                CameraFramingDirectionSource.PrimaryTarget),
            ShowTerrainDebug = true
        };
        view.MapControlForTesting.Map!.Layers.Clear();
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var observerWorld = WebMercator.FromWgs84(observer);
        view.MapControlForTesting.Map.Navigator.CenterOnAndZoomTo(
            new MPoint(observerWorld.X, observerWorld.Y), resolution, 0);
        var viewport = view.MapControlForTesting.Map.Navigator.Viewport;
        var winningCoordinate = Angles.Destination(observer, 90, 500);
        var winningWorld = WebMercator.FromWgs84(winningCoordinate);
        var winning = viewport.WorldToScreen(winningWorld.X, winningWorld.Y);

        using var rendered = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        rendered.Render(view.OverlayForTesting);
        using var pixels = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = pixels.Lock();
        rendered.CopyPixels(framebuffer);
        byte maximumRed = 0;
        for (var y = (int)winning.Y - 5; y <= (int)winning.Y + 5; y++)
        for (var x = (int)winning.X - 5; x <= (int)winning.X + 5; x++)
            maximumRed = Math.Max(maximumRed,
                System.Runtime.InteropServices.Marshal.ReadByte(framebuffer.Address,
                    y * framebuffer.RowBytes + x * 4 + 2));

        Assert.True(maximumRed > 180, $"Winning terrain debug marker was not rendered at {winning}.");
        window.Close();
    }

    [AvaloniaFact]
    public void TerrainDebugMiniMapReactivelyReplacesProfileIdentity()
    {
        var first = new TerrainHorizonProfile(new GeoCoordinate(51, -1), [], false,
            "First", Instant.FromUtc(2026, 1, 1, 0, 0));
        var second = new TerrainHorizonProfile(new GeoCoordinate(53, 1), [], false,
            "Second", Instant.FromUtc(2026, 1, 1, 0, 1));
        var observed = new List<TerrainHorizonProfile?>();
        var observedBearings = new List<double>();
        var observedFields = new List<double>();
        var observedWeather = new List<double?>();
        var control = new TerrainDebugMiniMap();
        using var subscription = control.GetObservable(TerrainDebugMiniMap.ProfileProperty)
            .Subscribe(new ValueObserver<TerrainHorizonProfile?>(observed));
        using var bearingSubscription = control.GetObservable(TerrainDebugMiniMap.CentreBearingDegreesProperty)
            .Subscribe(new ValueObserver<double>(observedBearings));
        using var fieldSubscription = control.GetObservable(TerrainDebugMiniMap.HorizontalFieldOfViewDegreesProperty)
            .Subscribe(new ValueObserver<double>(observedFields));
        using var weatherSubscription = control.GetObservable(TerrainDebugMiniMap.WeatherVisibilityDistanceMetresProperty)
            .Subscribe(new ValueObserver<double?>(observedWeather));

        control.Observer = first.Observer;
        control.Generation = 1;
        control.Profile = first;
        control.Observer = second.Observer;
        control.Generation = 2;
        control.CentreBearingDegrees = 95;
        control.HorizontalFieldOfViewDegrees = 42;
        control.WeatherVisibilityDistanceMetres = 8_000;
        control.Profile = null;
        control.Profile = second;

        Assert.Same(second, control.Profile);
        Assert.Equal(second.Observer, control.Observer);
        Assert.Equal(2, control.Generation);
        Assert.Contains(first, observed);
        Assert.Contains(null, observed);
        Assert.Same(second, observed[^1]);
        Assert.Equal(95, observedBearings[^1]);
        Assert.Equal(42, observedFields[^1]);
        Assert.Equal(8_000, observedWeather[^1]);
    }

    [AvaloniaFact]
    public void LocalTerrainMapUsesNorthUpBearingAndReactiveMapIdentity()
    {
        var centre = new Point(100, 100);
        Assert.True(LocalTerrainMap.BearingPoint(centre, 80, 0).Y < centre.Y);
        Assert.True(LocalTerrainMap.BearingPoint(centre, 80, 90).X > centre.X);
        Assert.True(LocalTerrainMap.BearingPoint(centre, 80, 180).Y > centre.Y);
        Assert.True(LocalTerrainMap.BearingPoint(centre, 80, 270).X < centre.X);

        var first = DebugMap(new GeoCoordinate(51, -1));
        var second = DebugMap(new GeoCoordinate(53, 1));
        var observed = new List<TerrainDebugMapSnapshot?>();
        var control = new LocalTerrainMap();
        using var subscription = control.GetObservable(LocalTerrainMap.MapProperty)
            .Subscribe(new ValueObserver<TerrainDebugMapSnapshot?>(observed));
        control.Map = first;
        control.Map = null;
        control.Observer = second.Observer;
        control.Generation = 2;
        control.CentreBearingDegrees = 90;
        control.HorizontalFieldOfViewDegrees = 40;
        control.Map = second;

        var left = LocalTerrainMap.BearingPoint(centre, 80,
            control.CentreBearingDegrees - control.HorizontalFieldOfViewDegrees / 2);
        var right = LocalTerrainMap.BearingPoint(centre, 80,
            control.CentreBearingDegrees + control.HorizontalFieldOfViewDegrees / 2);
        Assert.True(left.X > centre.X && left.Y < centre.Y);
        Assert.True(right.X > centre.X && right.Y > centre.Y);
        Assert.Contains(null, observed);
        Assert.Same(second, observed[^1]);
        Assert.Equal(second.Observer, control.Observer);
    }

    [Fact]
    public void NegativeCoastalHorizonDoesNotCreateMapHatchingForVisibleTarget()
    {
        var line = new[] { new TerrainSightlineSample(9_300, 0, 5.818, -0.072) };
        var terrain = new TerrainHorizonProfile(Observer,
            Enumerable.Range(0, 8).Select(index => new TerrainHorizonSample(
                index * 45d, -0.072, 9_300, Sightline: line)).ToArray(),
            true, "Synthetic coastal horizon", Instant.FromUtc(2026, 1, 1, 0, 0));
        var visibility = new FramingVisibilityCalculator().Calculate(
            new WeatherResult(DataState.Loading, null, "Unavailable"), terrain,
            targetAltitudeDegrees: 5, cameraBearingDegrees: 5,
            horizontalFovDegrees: 40, terrainCastAngularDetailDegrees: 5,
            verticalFovDegrees: 30);
        var overlay = new GeographicOverlayGeometryBuilder().BuildCameraOverlay(
            new GeoSector(Observer, 5, 40, MapOverlayGeometry.MaximumRangeMetres), visibility);

        Assert.False(visibility.IsTargetTerrainObstructed);
        Assert.All(visibility.EffectiveTerrainObstructions, sample => Assert.False(sample.IsObstructed));
        Assert.Empty(overlay.TerrainHatchRegions);
    }

    private sealed class ValueObserver<T>(List<T> values) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
        public void OnNext(T value) => values.Add(value);
    }

    private static TerrainDebugMapSnapshot DebugMap(GeoCoordinate observer) => new(observer,
        20_000, 1, 1, [observer], [10], [10], [Noctaxis.Core.Environment.LandCoverClass.Grassland],
        [false], [TerrainSampleStatus.Valid], ["12/1/1"],
        Noctaxis.Core.Environment.EnvironmentalDataState.Available,
        Instant.FromUtc(2026, 1, 1, 0, 0), "Synthetic");

    private static PlanningSnapshot Snapshot(GeoCoordinate observer)
    {
        var instant = Instant.FromUtc(2026, 8, 9, 0, 0);
        var target = new AstralTarget("test", "Test target", AstralTargetCategory.Star,
            0, 0, "J2000");
        var session = new PlanningSession(observer, instant, "Europe/London", target.Id,
            new LensConfiguration());
        var events = new TargetEvents(null, null, null);
        var position = new TargetPosition(target, instant, new HorizontalCoordinate(105, 30), events);
        var path = new AstralPath(new LocalDate(2026, 8, 9), session.TimeZoneId,
            Duration.FromMinutes(10), [new AstralPathSample(instant, position.Horizontal)], events, instant);
        var terrain = new TerrainHorizonProfile(observer,
            Enumerable.Range(0, 8).Select(index => new TerrainHorizonSample(index * 45, 0)).ToArray(),
            true, "Synthetic", instant);
        return new PlanningSnapshot(session, position, path, new FieldOfView(70, 40, 80), terrain,
            new TerrainCrossings(null, null), new WeatherResult(DataState.MissingCoverage, null, "Unavailable"),
            new AstronomyContext(position, position));
    }

    [Fact]
    public void CameraBaseFillProjectsItsApexToObserverAcrossElevationChanges()
    {
        var builder = new GeographicOverlayGeometryBuilder();
        foreach (var observer in new[] { Observer, Observer with { ElevationMetres = 125 } })
        {
            var sector = new GeoSector(observer, 90, 40, MapOverlayGeometry.MaximumRangeMetres);
            var fill = builder.BuildCameraBaseFill(sector);
            var observerWorld = WebMercator.FromWgs84(observer);
            var viewport = new Viewport(observerWorld.X + 5_000, observerWorld.Y - 3_000,
                100, 17, 1_200, 800);
            var expected = viewport.WorldToScreen(observerWorld.X, observerWorld.Y);
            var firstWorld = WebMercator.FromWgs84(fill[0]);
            var lastWorld = WebMercator.FromWgs84(fill[^1]);
            var first = viewport.WorldToScreen(firstWorld.X, firstWorld.Y);
            var last = viewport.WorldToScreen(lastWorld.X, lastWorld.Y);

            Assert.Equal(observer, fill[0]);
            Assert.Equal(observer, fill[^1]);
            Assert.Equal(expected.X, first.X, 8);
            Assert.Equal(expected.Y, first.Y, 8);
            Assert.Equal(expected.X, last.X, 8);
            Assert.Equal(expected.Y, last.Y, 8);
        }
    }

    [Fact]
    public void RevisionedNativeResourceCacheReusesAndDisposesExactlyOnce()
    {
        var first = new DisposableProbe();
        var second = new DisposableProbe();
        var cache = new EnvironmentalOverlayResourceCache<DisposableProbe>();

        Assert.Same(first, cache.GetOrCreate(1, () => first));
        Assert.Same(first, cache.GetOrCreate(1, () => throw new InvalidOperationException()));
        Assert.Same(second, cache.GetOrCreate(2, () => second));
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(2, cache.CreationCount);

        cache.Dispose();
        cache.Dispose();
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public void SkiaResourcesCompileOnceAndReuseProfileAcrossViewportWeatherAndStyleChanges()
    {
        var diagnostics = new EnvironmentalOverlayDiagnostics();
        var coordinator = new EnvironmentalOverlayStateCoordinator(diagnostics, 32);
        var sector = Sector(0, 40);
        var state = coordinator.Update(Observer, sector,
            Visibility(sector, null, (0, 100), (40, 200)), ProfileKey());
        var world = WebMercator.FromWgs84(Observer);
        var viewport = new Viewport(world.X, world.Y, 1_000, 0, 64, 64);
        var firstFrame = EnvironmentalOverlayMath.CreateFrame(
            viewport, 64, 64, EnvironmentalRenderParameters.Default);
        var pannedFrame = firstFrame with
        {
            WorldOriginX = firstFrame.WorldOriginX + 100,
            RenderKey = firstFrame.RenderKey with { WorldOriginX = firstFrame.RenderKey.WorldOriginX + 100 }
        };
        using var surface = SKSurface.Create(new SKImageInfo(64, 64));
        using var resources = new SkiaEnvironmentalOverlayResources(diagnostics);

        resources.Draw(surface.Canvas, state, firstFrame, Colors.DeepPink);
        resources.Draw(surface.Canvas, state, pannedFrame, Colors.DeepPink);
        var styledFrame = firstFrame with
        {
            RenderKey = firstFrame.RenderKey with
            {
                Parameters = firstFrame.RenderKey.Parameters with
                {
                    HatchOpacity = .5f,
                    HatchSpacingPixels = 11,
                    HatchThicknessPixels = 3
                }
            }
        };
        resources.Draw(surface.Canvas, state, styledFrame, Colors.DeepPink);
        var weatherState = coordinator.Update(Observer, sector,
            Visibility(sector, 5_000, (0, 100), (40, 200)), ProfileKey());
        resources.Draw(surface.Canvas, weatherState, firstFrame, Colors.DeepPink);

        Assert.Equal(1, diagnostics.ShaderCompilations);
        Assert.Equal(1, diagnostics.ProfileUploads);
        Assert.Equal(4, diagnostics.DrawCalls);
        Assert.Equal(state.TerrainTextureRevision, weatherState.TerrainTextureRevision);
    }

    [Fact]
    public void HatchDensityDoesNotChangeStateSizeOrDrawOperationCount()
    {
        var sector = Sector(0, 120);
        var coordinator = new EnvironmentalOverlayStateCoordinator(profileTextureWidth: 512);
        var state = coordinator.Update(Observer, sector,
            Visibility(sector, null, (0, 15), (60, 500), (120, 100)), ProfileKey());

        Assert.Equal(512, state.ProfileTexels.Length);
        Assert.Equal(1, EnvironmentalOverlayRenderer.DrawOperationsPerFrame);
        Assert.Equal(0, coordinator.Diagnostics.LegacyHatchPrimitiveCount);
        Assert.Equal(3, state.SourceSamples.Length);
    }

    [Fact]
    public void CameraOutlineDoesNotConstructContinuousFillOrHatchGeometry()
    {
        var sector = Sector(0, 120);
        var outline = new GeographicOverlayGeometryBuilder().BuildCameraOutline(
            sector, Visibility(sector, 20_000, (0, 15), (120, 100)));

        Assert.NotEmpty(outline.LeftBoundary);
        Assert.NotEmpty(outline.CentreBearing);
        Assert.NotEmpty(outline.RightBoundary);
        AssertBoundaryEndsAtMaximum(outline.LeftBoundary);
        AssertBoundaryEndsAtMaximum(outline.CentreBearing);
        AssertBoundaryEndsAtMaximum(outline.RightBoundary);
    }

    private static GeoSector Sector(double centreBearing, double fov) => new(
        Observer, centreBearing, fov, MapOverlayGeometry.MaximumRangeMetres);

    private static FramingVisibilityAssessment Visibility(
        GeoSector sector,
        double? weatherDistance,
        params (double Offset, double? Distance)[] samples) => new(
        false,
        null,
        null,
        weatherDistance,
        "Test",
        samples.Select(sample => new FramingTerrainObstructionSample(
            Angles.NormaliseDegrees(sector.LeftBearingDegrees + sample.Offset),
            sample.Distance.HasValue,
            sample.Distance)).ToArray());

    private static EnvironmentalProfileKey ProfileKey() => new(
        Observer.Latitude, Observer.Longitude, 1.7, 1, 10, 360, 1);

    private static EnvironmentalRenderKey RenderKey() => new(
        1_200, 800, 0, 0, 1, 0, 0, -1, EnvironmentalRenderParameters.Default);

    private static void AssertBoundaryEndsAtMaximum(IReadOnlyList<GeographicPathSegment> segments) =>
        Assert.Equal(MapOverlayGeometry.MaximumRangeMetres, segments[^1].EndDistanceMetres);

    private sealed class DisposableProbe : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }
}
