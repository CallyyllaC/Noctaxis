using Noctaxis.Core.Domain;

namespace Noctaxis.Desktop.ViewModels;

public sealed class PlanningPinInteractionState(GeoCoordinate initialCoordinate)
{
    public GeoCoordinate CommittedCoordinate { get; private set; } = initialCoordinate;
    public GeoCoordinate PreviewCoordinate { get; private set; } = initialCoordinate;
    public bool IsDragging { get; private set; }

    public void SetCommittedCoordinate(GeoCoordinate coordinate)
    {
        CommittedCoordinate = coordinate.Normalised();
        if (!IsDragging) PreviewCoordinate = CommittedCoordinate;
    }

    public void ViewportChanged()
    {
        // Deliberately does not affect either planning coordinate.
    }

    public void BeginDrag() => IsDragging = true;

    public void UpdateDrag(GeoCoordinate coordinate)
    {
        if (!IsDragging) return;
        PreviewCoordinate = coordinate.Normalised();
    }

    public GeoCoordinate CompleteDrag()
    {
        if (!IsDragging) return CommittedCoordinate;
        IsDragging = false;
        CommittedCoordinate = PreviewCoordinate;
        return CommittedCoordinate;
    }

    public void CancelDrag()
    {
        IsDragging = false;
        PreviewCoordinate = CommittedCoordinate;
    }
}
