#if DEBUG
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Noctaxis.Desktop.Controls;

internal static class TerrainConeTopologyDiagnostics
{
    private const string EnvironmentVariable = "NOCTAXIS_DEBUG_TERRAIN_TOPOLOGY";

    public static bool Enabled { get; } = string.Equals(
        Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.OrdinalIgnoreCase);

    public static void Write(EnvironmentalOverlayState overlay)
    {
        if (!Enabled) return;
        var text = new StringBuilder()
            .AppendLine("Terrain cone topology")
            .AppendLine("Bearing | State | Obstruction distance");
        foreach (var sample in overlay.SourceSamples)
        {
            text.Append(sample.BearingDegrees.ToString("F3", CultureInfo.InvariantCulture))
                .Append(" deg | ")
                .Append(sample.IsObstructed ? "blocked" : "clear")
                .Append(" | ");
            if (sample.ObstructionDistanceMetres is double distance)
                text.Append(distance.ToString("F1", CultureInfo.InvariantCulture)).Append(" m");
            else
                text.Append('—');
            text.AppendLine();
        }
        Debug.WriteLine(text.ToString());
    }
}
#endif
