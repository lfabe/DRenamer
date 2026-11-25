namespace DrawingRenamer;

public readonly record struct PageAlignment(double Angle, double Scale)
{
    public static PageAlignment Original => new(0, 1);

    internal static PageAlignment ForScan(Raster raster, double angle, CancellationToken token)
    {
        if (Math.Abs(angle) < .15) return Original;
        var (width, height) = (raster.Width, raster.Height);
        var (sin, cos) = Math.SinCos(angle * Math.PI / 180);
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var dy = y + .5 - height / 2d;
            for (var x = 0; x < width; x++)
            {
                var pixel = 4 * (x + y * width);
                if (raster.Pixels[pixel] >= 245 && raster.Pixels[pixel + 1] >= 245 &&
                    raster.Pixels[pixel + 2] >= 245) continue;
                var dx = x + .5 - width / 2d;
                if (Math.Abs(dx * cos - dy * sin) < width / 2d - 1 &&
                    Math.Abs(dx * sin + dy * cos) < height / 2d - 1) continue;

                // Fit the entire scan when rotation would put visible content outside the paper.
                var fitWidth = width * Math.Abs(cos) + height * Math.Abs(sin);
                var fitHeight = width * Math.Abs(sin) + height * Math.Abs(cos);
                return new(angle, Math.Min(width / fitWidth, height / fitHeight));
            }
        }
        return new(angle, 1);
    }

    public Area Bounds(Area area, double width, double height)
    {
        var (sin, cos) = Math.SinCos(Angle * Math.PI / 180);
        sin *= Scale;
        cos *= Scale;
        var corners = new[] { Map(area.X, area.Y), Map(area.Right, area.Y),
            Map(area.X, area.Bottom), Map(area.Right, area.Bottom) };
        return Area.Between(new(corners.Min(p => p.X), corners.Min(p => p.Y)),
            new(corners.Max(p => p.X), corners.Max(p => p.Y)));

        PointD Map(double x, double y)
        {
            var dx = (x - .5) * width;
            var dy = (y - .5) * height;
            return new(Math.Clamp(.5 + (dx * cos - dy * sin) / width, 0, 1),
                Math.Clamp(.5 + (dx * sin + dy * cos) / height, 0, 1));
        }
    }
}
