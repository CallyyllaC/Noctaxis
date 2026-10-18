using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Noctaxis.Core.Localisation;
using Noctaxis.Desktop.Diagnostics;

namespace Noctaxis.Desktop.ViewModels;

/// <summary>The platform's Save File dialog, asked for where Export Logs should write its ZIP.</summary>
public interface IDiagnosticsExportDialog
{
    /// <returns>The chosen file, or null when the dialog was cancelled.</returns>
    Task<IDiagnosticsExportDestination?> PickDiagnosticsExportDestinationAsync(string suggestedFileName,
        CancellationToken cancellationToken = default);
}

/// <summary>A file chosen in the Save File dialog.</summary>
public interface IDiagnosticsExportDestination : IDisposable
{
    string Name { get; }
    Task<Stream> OpenWriteAsync();
    Task DeleteAsync();
}

/// <summary>Settings → Diagnostics → Export Logs.</summary>
public partial class MainViewModel
{
    /// <summary>What Export Logs reads: this process's logs directory, or, in tests, one they own.</summary>
    internal DiagnosticsExportService DiagnosticsExport
    {
        get => _diagnosticsExport ??= DiagnosticsExportService.ForCurrentProcess();
        set => _diagnosticsExport = value;
    }
    private DiagnosticsExportService? _diagnosticsExport;

    [ObservableProperty] private string _diagnosticsExportStatus = string.Empty;
    [ObservableProperty] private bool _isExportingLogs;

    /// <summary>
    /// Asks where to save, then builds the export off the UI thread into a temporary file and copies it there, so a
    /// failure never leaves a partial export behind. Cancelling the dialog does nothing at all.
    /// </summary>
    [RelayCommand]
    private async Task ExportLogs()
    {
        if (_dialogs is not IDiagnosticsExportDialog dialog) return;
        var export = DiagnosticsExport;
        using var destination = await dialog.PickDiagnosticsExportDestinationAsync(export.SuggestedFileName());
        if (destination is null) return;
        IsExportingLogs = true;
        DiagnosticsExportStatus = Strings.Get("Settings_Diagnostics_ExportLogs_InProgress");
        var package = Path.Combine(Path.GetTempPath(), $"Noctaxis-Diagnostics-{Guid.NewGuid():N}.zip.tmp");
        var destinationOpened = false;
        try
        {
            var summary = await Task.Run(() =>
            {
                using var file = new FileStream(package, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                return export.WritePackage(file);
            });
            await using (var input = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var output = await destination.OpenWriteAsync())
            {
                destinationOpened = true;
                await input.CopyToAsync(output);
                if (output.CanSeek) output.SetLength(output.Position);
            }
            _logger.LogInformation("Diagnostics exported: {Archives} daily archive(s), {Sessions} session log(s)",
                summary.DailyArchives, summary.SessionLogs);
            DiagnosticsExportStatus = Strings.Format("Settings_Diagnostics_ExportLogs_Succeeded_Format", destination.Name);
        }
        // Whatever stopped the export, it is reported and the application carries on; the logs were only read.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Diagnostics export failed");
            if (destinationOpened)
            {
                try { await destination.DeleteAsync(); }
                catch (Exception deleteFailure) when (deleteFailure is IOException or UnauthorizedAccessException
                    or InvalidOperationException or NotSupportedException) { }
            }
            var message = Strings.Format("Settings_Diagnostics_ExportLogs_Failed_Format", ex.Message);
            DiagnosticsExportStatus = message;
            ShowOperationalNotice(message);
        }
        finally
        {
            IsExportingLogs = false;
            try { File.Delete(package); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
