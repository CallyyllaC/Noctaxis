using Noctaxis.Core.Localisation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Media.Imaging;
using Avalonia.Input.Platform;
using Avalonia.Input;
using Noctaxis.Desktop.Controls;
using Noctaxis.Desktop.ViewModels;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Views;

public partial class MainWindow : Window
{
    private MainViewModel _viewModel = null!;
    private bool _closeCleanupStarted;
    private bool _closeCleanupComplete;
    private readonly TaskCompletionSource _closeCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task CloseCleanup => _closeCleanup.Task;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel, DesktopDialogService dialogs) : this()
    {
        _viewModel = viewModel;
        dialogs.Owner = this;
        DataContext = viewModel;
        PlannerMap.Layers.PreferencesEdited += (_, _) =>
        {
            viewModel.PreviewTerrainRelief(PlannerMap.Layers.ShowTerrainRelief, PlannerMap.Layers.TerrainReliefOpacityPercent);
            viewModel.PreviewPlannerLayers(PlannerMap.Layers.ShowLightPollution, PlannerMap.Layers.LightPollutionOpacityPercent);
        };
        PlannerMap.Layers.PersistAsync = viewModel.CommitPlannerLayersAsync;
        if (viewModel.TerrainReliefSource is { } terrain) PlannerMap.ConfigureTerrainRelief(terrain);
        if (viewModel.LightPollutionInstallation is { } lightPollution)
        {
            PlannerMap.ConfigureLightPollution(lightPollution);
            if (PlannerMap.LightPollutionProvider is { } reader) viewModel.UseLightPollutionReader(reader);
        }
        PlannerMap.PreviewCoordinateChanged += (_, coordinate) => _viewModel.PreviewObserverLocation(coordinate);
        PlannerMap.CoordinateCommitted += (_, coordinate) => _viewModel.CommitUnresolvedObserverLocation(coordinate);
        PlannerMap.InteractionStateChanged += (_, interacting) => _viewModel.SetLocationInteraction(interacting);
        PlannerMap.SaveCurrentPinRequested += async (_, _) => await _viewModel.SaveLocationCommand.ExecuteAsync(null);
        PlannerMap.OpenInMapsRequested += (_, coordinate) => _viewModel.OpenMapCoordinateCommand.Execute(coordinate);

        // The time bar's pickers open on the plan's own date and time and write back through the view model:
        // the calendar commits a day when it is clicked or tapped, or with Enter; Escape leaves the plan as it was.
        DateFlyout.Opening += (_, _) =>
        {
            var date = viewModel.LocalDate?.Date ?? DateTime.Today;
            PlannerDateCalendar.DisplayDate = date;
            PlannerDateCalendar.SelectedDate = date;
        };
        DateFlyout.Opened += (_, _) => PlannerDateCalendar.Focus(NavigationMethod.Tab);
        PlannerDateCalendar.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            // A day was clicked or tapped (the calendar handles the release itself, so this listens anyway).
            if (e.Source is Avalonia.Visual source && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Avalonia.Controls.Primitives.CalendarDayButton>(source, includeSelf: true) is not null)
                CommitPickedDate();
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        PlannerDateCalendar.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            CommitPickedDate();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        TimeFlyout.Opening += (_, _) =>
        {
            if (LocalTimePatternHelper.TryParse(viewModel.TimeText, out var time)) PlannerTimePicker.Time = new TimeSpan(time.Hour, time.Minute, 0);
        };
        PlannerTimePicker.Confirmed += (_, _) =>
        {
            viewModel.SetPlannerTime(PlannerTimePicker.Time);
            TimeFlyout.Hide();
        };
        PlannerTimePicker.Dismissed += (_, _) => TimeFlyout.Hide();

        // The Picture-in-Picture map: created while enabled and released when not, so a disabled one
        // costs nothing; sized to the map; and the only input it takes is the wheel and its two buttons.
        WirePlanImageDrop();
        PlannerPipInput.PointerWheelChanged += PipWheel;
        PlannerPipInput.PointerPressed += (_, e) => e.Handled = true;
        // The wheel over the Camera frame turns the focal length, and only that: it is taken on its way in, so
        // neither the frame's own controls nor the map beneath see it.
        PlannerTerrainFrame.AddHandler(PointerWheelChangedEvent, CameraFrameWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PlannerMap.SizeChanged += (_, _) => FitMapOverlays();
        PlannerMapBottomLeft.SizeChanged += (_, _) => FitMapOverlays();
        WireOverlayResizing();
        ((Flyout)ShotViabilityButton.Flyout!).Opening += (_, _) => FitShotViabilityDetails();
        PlannerTopBar.SizeChanged += (_, _) => FitTopBar();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsPlannerPipEnabled)) UpdatePip();
            // Turned off, the Picture-in-Picture map keeps the centre it had, so that is what is saved.
            else if (e.PropertyName == nameof(MainViewModel.PlannerPipFollowPin) && !viewModel.PlannerPipFollowPin) SavePipView();
            else if (e.PropertyName == nameof(MainViewModel.PlannerOverlaySizes)) FitMapOverlays();
        };
        viewModel.PlannerPipViewReset += (_, _) => RestorePipView();
        UpdatePip();
    }

    private Flyout DateFlyout => (Flyout)PlannerDateButton.Flyout!;
    private Flyout TimeFlyout => (Flyout)PlannerTimeButton.Flyout!;

    private void CommitPickedDate()
    {
        if (PlannerDateCalendar.SelectedDate is { } date) _viewModel.SetPlannerDate(date);
        DateFlyout.Hide();
    }

    private Controls.PlannerMapView? _pip;
    private double _pipWheel;
    internal Controls.PlannerMapView? PipForTesting => _pip;

    // A width being dragged, as a share of the map: shown at once, saved as the preference when the drag ends.
    private double? _pipResizeFraction, _frameResizeFraction;
    private double _resizeStartWidth;
    internal MapInstrumentLayout.Result OverlayLayoutForTesting { get; private set; }

    /// <summary>
    /// Sizes the PiP and the Camera frame from the user's preferred shares of the map's own width (the room the
    /// docked panels leave), fitted together by <see cref="MapInstrumentLayout"/> so they never overlap. Only
    /// sizes change: no map, layer or frame data is touched.
    /// </summary>
    /// <summary>The shot-viability details' preferred size: wide enough that explanations wrap rarely, tall
    /// enough for every metric at the usual text size.</summary>
    internal const double ShotViabilityDetailsWidth = 440, ShotViabilityDetailsHeight = 640;
    // Kept clear of the window's edges, so the popup never runs off a small window; it scrolls instead.
    private const double ShotViabilityDetailsMargin = 48;

    /// <summary>
    /// Sizes the shot-viability details for the window as it is now: the preferred size, made smaller to fit a
    /// small or resized window, never smaller than reads at all. The popup itself is kept on screen by its
    /// placement; this keeps its content inside it.
    /// </summary>
    internal void FitShotViabilityDetails()
    {
        var client = ClientSize;
        ShotViabilityDetails.Width = Math.Max(240, Math.Min(ShotViabilityDetailsWidth, client.Width - ShotViabilityDetailsMargin));
        ShotViabilityDetails.MaxHeight = Math.Max(160, Math.Min(ShotViabilityDetailsHeight, client.Height - ShotViabilityDetailsMargin * 2));
    }

    private void FitMapOverlays()
    {
        var map = PlannerMap.Bounds.Size;
        if (map.Width > 0 && map.Height > 0)
        {
            var saved = _viewModel.PlannerOverlaySizes;
            var preferred = saved with
            {
                PipWidthFraction = _pipResizeFraction ?? saved.PipWidthFraction,
                CameraFrameWidthFraction = _frameResizeFraction ?? saved.CameraFrameWidthFraction
            };
            var layout = MapInstrumentLayout.Resolve(map, preferred, _viewModel.IsPlannerPipEnabled, BottomLeftRowHeight);
            OverlayLayoutForTesting = layout;
            var content = Math.Round(layout.CameraFrameWidth - CameraFrameChrome());
            if (Math.Abs(PlannerCameraFrameContent.Width - content) > .5) PlannerCameraFrameContent.Width = content;
            var side = Math.Round(layout.PipSide);
            if (layout.PipShown && Math.Abs(PlannerPip.Width - side) > .5) PlannerPip.Width = PlannerPip.Height = side;
            PlannerPip.IsVisible = _viewModel.IsPlannerPipEnabled && layout.PipShown;
        }
        else PlannerPip.IsVisible = _viewModel.IsPlannerPipEnabled;
        // The main map's credits leave the bottom-left row clear: its 12 margin and an 8 gap.
        PlannerMap.AttributionReservedLeft = PlannerMapBottomLeft.Bounds.Width + 12;
    }

    // The Camera frame card's padding and border across its content.
    private double CameraFrameChrome() => PlannerTerrainFrame.Padding.Left + PlannerTerrainFrame.Padding.Right +
        PlannerTerrainFrame.BorderThickness.Left + PlannerTerrainFrame.BorderThickness.Right;

    /// <summary>
    /// The bottom-left row's height with a Camera frame card of <paramref name="cardWidth"/>: the card's fixed
    /// parts as measured now, plus its frame at that width.
    /// </summary>
    private double BottomLeftRowHeight(double cardWidth)
    {
        var card = PlannerTerrainFrame.Bounds.Height;
        var view = PlannerCameraFrameView.Bounds.Height;
        // Layout rounds the frame's height up to whole pixels, so the estimate does too.
        var frame = Math.Ceiling(PlannerCameraFrameView.HeightAt(cardWidth - CameraFrameChrome()));
        return card - view + frame;
    }

    // The bar's fixed parts at their ease: measured while the bar is not compact, the width below which it is.
    private double _topBarAtEase;

    /// <summary>
    /// Keeps the bar on one line. The location takes what is left and gives it up first; when even that is
    /// not enough for the rest at their ease, the bar becomes compact (closer spacing, no minimum widths on the
    /// date and time) rather than hiding, shortening or wrapping anything.
    /// </summary>
    private void FitTopBar()
    {
        var compact = PlannerTopBarHost.Classes.Contains("compact");
        var fixedWidth = PlannerTopBar.Children.Where(child => child.IsVisible && !ReferenceEquals(child, PlannerLocationCopy))
            .Sum(child => child.DesiredSize.Width) + PlannerTopBar.ColumnSpacing * (PlannerTopBar.ColumnDefinitions.Count - 1);
        if (!compact) _topBarAtEase = fixedWidth;
        var shouldBeCompact = PlannerTopBar.Bounds.Width > 0 && PlannerTopBar.Bounds.Width < _topBarAtEase;
        if (shouldBeCompact != compact) PlannerTopBarHost.Classes.Set("compact", shouldBeCompact);
    }

    private void ResizeOverlay(bool pip, double width)
    {
        var map = PlannerMap.Bounds.Width;
        if (map <= 0) return;
        var fraction = MapInstrumentLayout.FractionOf(width,
            pip ? MapInstrumentLayout.MinimumPipSide : MapInstrumentLayout.MinimumCameraFrameWidth, map);
        if (pip) _pipResizeFraction = fraction;
        else _frameResizeFraction = fraction;
        FitMapOverlays();
    }

    // The preference saved is what the user asked for, within the drawable bounds, not what the map had room
    // for just now; the other instrument's preference is left alone.
    private void CommitOverlayResize()
    {
        if (_pipResizeFraction is null && _frameResizeFraction is null) return;
        _viewModel.SetPlannerOverlaySizes(_pipResizeFraction, _frameResizeFraction);
        _pipResizeFraction = _frameResizeFraction = null;
        FitMapOverlays();
    }

    private double PreferredWidth(bool pip)
    {
        var (side, frame) = MapInstrumentLayout.Preferred(PlannerMap.Bounds.Width, _viewModel.PlannerOverlaySizes);
        return pip ? side : frame;
    }

    private void WireOverlayResizing()
    {
        void Wire(OverlayResizeGrip grip, bool pip)
        {
            grip.ResizeStarted += (_, _) => _resizeStartWidth = PreferredWidth(pip);
            grip.Resizing += (_, across) => ResizeOverlay(pip, _resizeStartWidth + across);
            grip.ResizeCompleted += (_, _) => CommitOverlayResize();
            grip.Stepped += (_, step) =>
            {
                ResizeOverlay(pip, PreferredWidth(pip) + step);
                CommitOverlayResize();
            };
        }
        Wire(PlannerPipResizeGrip, pip: true);
        Wire(PlannerCameraFrameResizeGrip, pip: false);
    }

    private void UpdatePip()
    {
        FitMapOverlays();
        if (_viewModel.IsPlannerPipEnabled == _pip is not null) return;
        if (_pip is null) CreatePip();
        else _ = ReleasePipAsync();
    }

    private void CreatePip()
    {
        var pip = PlannerMap.CreatePictureInPicture();
        // The Planner's shared state, the same as the main map's: the pin, the plan and the camera.
        void Bind(AvaloniaProperty property, string path) => pip.Bind(property, new Avalonia.Data.Binding(path));
        Bind(Controls.NoctaxisMapView.ObserverProperty, nameof(MainViewModel.Observer));
        Bind(Controls.NoctaxisMapView.SnapshotProperty, nameof(MainViewModel.Snapshot));
        Bind(Controls.NoctaxisMapView.FramingGuideProperty, nameof(MainViewModel.CameraFramingGuide));
        Bind(Controls.NoctaxisMapView.FramingVisibilityProperty, nameof(MainViewModel.CameraFramingVisibility));
        Bind(Controls.NoctaxisMapView.FramingSettingsProperty, nameof(MainViewModel.CameraFramingMapSettings));
        Bind(Controls.NoctaxisMapView.ShowCameraOverlayProperty, nameof(MainViewModel.CameraOverlayReady));
        Bind(Controls.NoctaxisMapView.PinActivityProperty, nameof(MainViewModel.PlannerPinActivity));
        // Its own view: its layers and whether it follows the pin.
        Bind(Controls.NoctaxisMapView.TerrainReliefPreferencesProperty, nameof(MainViewModel.PlannerPipTerrainRelief));
        Bind(Controls.NoctaxisMapView.LightPollutionPreferencesProperty, nameof(MainViewModel.PlannerPipLightPollution));
        Bind(Controls.NoctaxisMapView.FollowObserverProperty, nameof(MainViewModel.PlannerPipFollowPin));
        Bind(Controls.NoctaxisMapView.ShowRoadsAndLabelsProperty, nameof(MainViewModel.PlannerPipShowRoadsAndLabels));
        Bind(Controls.NoctaxisMapView.EnhanceReliefContrastProperty, nameof(MainViewModel.PlannerPipEnhanceContrast));
        // The main map's own terrain relief source and light pollution reader, so shaded relief and the
        // decoded atlas are computed and held once for both maps.
        pip.ShareEnvironmentalSources(PlannerMap);
        var saved = _viewModel.PlannerPip;
        pip.ShowView(new(saved.EffectiveCentre ?? _viewModel.Observer, saved.EffectiveResolution));
        PlannerPipHost.Content = pip;
        _pip = pip;
        PlannerMap.AttributionCompanion = pip;
    }

    /// <summary>
    /// The saved view, once the settings have loaded (the window built the map before then) and after Reset
    /// Planner layout: its zoom, saved or shipped, and its centre unless it follows the pin.
    /// </summary>
    private void RestorePipView()
    {
        if (_pip is null) return;
        var saved = _viewModel.PlannerPip;
        // By the saved Follow, which the map's own binding has not yet taken.
        _pip.ShowView(new(saved.EffectiveCentre ?? _viewModel.Observer, saved.EffectiveResolution), centreOnPin: saved.FollowPin);
    }

    private async Task ReleasePipAsync()
    {
        if (_pip is not { } pip) return;
        _pip = null;
        PlannerMap.AttributionCompanion = null;
        PlannerPipHost.Content = null;
        await pip.ReleaseLightPollutionAsync();
    }

    private void SavePipView()
    {
        if (_pip?.CurrentView is { } view) _viewModel.SavePlannerPipView(view.Centre, view.Resolution);
    }

    /// <summary>The wheel over the Picture-in-Picture map zooms it, about its centre, and nothing else.</summary>
    private void PipWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        if (_pip is null || e.Delta.Y == 0) return;
        _pipWheel += e.Delta.Y;
        var steps = (int)Math.Truncate(_pipWheel);
        if (steps == 0) return;
        _pipWheel -= steps;
        if (_pip.Zoom(steps)) SavePipView();
    }

    private double _frameWheel;

    /// <summary>The wheel over the Camera frame turns the focal length, a notch at a time, as the lens allows.</summary>
    private void CameraFrameWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
        if (e.Delta.Y == 0) return;
        _frameWheel += e.Delta.Y;
        var notches = (int)Math.Truncate(_frameWheel);
        if (notches == 0) return;
        _frameWheel -= notches;
        _viewModel.StepFocalLength(notches);
    }

    /// <summary>
    /// Switch: the two maps exchange their views — centre, zoom and layers. The pin, the plan, the camera
    /// and everything else the Planner shares stay exactly as they are.
    /// </summary>
    private void SwitchMapsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => SwitchMaps();

    internal void SwitchMaps()
    {
        if (_pip is null || PlannerMap.CurrentView is not { } main || _pip.CurrentView is not { } pip) return;
        _viewModel.SwitchPlannerMapLayers();
        PlannerMap.ShowView(pip, centreOnPin: false);
        _pip.ShowView(main);
        SavePipView();
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _viewModel is null || _closeCleanupComplete) return;

        // Keep the window and Avalonia lifetime alive until the local reader gate and
        // Mapsui render resources have shut down. A second Close while this is pending
        // is coalesced; the final Close below passes through after cleanup completes.
        e.Cancel = true;
        if (_closeCleanupStarted) return;
        _closeCleanupStarted = true;
        _viewModel.CancelEnvironmentalInstall();
        try
        {
            // The application lifetime does not await Exit handlers, so the final save must
            // complete here while the window is still held open. ShutdownAsync cancels planner work,
            // completes any pending debounced save, writes the newest state and then refuses later
            // saves, so nothing finishing during shutdown writes after it; it writes nothing if startup never
            // finished loading, so a failed start cannot overwrite the saved file. An expected
            // filesystem failure is logged by the save pipeline and does not prevent closing.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? persistFailure = null;
            try { await PlannerMap.Layers.CommitSafelyAsync(); }
            catch (Exception ex) { persistFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            try { await _viewModel.ShutdownAsync(); }
            catch (Exception ex) { persistFailure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            // Native render resources and the local reader gate are released even if saving failed.
            // The Picture-in-Picture map borrows the main map's light pollution reader, so it goes first.
            await ReleasePipAsync();
            await PlannerMap.ReleaseLightPollutionAsync();
            persistFailure?.Throw();
            _closeCleanup.TrySetResult();
        }
        catch (Exception ex)
        {
            _closeCleanup.TrySetException(ex);
        }
        finally
        {
            _closeCleanupComplete = true;
            Close();
        }
    }

    /// <summary>
    /// The plan image, from the one renderer: Save PNG and Copy PNG only deliver it. Its map is the Planner map's
    /// own themed basemap with nothing laid over it.
    /// </summary>
    internal Task<byte[]> CreatePlanImageAsync(CancellationToken cancellationToken) =>
        _viewModel.CreateExportPngAsync(cancellationToken, ExportMap);

    private Task<Export.ExportMapImage?> ExportMap(GeoCoordinate centre, int width, int height, CancellationToken cancellationToken) =>
        PlannerMap.VectorBasemapSource is { } source
            ? Export.ExportMapComposer.ComposeAsync(source, centre, width, height, cancellationToken)
            : Task.FromResult<Export.ExportMapImage?>(null);

    private async void ExportPngClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Get("Planner_Export_SaveDialog_Title"),
            SuggestedFileName = "noctaxis-plan.png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType(Strings.Get("Planner_Export_SaveDialog_PngFileType")) { Patterns = ["*.png"] }]
        });
        if (file is null) return;
        try
        {
            var png = await CreatePlanImageAsync(CancellationToken.None);
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(png);
        }
        catch (OperationCanceledException) { }
        // Expected and already explained in the status line; any other failure is reported below.
        catch (ViewModels.ExportNotPossibleException) { }
        catch (Exception ex) { _viewModel.ReportExportDestinationFailure(ex); }
    }

    // ---- Plan image import: three ways in, one importer --------------------------------------------------

    private async void ImportOpenClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("Planner_Import_OpenDialog_Title"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Strings.Get("Planner_Export_SaveDialog_PngFileType")) { Patterns = ["*.png"] }]
        });
        if (files.Count == 0) return;
        await ImportFromFilesAsync(files);
    }

    private async void ImportClipboardClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Avalonia.Input.IAsyncDataTransfer? data = null;
        try { data = TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? await clipboard.TryGetDataAsync() : null; }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException) { }
        await ImportFromClipboardAsync(data);
    }

    internal async Task<ViewModels.PlanImportOutcome> ImportFromFilesAsync(IReadOnlyList<IStorageItem> files) =>
        _viewModel.ImportPlanImage(await Export.PlanImageSources.FromItemsAsync(files));

    internal async Task<ViewModels.PlanImportOutcome> ImportFromClipboardAsync(Avalonia.Input.IAsyncDataTransfer? data) =>
        _viewModel.ImportPlanImage(await Export.PlanImageSources.FromClipboardAsync(data));

    internal async Task<ViewModels.PlanImportOutcome> ImportFromDropAsync(Avalonia.Input.IDataTransfer data) =>
        _viewModel.ImportPlanImage(await Export.PlanImageSources.FromDropAsync(data));

    /// <summary>A plan image dropped anywhere on the Planner: files only, shown as droppable while one is dragged over.</summary>
    private void WirePlanImageDrop()
    {
        Avalonia.Input.DragDrop.SetAllowDrop(PlannerPage, true);
        Avalonia.Input.DragDrop.AddDragOverHandler(PlannerPage, (_, e) =>
        {
            var offers = Export.PlanImageSources.OffersFiles(e.DataTransfer);
            e.DragEffects = offers ? Avalonia.Input.DragDropEffects.Copy : Avalonia.Input.DragDropEffects.None;
            PlannerDropHint.IsVisible = offers;
        });
        Avalonia.Input.DragDrop.AddDragLeaveHandler(PlannerPage, (_, _) => PlannerDropHint.IsVisible = false);
        Avalonia.Input.DragDrop.AddDropHandler(PlannerPage, async (_, e) =>
        {
            PlannerDropHint.IsVisible = false;
            e.Handled = true;
            await ImportFromDropAsync(e.DataTransfer);
        });
    }

    // The Camera direction arrows: one nudge per press, repeated while held, through the instrument so
    // bearing wraps and pitch stops at its limits exactly as a drag or an arrow key does.
    private void CameraTurnLeftClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        CameraDirection.Turn(-Controls.CameraDirectionPad.NudgeDegrees);
    private void CameraTurnRightClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        CameraDirection.Turn(Controls.CameraDirectionPad.NudgeDegrees);
    private void CameraTiltUpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        CameraDirection.Tilt(Controls.CameraDirectionPad.NudgeDegrees);
    private void CameraTiltDownClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        CameraDirection.Tilt(-Controls.CameraDirectionPad.NudgeDegrees);

    /// <summary>
    /// What Copy PNG puts on the clipboard: the same bytes Save PNG writes, as a PNG file of Noctaxis' own (which
    /// keeps the plan wherever it is pasted), as raw PNG, and as a picture for anything that takes only pictures.
    /// </summary>
    internal async Task<Avalonia.Input.DataTransfer> CopyPngPayloadAsync(byte[] png, string? root = null)
    {
        var path = await Export.ClipboardPlanFile.WriteAsync(png, root);
        var file = await StorageProvider.TryGetFileFromPathAsync(new Uri(path));
        return Export.PlanImageSources.ForClipboard(png, file);
    }

    private async void CopyPngClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (!ClipboardAvailable(clipboard)) return;
        try
        {
            var png = await CreatePlanImageAsync(CancellationToken.None);
            await clipboard!.SetDataAsync(await CopyPngPayloadAsync(png));
            await clipboard.FlushAsync();
        }
        catch (OperationCanceledException) { }
        catch (ViewModels.ExportNotPossibleException) { }
        catch (Exception ex) { _viewModel.ReportExportDestinationFailure(ex); }
    }

    /// <summary>
    /// Whether Copy PNG has a clipboard to copy to. Without one nothing is rendered and the failure is reported like
    /// any other export destination failure, never silently (S-V04).
    /// </summary>
    internal bool ClipboardAvailable(Avalonia.Input.Platform.IClipboard? clipboard)
    {
        if (clipboard is not null) return true;
        _viewModel.ReportExportDestinationFailure(new IOException(Strings.Get("Planner_Export_ClipboardUnavailable")));
        return false;
    }

    private readonly Avalonia.Threading.DispatcherTimer _locationCopiedTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    /// <summary>
    /// Copies the planning location exactly as the bar shows it, and nothing else: not the actions beside it,
    /// a link, or more precision than is shown. A tick stands in for the pin for a moment to say it is done.
    /// </summary>
    private async void CopyLocationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            await clipboard.SetTextAsync(_viewModel.PlannerLocationText);
            await clipboard.FlushAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException) { return; }
        _viewModel.ReportLocationCopied();
        PlannerLocationGlyph.IsVisible = false;
        PlannerLocationCopied.IsVisible = true;
        _locationCopiedTimer.Stop();
        _locationCopiedTimer.Tick -= EndLocationCopied;
        _locationCopiedTimer.Tick += EndLocationCopied;
        _locationCopiedTimer.Start();
    }

    private void EndLocationCopied(object? sender, EventArgs e)
    {
        _locationCopiedTimer.Stop();
        PlannerLocationCopied.IsVisible = false;
        PlannerLocationGlyph.IsVisible = true;
    }

    private async void CopyTerrainDebugClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(_viewModel.TerrainDebugText);
        await clipboard.FlushAsync();
    }

    /// <summary>A tap or a drag on the Timeline has ended: its moment is committed at once.</summary>
    private void TimelineScrubCompleted(object? sender, EventArgs e) => _viewModel.CommitTemporalPreview();

    /// <summary>
    /// Enter hands the text to whichever search the open page has; Escape empties the field, which on a
    /// page that filters restores the full list. Neither key is the only way to do either thing.
    /// </summary>
    private async void ShellSearchKeyDown(object? sender, KeyEventArgs e)
    {
        // The field lives in the navigation template, which is bound to the DataContext rather than to
        // the constructor's view model, so the search is read from there.
        if (DataContext is not MainViewModel viewModel) return;
        if (e.Key == Key.Escape && viewModel.Search.HasText)
        {
            viewModel.Search.ClearCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Enter or Key.Return)) return;
        e.Handled = true;
        await viewModel.Search.SubmitCommand.ExecuteAsync(null);
    }

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
