namespace DrawingRenamer;

public readonly record struct PointD(double X, double Y)
{
    public PointD Rotate(int turns) => (turns & 3) switch
    {
        1 => new(1 - Y, X),
        2 => new(1 - X, 1 - Y),
        3 => new(Y, 1 - X),
        _ => this
    };
}

public readonly record struct Area(double X, double Y, double Width, double Height)
{
    public static Area Full => new(0, 0, 1, 1);
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsValid => X >= 0 && Y >= 0 && Width > 0 && Height > 0 &&
        Right <= 1.000000001 && Bottom <= 1.000000001;

    public bool Contains(PointD point) => point.X >= X && point.X <= Right &&
        point.Y >= Y && point.Y <= Bottom;

    public static Area Between(PointD a, PointD b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public Area Rotate(int turns) => Between(new PointD(X, Y).Rotate(turns),
        new PointD(Right, Bottom).Rotate(turns));

    public Area Inside(Area frame) => new(frame.X + X * frame.Width, frame.Y + Y * frame.Height,
        Width * frame.Width, Height * frame.Height);

    internal Area Inset(double fraction) => new(X + Width * fraction, Y + Height * fraction,
        Width * (1 - 2 * fraction), Height * (1 - 2 * fraction));
}

public sealed record FieldRegions(Area Designation, Area Name, Area? TitleBlock = null);

internal readonly record struct Segment(PointD A, PointD B)
{
    public Segment Rotate(int turns) => new(A.Rotate(turns), B.Rotate(turns));
}
