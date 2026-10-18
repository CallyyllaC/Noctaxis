using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using Noctaxis.Desktop.Diagnostics;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Desktop.Views;

namespace Noctaxis.Desktop.Tests;

/// <summary>Settings → Diagnostics → Export Logs, as the user reaches and uses it.</summary>
public sealed partial class MainViewModelTests
{
    private sealed class ExportDialogs : IPlannerDialogService, IDiagnosticsExportDialog
    {
        public Func<string, IDiagnosticsExportDestination?> Choose { get; set; } = _ => null;
        public List<string> SuggestedNames { get; } = [];
        public Task<IDiagnosticsExportDestination?> PickDiagnosticsExportDestinationAsync(string suggestedFileName, CancellationToken cancellationToken = default)
        {
            SuggestedNames.Add(suggestedFileName);
            return Task.FromResult(Choose(suggestedFileName));
        }
        public Task<LocationSearchResult?> ShowLocationSearchAsync(string? initialQuery = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<LocationSearchResult?>(null);
        public Task<SavedLocationEdit?> ShowSavedLocationEditAsync(SavedLocation location, bool isCreateMode = false, CancellationToken cancellationToken = default) =>
            Task.FromResult<SavedLocationEdit?>(null);
    }

    private sealed class FileDestination(string path, Func<Stream>? open = null) : IDiagnosticsExportDestination
    {
        public bool Deleted { get; private set; }
        public string Name => Path.GetFileName(path);
        public Task<Stream> OpenWriteAsync() => Task.FromResult(open?.Invoke() ?? new FileStream(path, FileMode.Create, FileAccess.Write));
        public Task DeleteAsync() { Deleted = true; File.Delete(path); return Task.CompletedTask; }
        public void Dispose() { }
    }

    /// <summary>A stream that accepts a little and then fails, as a full or disconnected disk does.</summary>
    private sealed class FailingStream : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("the device is not ready");
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("the device is not ready");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("the device is not ready"));
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("the device is not ready"));
    }

    private static (MainViewModel Vm, ExportDialogs Dialogs, LogTestDirectory Logs, RollingFileLoggerProvider Provider) CreateExportViewModel()
    {
        var catalogue = new OpenNgcTargetCatalogue();
        var dialogs = new ExportDialogs();
        var vm = CreateViewModel(new FakePlanning(catalogue), catalogue, CreateSupporterStore(new AppSettings()), new FakeExporter(), dialogs: dialogs);
        var logs = new LogTestDirectory();
        var time = ManualTimeProvider.AtLocal(LogTestZones.PlusOne, new DateTime(2026, 10, 17, 21, 0, 0));
        using (var yesterday = logs.Provider(time)) yesterday.CreateLogger("Noctaxis.Test").LogInformation("Yesterday");
        time.SetLocal(new DateTime(2026, 10, 18, 10, 0, 0));
        var provider = logs.Provider(time);
        provider.CreateLogger("Noctaxis.Test").LogInformation("Today");
        vm.DiagnosticsExport = new DiagnosticsExportService(logs.Path, () => provider.FilePath, time);
        return (vm, dialogs, logs, provider);
    }

    [AvaloniaFact]
    [CoversSettingsInput("ExportLogsCommand")]
    public async Task ExportLogsIsReachableFromSettingsDiagnosticsForEveryone()
    {
        var (vm, dialogs, logs, provider) = CreateExportViewModel();
        using var _ = logs;
        using var __ = provider;
        await vm.InitializeAsync();
        var window = new MainWindow { Width = 1280, Height = 900, DataContext = vm };
        window.Show();
        try
        {
            ((TabControl)((Grid)window.Content!).Children[1]).SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            var tab = window.SelectSettingsCategory("Diagnostics");
            Assert.True(tab.IsVisible);
            Assert.False(vm.IsDeveloperDiagnosticsEnabled);
            Assert.False(window.FindControl<StackPanel>("DeveloperDiagnostics")!.IsEffectivelyVisible);

            var button = window.FindControl<Button>("ExportLogsButton")!;
            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal("Export Logs", button.Content);
            Assert.Same(vm.ExportLogsCommand, button.Command);
            var peer = ControlAutomationPeer.CreatePeerForElement(button);
            Assert.Equal("Export Logs", peer.GetName());
            const string note = "Exports available application logs and basic diagnostic information to a ZIP file. " +
                                "The archive may contain file paths or location information. Review it before sharing.";
            Assert.Equal(note, AutomationProperties.GetHelpText(button));
            Assert.Equal(note, window.FindControl<TextBlock>("ExportLogsNote")!.Text);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible &&
                text.Text == "Export application logs to help investigate crashes, errors and unexpected behaviour.");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible &&
                text.Classes.Contains("settingsTitle") && text.Text == "Diagnostics");

            // Pressing it opens the platform's Save File dialog with a dated name; cancelling it does nothing.
            button.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["Noctaxis-Diagnostics-2026-10-18-1000.zip"], dialogs.SuggestedNames);
            Assert.False(window.FindControl<TextBlock>("ExportLogsStatus")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    [CoversSettingsInput("ExportLogsCommand")]
    public async Task CancellingTheExportDialogChangesNothing()
    {
        var (vm, dialogs, logs, provider) = CreateExportViewModel();
        using var _ = logs;
        using var __ = provider;
        var before = logs.Snapshot();

        await vm.ExportLogsCommand.ExecuteAsync(null);

        Assert.Single(dialogs.SuggestedNames);
        Assert.Equal(string.Empty, vm.DiagnosticsExportStatus);
        Assert.False(vm.HasOperationalNotice);
        Assert.False(vm.IsExportingLogs);
        Assert.Equal(before, logs.Snapshot());
    }

    [AvaloniaFact]
    [CoversSettingsInput("ExportLogsCommand")]
    public async Task ExportLogsWritesTheChosenZipAndSaysWhere()
    {
        var (vm, dialogs, logs, provider) = CreateExportViewModel();
        using var _ = logs;
        using var __ = provider;
        var target = Path.Combine(Path.GetDirectoryName(logs.Path)!, "Exports ünïcødé", "Noctaxis-Diagnostics-2026-10-18-1000.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "an older export, overwritten by the user's choice");
        dialogs.Choose = _ => new FileDestination(target);
        var before = logs.Snapshot();

        await vm.ExportLogsCommand.ExecuteAsync(null);

        Assert.Equal("Logs exported to Noctaxis-Diagnostics-2026-10-18-1000.zip.", vm.DiagnosticsExportStatus);
        Assert.False(vm.HasOperationalNotice);
        Assert.False(vm.IsExportingLogs);
        Assert.Equal(["archived/Noctaxis-2026-10-17.zip", "diagnostics.json", "sessions/Noctaxis-2026-10-18_10-00-00.log"],
            LogTestDirectory.Entries(target));
        Assert.Contains("Today", LogTestDirectory.EntryText(target, "sessions/Noctaxis-2026-10-18_10-00-00.log"));
        Assert.Equal(before.Keys.Order(), logs.Snapshot().Keys.Order());
        Assert.Equal(before.Where(file => !file.Key.EndsWith(".log", StringComparison.Ordinal)),
            logs.Snapshot().Where(file => !file.Key.EndsWith(".log", StringComparison.Ordinal)));
    }

    [AvaloniaTheory]
    [CoversSettingsInput("ExportLogsCommand")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedExportIsReportedAndLeavesTheLogsAsTheyWere(bool failsWhileWriting)
    {
        var (vm, dialogs, logs, provider) = CreateExportViewModel();
        using var _ = logs;
        using var __ = provider;
        var target = Path.Combine(Path.GetDirectoryName(logs.Path)!, "export.zip");
        var destination = new FileDestination(target, () => failsWhileWriting ? new FailingStream() : throw new IOException("the device is not ready"));
        dialogs.Choose = _ => destination;
        var before = logs.Snapshot();

        await vm.ExportLogsCommand.ExecuteAsync(null);

        Assert.Equal("Logs could not be exported: the device is not ready", vm.DiagnosticsExportStatus);
        Assert.True(vm.HasOperationalNotice);
        Assert.Equal(vm.DiagnosticsExportStatus, vm.OperationalNotice);
        Assert.False(vm.IsExportingLogs);
        // A partly written export is removed; one never opened is left alone.
        Assert.Equal(failsWhileWriting, destination.Deleted);
        Assert.Equal(before, logs.Snapshot());
        Assert.True(provider.CreateLogger("T").IsEnabled(LogLevel.Information));
    }
}
