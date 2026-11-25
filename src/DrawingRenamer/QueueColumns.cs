using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace DrawingRenamer;

public sealed partial class QueueColumns : ObservableObject
{
    private const double Gutter = 108;
    private bool resized;

    [ObservableProperty] public partial GridLength Source { get; set; } = new(126);
    [ObservableProperty] public partial GridLength Designation { get; set; } = new(105);
    [ObservableProperty] public partial GridLength Title { get; set; } = new(105);
    [ObservableProperty] public partial GridLength Sheet { get; set; } = new(36);
    [ObservableProperty] public partial GridLength Target { get; set; } = new(126);
    [ObservableProperty] public partial GridLength Status { get; set; } = new(100);
    [ObservableProperty] public partial double TableWidth { get; set; } = 706;

    internal void Fit(double viewport)
    {
        if (resized || viewport <= 0) return;
        var flexible = (Math.Max(viewport, 600) - Gutter - Sheet.Value - Status.Value) / 4.4;
        Source = Target = new(flexible * 1.2);
        Designation = Title = new(flexible);
        Measure();
    }

    internal void Resize(string column, double change)
    {
        if (change == 0 || !double.IsFinite(change)) return;
        resized = true;
        switch (column)
        {
            case nameof(Source): Source = Adjust(Source, 58); break;
            case nameof(Designation): Designation = Adjust(Designation, 58); break;
            case nameof(Title): Title = Adjust(Title, 58); break;
            case nameof(Sheet): Sheet = Adjust(Sheet, 34); break;
            case nameof(Target): Target = Adjust(Target, 58); break;
            case nameof(Status): Status = Adjust(Status, 66); break;
        }
        Measure();

        GridLength Adjust(GridLength width, double minimum) => new(Math.Clamp(width.Value + change, minimum, 1200));
    }

    private void Measure() => TableWidth = Gutter + Source.Value + Designation.Value + Title.Value +
        Sheet.Value + Target.Value + Status.Value;
}
