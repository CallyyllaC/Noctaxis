using Microsoft.Extensions.DependencyInjection;
using Noctaxis.Core.Environment;
using Noctaxis.Core.Locations;
using Noctaxis.Core.Weather;

namespace Noctaxis.Desktop.Tests;

/// <summary>
/// NX-23 (Opus A-04): every factory client the application registers uses a pooled connection lifetime, because the
/// singletons that hold them never let the factory rotate handlers. NX-51: unused services are not in the container.
/// </summary>
public sealed class HttpConnectionLifetimeTests
{
    [Theory]
    [InlineData(nameof(ITerrainElevationProvider))]
    [InlineData(nameof(IWeatherProvider))]
    [InlineData("LorenzAtlas")]
    public async Task FactoryClientsRecycleTheirConnections(string name)
    {
        await using var services = App.ConfigureServices();
        var handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        var primary = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.Equal(App.PooledConnectionLifetime, primary.PooledConnectionLifetime);
    }

    [Fact]
    public async Task ServicesNothingUsesAreNotRegistered()
    {
        await using var services = App.ConfigureServices();
        Assert.Null(services.GetService<ILocationEnvironmentService>());
        Assert.Null(services.GetService<IAuroraProvider>());
        Assert.Null(services.GetService<ILightPollutionProvider>());
    }
}
