namespace Noctaxis.Core.Domain;

/// <summary>The Planner map's terrain relief layer: whether it is shown and how strongly.</summary>
public sealed record TerrainReliefPreferences(bool IsVisible = true, double Opacity = TerrainReliefPreferences.DefaultOpacity)
{
    /// <summary>Restrained, so relief reads beneath light pollution and never competes with roads or names.</summary>
    public const double DefaultOpacity = .6;

    public TerrainReliefPreferences Normalised() =>
        this with { Opacity = Math.Clamp(double.IsFinite(Opacity) ? Opacity : DefaultOpacity, 0, 1) };
}
