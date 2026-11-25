using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Input;

namespace DrawingRenamer;

public sealed partial class QueueColumns : ObservableObject
{
    private const double Gutter = 108;
    private readonly List<string> order = [nameof(Source), nameof(Designation), nameof(Title), nameof(Sheet), nameof(Target), nameof(Status)];
    private bool resized;

    [ObservableProperty] public partial GridLength Source { get; set; } = new(126);
    [ObservableProperty] public partial GridLength Designation { get; set; } = new(105);
    [ObservableProperty] public partial GridLength Title { get; set; } = new(105);
    [ObservableProperty] public partial GridLength Sheet { get; set; } = new(36);
    [ObservableProperty] public partial GridLength Target { get; set; } = new(126);
    [ObservableProperty] public partial GridLength Status { get; set; } = new(100);
    [ObservableProperty] public partial double TableWidth { get; set; } = 706;

    public GridLength Width1 => WidthOf(order[0]);
    public GridLength Width2 => WidthOf(order[1]);
    public GridLength Width3 => WidthOf(order[2]);
    public GridLength Width4 => WidthOf(order[3]);
    public GridLength Width5 => WidthOf(order[4]);
    public GridLength Width6 => WidthOf(order[5]);

    public int SourceIndex => IndexOf(nameof(Source));
    public int DesignationIndex => IndexOf(nameof(Designation));
    public int TitleIndex => IndexOf(nameof(Title));
    public int SheetIndex => IndexOf(nameof(Sheet));
    public int TargetIndex => IndexOf(nameof(Target));
    public int StatusIndex => IndexOf(nameof(Status));
    public int GroupIndex => StatusIndex <= 3 ? StatusIndex + 1 : 1;
    public int GroupSpan => StatusIndex <= 3 ? 6 - StatusIndex : StatusIndex - 1;

    internal int IndexOf(string column) => order.IndexOf(column) + 1;
    internal GridLength WidthOf(string column) => column switch
    {
        nameof(Source) => Source, nameof(Designation) => Designation, nameof(Title) => Title,
        nameof(Sheet) => Sheet, nameof(Target) => Target, nameof(Status) => Status, _ => default
    };

    // Header padding, checkbox, then spacing before the first document column.
    internal double StartOf(int index) => 48 + order.Take(index - 1).Sum(column => WidthOf(column).Value + 8);

    internal int DropIndex(string column, double x)
    {
        var source = IndexOf(column);
        var destination = source;
        for (var i = 1; i <= order.Count; i++)
        {
            if (i == source) continue;
            var center = StartOf(i) + WidthOf(order[i - 1]).Value / 2;
            if ((i < source && x < center) || (i > source && x > center))
            {
                destination = i;
                if (i < source) break;
            }
        }
        return destination;
    }

    internal double DropGuide(int from, int to) => to < from ? StartOf(to) - 4 : StartOf(to + 1) - 4;

    internal void Move(string column, int destination)
    {
        var source = IndexOf(column);
        if (source == destination || source == 0 || destination < 1 || destination > order.Count) return;
        order.RemoveAt(source - 1);
        order.Insert(destination - 1, column);
        OnPropertyChanged(nameof(SourceIndex));
        OnPropertyChanged(nameof(DesignationIndex));
        OnPropertyChanged(nameof(TitleIndex));
        OnPropertyChanged(nameof(SheetIndex));
        OnPropertyChanged(nameof(TargetIndex));
        OnPropertyChanged(nameof(StatusIndex));
        OnPropertyChanged(nameof(GroupIndex));
        OnPropertyChanged(nameof(GroupSpan));
        Measure();
    }

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

    private void Measure()
    {
        TableWidth = Gutter + order.Sum(column => WidthOf(column).Value);
        OnPropertyChanged(nameof(Width1));
        OnPropertyChanged(nameof(Width2));
        OnPropertyChanged(nameof(Width3));
        OnPropertyChanged(nameof(Width4));
        OnPropertyChanged(nameof(Width5));
        OnPropertyChanged(nameof(Width6));
    }
}

public sealed class QueueColumnDivider : ContentControl
{
    public QueueColumnDivider() => Loaded += (_, _) =>
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
}
