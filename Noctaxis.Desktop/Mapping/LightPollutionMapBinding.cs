using Avalonia.Threading;
using Mapsui.Tiling.Layers;
using Noctaxis.Core.Domain;
using Noctaxis.Core.LightPollution;

namespace Noctaxis.Desktop.Mapping;

public sealed class LightPollutionMapBinding : IAsyncDisposable
{
    public const string LayerId = "light-pollution";
    public static MapLayerDefinition Definition { get; } = new(LayerId, MapLayerGroup.Environmental, -10,
        new(LorenzAtlas.Name, Credits: [MapAttributions.LightPollutionAtlas]),
        MapLayerCapabilities.Visibility | MapLayerCapabilities.Opacity,
        MapLayerCapabilities.Visibility | MapLayerCapabilities.Opacity);
    /// <summary>
    /// The Planner's composition with both environmental layers registered: terrain relief beneath
    /// light pollution. Each is unavailable until its data source is configured.
    /// </summary>
    public static PlannerMapComposition CreateComposition(IMapsuiBasemap? basemap = null)
    {
        var map = new PlannerMapComposition(basemap, [TerrainReliefMapBinding.Definition, Definition]);
        map.Runtime.SetAvailability(LayerId, MapLayerAvailability.Unavailable);
        map.Runtime.SetAvailability(TerrainReliefMapBinding.LayerId, MapLayerAvailability.Unavailable);
        ApplyPreferences(map, new());
        TerrainReliefMapBinding.ApplyPreferences(map, new());
        return map;
    }
    public static void ApplyPreferences(PlannerMapComposition map, LightPollutionPreferences preferences)
    {
        if (!map.Composition.Layers.Any(x => x.Id == LayerId)) return;
        var value = preferences.Normalised();
        MapLayerPreferences.Restore(map.Runtime, [new(LayerId, value.IsVisible, value.Opacity)]);
    }
    private readonly PlannerMapComposition _map;
    private readonly LorenzInstallation _installation;
    private readonly LightPollutionDataProvider _provider;
    private readonly bool _ownsProvider;
    private readonly LightPollutionRasterSource _source;
    private readonly TileLayer _layer;
    private bool _attached;
    private int _generation;
    private volatile bool _paletteRefreshPending;
    internal string PaletteId => _source.PaletteId;
    internal LorenzInstallation Installation => _installation;
    internal LightPollutionDataProvider Provider => _provider;
    /// <param name="sharedProvider">
    /// Another view's reader of the same installation, such as the main map's for the Picture-in-Picture
    /// map: the decoded atlas is then read and held once. It stays its owner's, which releases it.
    /// </param>
    public LightPollutionMapBinding(PlannerMapComposition map, LorenzInstallation installation,
        LightPollutionDataProvider? sharedProvider = null)
    {
        _map = map; _installation = installation;
        _ownsProvider = sharedProvider is null;
        _provider = sharedProvider ?? new(installation);
        _source = new(_provider, installation, new GrayscaleLightPollutionColorMap());
        _layer = new LightPollutionTileLayer(_source) { Name = LorenzAtlas.Name };
        _layer.DataChanged += TilesChanged;
        map.InstallNativeOverlay(LayerId, [_layer]);
        Refresh();
    }
    public void Attach()
    {
        if (_attached) return;
        _attached = true; _installation.Changed += Changed; Refresh();
    }
    public void Detach() { if (!_attached) return; _attached = false; _generation++; _installation.Changed -= Changed; }
    public void SetPalette(string? id)
    {
        if (!_source.SetColorMap(LightPollutionColorMaps.Resolve(id))) return;
        // A native fetch may still be between source completion and cache publication.
        // Mark pending before inspecting Busy so completion cannot fall between the
        // cache clear and the Busy snapshot. Old features cannot paint while it drains.
        _paletteRefreshPending = true;
        FinishPaletteRefresh();
    }
    private void TilesChanged(object? sender, EventArgs args)
    {
        if (!_paletteRefreshPending) return;
        var generation = _generation;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_attached || generation != _generation) return;
            FinishPaletteRefresh();
        });
    }
    private void FinishPaletteRefresh()
    {
        if (!_paletteRefreshPending || _layer.Busy) return;
        _paletteRefreshPending = false;
        _layer.ClearCache();
        if (_attached && _layer.Enabled) _map.Map.Refresh();
    }
    private void Changed(object? sender, EventArgs args)
    {
        var generation = _generation;
        Dispatcher.UIThread.Post(() => { if (_attached && generation == _generation) Refresh(); });
    }
    private void Refresh()
    {
        _map.Runtime.SetAvailability(LayerId, MapLayerAvailability.Unavailable);
        _layer.ClearCache();
        if (_installation.IsAvailable) _map.Runtime.SetAvailability(LayerId, MapLayerAvailability.Available);
    }
    public async ValueTask DisposeAsync()
    {
        Detach(); _layer.DataChanged -= TilesChanged; _source.Dispose();
        if (_ownsProvider) await _provider.DisposeAsync();
    }
}
