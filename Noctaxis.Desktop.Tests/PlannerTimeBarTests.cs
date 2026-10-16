using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Time;
using Noctaxis.Desktop.Views;
using NodaTime;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// The Planner's time bar: ‹ date › ‹ time › Now and the time zone, then Export. The arrows step the one
/// date and time the Planner has, each value opens its own picker, Now is the plan's zone's present moment,
/// and the zone is named by what it resolves to, never by how Settings chose it.
/// </summary>
public sealed partial class MainViewModelTests
{
    private static readonly AppSettings London = new(SelectedTimeZoneId: "Europe/London");

    /// <summary>A real click or tap on the named button, as a person makes it.</summary>
    private static void Click(Window window, string name)
    {
        var button = window.FindControl<Button>(name)!;
        var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Settle(window);
    }

    private static Instant PlanInstant(Noctaxis.Desktop.ViewModels.MainViewModel vm) =>
        vm.Session.Instant;

    [AvaloniaFact]
    public async Task TheTimeBarIsOneCompactGroupThenExport()
    {
        var (vm, window, _) = await CreatePlannerAsync(London);
        try
        {
            var bar = window.FindControl<StackPanel>("PlannerTimeBar")!;
            Assert.Equal(["PlannerDateControl", "PlannerTimeControl", "PlannerNow", "PlannerTimeZone"],
                bar.Children.Select(child => child.Name));
            var named = new Dictionary<string, string>
            {
                ["PlannerPreviousDay"] = "Previous day", ["PlannerNextDay"] = "Next day",
                ["PlannerPreviousTime"] = "Previous 30 minutes", ["PlannerNextTime"] = "Next 30 minutes"
            };
            foreach (var (name, label) in named)
            {
                var arrow = window.FindControl<Button>(name)!;
                Assert.Equal(label, Avalonia.Automation.AutomationProperties.GetName(arrow));
                Assert.True(arrow.Bounds.Width >= 32 && arrow.Bounds.Height >= 32, $"{name} is {arrow.Bounds.Size}");
                Assert.True(arrow.Focusable);
            }
            Assert.Equal("05 Mar 2026", window.FindControl<Button>("PlannerDateButton")!.Content);
            Assert.Equal("Date, 05 Mar 2026", Avalonia.Automation.AutomationProperties.GetName(window.FindControl<Button>("PlannerDateButton")!));
            Assert.Equal("21:00", window.FindControl<Button>("PlannerTimeButton")!.Content);
            Assert.Equal("Local time, 21:00", Avalonia.Automation.AutomationProperties.GetName(window.FindControl<Button>("PlannerTimeButton")!));
            // No headings over the values, and the retired buttons are gone.
            var markup = TestMarkup.ReadMainWindow();
            foreach (var retired in new[] { "Planner_Date_Label", "Planner_LocalTime_Label", "<DatePicker", "PlannerPanelsMenu", "Content=\"Save PNG\"" })
                Assert.DoesNotContain(retired, markup);
            // Export is on the right of the bar, apart from the time controls.
            var export = window.FindControl<Button>("PlannerExportMenu")!;
            Assert.True(export.TranslatePoint(default, window)!.Value.X > bar.TranslatePoint(default, window)!.Value.X + bar.Bounds.Width);
            Assert.Equal(vm.PlannerTimeZoneText, window.FindControl<TextBlock>("PlannerTimeZone")!.Text);
        }
        finally { Close(window); }
    }

    [AvaloniaFact]
    public async Task TheDayArrowsStepOneDayAcrossMonthsAndYears()
    {
        var (vm, window, _) = await CreatePlannerAsync(London);
        try
        {
            var before = PlanInstant(vm);
            Click(window, "PlannerNextDay");
            Assert.Equal(new DateTime(2026, 3, 6), vm.LocalDate!.Value.Date);
            Assert.Equal("21:00", vm.TimeText);
            Assert.Equal(Duration.FromDays(1), PlanInstant(vm) - before);
            Click(window, "PlannerPreviousDay");
            Click(window, "PlannerPreviousDay");
            Assert.Equal(new DateTime(2026, 3, 4), vm.LocalDate!.Value.Date);

            vm.SetPlannerDate(new DateTime(2026, 12, 31));
            Click(window, "PlannerNextDay");
            Assert.Equal(new DateTime(2027, 1, 1), vm.LocalDate!.Value.Date);
            Assert.Equal("01 Jan 2027", window.FindControl<Button>("PlannerDateButton")!.Content);
            vm.SetPlannerDate(new DateTime(2028, 3, 1));
            Click(window, "PlannerPreviousDay");
            Assert.Equal(new DateTime(2028, 2, 29), vm.LocalDate!.Value.Date);
        }
        finally { Close(window); }
    }

    /// <summary>The date opens a calendar on the plan's date; a day chosen with Enter (or a tap) becomes the plan's date.</summary>
    [AvaloniaFact]
    public async Task TheDateOpensACalendarThatSetsThePlansDate()
    {
        var (vm, window, _) = await CreatePlannerAsync(London);
        try
        {
            var button = window.FindControl<Button>("PlannerDateButton")!;
            var flyout = Assert.IsType<Flyout>(button.Flyout);
            Click(window, "PlannerDateButton");
            Assert.True(flyout.IsOpen);
            var calendar = Assert.IsType<Calendar>(flyout.Content);
            Assert.Equal(new DateTime(2026, 3, 5), calendar.SelectedDate);

            // Opened, the calendar has the keyboard: its arrow keys move the day, Enter chooses it.
            Assert.True(calendar.IsKeyboardFocusWithin, "the opened calendar should take the keyboard");
            calendar.SelectedDate = new DateTime(2026, 4, 18);
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Settle(window);
            Assert.False(flyout.IsOpen);
            Assert.Equal(new DateTime(2026, 4, 18), vm.LocalDate!.Value.Date);
            Assert.Equal("21:00", vm.TimeText);

            // Closed without choosing, the plan keeps its date.
            Click(window, "PlannerDateButton");
            calendar.SelectedDate = new DateTime(2026, 5, 1);
            flyout.Hide();
            Settle(window);
            Assert.Equal(new DateTime(2026, 4, 18), vm.LocalDate!.Value.Date);
        }
        finally { Close(window); }
    }

    /// <summary>The time arrows move the clock exactly 30 minutes, carrying the date across midnight both ways.</summary>
    [AvaloniaFact]
    public async Task TheTimeArrowsStepThirtyMinutesAcrossMidnight()
    {
        var (vm, window, _) = await CreatePlannerAsync(London);
        try
        {
            var before = PlanInstant(vm);
            Click(window, "PlannerNextTime");
            Assert.Equal("21:30", vm.TimeText);
            Assert.Equal(Duration.FromMinutes(30), PlanInstant(vm) - before);
            Click(window, "PlannerPreviousTime");
            Assert.Equal("21:00", vm.TimeText);
            Assert.Equal(before, PlanInstant(vm));

            vm.SetPlannerTime(new TimeSpan(23, 45, 0));
            Click(window, "PlannerNextTime");
            Assert.Equal("00:15", vm.TimeText);
            Assert.Equal(new DateTime(2026, 3, 6), vm.LocalDate!.Value.Date);
            Assert.Equal("06 Mar 2026", window.FindControl<Button>("PlannerDateButton")!.Content);
            Click(window, "PlannerPreviousTime");
            Assert.Equal("23:45", vm.TimeText);
            Assert.Equal(new DateTime(2026, 3, 5), vm.LocalDate!.Value.Date);
            Assert.Equal(Instant.FromUtc(2026, 3, 5, 23, 45), PlanInstant(vm));
        }
        finally { Close(window); }
    }

    /// <summary>The time opens a time picker on the plan's time; accepting it sets the plan's time, on the same date.</summary>
    [AvaloniaFact]
    public async Task TheTimeOpensATimePickerThatSetsThePlansTime()
    {
        var (vm, window, _) = await CreatePlannerAsync(London);
        try
        {
            var flyout = Assert.IsType<Flyout>(window.FindControl<Button>("PlannerTimeButton")!.Flyout);
            Click(window, "PlannerTimeButton");
            Assert.True(flyout.IsOpen);
            var picker = Assert.IsType<TimePickerPresenter>(flyout.Content);
            Assert.Equal(new TimeSpan(21, 0, 0), picker.Time);

            picker.Time = new TimeSpan(5, 40, 0);
            var accept = picker.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PART_AcceptButton");
            accept.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle(window);
            Assert.False(flyout.IsOpen);
            Assert.Equal("05:40", vm.TimeText);
            Assert.Equal(new DateTime(2026, 3, 5), vm.LocalDate!.Value.Date);
            Assert.Equal(Instant.FromUtc(2026, 3, 5, 5, 40), PlanInstant(vm));
        }
        finally { Close(window); }
    }

    /// <summary>Now is the present moment in the plan's own time zone: here, British Summer Time.</summary>
    [Fact]
    public async Task NowIsThePresentInThePlansTimeZone()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, CreatePlannerStore(London), new FakeExporter(),
            clock: new FixedClock(Instant.FromUtc(2026, 7, 1, 6, 7)));
        await vm.InitializeAsync();
        vm.SetPlannerDate(new DateTime(2026, 1, 10));
        Assert.Equal("GMT", vm.PlannerTimeZoneText);

        vm.NowCommand.Execute(null);

        Assert.Equal(new DateTime(2026, 7, 1), vm.LocalDate!.Value.Date);
        Assert.Equal("07:07", vm.TimeText);
        Assert.Equal(Instant.FromUtc(2026, 7, 1, 6, 7), vm.Session.Instant);
        Assert.Equal("01 Jul 2026", vm.PlannerDateText);
        Assert.Equal("BST", vm.PlannerTimeZoneText);
    }

    /// <summary>
    /// The zone is named by what it resolves to: the system zone by the system's zone, never "System"; an
    /// explicit zone by its short name at the plan's date, with its identifier in the detail.
    /// </summary>
    [Fact]
    public async Task TheTimeZoneIsNamedByWhatItResolvesTo()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var system = CreateViewModel(new FakePlanning(catalogue), catalogue,
            CreatePlannerStore(new AppSettings(SelectedTimeZoneId: AppSettings.UseSystemTimeZoneId)), new FakeExporter());
        await system.InitializeAsync();
        var resolver = new TimeZoneResolver();
        var machine = TimeZoneDisplay.Describe(resolver.Resolve(resolver.MachineTimeZoneId), system.Session.Instant);
        Assert.Equal(machine.ShortName, system.PlannerTimeZoneText);
        Assert.DoesNotContain("system", system.PlannerTimeZoneText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("system", system.PlannerTimeZoneDetail, StringComparison.OrdinalIgnoreCase);

        var london = CreateViewModel(new FakePlanning(catalogue), catalogue, CreatePlannerStore(London), new FakeExporter());
        await london.InitializeAsync();
        Assert.Equal("GMT", london.PlannerTimeZoneText);
        Assert.StartsWith("Europe/London, UTC.", london.PlannerTimeZoneDetail);
        london.SetPlannerDate(new DateTime(2026, 7, 1));
        Assert.Equal("BST", london.PlannerTimeZoneText);
        Assert.Contains("UTC+1", london.PlannerTimeZoneDetail);

        var tokyo = CreateViewModel(new FakePlanning(catalogue), catalogue,
            CreatePlannerStore(new AppSettings(SelectedTimeZoneId: "Asia/Tokyo")), new FakeExporter());
        await tokyo.InitializeAsync();
        Assert.Equal("JST", tokyo.PlannerTimeZoneText);
    }

    /// <summary>Save PNG and Copy PNG are the Export menu's two items, running the export actions they always ran.</summary>
    [AvaloniaFact]
    public async Task ExportIsOneMenuOfTheExistingActions()
    {
        var exporter = new FakeExporter();
        var (vm, window, _) = await CreatePlannerAsync(London, exporter: exporter);
        try
        {
            var export = window.FindControl<Button>("PlannerExportMenu")!;
            Assert.Contains(export.GetLogicalDescendants().OfType<TextBlock>(), text => text.Text == "Export");
            var menu = Assert.IsType<MenuFlyout>(export.Flyout);
            var items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(["Save PNG", "Copy PNG"], items.Select(item => item.Header as string));
            // Each item calls the handler the separate buttons called.
            var markup = TestMarkup.LoadMainWindow().Descendants().Where(element => element.Name.LocalName == "MenuItem").ToArray();
            Assert.Equal("ExportPngClick", markup.Single(element => element.Attribute("Header")?.Value == "Save PNG").Attribute("Click")?.Value);
            Assert.Equal("CopyPngClick", markup.Single(element => element.Attribute("Header")?.Value == "Copy PNG").Attribute("Click")?.Value);
            Assert.Equal(vm.CanExport, export.IsEnabled);

            Click(window, "PlannerExportMenu");
            Assert.True(menu.IsOpen);
            items[1].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            for (var i = 0; i < 100 && exporter.RenderCount == 0; i++)
            {
                Settle(window);
                await Task.Delay(20);
            }
            Assert.Equal(1, exporter.RenderCount);
        }
        finally { Close(window); }
    }
}
