using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace DrawingRenamer;

internal sealed class PdfSheet(Page page)
{
    private PointD Point(PdfPoint p) => new(p.X / page.Width, 1 - p.Y / page.Height);

    internal IReadOnlyList<Segment> Lines(CancellationToken token)
    {
        var lines = new List<Segment>();
        foreach (var path in page.Paths)
        foreach (var subpath in path)
        {
            token.ThrowIfCancellationRequested();
            PdfPoint? start = null;
            PdfPoint? end = null;
            foreach (var command in subpath.Commands)
            {
                switch (command)
                {
                    case PdfSubpath.Move move:
                        start = end = move.Location;
                        break;
                    case PdfSubpath.Line line:
                        Add(line.From, line.To);
                        end = line.To;
                        break;
                    case PdfSubpath.Close when start is { } first && end is { } last:
                        Add(last, first);
                        break;
                    default:
                        end = null;
                        break;
                }
            }
        }
        return lines;

        void Add(PdfPoint from, PdfPoint to)
        {
            var a = Point(from);
            var b = Point(to);
            // Preserve 5 mm stamp rows after normalization on large sheets.
            if (Math.Abs(from.X - to.X) + Math.Abs(from.Y - to.Y) >= 3)
                lines.Add(new(a, b));
        }
    }

    internal string Read(Area area, int turn)
    {
        var upright = area.Rotate(turn);
        var glyphs = page.Letters.Select(letter =>
        {
            var start = Point(letter.StartBaseLine).Rotate(turn);
            var end = Point(letter.EndBaseLine).Rotate(turn);
            var center = Point(letter.BoundingBox.Centroid).Rotate(turn);
            return (letter.Value, Start: start, End: end, Center: center);
        }).Where(g => upright.Contains(g.Center) && Math.Abs(g.End.Y - g.Start.Y) < .004 &&
            g.End.X >= g.Start.X - .0001).OrderBy(g => g.Start.Y).ToArray();
        var lines = new List<List<(string Value, PointD Start, PointD End, PointD Center)>>();
        var tolerance = Math.Min(.004, upright.Height * .11);
        foreach (var glyph in glyphs)
        {
            if (lines.Count == 0 || glyph.Start.Y - lines[^1][0].Start.Y > tolerance)
                lines.Add([]);
            lines[^1].Add(glyph);
        }
        var output = new StringBuilder();
        foreach (var line in lines)
        {
            var ordered = line.OrderBy(g => g.Start.X).Distinct().ToArray();
            var widths = ordered.Where(g => g.End.X - g.Start.X > .00001)
                .Select(g => g.End.X - g.Start.X).Order().ToArray();
            var gap = widths.Length > 0 ? widths[widths.Length / 2] * .34 : .0006;
            double? last = null;
            foreach (var glyph in ordered)
            {
                if (last is { } end && glyph.Start.X - end > gap) output.Append(' ');
                output.Append(glyph.Value);
                last = glyph.End.X;
            }
            output.AppendLine();
        }
        return output.ToString();
    }
}
