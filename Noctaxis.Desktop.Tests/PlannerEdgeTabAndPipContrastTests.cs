using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.Controls.Docking;
using Noctaxis.Desktop.Mapping;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Two small polishes: an unpinned panel's tab on the right edge reads down the edge, so the strip stays a
/// narrow column; and the Picture-in-Picture map draws its terrain relief in a stronger tone, on by default and
/// its own setting, without touching the main map or any layer's state.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static Button EdgeTab(MainWindow window, PlannerPanelViewModel panel) =>
        window.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("panelEdgeTab") &&
            Avalonia.Automation.AutomationProperties.GetName(button) == panel.RevealAccessibleName);

    /// <summary>
    /// The right edge's tab turns its name to read down the edge: narrow and tall, the name centred, still
    /// named for what it opens, and one tap still opens the panel. Nothing about the panel's docking changes,
    /// and a tab on the bottom edge still reads across.
    /// </summary>
    [AvaloniaFact]
    public async Task ARightEdgeTabReadsDownTheEdge()
    {
        var (vm, window, workspace) = await CreatePlannerAsync();
        try
        {
            var inspector = vm.PlannerDock.Inspector;
            inspector.UnpinCommand.Execute(null);
            vm.PlannerDock["timeline"]!.UnpinCommand.Execute(null);
            Settle(window);
            Assert.Contains(PanelEdgeStrip.EdgeClass(PanelDockSide.Right), workspace.StripFor(PanelDockSide.Right).Classes);

            var tab = EdgeTab(window, inspector);
            Assert.Equal("Open Inspector", Avalonia.Automation.AutomationProperties.GetName(tab));
            var label = tab.GetVisualDescendants().OfType<LayoutTransformControl>().Single();
            // A quarter turn clockwise: across becomes down.
            var turn = label.LayoutTransform!.Value;
            Assert.Equal((0, 1, -1, 0), (Math.Round(turn.M11, 6), Math.Round(turn.M12, 6), Math.Round(turn.M21, 6), Math.Round(turn.M22, 6)));
            Assert.Equal("Inspector", label.GetVisualDescendants().OfType<TextBlock>().Single().Text);
            Assert.True(tab.Bounds.Height > tab.Bounds.Width * 2, $"the tab is {tab.Bounds.Size}");
            Assert.InRange(tab.Bounds.Width, 32, 44);
            var centre = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), tab)!.Value;
            Assert.Equal(tab.Bounds.Width / 2, centre.X, 0);
            Assert.Equal(tab.Bounds.Height / 2, centre.Y, 0);

            // The bottom edge's tab still reads across.
            var bottom = EdgeTab(window, vm.PlannerDock["timeline"]!);
            Assert.Null(bottom.GetVisualDescendants().OfType<LayoutTransformControl>().Single().LayoutTransform);

            // One tap opens the panel; its docking is as it was.
            var at = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, Avalonia.Input.MouseButton.Left);
            window.MouseUp(at, Avalonia.Input.MouseButton.Left);
            Settle(window);
            Assert.True(inspector.IsRevealed);
            Assert.True(inspector.IsAutoHidden);
            Assert.Equal(PanelDockSide.Right, inspector.Side);
            Assert.True(tab.Focusable);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// Enhance PiP contrast is on by default and is the PiP's own: the PiP draws its relief in the stronger tone,
    /// the main map never does, and turning it off returns the PiP to the usual tone. It saves with the PiP's other
    /// settings and changes none of them.
    /// </summary>
    [AvaloniaFact]
    [CoversSettingsInput("SettingsPlannerPipEnhanceContrast")]
    public async Task EnhancePipContrastIsOnByDefaultAndOnlyThePips()
    {
        var (vm, window, _) = await CreatePlannerAsync();
        try
        {
            Assert.True(vm.SettingsPlannerPipEnhanceContrast);
            Assert.True(Pip(window).EnhanceReliefContrast);
            Assert.False(MainMap(window).EnhanceReliefContrast);
            var toggle = window.FindControl<CheckBox>("PlannerPipEnhanceContrastToggle")!;
            Assert.Equal("Enhance PiP contrast", toggle.Content);
            Assert.True(toggle.IsChecked);
            var before = vm.Settings.EffectivePlannerPip;

            toggle.IsChecked = false;
            Settle(window);
            Assert.False(Pip(window).EnhanceReliefContrast);
            Assert.False(MainMap(window).EnhanceReliefContrast);
            await vm.WaitForPendingSavesAsync();
            Assert.Equal(before with { EnhanceContrast = false }, vm.Settings.EffectivePlannerPip);
            vm.SettingsPlannerPipEnhanceContrast = true;
            Settle(window);
            Assert.True(Pip(window).EnhanceReliefContrast);
        }
        finally { Close(window); }
    }

    /// <summary>
    /// The stronger tone is the PiP relief layer's own, drawn over the one shared shaded grid: the main map's
    /// layer keeps the usual tone, nothing is shaded again, and neither map's layer state moves.
    /// </summary>
    [AvaloniaFact]
    public void TheStrongerToneIsThePipReliefLayersOwnOverTheSharedSource()
    {
        var main = new PlannerMapView();
        main.ConfigureTerrainRelief(new SlopeElevation());
        var pip = main.CreatePictureInPicture();
        pip.EnhanceReliefContrast = true;
        pip.ShareEnvironmentalSources(main);
        Assert.Same(main.TerrainReliefForTesting!.Source, pip.TerrainReliefForTesting!.Source);
        Assert.Equal(TerrainReliefTileSource.ReliefTone.Normal, main.TerrainReliefForTesting.Layer.Tone);
        Assert.Equal(TerrainReliefTileSource.ReliefTone.Enhanced, pip.TerrainReliefForTesting.Layer.Tone);
        var states = (main.CompositionForTesting.Runtime[TerrainReliefMapBinding.LayerId], pip.CompositionForTesting.Runtime[TerrainReliefMapBinding.LayerId]);

        pip.EnhanceReliefContrast = false;
        Assert.Equal(TerrainReliefTileSource.ReliefTone.Normal, pip.TerrainReliefForTesting.Layer.Tone);
        Assert.Equal(TerrainReliefTileSource.ReliefTone.Normal, main.TerrainReliefForTesting.Layer.Tone);
        Assert.Equal(states, (main.CompositionForTesting.Runtime[TerrainReliefMapBinding.LayerId], pip.CompositionForTesting.Runtime[TerrainReliefMapBinding.LayerId]));
        Assert.False(main.TerrainReliefForTesting.SetTone(TerrainReliefTileSource.ReliefTone.Normal));
    }

    /// <summary>
    /// The stronger tone separates the same shading further, within a higher cap, and stays a neutral grey, so it
    /// never colours the land; the usual tone draws exactly what it always did.
    /// </summary>
    [Fact]
    public void TheStrongerToneSeparatesTheSameShadingFurther()
    {
        float[] Filled(float value) => Enumerable.Repeat(value, 256 * 256).ToArray();
        static byte Alpha(byte[] png) { using var bitmap = SkiaSharp.SKBitmap.Decode(png); return bitmap.GetPixel(128, 128).Alpha; }
        foreach (var shading in new[] { .2f, -.2f, -1f })
        {
            var usual = TerrainReliefTileSource.Encode(Filled(shading), 1, 0, 0)!;
            Assert.Equal(usual, TerrainReliefTileSource.Encode(Filled(shading), 1, 0, 0, TerrainReliefTileSource.ReliefTone.Normal));
            var strong = TerrainReliefTileSource.Encode(Filled(shading), 1, 0, 0, TerrainReliefTileSource.ReliefTone.Enhanced)!;
            Assert.True(Alpha(strong) > Alpha(usual), $"{shading}: {Alpha(strong)} should exceed {Alpha(usual)}");
            Assert.True(Alpha(strong) <= Math.Round(255 * TerrainReliefTileSource.ReliefTone.Enhanced.MaximumAlpha));
            using var bitmap = SkiaSharp.SKBitmap.Decode(strong);
            var pixel = bitmap.GetPixel(128, 128);
            Assert.Equal(pixel.Red, pixel.Green);
            Assert.Equal(pixel.Green, pixel.Blue);
        }
    }

    private sealed class SlopeElevation : Noctaxis.Core.Environment.ITerrariumTileSource
    {
        public long Generation => 0;
        public Task<Noctaxis.Core.Environment.TerrariumTileResult> GetTerrainTileAsync(Noctaxis.Core.Environment.TerrariumTileKey key,
            CancellationToken cancellationToken) =>
            Task.FromResult(new Noctaxis.Core.Environment.TerrariumTileResult(null, TerrainSampleStatus.Unavailable));
    }
}
