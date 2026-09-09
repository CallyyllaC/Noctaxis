using Noctaxis.Desktop.Services;
using Noctaxis.Desktop.Diagnostics;
using Noctaxis.Desktop.Controls;

namespace Noctaxis.Desktop.Tests;

public sealed class ApplicationHardeningTests
{
    [Fact]
    public void TileCacheEvictsLeastRecentlyUsedAndPreservesActiveBuffers()
    {
        var cache = new TileMemoryCache<int>(200);
        var first = new byte[100];
        cache.Store(1, first);
        cache.Store(2, new byte[100]);
        Assert.True(cache.TryGetValue(1, out var held));
        cache.Store(3, new byte[100]);
        Assert.False(cache.TryGetValue(2, out _));
        Assert.Same(first, held);
        Assert.True(cache.TryGetValue(1, out _));
        Assert.Equal(200, cache.RetainedBytes);
        cache.Store(1, new byte[300]);
        Assert.False(cache.TryGetValue(1, out _));
        Assert.Equal(100, cache.RetainedBytes);
        cache.Remove(3);
        Assert.Equal(0, cache.RetainedBytes);
    }

    [Fact]
    public void DiagnosticRenderersHaveExplicitOwnershipAndProductMinimapRemainsProduction()
    {
        Assert.Equal("Noctaxis.Desktop.Diagnostics", typeof(TerrainDebugMiniMap).Namespace);
        Assert.Equal("Noctaxis.Desktop.Diagnostics", typeof(TerrainSampleRenderer).Namespace);
        Assert.Equal("Noctaxis.Desktop.Controls", typeof(LocalTerrainMap).Namespace);
    }
}
