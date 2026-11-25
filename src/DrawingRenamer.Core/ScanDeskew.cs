namespace DrawingRenamer;

internal static class ScanDeskew
{
    private const int Samples = 72;
    private const int MaxStep = 120;
    private const double SlopeStep = .0005;

    internal static double? FindFrameSlope(Raster raster, CancellationToken token)
    {
        var horizontal = EdgeVotes(raster, vertical: false, token);
        var vertical = EdgeVotes(raster, vertical: true, token);
        var scores = horizontal.Zip(vertical, (h, v) =>
            h.Rules == 2 || h.Rules == 1 && v.Rules > 0 ? 2 * h.Score + v.Score : 0).ToArray();
        var best = scores.Max();
        if (best == 0) return null;
        var nearBest = Enumerable.Range(0, scores.Length).Where(i => scores[i] >= best - 4).ToArray();
        if (nearBest[^1] - nearBest[0] > 12) return null;
        return (nearBest.Average() - MaxStep) * SlopeStep;
    }

    private static (int Rules, int Score)[] EdgeVotes(Raster raster, bool vertical, CancellationToken token)
    {
        var (length, depth) = vertical ? (raster.Height, raster.Width) : (raster.Width, raster.Height);
        var votes = new (int Rules, int Score)[2 * MaxStep + 1];
        if (Math.Min(length, depth) < 160) return votes;

        // Standard drawing borders can be only 5 mm from the paper edge.
        var start = Math.Max(3, depth / 150);
        var end = depth / 6;
        var offsets = new int[Samples];
        var positions = Enumerable.Range(0, Samples)
            .Select(i => (int)Math.Round(length * (.14 + .72 * i / (Samples - 1)))).ToArray();
        var ink = new byte[depth * Samples];
        var stride = vertical ? 4 : 4 * raster.Width;
        for (var y = 4; y < depth - 4; y++)
        for (var i = 0; i < Samples; i++)
        {
            var pixel = vertical ? 4 * (y + raster.Width * positions[i]) : 4 * (positions[i] + raster.Width * y);
            // A faded rule is still darker than the paper on both sides. Scanner shadows often have only one edge.
            var contrast = Math.Min(Light(pixel - 4 * stride), Light(pixel + 4 * stride)) - Light(pixel);
            ink[y * Samples + i] = contrast >= 24 ? (byte)Math.Clamp(contrast / 40, 1, 5) : (byte)0;
        }

        for (var step = -MaxStep; step <= MaxStep; step++)
        {
            token.ThrowIfCancellationRequested();
            var slope = step * SlopeStep * (vertical ? -1 : 1);
            for (var i = 0; i < Samples; i++)
                offsets[i] = (int)Math.Round(slope * (positions[i] - length / 2d));
            var first = BestRule(start, end);
            var last = BestRule(depth - end, depth - start);
            votes[step + MaxStep] = ((first > 0 ? 1 : 0) + (last > 0 ? 1 : 0), first + last);
        }
        return votes;

        int Light(int p) => raster.Pixels[p] + raster.Pixels[p + 1] + raster.Pixels[p + 2];

        int BestRule(int from, int to)
        {
            var best = 0;
            for (var y = from; y <= to; y++)
            {
                var hits = 0;
                var strength = 0;
                for (var i = 0; i < Samples; i++)
                {
                    var sample = y + offsets[i];
                    if ((uint)sample >= (uint)depth) continue;
                    var value = ink[sample * Samples + i];
                    if (value == 0) continue;
                    hits++;
                    strength += value;
                }
                if (hits >= Samples * 2 / 3) best = Math.Max(best, 4 * hits + strength);
            }
            return best;
        }
    }
}
