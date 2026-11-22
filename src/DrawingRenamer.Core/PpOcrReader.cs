using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DrawingRenamer;

internal sealed class PpOcrReader(string languages)
{
    private static readonly Lazy<Model> EastSlavic = new(() => new("eslav"));
    private static readonly Lazy<Model> English = new(() => new("en"));
    private static readonly double[] Shears = [.20, .27, .35];
    private readonly bool russian = languages.Split('+').Contains("rus");

    internal (string Text, double Confidence, double Score) Read(Raster raster, int turn, CancellationToken token,
        bool preferEnglish = false, bool isDesignation = false, bool singleLine = false)
    {
        token.ThrowIfCancellationRequested();
        var upright = raster.Orient(turn);
        var model = (russian ? EastSlavic : English).Value;
        var best = Recognize(upright, model);
        if (best.Confidence < .97 && best.Text.Length > 0)
        {
            // The lettering can be inclined even after the page and stamp are aligned.
            foreach (var shear in Shears)
            {
                var corrected = Recognize(upright.Orient(0, shear), model);
                // A small confidence gain alone does not justify distorting the original glyphs.
                corrected.Score -= .05;
                if (corrected.Score > best.Score) best = corrected;
            }
        }

        if (preferEnglish && languages == "rus+eng" && !best.Text.Any(IsCyrillic))
        {
            var english = Recognize(upright, English.Value);
            if (english.Score > best.Score) best = english;
        }

        if (Regex.IsMatch(best.Text, @"^\(\d+$")) best.Text = (russian ? "С" : "C") + best.Text[1..];
        if (russian) best.Text = PreferCyrillic(best.Text);
        return best;

        (string Text, double Confidence, double Score) Recognize(Raster image, Model recognizer)
        {
            token.ThrowIfCancellationRequested();
            var lines = FindLines(image);
            if (lines.Count == 0) return ("", 0, 0);

            var height = lines.Max(line => line.Bottom - line.Top);
            var readings = lines.Where(line => line.Bottom - line.Top >= height * .35)
                .Select(line => recognizer.ReadLine(image, line, token))
                .Where(line => line.Text.Length > 0).ToArray();
            if (readings.Length == 0) return ("", 0, 0);

            var text = string.Join(" ", readings.Select(line => line.Text));
            if (isDesignation) text = text.Trim('.', ':', ' ');
            var confidence = readings.Average(line => line.Confidence);
            var score = confidence;
            if (isDesignation && !text.Any(char.IsDigit)) score -= .12;
            return (text, confidence, score);
        }

        List<Line> FindLines(Raster image)
        {
            var rows = new int[image.Height];
            for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
                if (Dark(image, x, y)) rows[y]++;

            var minInk = Math.Max(2, image.Width / 140);
            var gap = Math.Clamp(image.Height / 80, 2, 6);
            var lines = new List<Line>();
            for (var top = 0; top < image.Height; top++)
            {
                if (!IsTextRow(top)) continue;
                var bottom = top;
                for (var y = top + 1; y < image.Height && (singleLine || y - bottom <= gap); y++)
                    if (IsTextRow(y)) bottom = y;
                if (bottom - top >= 2)
                {
                    var left = image.Width;
                    var right = 0;
                    for (var y = top; y <= bottom; y++)
                    {
                        if (rows[y] >= image.Width * .85) continue;
                        for (var x = 0; x < image.Width; x++)
                        {
                            if (!Dark(image, x, y)) continue;
                            left = Math.Min(left, x);
                            right = Math.Max(right, x + 1);
                        }
                    }
                    if (right > left) lines.Add(new(left, top, right, bottom + 1));
                }
                top = bottom;
            }
            return lines;

            bool IsTextRow(int y) => rows[y] >= minInk && rows[y] < image.Width * .85;
        }
    }

    private static bool Dark(Raster image, int x, int y)
    {
        var offset = (y * image.Width + x) * 4;
        return image.Pixels[offset] + image.Pixels[offset + 1] + image.Pixels[offset + 2] < 660;
    }

    private static bool IsCyrillic(char c) => c is >= '\u0400' and <= '\u052f';

    private static string PreferCyrillic(string text) => Regex.Replace(text.Replace('(', 'С'), @"[\p{L}\p{M}\d]+", match =>
    {
        const string latin = "ABCEHKMOPTXYaceopxy";
        const string cyrillic = "АВСЕНКМОРТХУасеорху";
        if (match.Value.Any(c => char.IsLetter(c) && !IsCyrillic(c) && !latin.Contains(c)))
            return match.Value;

        return string.Concat(match.Value.Select(c =>
        {
            var index = latin.IndexOf(c);
            return index >= 0 ? cyrillic[index] : c;
        }));
    });

    private readonly record struct Line(int Left, int Top, int Right, int Bottom);

    private sealed class Model
    {
        private const int ImageHeight = 48;
        private readonly InferenceSession session;
        private readonly string inputName;
        private readonly string[] alphabet;

        internal Model(string language)
        {
            using var options = new SessionOptions { IntraOpNumThreads = 2 };
            var path = Path.Combine(AppContext.BaseDirectory, "ppocr", $"{language}_PP-OCRv5_rec_mobile.onnx");
            session = new InferenceSession(path, options);
            inputName = session.InputNames.Single();
            var characters = session.ModelMetadata.CustomMetadataMap["character"];
            alphabet = ["", .. characters.TrimEnd('\r', '\n').Split('\n').Select(c => c.TrimEnd('\r')), " "];
        }

        internal (string Text, double Confidence) ReadLine(Raster image, Line line, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var height = line.Bottom - line.Top;
            var margin = Math.Max(1, height / 6);
            var scale = (height + 2 * margin) / (double)ImageHeight;
            var resizedWidth = Math.Clamp((int)Math.Ceiling((line.Right - line.Left + 2 * margin) / scale), 1, 1280);
            var scaleX = Math.Max(scale, (line.Right - line.Left + 2 * margin) / (double)resizedWidth);
            var width = Math.Max(320, resizedWidth);
            var input = new DenseTensor<float>([1, 3, ImageHeight, width]);
            var values = input.Buffer.Span;
            var plane = width * ImageHeight;
            for (var y = 0; y < ImageHeight; y++)
            for (var x = 0; x < resizedWidth; x++)
            {
                var sourceX = line.Left - margin + (x + .5) * scaleX - .5;
                var sourceY = line.Top - margin + (y + .5) * scale - .5;
                var ix = (int)Math.Floor(sourceX);
                var iy = (int)Math.Floor(sourceY);
                var fx = (float)(sourceX - ix);
                var fy = (float)(sourceY - iy);
                for (var channel = 0; channel < 3; channel++)
                {
                    var top = Sample(ix, iy, channel) * (1 - fx) + Sample(ix + 1, iy, channel) * fx;
                    var bottom = Sample(ix, iy + 1, channel) * (1 - fx) + Sample(ix + 1, iy + 1, channel) * fx;
                    values[channel * plane + y * width + x] = (top * (1 - fy) + bottom * fy) / 127.5f - 1;
                }
            }

            using var output = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
            token.ThrowIfCancellationRequested();
            var probabilities = output.Single().AsTensor<float>();
            var classes = probabilities.Dimensions[2];
            if (classes != alphabet.Length) throw new InvalidDataException("PP-OCR model alphabet does not match its output.");

            var text = new StringBuilder();
            var sum = 0d;
            var count = 0;
            var previous = 0;
            var scores = probabilities.ToArray();
            for (var time = 0; time < scores.Length; time += classes)
            {
                var best = 0;
                for (var i = 1; i < classes; i++)
                    if (scores[time + i] > scores[time + best]) best = i;
                if (best != 0 && best != previous)
                {
                    text.Append(alphabet[best]);
                    sum += scores[time + best];
                    count++;
                }
                previous = best;
            }
            return (text.ToString().Trim(), count == 0 ? 0 : sum / count);

            byte Sample(int x, int y, int channel) => x >= 0 && x < image.Width &&
                y >= 0 && y < image.Height ? image.Pixels[(y * image.Width + x) * 4 + channel] : (byte)255;
        }
    }
}
