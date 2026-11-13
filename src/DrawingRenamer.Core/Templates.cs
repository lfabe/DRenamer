using System.Text.Json;

namespace DrawingRenamer;

public sealed record TitleTemplate(string Name, double WidthMm, double HeightMm,
    Area Designation, Area Title, double[] HorizontalRules)
{
    internal static readonly TitleTemplate ConstructionWorkingDrawing = new("СПДС, форма 3", 185, 55,
        new(65d / 185, 0, 120d / 185, 10d / 55), new(65d / 185, 40d / 55, 70d / 185, 15d / 55),
        [10d / 55, 25d / 55, 30d / 55, 40d / 55]);

    public static IReadOnlyList<TitleTemplate> Defaults { get; } =
    [
        new("ЕСКД, форма 1", 185, 55, new(65d / 185, 0, 110d / 185, 15d / 55),
            new(65d / 185, 15d / 55, 70d / 185, 25d / 55), [15d / 55, 35d / 55]),
        new("ЕСКД, форма 2", 185, 40, new(65d / 185, 0, 110d / 185, 15d / 40),
            new(65d / 185, 15d / 40, 70d / 185, 25d / 40), [15d / 40, 25d / 40]),
        ConstructionWorkingDrawing,
        new("СПДС, форма 4", 185, 55, new(65d / 185, 0, 120d / 185, 15d / 55),
            new(65d / 185, 15d / 55, 70d / 185, 25d / 55), [15d / 55, 35d / 55, 40d / 55]),
        new("СПДС, форма 5", 185, 40, new(65d / 185, 0, 120d / 185, 15d / 40),
            new(65d / 185, 15d / 40, 70d / 185, 25d / 40), [15d / 40, 20d / 40, 25d / 40])
    ];

    public static IReadOnlyList<TitleTemplate> Load(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 256_000) throw new InvalidDataException("Файл шаблонов слишком велик.");
        var templates = JsonSerializer.Deserialize<TitleTemplate[]>(input, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("Ожидается массив шаблонов.");
        if (templates.Length is < 1 or > 32 || templates.Any(t => t is null || string.IsNullOrWhiteSpace(t.Name) ||
            !double.IsFinite(t.WidthMm) || !double.IsFinite(t.HeightMm) || t.WidthMm is < 20 or > 500 ||
            t.HeightMm is < 5 or > 200 || !t.Designation.IsValid || !t.Title.IsValid ||
            t.HorizontalRules is not { Length: >= 1 and <= 12 } ||
            t.HorizontalRules.Any(y => !double.IsFinite(y) || y is <= 0 or >= 1)))
            throw new InvalidDataException("Проверьте размеры и координаты шаблонов.");
        return templates;
    }
}

internal sealed record LocatedTitle(TitleTemplate Template, Area Frame, int Turn, bool Verified)
{
    public bool IsContinuation => !Template.Title.IsValid;
    public Area PageFrame => Frame.Rotate(-Turn);
    public FieldRegions Regions => new(Template.Designation.Inside(Frame).Rotate(-Turn),
        Template.Title.Inside(Frame).Rotate(-Turn), PageFrame);
}

internal static class TitleLocator
{
    // Forms 2а and 2б have designation (2), but no title (1).
    private static readonly TitleTemplate RightContinuation = new("ЕСКД, форма 2а/2б", 185, 15,
        new(65d / 185, 0, 100d / 185, 1), default, []);
    private static readonly TitleTemplate LeftContinuation = RightContinuation with
    {
        Designation = new(10d / 185, 0, 100d / 185, 1)
    };

    internal static IEnumerable<LocatedTitle> Find(IEnumerable<Segment> segments, double width, double height,
        int turn, IReadOnlyList<TitleTemplate> templates, CancellationToken token)
    {
        var lines = segments.Select(s => s.Rotate(turn)).ToArray();
        var horizontal = lines.Where(s => Math.Abs(s.A.Y - s.B.Y) < .0025 &&
            Math.Abs(s.A.X - s.B.X) >= .004 && s.A.Y > .35)
            .Select(s => new Rule((s.A.Y + s.B.Y) / 2, Math.Min(s.A.X, s.B.X), Math.Max(s.A.X, s.B.X)))
            .OrderBy(r => r.At).ToArray();
        var vertical = lines.Where(s => Math.Abs(s.A.X - s.B.X) < .0025 &&
            Math.Abs(s.A.Y - s.B.Y) > .004)
            .Select(s => new Rule((s.A.X + s.B.X) / 2, Math.Min(s.A.Y, s.B.Y), Math.Max(s.A.Y, s.B.Y)))
            .OrderBy(rule => rule.At).ToArray();
        if (FindContinuation(horizontal, vertical, width, height, turn, token) is { } continuation)
        {
            yield return continuation;
            yield break;
        }

        // shortcut: the rightmost table rule must be continuous, merge collinear fragments when real exports require it
        var rightRules = vertical.Where(rule => rule.At > .55 && rule.End - rule.Start > .012)
            .OrderByDescending(r => r.End - r.Start).Take(160).ToArray();
        foreach (var template in templates)
        {
            var results = new List<LocatedTitle>();
            foreach (var right in rightRules)
            foreach (var top in horizontal)
            {
                if (results.Count >= 12) break;
                token.ThrowIfCancellationRequested();
                if (top.At < Math.Max(.40, right.Start - .008) || top.At > right.End - .01 ||
                    top.End - top.Start < .012 ||
                    top.End < right.At - .007 || top.Start >= right.At - .04 || right.At < .65)
                    continue;
                var h = right.End - top.At;
                var w = h * height / width * template.WidthMm / template.HeightMm;
                var frame = new Area(right.At - w, top.At, w, h);
                if (!frame.IsValid || frame.Bottom < .63 || frame.Height > .5 ||
                    frame.Width < .09 || top.Start > frame.X + .016 ||
                    !Covers(vertical, frame.X, frame.Y, frame.Bottom, frame.Width * .025, frame.Height * .04) ||
                    !Matches(horizontal, frame.Bottom, frame.X + frame.Width * .15, frame.Right, frame.Height))
                    continue;
                if (!template.HorizontalRules.All(rule => Matches(horizontal, frame.Y + rule * frame.Height,
                    frame.X + frame.Width * .77, frame.X + frame.Width * .90, frame.Height)))
                    continue;
                if (template == TitleTemplate.ConstructionWorkingDrawing && !Matches(horizontal,
                    frame.Y + frame.Height * template.Title.Y, frame.X + frame.Width * template.Title.X,
                    frame.Right, frame.Height))
                    continue;
                if (results.Any(previous => Math.Abs(previous.Frame.X - frame.X) < .012 &&
                    Math.Abs(previous.Frame.Y - frame.Y) < .007 && Math.Abs(previous.Frame.Bottom - frame.Bottom) < .007))
                    continue;
                var found = new LocatedTitle(template, frame, turn, true);
                results.Add(found);
                yield return found;
            }
        }
    }

    private static LocatedTitle? FindContinuation(Rule[] horizontal, Rule[] vertical, double width, double height,
        int turn, CancellationToken token)
    {
        foreach (var edge in vertical.Where(rule => rule.End > .89 && (rule.At < .10 || rule.At > .90)))
        foreach (var top in horizontal)
        {
            token.ThrowIfCancellationRequested();
            var h = edge.End - top.At;
            if (h is <= .010 or > .12 || top.At < edge.Start - .003) continue;
            var left = edge.At < .10;
            var template = left ? LeftContinuation : RightContinuation;
            var w = h * height / width * template.WidthMm / template.HeightMm;
            var frame = new Area(left ? edge.At : edge.At - w, top.At, w, h);
            if (!frame.IsValid || frame.Width < .09 ||
                !Matches(horizontal, frame.Y, frame.X, frame.Right, h) ||
                !Matches(horizontal, frame.Bottom, frame.X, frame.Right, h))
                continue;

            var revisionStart = left ? frame.Right - w * 65 / 185 : frame.X;
            var revisionEnd = revisionStart + w * 65 / 185;
            var designationStart = frame.X + w * template.Designation.X;
            var pageDivider = left ? designationStart : frame.Right - w * 10 / 185;
            if (!HasVertical(frame.X) || !HasVertical(frame.Right) ||
                !HasVertical(left ? revisionStart : designationStart) || !HasVertical(pageDivider) ||
                !Matches(horizontal, frame.Y + h / 3, revisionStart, revisionEnd, h) ||
                !Matches(horizontal, frame.Y + h * 2 / 3, revisionStart, revisionEnd, h))
                continue;
            return new(template, frame, turn, true);

            bool HasVertical(double x) => vertical.Any(rule =>
                Math.Abs(rule.At - x) < .004 && rule.Start <= frame.Y + .003 && rule.End >= frame.Bottom - .003);
        }
        return null;
    }

    private readonly record struct Rule(double At, double Start, double End);

    private static bool Matches(Rule[] rules, double at, double from, double to, double height)
        => Covers(rules, at, from, to, Math.Clamp(height * .038, .0016, .006), .01);

    private static bool Covers(Rule[] rules, double at, double from, double to, double tolerance, double trim)
    {
        var index = Array.BinarySearch(rules, new Rule(at - tolerance, 0, 0), RuleComparer);
        var start = index < 0 ? ~index : index;
        var end = start;
        for (; end < rules.Length && rules[end].At <= at + tolerance; end++)
            if (rules[end].Start <= from + trim && rules[end].End >= to - trim) return true;
        if (end - start < 2) return false;

        // Some PDF exporters split a rule at each cell border.
        var covered = from + trim;
        foreach (var fragment in rules[start..end].OrderBy(rule => rule.Start))
        {
            if (fragment.Start > covered + .001) break;
            covered = Math.Max(covered, fragment.End);
            if (covered >= to - trim) return true;
        }
        return false;
    }

    private static readonly IComparer<Rule> RuleComparer = Comparer<Rule>.Create((a, b) => a.At.CompareTo(b.At));

    internal static LocatedTitle? Guess(TitleTemplate template, double width, double height, int turn)
    {
        const double pointsPerMm = 72 / 25.4;
        var w = template.WidthMm * pointsPerMm / width;
        var h = template.HeightMm * pointsPerMm / height;
        var frame = new Area(1 - w - 5 * pointsPerMm / width, 1 - h - 5 * pointsPerMm / height, w, h);
        return frame.IsValid ? new(template, frame, turn, false) : null;
    }
}
