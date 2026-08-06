using Avalonia.Media;
using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.Controls;

internal static class CelestialPalette
{
    private static readonly string[] DeepSky = ["#B790FF", "#63D6C5", "#F48FB1", "#A5D66A", "#FF9F68"];

    public static string Hex(AstralTarget target, int order) => target.IsSun ? "#F3B34C" : target.IsMoon ? "#79B8FF" : DeepSky[Math.Abs(order) % DeepSky.Length];
    public static Color Colour(AstralTarget target, int order) => Color.Parse(Hex(target, order));
    public static int OrderFor(PlanningSnapshot snapshot, AstralTarget target) =>
        snapshot.Session.EffectiveVisibleObjects.FirstOrDefault(item => item.TargetId.Equals(target.Id, StringComparison.OrdinalIgnoreCase))?.Order ?? 0;
}
