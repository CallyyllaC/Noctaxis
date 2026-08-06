using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(Noctaxis.Desktop.Tests.TestAppBuilder))]

namespace Noctaxis.Desktop.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Noctaxis.Desktop.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
