using BruTile;
using Mapsui.Layers;
using Mapsui.Tiling.Layers;

namespace Noctaxis.Desktop.Mapping;

/// <summary>
/// Photographic imagery (satellite or aerial). It is deliberately not an
/// <see cref="Vector.IThemeableBasemap"/>: the theme styles the controls and symbols around it, never
/// the photograph. Each activation receives fresh layers over the supplied tile source, unmodified.
/// </summary>
public sealed class RasterImageryBasemap(BasemapDefinition definition, Func<ITileSource> createSource) : IMapsuiBasemap
{
    public BasemapDefinition Definition { get; } = definition.Kind == BasemapKind.Satellite
        ? definition
        : throw new ArgumentException("Raster imagery basemaps are satellite basemaps.", nameof(definition));

    public IReadOnlyList<ILayer> CreateLayers() => [new TileLayer(createSource()) { Name = "basemap-imagery" }];
}
