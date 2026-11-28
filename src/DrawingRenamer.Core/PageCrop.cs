namespace DrawingRenamer;

public sealed record PageCrop(string Format, int WidthMm, int HeightMm, Area Bounds, int Turns, bool HasOutsideInk)
{
    internal const double PointsPerMm = 72d / 25.4;

    public Area Map(Area region) => Area.Between(
        new(Math.Clamp((region.X - Bounds.X) / Bounds.Width, 0, 1), Math.Clamp((region.Y - Bounds.Y) / Bounds.Height, 0, 1)),
        new(Math.Clamp((region.Right - Bounds.X) / Bounds.Width, 0, 1), Math.Clamp((region.Bottom - Bounds.Y) / Bounds.Height, 0, 1)));

    public Raster Preview(Raster original, PageAlignment alignment, CancellationToken token)
    {
        var upright = original.Orient(Turns);
        var pixelsPerMm = Math.Max(upright.Width, upright.Height) / (double)Math.Max(WidthMm, HeightMm);
        var width = Math.Max(1, (int)Math.Round(WidthMm * pixelsPerMm));
        var height = Math.Max(1, (int)Math.Round(HeightMm * pixelsPerMm));
        return Resample(upright, Bounds, width, height, alignment, token);
    }

    internal static Raster Resample(Raster raster, Area view, int width, int height, PageAlignment alignment, CancellationToken token)
    {
        if (view == Area.Full && width == raster.Width && height == raster.Height && alignment == PageAlignment.Original)
            return raster;
        var pixels = new byte[4 * width * height];
        pixels.AsSpan().Fill(255);
        var (sin, cos) = Math.SinCos(-alignment.Angle * Math.PI / 180);
        sin /= alignment.Scale;
        cos /= alignment.Scale;
        for (var y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            var dy = (view.Y + (y + .5) * view.Height / height - .5) * raster.Height;
            var left = (view.X + .5 * view.Width / width - .5) * raster.Width;
            var step = view.Width * raster.Width / width;
            for (var x = 0; x < width; x++)
            {
                var dx = left + x * step;
                var sx = (int)Math.Floor(raster.Width / 2d + dx * cos - dy * sin);
                var sy = (int)Math.Floor(raster.Height / 2d + dx * sin + dy * cos);
                if ((uint)sx >= (uint)raster.Width || (uint)sy >= (uint)raster.Height) continue;
                raster.Pixels.AsSpan(4 * (sx + sy * raster.Width), 4).CopyTo(pixels.AsSpan(4 * (x + y * width), 4));
            }
        }
        return new(width, height, pixels);
    }
}

internal static class DrawingFrameCrop
{
    private readonly record struct Format(string Name, int Width, int Height);
    private readonly record struct Rule(int Position, double Score);
    private static readonly Format[] Formats =
    [
        new("А0", 841, 1189), new("А1", 594, 841), new("А2", 420, 594),
        new("А3", 297, 420), new("А4", 210, 297), new("А5", 148, 210),
        new("А0×2", 1189, 1682), new("А0×3", 1189, 2523),
        new("А1×3", 841, 1783), new("А1×4", 841, 2378),
        new("А2×3", 594, 1261), new("А2×4", 594, 1682), new("А2×5", 594, 2102),
        new("А3×3", 420, 891), new("А3×4", 420, 1189), new("А3×5", 420, 1486),
        new("А3×6", 420, 1783), new("А3×7", 420, 2080),
        new("А4×3", 297, 630), new("А4×4", 297, 841), new("А4×5", 297, 1051),
        new("А4×6", 297, 1261), new("А4×7", 297, 1471), new("А4×8", 297, 1682), new("А4×9", 297, 1892)
    ];

    internal static PageCrop? Find(Raster raster, double widthPt, double heightPt, int turns, Area? stamp, CancellationToken token,
        bool checkContent = true)
    {
        var (w, h) = (raster.Width, raster.Height);
        if (Math.Min(w, h) < 160 || widthPt <= 0 || heightPt <= 0) return null;
        var (left, right) = FindRules(vertical: true);
        var (top, bottom) = FindRules(vertical: false);
        var inkW = w / 3;
        var inkH = h / 3;
        var inkStride = inkW + 1;
        var ink = new int[inkStride * (inkH + 1)];
        for (var y = 1; checkContent && y <= inkH; y++)
        {
            token.ThrowIfCancellationRequested();
            var row = 0;
            for (var x = 1; x <= inkW; x++)
            {
                if (x > .01 * inkW && x < .99 * inkW && y > .01 * inkH && y < .99 * inkH &&
                    Light(3 * x - 2, 3 * y - 2) < 595) row++;
                ink[x + y * inkStride] = ink[x + (y - 1) * inkStride] + row;
            }
        }
        var bestScore = double.NegativeInfinity;
        PageCrop? best = null;
        foreach (var l in left)
        foreach (var r in right)
        foreach (var t in top)
        foreach (var b in bottom)
        {
            token.ThrowIfCancellationRequested();
            var frameW = r.Position - l.Position;
            var frameH = b.Position - t.Position;
            if (frameW < .48 * w || frameH < .48 * h) continue;
            var widthMm = frameW * widthPt / w / PageCrop.PointsPerMm;
            var heightMm = frameH * heightPt / h / PageCrop.PointsPerMm;
            var paper = ClosestFormat(widthMm, heightMm);
            if (paper is not { } format) continue;
            var mmX = frameW / (format.Width - 25d);
            var mmY = frameH / (format.Height - 10d);
            var bounds = new Area((l.Position - 20 * mmX) / w, (t.Position - 5 * mmY) / h,
                format.Width * mmX / w, format.Height * mmY / h);
            if (checkContent && CutsDrawing(bounds)) continue;

            // A parallel outer rule at the expected margin supports the inside rule, never the outer one.
            var outside = CountNear(left, l.Position - 20 * mmX, w) + CountNear(right, r.Position + 5 * mmX, w) +
                CountNear(top, t.Position - 5 * mmY, h) + CountNear(bottom, b.Position + 5 * mmY, h);
            var inside = CountNear(left, l.Position + 20 * mmX, w) + CountNear(right, r.Position - 5 * mmX, w) +
                CountNear(top, t.Position + 5 * mmY, h) + CountNear(bottom, b.Position - 5 * mmY, h);
            var stampError = stamp is { IsValid: true } block
                ? Math.Abs(block.Right - (double)r.Position / w) + Math.Abs(block.Bottom - (double)b.Position / h) : 0;
            var stampMatches = stamp is { IsValid: true } && stampError < .025;
            var corners = Corner(l.Position, t.Position, 1, 1) + Corner(r.Position, t.Position, -1, 1) +
                Corner(l.Position, b.Position, 1, -1) + Corner(r.Position, b.Position, -1, -1);
            var outerError = Math.Abs(Math.Log(format.Width / widthMm)) + Math.Abs(Math.Log(format.Height / heightMm));
            var innerError = Math.Abs(Math.Log((format.Width - 25d) / widthMm)) + Math.Abs(Math.Log((format.Height - 10d) / heightMm));
            if (corners < 3 || (outside == 0 &&
                (inside > 0 || outerError < innerError || bounds.X < -.007 || bounds.Y < -.007 ||
                bounds.Right > 1.007 || bounds.Bottom > 1.007))) continue;
            var score = l.Score + r.Score + t.Score + b.Score + .7 * corners + 1.7 * (outside - inside) +
                (stampMatches ? 2.5 - 80 * stampError : 0) -
                2 * (Math.Abs(Math.Log(format.Width * PageCrop.PointsPerMm / widthPt)) +
                Math.Abs(Math.Log(format.Height * PageCrop.PointsPerMm / heightPt)));
            if (score <= bestScore) continue;
            bestScore = score;
            best = new(format.Name, format.Width, format.Height, bounds, turns, false);
        }
        return best is null ? null : best with { HasOutsideInk = checkContent && ContainsOutsideInk(best.Bounds) };

        static int CountNear(Rule[] rules, double position, int depth) =>
            rules.Any(rule => Math.Abs(rule.Position - position) < Math.Max(3, .003 * depth)) ? 1 : 0;

        bool CutsDrawing(Area bounds)
        {
            // Ignore a narrow band around the proposed paper edge where scanner noise and line thickness vary.
            var l = Math.Clamp((int)((bounds.X - .007) * inkW), 0, inkW);
            var r = Math.Clamp((int)Math.Ceiling((bounds.Right + .007) * inkW), 0, inkW);
            var t = Math.Clamp((int)((bounds.Y - .007) * inkH), 0, inkH);
            var b = Math.Clamp((int)Math.Ceiling((bounds.Bottom + .007) * inkH), 0, inkH);
            var inside = ink[r + b * inkStride] - ink[l + b * inkStride] - ink[r + t * inkStride] + ink[l + t * inkStride];
            return ink[^1] - inside > Math.Max(50, .03 * ink[^1]);
        }

        int Corner(int x, int y, int dx, int dy)
        {
            return Branch(dx, 0) && Branch(0, dy) && !Branch(0, -dy) ? 1 : 0;

            bool Branch(int alongX, int alongY)
            {
                var hits = 0;
                var length = Math.Clamp(Math.Min(w, h) / 80, 10, 24);
                for (var step = 0; step < 8; step++)
                {
                    var distance = 3 + step * length / 7;
                    for (var across = -3; across <= 3; across++)
                        if (Light(x + alongX * distance - alongY * across,
                            y + alongY * distance + alongX * across) < 620)
                        {
                            hits++;
                            break;
                        }
                }
                return hits >= 6;
            }
        }

        (Rule[], Rule[]) FindRules(bool vertical)
        {
            var (depth, length) = vertical ? (w, h) : (h, w);
            var first = new List<Rule>();
            var last = new List<Rule>();
            const int count = 100;
            for (var p = 5; p < depth - 5; p++)
            {
                if (p > .28 * depth && p < .72 * depth) continue;
                token.ThrowIfCancellationRequested();
                var hits = 0;
                var strength = 0;
                for (var k = 0; k < count; k++)
                {
                    var along = (int)(length * (.23 + .54 * k / (count - 1)));
                    var background = Math.Min(At(p - 4, along), At(p + 4, along));
                    var center = At(p, along);
                    var contrast = background - Math.Min(center, Math.Min(At(p - 1, along), At(p + 1, along)));
                    if (contrast < 18) continue;
                    hits++;
                    strength += Math.Min(contrast, 150) + Math.Clamp(background - center, 0, 150);
                }
                if (hits >= 63)
                    (p < depth / 2 ? first : last).Add(new(p, hits / 100d + strength / 40000d));
            }
            return (Distinct(first, true), Distinct(last, false));

            int At(int p, int along) => vertical ? Light(p, along) : Light(along, p);
            static Rule[] Distinct(List<Rule> rules, bool fromStart)
            {
                var best = new List<Rule>();
                foreach (var rule in rules.OrderByDescending(r => r.Score))
                {
                    if (best.Any(previous => Math.Abs(previous.Position - rule.Position) <= 4)) continue;
                    var center = rules.Where(candidate => Math.Abs(candidate.Position - rule.Position) <= 3 &&
                        candidate.Score >= rule.Score - .025).Average(candidate => candidate.Position);
                    best.Add(rule with { Position = (int)Math.Round(center) });
                }
                // Table rows must not displace faint frame rules nearest the paper edges.
                var outside = fromStart ? best.OrderBy(rule => rule.Position) : best.OrderByDescending(rule => rule.Position);
                return best.Take(8).Concat(outside.Take(8)).Distinct().ToArray();
            }
        }

        int Light(int x, int y)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return 765;
            var p = 4 * (x + y * w);
            return raster.Pixels[p] + raster.Pixels[p + 1] + raster.Pixels[p + 2];
        }

        bool ContainsOutsideInk(Area bounds)
        {
            for (var y = 6; y < h - 6; y += 2)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 6; x < w - 6; x += 2)
                {
                    if (bounds.Contains(new(x / (double)w, y / (double)h))) continue;
                    var p = 4 * (x + y * w);
                    var colors = raster.Pixels.AsSpan(p, 3);
                    var saturation = Math.Max(colors[0], Math.Max(colors[1], colors[2])) -
                        Math.Min(colors[0], Math.Min(colors[1], colors[2]));
                    if (saturation > 35 && Light(x, y) < 680) return true;
                    if (Light(x, y) < 430 && Light(x + 1, y + 1) < 490 && Light(x - 1, y + 1) < 490) return true;
                }
            }
            return false;
        }
    }

    private static Format? ClosestFormat(double frameWidth, double frameHeight)
    {
        Format? best = null;
        var distance = double.PositiveInfinity;
        foreach (var size in Formats)
        {
            Consider(size);
            Consider(new(size.Name, size.Height, size.Width));
        }
        return best;

        void Consider(Format format)
        {
            var x = Math.Log((format.Width - 25d) / frameWidth);
            var y = Math.Log((format.Height - 10d) / frameHeight);
            if (Math.Abs(x - y) > Math.Log(1.04) || Math.Max(Math.Abs(x), Math.Abs(y)) > Math.Log(1.35)) return;
            var score = Math.Abs(x) + Math.Abs(y) + 2 * Math.Abs(x - y);
            if (score >= distance) return;
            distance = score;
            best = format;
        }
    }
}
