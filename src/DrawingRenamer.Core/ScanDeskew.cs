namespace DrawingRenamer;

internal static class ScanDeskew
{
    private const int Samples = 72;
    private const int MaxStep = 30;
    private const double SlopeStep = .002;

    internal static double? FindFrameSlope(Raster raster, CancellationToken token)
    {
        var horizontal = EdgeVotes(raster, vertical: false, token);
        var vertical = EdgeVotes(raster, vertical: true, token);
        var scores = horizontal.Zip(vertical, (h, v) => h == 0 || v == 0 ? 0 : h + v).ToArray();
        var best = scores.Max();
        if (best == 0) return null;
        var nearBest = Enumerable.Range(0, scores.Length).Where(i => scores[i] >= best - 3).ToArray();
        if (nearBest[^1] - nearBest[0] > 4) return null;
        return (nearBest.Average() - MaxStep) * SlopeStep;
    }

    private static int[] EdgeVotes(Raster raster, bool vertical, CancellationToken token)
    {
        var (length, depth) = vertical ? (raster.Height, raster.Width) : (raster.Width, raster.Height);
        var votes = new int[2 * MaxStep + 1];
        if (Math.Min(length, depth) < 160) return votes;

        // Standard drawing borders can be only 5 mm from the paper edge.
        var start = Math.Max(3, depth / 150);
        var end = depth / 6;
        var offsets = new int[Samples];
        var positions = Enumerable.Range(0, Samples)
            .Select(i => (int)Math.Round(length * (.14 + .72 * i / (Samples - 1)))).ToArray();
        var ink = new bool[depth * Samples];
        for (var y = 1; y < depth - 1; y++)
        for (var i = 0; i < Samples; i++)
        {
            var pixel = vertical ? 4 * (y + raster.Width * positions[i]) : 4 * (positions[i] + raster.Width * y);
            var stride = vertical ? 4 : 4 * raster.Width;
            ink[y * Samples + i] = Dark(pixel) || Dark(pixel - stride) || Dark(pixel + stride);
        }

        for (var step = -MaxStep; step <= MaxStep; step++)
        {
            token.ThrowIfCancellationRequested();
            var slope = step * SlopeStep * (vertical ? -1 : 1);
            for (var i = 0; i < Samples; i++)
                offsets[i] = (int)Math.Round(slope * (positions[i] - length / 2d));
            votes[step + MaxStep] = BestRule(start, end) + BestRule(depth - end, depth - start);
        }
        return votes;

        bool Dark(int p) => raster.Pixels[p] + raster.Pixels[p + 1] + raster.Pixels[p + 2] < 560;

        int BestRule(int from, int to)
        {
            var best = 0;
            for (var y = from; y <= to; y++)
            {
                var hits = 0;
                for (var i = 0; i < Samples; i++)
                {
                    var sample = y + offsets[i];
                    if ((uint)sample < (uint)depth && ink[sample * Samples + i]) hits++;
                }
                best = Math.Max(best, hits);
            }
            return best >= 3 * Samples / 4 ? best : 0;
        }
    }
}
