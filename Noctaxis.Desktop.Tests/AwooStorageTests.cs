using Microsoft.Extensions.Logging;
using Noctaxis.Core.Catalogues;
using Noctaxis.Core.Persistence;

namespace Noctaxis.Desktop.Tests;

public sealed partial class MainViewModelTests
{
    private sealed record AwooPaths(string Path) : IUserDataPathProvider { public string GetApplicationDataDirectory() => Path; }
    private sealed class AwooStoreLog : ILogger<JsonUserDataStore>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
    [Fact]
    public async Task AwooRealStoreActivationRestartRemovalAndLogs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-awoo-" + Guid.NewGuid());
        try
        {
            var logger = new AwooStoreLog();
            var store = new JsonUserDataStore(new AwooPaths(directory), logger);
            var catalogue = new OpenNgcTargetCatalogue(); var planning = new FakePlanning(catalogue);
            var vm = CreateViewModel(planning, catalogue, store, new FakeExporter(), supporterLicenceVerifier: AwooV1Fixture.Verifier());
            await vm.InitializeAsync(); vm.SupporterLicenceInput = AwooV1Fixture.Licence;
            await vm.ActivateSupporterLicenceCommand.ExecuteAsync(null);
            var reloaded = new JsonUserDataStore(new AwooPaths(directory), logger);
            var restarted = CreateViewModel(planning, catalogue, reloaded, new FakeExporter(), supporterLicenceVerifier: AwooV1Fixture.Verifier());
            await restarted.InitializeAsync(); Assert.True(restarted.HasAwooSupporterEntitlement); Assert.Empty(restarted.SupporterLicenceInput);
            await restarted.RemoveSupporterLicenceCommand.ExecuteAsync(null);
            Assert.Null((await reloaded.LoadAsync(CancellationToken.None)).Settings.AwooSupporterLicenceCode);
            Assert.DoesNotContain(AwooV1Fixture.Licence, string.Join('\n', logger.Messages));
            Assert.DoesNotContain(AwooV1Fixture.Licence.Split('.')[3], string.Join('\n', logger.Messages));
            Assert.Equal(0, planning.SnapshotCalculations); Assert.Equal(0, planning.EnvironmentRequests); Assert.Equal(0, planning.WeatherRequests);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
