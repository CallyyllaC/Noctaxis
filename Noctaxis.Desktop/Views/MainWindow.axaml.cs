using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using Avalonia.Input.Platform;
using Avalonia.Input;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Views;

public partial class MainWindow : Window
{
    private MainViewModel _viewModel = null!;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel, DesktopDialogService dialogs) : this()
    {
        _viewModel = viewModel;
        dialogs.Owner = this;
        DataContext = viewModel;
        PlannerMap.PreviewCoordinateChanged += (_, coordinate) => _viewModel.PreviewObserverLocation(coordinate);
        PlannerMap.CoordinateCommitted += (_, coordinate) => _viewModel.CommitUnresolvedObserverLocation(coordinate);
        PlannerMap.InteractionStateChanged += (_, interacting) => _viewModel.SetLocationInteraction(interacting);
        PlannerMap.SaveCurrentPinRequested += async (_, _) => await _viewModel.SaveLocationCommand.ExecuteAsync(null);
    }

    private async void ExportPngClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Noctaxis scouting card",
            SuggestedFileName = "noctaxis-scouting-card.png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }]
        });
        if (file is null) return;
        try
        {
            var png = await _viewModel.CreateExportPngAsync(CancellationToken.None);
            await File.WriteAllBytesAsync(file.Path.LocalPath, png);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex) { _viewModel.ReportExportDestinationFailure(ex); }
    }

    private async void CopyPngClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var png = await _viewModel.CreateExportPngAsync(CancellationToken.None);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) throw new InvalidOperationException("The system clipboard is unavailable.");
            var bitmap = new Bitmap(new MemoryStream(png, writable: false));
            await clipboard.SetBitmapAsync(bitmap);
            await clipboard.FlushAsync();
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        catch (Exception ex) { _viewModel.ReportExportDestinationFailure(ex); }
    }

    private async void CopyTerrainDebugClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(_viewModel.TerrainDebugText);
        await clipboard.FlushAsync();
    }

    private void TimeSliderReleased(object? sender, PointerReleasedEventArgs e) => _viewModel.CommitTemporalPreview();
    private async void PlannerSearchResultSelected(object? sender, LocationSearchResult result) =>
        await _viewModel.UsePlannerSearchResultCommand.ExecuteAsync(result);

    private void MinimizeWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestoreWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && MaximizeWindowButton is not null)
            MaximizeWindowButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }
}
