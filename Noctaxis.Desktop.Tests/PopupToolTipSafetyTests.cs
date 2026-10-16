using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;
using Noctaxis.Desktop.Controls;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// A tooltip is hosted in its window's overlay layer only where its control has one. An item in an open
/// drop-down — on Windows its own native popup window, with no overlay layer — once had its tooltip forced into
/// an overlay that did not exist, and Avalonia's refusal ended the application (pointer resting on a theme in
/// the Theme drop-down).
/// </summary>
public sealed class PopupToolTipSafetyTests
{
    private static (Window Window, StackPanel Panel) Host(params Control[] content)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var control in content) panel.Children.Add(control);
        var window = new Window { Width = 600, Height = 500, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, panel);
    }

    /// <summary>The tooltip Avalonia made for <paramref name="control"/>, if any (its own property is internal).</summary>
    internal static ToolTip? TipOf(Control control) =>
        control.GetValue((AvaloniaProperty)typeof(ToolTip).GetField("ToolTipProperty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!) as ToolTip;

    /// <summary>A top level with no overlay layer, as a native popup (a Windows drop-down) has none.</summary>
    private static Window WithoutOverlayLayer(Control content)
    {
        var window = new Window
        {
            Width = 300, Height = 200, Content = content,
            Template = new FuncControlTemplate<Window>((w, _) => new VisualLayerManager
            {
                EnableOverlayLayer = false,
                Child = new ContentPresenter { Name = "PART_ContentPresenter", [!ContentPresenter.ContentProperty] = w[!ContentControl.ContentProperty] }
            })
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>
    /// The failure itself, as Avalonia gives it: a tooltip popup told to use an overlay layer its control does not
    /// have cannot open. This is what the informational tooltips used to ask for inside a native drop-down.
    /// </summary>
    [AvaloniaFact]
    public void AnOverlayTooltipWhereThereIsNoOverlayIsWhatFailed()
    {
        var target = new Button { Content = "Theme" };
        var window = WithoutOverlayLayer(target);
        try
        {
            Assert.Null(OverlayLayer.GetOverlayLayer(target));
            var popup = new Popup { Child = new ToolTip { Content = "Supporter theme" }, PlacementTarget = target };
            ((ISetLogicalParent)popup).SetParent(target);
            popup.ShouldUseOverlayLayer = true;
            var error = Assert.Throws<InvalidOperationException>(() => popup.IsOpen = true);
            Assert.Contains("no overlay layer is found", error.Message);
        }
        finally { window.Close(); }
    }

    /// <summary>
    /// Each time a tooltip is placed, the overlay is chosen only where its control has one: in a window, yes; in a
    /// top level without an overlay (a native drop-down) or out of any window, the platform's own popup instead.
    /// The same tooltip popup, placed again elsewhere, decides again.
    /// </summary>
    [AvaloniaFact]
    public void ATooltipUsesTheOverlayOnlyWhereItsControlHasOne()
    {
        var inWindow = new Button { Content = "In the window" };
        var inDropDown = new Button { Content = "In a native drop-down" };
        var detached = new Button { Content = "Gone" };
        var (window, _) = Host(inWindow);
        var bare = WithoutOverlayLayer(inDropDown);
        try
        {
            Assert.True(InformationalToolTips.CanUseOverlayLayer(inWindow));
            Assert.False(InformationalToolTips.CanUseOverlayLayer(inDropDown));
            Assert.False(InformationalToolTips.CanUseOverlayLayer(detached));

            var popup = new Popup { Child = new ToolTip { Content = "tip" } };
            foreach (var (target, overlay) in new[] { (inWindow, true), (inDropDown, false), (inWindow, true), (detached, false), (inWindow, true) })
            {
                popup.PlacementTarget = target;
                Assert.Equal(overlay, popup.ShouldUseOverlayLayer);
            }
            // Other popups — flyouts, menus, drop-downs — are never touched.
            var other = new Popup { Child = new Border(), ShouldUseOverlayLayer = false, PlacementTarget = inWindow };
            Assert.False(other.ShouldUseOverlayLayer);
        }
        finally
        {
            bare.Close();
            window.Close();
        }
    }

    /// <summary>An ordinary tooltip in a window is still informational: in the overlay, and not hit-testable.</summary>
    [AvaloniaFact]
    public void AWindowTooltipIsStillInformational()
    {
        var target = new Button { Content = "Card", Width = 200, Height = 60 };
        ToolTip.SetTip(target, "Open location in Planner");
        var (window, _) = Host(target);
        try
        {
            ToolTip.SetIsOpen(target, true);
            Dispatcher.UIThread.RunJobs();
            var tip = window.GetVisualDescendants().OfType<ToolTip>().Single();
            var popup = Assert.IsType<Popup>(tip.Parent);
            Assert.True(popup.IsUsingOverlayLayer);
            Assert.False(tip.IsHitTestVisible);
        }
        finally { window.Close(); }
    }

    private static ComboBox ComboWithTips()
    {
        var combo = new ComboBox { Width = 240, ItemsSource = new[] { "Noctaxis", "Aurora", "High contrast" } };
        combo.ItemContainerTheme = new Avalonia.Styling.ControlTheme(typeof(ComboBoxItem))
        {
            BasedOn = (Avalonia.Styling.ControlTheme?)Application.Current!.FindResource(typeof(ComboBoxItem)),
            Setters = { new Avalonia.Styling.Setter(ToolTip.TipProperty, "Supporter theme") }
        };
        return combo;
    }

    /// <summary>A drop-down item's tooltip opens and closes, the drop-down open, and its popup's choice matches its item.</summary>
    [AvaloniaFact]
    public void ATooltipInsideAnOpenDropDownOpens()
    {
        var combo = ComboWithTips();
        var (window, _) = Host(combo);
        try
        {
            combo.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            var item = Assert.IsType<ComboBoxItem>(combo.ContainerFromIndex(1));
            ToolTip.SetIsOpen(item, true);
            Dispatcher.UIThread.RunJobs();
            Assert.True(ToolTip.GetIsOpen(item));
            var popup = Assert.IsType<Popup>(TipOf(item)!.Parent);
            Assert.Equal(InformationalToolTips.CanUseOverlayLayer(item), popup.ShouldUseOverlayLayer);
            ToolTip.SetIsOpen(item, false);
            combo.IsDropDownOpen = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(ToolTip.GetIsOpen(item));
        }
        finally { window.Close(); }
    }

    /// <summary>A control that has left the window: asking for its tooltip opens nothing and throws nothing.</summary>
    [AvaloniaFact]
    public void ADetachedControlsTooltipOpensNothing()
    {
        var target = new Button { Content = "Gone", Width = 200, Height = 60 };
        ToolTip.SetTip(target, "Was here");
        var (window, panel) = Host(target);
        try
        {
            panel.Children.Remove(target);
            Dispatcher.UIThread.RunJobs();
            ToolTip.SetIsOpen(target, true);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(window.GetVisualDescendants(), visual => visual is ToolTip);
            Assert.False(PopupToolTipSafetyTests.TipOf(target)?.IsVisible == true && ((Visual)target).IsAttachedToVisualTree());
            ToolTip.SetIsOpen(target, false);
        }
        finally { window.Close(); }
    }

    /// <summary>A tooltip waiting to show when its control leaves the window neither shows nor throws when its time comes.</summary>
    [AvaloniaFact]
    public void APendingTooltipForADetachedControlNeverOpens()
    {
        var target = new Button { Content = "Hover me", Width = 200, Height = 60 };
        ToolTip.SetTip(target, "Pending");
        ToolTip.SetShowDelay(target, 50);
        var (window, panel) = Host(target);
        try
        {
            window.MouseMove(target.TranslatePoint(new Point(100, 30), window)!.Value);
            Dispatcher.UIThread.RunJobs();
            panel.Children.Remove(target);
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(200);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(window.GetVisualDescendants(), visual => visual is ToolTip);
        }
        finally { window.Close(); }
    }
}

public sealed partial class MainViewModelTests
{
    /// <summary>
    /// The crash's own setting: the Theme drop-down open, a theme's tooltip showing, and the appearance changed
    /// rapidly through every theme, Dark, Light and System, and High Contrast — from the drop-down itself and from the
    /// mode choices. Every change applies at once and is saved, the supporter-only themes stay unavailable, and no
    /// tooltip asks for an overlay its item does not have.
    /// </summary>
    [AvaloniaFact]
    public async Task RapidThemeSwitchingWithTheThemeDropDownAndATooltipOpen()
    {
        var catalogue = new Noctaxis.Core.Catalogues.OpenNgcTargetCatalogue();
        var store = CreatePlannerStore(new AppSettings());
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, store, new FakeExporter());
        // The real theme service: every change replaces the application's theme resources, as in the app.
        using var themes = new Noctaxis.Desktop.Themes.ThemeService(Application.Current!);
        vm.AttachThemeService(themes);
        await vm.InitializeAsync();
        var window = new Noctaxis.Desktop.Views.MainWindow { Width = 1440, Height = 1000, DataContext = vm };
        window.Show();
        try
        {
            ((TabControl)((Grid)window.Content!).Children[1]).SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            window.SelectSettingsCategory("Appearance");
            var combo = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(),
                box => Avalonia.Automation.AutomationProperties.GetName(box) == "Theme");

            var available = vm.ThemeOptions.Where(option => option.IsAvailable).ToArray();
            Assert.Contains(available, option => option.Id == "builtin.highcontrast");
            Assert.All(vm.ThemeOptions.Where(option => option.IsSupporter), option => Assert.False(option.IsAvailable));

            for (var round = 0; round < 3; round++)
            foreach (var option in available)
            foreach (var mode in new[] { AppearanceMode.Dark, AppearanceMode.Light, AppearanceMode.System })
            {
                combo.IsDropDownOpen = true;
                Dispatcher.UIThread.RunJobs();
                // A tooltip showing on an item — a supporter theme's if there is one — as the theme changes under it.
                var index = Math.Max(0, vm.ThemeOptions.ToList().FindIndex(o => o.IsSupporter));
                var item = Assert.IsType<ComboBoxItem>(combo.ContainerFromIndex(index));
                ToolTip.SetShowOnDisabled(item, true);
                ToolTip.SetIsOpen(item, true);
                combo.SelectedItem = option;
                vm.SelectedAppearanceMode = mode;
                Dispatcher.UIThread.RunJobs();
                if (PopupToolTipSafetyTests.TipOf(item)?.Parent is Popup popup)
                    Assert.Equal(InformationalToolTips.CanUseOverlayLayer(item), popup.ShouldUseOverlayLayer);
                ToolTip.SetIsOpen(item, false);
                combo.IsDropDownOpen = false;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal((option.Id, mode), ((vm.Settings.Appearance ?? new()).ThemeFamilyId, (vm.Settings.Appearance ?? new()).AppearanceMode));
            }
            await vm.ApplyAppearanceAsync(vm.Settings.Appearance!);
            Assert.Equal(vm.Settings.Appearance, store.State.Settings.Appearance);
        }
        finally { window.Close(); }
    }
}
