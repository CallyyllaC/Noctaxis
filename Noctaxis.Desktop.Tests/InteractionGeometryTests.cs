using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// Interaction states (hover, press, keyboard focus, selection, check, expansion, disabled) may change how a
/// control looks, never where it is or how big it is. A state that moved or resized a control would move
/// the region the pointer is tested against, so hovering near an edge could switch the state off and on
/// indefinitely (the location-card flicker).
/// </summary>
public sealed class InteractionGeometryTests
{
    private static (Window Window, StackPanel Panel) Host(Control content)
    {
        var panel = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Left, Children = { content } };
        var window = new Window { Width = 900, Height = 700, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, panel);
    }

    private static Rect[] Layout(Control root) =>
        [.. root.GetVisualDescendants().OfType<Control>().Where(c => c is Button or ToggleButton or TextBox or ComboBox or NumericUpDown
            or RadioButton or CheckBox or TabItem or ListBoxItem or Expander or Slider or Border { Name: "Sentinel" })
            .Select(c => new Rect(c.TranslatePoint(new Point(0, 0), root) ?? default, c.Bounds.Size))];

    public static TheoryData<string> Themes => new() { "builtin.noctaxis", "builtin.highcontrast" };

    [AvaloniaFact]
    public void InformationalToolTipsLetThePointerThroughToTheirControl()
    {
        var target = new Button { Content = "Card", Width = 360, Height = 160 };
        ToolTip.SetTip(target, "Open location in Planner");
        ToolTip.SetPlacement(target, PlacementMode.Center); // Overlapping its control, as a tooltip may.
        var (window, _) = Host(target);
        try
        {
            var centre = target.TranslatePoint(new Point(180, 80), window)!.Value;
            window.MouseMove(centre);
            ToolTip.SetIsOpen(target, true);
            Dispatcher.UIThread.RunJobs();
            var tip = window.GetVisualDescendants().OfType<ToolTip>().Single();
            var popup = Assert.IsType<Popup>(tip.Parent);
            Assert.True(popup.IsUsingOverlayLayer);
            Assert.False(tip.IsHitTestVisible);

            // Across the tooltip's own pixels, still inside the control: hover and tooltip are unaffected.
            foreach (var offset in new[] { -20d, 0, 20 })
            {
                var overTip = tip.TranslatePoint(new Point(tip.Bounds.Width / 2 + offset, tip.Bounds.Height / 2), window)!.Value;
                window.MouseMove(overTip);
                Dispatcher.UIThread.RunJobs();
                Assert.True(target.IsPointerOver, $"hover lost over the tooltip at {overTip}");
                Assert.True(ToolTip.GetIsOpen(target));
                Assert.Same(target, window.InputHitTest(overTip) is Visual hit ? hit.FindAncestorOfType<Button>(includeSelf: true) : null);
            }

            // Leaving the control still clears hover and dismisses the tooltip.
            window.MouseMove(new Point(890, 690));
            Dispatcher.UIThread.RunJobs();
            Assert.False(target.IsPointerOver);
            Assert.False(ToolTip.GetIsOpen(target));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FlyoutsRemainInteractive()
    {
        var inside = new Button { Content = "Inside the flyout" };
        var owner = new Button { Content = "Owner", Flyout = new Flyout { Content = inside } };
        var (window, _) = Host(owner);
        try
        {
            owner.Flyout!.ShowAt(owner);
            Dispatcher.UIThread.RunJobs();
            Assert.True(inside.IsHitTestVisible);
            var point = inside.TranslatePoint(new Point(inside.Bounds.Width / 2, inside.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point);
            Dispatcher.UIThread.RunJobs();
            Assert.True(inside.IsPointerOver, "the flyout's content takes the pointer");
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void EveryInteractionStateKeepsControlsInPlace(string theme)
    {
        var service = ((App)Application.Current!).Themes!;
        service.Apply(new(theme, TextScale: 1.5));
        Control[] controls =
        [
            new Button { Content = "Default button" },
            new Button { Content = "Accent button", Classes = { "accent" } },
            new Button { Content = "Secondary", Classes = { "secondaryAction" } },
            new Button { Content = "Card action", Classes = { "cardAction" } },
            new Button { Content = "Map chrome", Classes = { "mapChrome" } },
            new Button { Content = "Destructive", Classes = { "destructive" } },
            new ToggleButton { Content = "Toggle" },
            new TextBox { Text = "Text", Width = 200 },
            new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0, Width = 200 },
            new NumericUpDown { Value = 3, Width = 200 },
            new CheckBox { Content = "Check" },
            new RadioButton { Content = "Radio", Classes = { "settingsChoice" } },
            new ListBox { ItemsSource = new[] { "First item", "Second item", "Third item" } },
            new Expander { Header = "Expander", Content = new TextBlock { Text = "Body" } },
            new TabControl { ItemsSource = new[] { new TabItem { Header = "Tab one" }, new TabItem { Header = "Tab two" } } },
        ];
        var stack = new StackPanel { Spacing = 0 };
        foreach (var control in controls)
        {
            control.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(control);
        }
        // Anything whose position depends on the controls above it.
        stack.Children.Add(new Border { Name = "Sentinel", Height = 10, Width = 10 });
        var (window, _) = Host(stack);
        try
        {
            var baseline = Layout(stack);
            void AssertStable(string state)
            {
                Dispatcher.UIThread.RunJobs();
                var now = Layout(stack);
                Assert.True(baseline.SequenceEqual(now), $"{theme}: {state} moved or resized a control:\n" +
                    string.Join("\n", baseline.Zip(now).Where(pair => pair.First != pair.Second).Select(pair => $"  {pair.First} -> {pair.Second}")));
            }
            Point Centre(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

            foreach (var control in stack.Children.OfType<Control>().Where(c => c is not Border))
            {
                var target = control is ListBox list ? list.GetVisualDescendants().OfType<ListBoxItem>().First() :
                    control is TabControl tabs ? tabs.GetVisualDescendants().OfType<TabItem>().Last() : control;
                window.MouseMove(Centre(target));
                AssertStable($"hover on {control.GetType().Name} {(control as ContentControl)?.Content}");
                if (target is Button or ToggleButton)
                {
                    window.MouseDown(Centre(target), MouseButton.Left);
                    AssertStable($"press on {control.GetType().Name} {(control as ContentControl)?.Content}");
                    window.MouseUp(Centre(target), MouseButton.Left);
                    Dispatcher.UIThread.RunJobs();
                    baseline = Layout(stack); // A click may legitimately change content (e.g. expanding).
                }
                window.MouseMove(new Point(890, 690));
                Dispatcher.UIThread.RunJobs();
                baseline = Layout(stack);
                if (target is InputElement { Focusable: true } input)
                {
                    input.Focus(NavigationMethod.Tab);
                    AssertStable($"keyboard focus on {control.GetType().Name} {(control as ContentControl)?.Content}");
                }
            }

            // Selection, checked and disabled states.
            var listBox = stack.Children.OfType<ListBox>().Single();
            listBox.SelectedIndex = 1;
            AssertStable("list selection");
            var toggle = stack.Children.OfType<ToggleButton>().First(t => t is not CheckBox and not RadioButton);
            toggle.IsChecked = false; Dispatcher.UIThread.RunJobs(); baseline = Layout(stack);
            toggle.IsChecked = true;
            AssertStable("toggle checked");
            foreach (var button in stack.Children.OfType<Button>()) button.IsEnabled = false;
            AssertStable("buttons disabled");
            var tabControl = stack.Children.OfType<TabControl>().Single();
            tabControl.SelectedIndex = 1; Dispatcher.UIThread.RunJobs(); baseline = Layout(stack);
            tabControl.SelectedIndex = 0;
            AssertStable("tab selection");
        }
        finally { window.Close(); service.Apply(new()); }
    }
}
