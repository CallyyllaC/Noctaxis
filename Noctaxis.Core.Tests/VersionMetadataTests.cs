using System.Reflection;
using Noctaxis.Core.Export;

namespace Noctaxis.Core.Tests;

public sealed class VersionMetadataTests
{
    // CI sets NOCTAXIS_EXPECTED_VERSION to the X.Y.Z it built with, so these tests prove the
    // --no-build test run exercises that build rather than a differently versioned one.
    [Fact]
    public void BuiltAssembliesCarryTheNoctaxisVersion()
    {
        var core = typeof(ScoutingCardExporter).Assembly;
        var version = ProductVersion(core);

        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        Assert.Equal(new Version(version + ".0"), core.GetName().Version);
        Assert.Equal(version, ProductVersion(typeof(VersionMetadataTests).Assembly));
        var expected = System.Environment.GetEnvironmentVariable("NOCTAXIS_EXPECTED_VERSION");
        if (!string.IsNullOrEmpty(expected)) Assert.Equal(expected, version);
    }

    // The SDK appends "+<commit>" to the informational version when source control metadata exists.
    private static string ProductVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
}
