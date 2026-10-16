using Microsoft.Extensions.Logging.Abstractions;
using Noctaxis.Core.Domain;
using Noctaxis.Core.Persistence;
using NodaTime;

namespace Noctaxis.Core.Tests;

/// <summary>The Camera frame's drag inversions are saved with the camera framing, and absent from older files.</summary>
public sealed class CameraDragPreferenceTests
{
    [Fact]
    public async Task TheDragInversionsAreSavedAndReadBackAndOlderFilesHaveNeither()
    {
        var directory = Path.Combine(Path.GetTempPath(), "noctaxis-drag-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new Paths(directory);
            JsonUserDataStore Store() => new(paths, NullLogger<JsonUserDataStore>.Instance);
            var settings = new AppSettings(CameraFraming: new CameraFramingSettings(CameraPitchDegrees: 12,
                InvertHorizontalDrag: true, InvertVerticalDrag: false));
            await Store().SaveAsync(new PersistedState(4, settings, [],
                PlanningSession.Default(Instant.FromUtc(2026, 3, 5, 21, 30), "UTC"), null), CancellationToken.None);
            var loaded = (await Store().LoadAsync(CancellationToken.None)).Settings.EffectiveCameraFraming;
            Assert.True(loaded.InvertHorizontalDrag);
            Assert.False(loaded.InvertVerticalDrag);
            Assert.Equal(12, loaded.CameraPitchDegrees);

            // A file from before the settings existed: both off, and the rest of the framing as it was.
            var file = JsonUserDataStore.StateFilePath(paths);
            var json = await File.ReadAllTextAsync(file);
            var old = System.Text.RegularExpressions.Regex.Replace(json, @",\s*""InvertHorizontalDrag"":\s*true|,\s*""InvertVerticalDrag"":\s*false", "");
            Assert.DoesNotContain("Invert", old);
            await File.WriteAllTextAsync(file, old);
            var fromOld = (await Store().LoadAsync(CancellationToken.None)).Settings.EffectiveCameraFraming;
            Assert.False(fromOld.InvertHorizontalDrag);
            Assert.False(fromOld.InvertVerticalDrag);
            Assert.Equal(12, fromOld.CameraPitchDegrees);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void NormalisingKeepsTheDragInversions()
    {
        var framing = new CameraFramingSettings(InvertHorizontalDrag: true, InvertVerticalDrag: true).Normalised();
        Assert.True(framing.InvertHorizontalDrag);
        Assert.True(framing.InvertVerticalDrag);
    }

    private sealed record Paths(string Root) : IUserDataPathProvider
    {
        public string GetApplicationDataDirectory() => Root;
    }
}
