namespace DrawingRenamer;

internal sealed record ScanTitle(FieldRegions Regions, int Turn, double Slope, IReadOnlyList<Area> NameLines)
{
    internal ScanTitle OnOriginalPage(int turn) => new(new(Regions.Designation.Rotate(-turn),
        Regions.Name.Rotate(-turn), Regions.TitleBlock?.Rotate(-turn)), turn, Slope,
        NameLines.Select(line => line.Rotate(-turn)).ToArray());
}

internal sealed class ScanTitleLocator(Raster raster, int turn)
{
    private readonly int width = (turn & 1) == 0 ? raster.Width : raster.Height;
    private readonly int height = (turn & 1) == 0 ? raster.Height : raster.Width;

    internal static IEnumerable<ScanTitle> Find(Raster raster, double pageWidth, double pageHeight,
        IReadOnlyList<TitleTemplate> templates, Func<int, Raster> renderUpright, CancellationToken token)
    {
        var layouts = templates.Where(t => t.PageTurn is null).ToLookup(t => t.WidthMm);
        for (var turn = 0; turn < 4; turn++)
        {
            var view = new ScanTitleLocator(raster, turn);
            var pixelsPerMm = view.width / ((turn & 1) == 0 ? pageWidth : pageHeight) * 72 / 25.4;
            IEnumerable<ScanTitle> Matches(ScanTitleLocator v) =>
                layouts.SelectMany(g => new[] { 1d, .75, .5 }.SelectMany(scale =>
                    v.Match(g, g.Key * pixelsPerMm * scale, token)))
                    .DistinctBy(s => (s.Regions, s.Turn, s.Slope));
            var found = Matches(view).ToList();
            if (turn != 0 && found.Count > 0)
            {
                // Fit at the same pixel alignment for a scan and its PDF quarter-turn copies.
                var upright = new ScanTitleLocator(renderUpright(turn), 0);
                var refined = Matches(upright).Select(s => s.OnOriginalPage(turn)).ToList();
                if (refined.Count > 0) found = refined;
            }
            foreach (var stamp in found) yield return stamp;
        }
    }

    private IEnumerable<ScanTitle> Match(IEnumerable<TitleTemplate> layouts, double nominalWidth, CancellationToken token)
    {
        if (nominalWidth < 40 || nominalWidth > width * 1.3) yield break;
        // Intersections in two columns give the position and inclination of the top rule.
        var x1 = (int)(width - nominalWidth * .72);
        var x2 = (int)(width - nominalWidth * .27);
        var rightCrossings = Crossings(x2).ToArray();
        foreach (var y1 in Crossings(x1))
        foreach (var y2 in rightCrossings)
        {
            token.ThrowIfCancellationRequested();
            var slope = (y2 - y1) / (double)(x2 - x1);
            if (Math.Abs(slope) > .06) continue;
            var left = EndOfRule(x1, y1, slope, -1);
            if (left >= x1 - nominalWidth * .08) continue;
            var right = EndOfRule(x1, y1, slope, 1);
            var span = right - left;
            if (span < .75 * nominalWidth || span > 1.25 * nominalWidth || right < width * .8) continue;
            var top = y1 + slope * (left - x1);
            foreach (var template in layouts)
            {
                var depth = span * template.HeightMm / template.WidthMm;
                if (top + depth < height * .8 || top + depth > height * 1.04) continue;
                var grid = new Grid(left, top, span, depth, slope);
                // A cropped scan may lack the bottom border. Require both sides and the internal rules.
                if (!HasRule(grid, 0, .05, 0, .8) || !HasRule(grid, 1, .05, 1, .8) ||
                    !template.HorizontalRules.All(y => HasRule(grid, .77, y, .90, y))) continue;
                if (template == TitleTemplate.ConstructionWorkingDrawing &&
                    !HasRule(grid, template.Title.X, template.Title.Y, 1, template.Title.Y)) continue;

                var designation = FitText(grid, template.Designation, null);
                if (!designation.IsValid) continue;
                var lines = new List<Area>();
                var name = FitText(grid, template.Title, lines);
                if (!name.IsValid) continue;
                var frame = grid.Bounds(width, height).Rotate(-turn);
                yield return new(new(designation, name, frame), turn, slope, lines);
            }
        }
    }

    private readonly record struct Grid(double Left, double Top, double Width, double Height, double Slope)
    {
        public PointD Map(double x, double y) => new(Left + x - Slope * y, Top + y + Slope * x);

        public Area Bounds(int w, int h) => Area.Between(
            new(Math.Max(0, Left - Math.Max(0, Slope * Height)) / w, Math.Max(0, Top + Math.Min(0, Slope * Width)) / h),
            new(Math.Min(w, Left + Width - Math.Min(0, Slope * Height) + 1) / w,
                Math.Min(h, Top + Height + Math.Max(0, Slope * Width) + 1) / h));
    }

    private bool HasRule(Grid grid, double x1, double y1, double x2, double y2)
    {
        var hits = 0;
        var dx = x1 == x2 ? 1 : 0;
        var dy = 1 - dx;
        for (var i = 0; i <= 20; i++)
        {
            var p = grid.Map((x1 + (x2 - x1) * i / 20) * grid.Width, (y1 + (y2 - y1) * i / 20) * grid.Height);
            var x = (int)Math.Round(p.X);
            var y = (int)Math.Round(p.Y);
            if (Dark(x, y) || Dark(x - dx, y - dy) || Dark(x + dx, y + dy)) hits++;
        }
        return hits >= 16;
    }

    private Area FitText(Grid grid, Area field, List<Area>? lines)
    {
        var w = (int)Math.Round(field.Width * grid.Width);
        var h = (int)Math.Round(field.Height * grid.Height);
        if (w < 8 || h < 8) return default;
        var origin = grid.Map(field.X * grid.Width, field.Y * grid.Height);
        var columns = new int[w];
        var rows = new int[h];
        var ink = new bool[w * h];
        for (var y = 2; y < h - 2; y++)
        for (var x = 2; x < w - 2; x++)
        {
            if (!Dark((int)Math.Round(origin.X + x - grid.Slope * y),
                (int)Math.Round(origin.Y + y + grid.Slope * x))) continue;
            columns[x]++;
            rows[y]++;
            ink[y * w + x] = true;
        }
        var minX = Array.FindIndex(columns, count => count >= Math.Max(2, h * .04));
        var maxX = Array.FindLastIndex(columns, count => count >= Math.Max(2, h * .04));
        var minY = Array.FindIndex(rows, IsTextRow);
        var maxY = Array.FindLastIndex(rows, IsTextRow);
        if (minX < 0 || minY < 0 || maxX <= minX || maxY <= minY) return default;
        if (lines is not null)
        {
            for (var first = minY; first <= maxY; first++)
            {
                if (!IsTextRow(rows[first])) continue;
                var last = first;
                while (last < maxY && (IsTextRow(rows[last + 1]) ||
                       last + 2 <= maxY && IsTextRow(rows[last + 2]))) last++;
                if (last - first >= 3)
                {
                    var lineColumns = new int[w];
                    for (var y = first; y <= last; y++)
                    for (var x = minX; x <= maxX; x++)
                        if (ink[y * w + x]) lineColumns[x]++;
                    var from = Array.FindIndex(lineColumns, n => n >= 2);
                    var to = Array.FindLastIndex(lineColumns, n => n >= 2);
                    if (to > from) lines.Add(Box(first, last, from, to));
                }
                first = last;
            }
        }
        return Box(minY, maxY, minX, maxX);

        bool IsTextRow(int ink) => ink >= Math.Max(3, w * .025);
        Area Box(int first, int last, int from, int to)
        {
            var x1 = Math.Max(0, from - 4);
            var x2 = Math.Min(w, to + 5);
            var y1 = Math.Max(1, first - 4);
            var y2 = Math.Min(h - 1, last + 5);
            var cx = origin.X + (x1 + x2 - grid.Slope * (y1 + y2)) / 2;
            var cy = origin.Y + (y1 + y2 + grid.Slope * (x1 + x2)) / 2;
            return new Area((cx - (x2 - x1) / 2d) / width, (cy - (y2 - y1) / 2d) / height,
                (x2 - x1) / (double)width, (y2 - y1) / (double)height).Rotate(-turn);
        }
    }

    private IEnumerable<int> Crossings(int x)
    {
        for (var y = (int)(height * .4); y < height; y++)
        {
            if (!Across(y)) continue;
            var start = y;
            while (y + 1 < height && Across(y + 1)) y++;
            if (y - start <= 6) yield return (start + y) / 2;
        }
        bool Across(int y) => Dark(x - 2, y) && Dark(x + 2, y);
    }

    private int EndOfRule(int start, int row, double slope, int step)
    {
        var last = start;
        for (var x = start; x >= 0 && x < width && Math.Abs(x - last) <= 3; x += step)
        {
            var y = (int)Math.Round(row + (x - start) * slope);
            if (Dark(x, y) || Dark(x, y - 1) || Dark(x, y + 1) || Dark(x, y - 2) || Dark(x, y + 2)) last = x;
        }
        return last;
    }

    private bool Dark(int x, int y)
    {
        if ((uint)x >= width || (uint)y >= height) return false;
        var index = turn switch
        {
            1 => (raster.Height - 1 - x) * raster.Width + y,
            2 => (height - 1 - y) * width + width - 1 - x,
            3 => x * raster.Width + raster.Width - 1 - y,
            _ => y * width + x
        };
        var p = index * 4;
        return raster.Pixels[p] + raster.Pixels[p + 1] + raster.Pixels[p + 2] < 560;
    }
}
