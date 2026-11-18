using System.Text;
using System.Text.RegularExpressions;
using TesseractOCR;
using TesseractOCR.Enums;
using UglyToad.PdfPig;

namespace DrawingRenamer;

public enum RecognitionStatus { Ready, Review, Missing, Error }

public sealed record RecognitionOptions(bool OcrEnabled = true, string Languages = "rus+eng",
    IReadOnlyList<TitleTemplate>? Templates = null);
public sealed record Candidate(string Designation, string Name, int Page, int Turn, FieldRegions Regions,
    string Method, double? OcrConfidence = null, bool Verified = false);
public sealed record RecognitionResult(RecognitionStatus Status, int PageCount, IReadOnlyList<Candidate> Candidates,
    string Diagnostic)
{
    public string? SourceHash { get; init; }
}

public sealed class DrawingAnalyzer
{
    private readonly PdfRenderer renderer = new();

    public RecognitionResult Analyze(string path, RecognitionOptions options, CancellationToken token)
    {
        var matches = new List<Candidate>();
        var continuations = new List<Candidate>();
        var incomplete = new List<Candidate>();
        var pages = 0;
        var failed = 0;
        var locatedTitle = false;
        string? failure = null;
        using var ocr = new OcrReader(options);
        var templates = options.Templates ?? TitleTemplate.Defaults;
        string? sourceHash;
        try
        {
            token.ThrowIfCancellationRequested();
            using var input = File.OpenRead(path);
            sourceHash = RenameService.Hash(input);
            input.Position = 0;
            using var document = PdfDocument.Open(input);
            pages = document.NumberOfPages;
            for (var number = 1; number <= pages; number++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var page = document.GetPage(number);
                    var sheet = new PdfSheet(page);
                    var lines = sheet.Lines(token);
                    var found = Find(lines, page.Width, page.Height, templates, token).ToList();
                    if (found.Count == 0 && options.OcrEnabled)
                    {
                        var raster = renderer.Render(path, number, Area.Full, 2300, token);
                        var rasterLines = raster.FindLines(Area.Full, token).ToArray();
                        found.AddRange(Find(rasterLines, page.Width, page.Height, templates, token));
                    }
                    if (found.FirstOrDefault(title => title.IsContinuation) is { } continuation)
                    {
                        locatedTitle = true;
                        var candidate = ReadText(sheet, number, continuation.Regions, continuation.Turn, false);
                        if (candidate.Designation.Length == 0 && options.OcrEnabled)
                            candidate = ReadOcr(path, candidate, ocr, token);
                        if (candidate.Designation.Length > 0 && (candidate.OcrConfidence is null or >= .35))
                            continuations.Add(candidate);
                        continue;
                    }
                    var verified = found.Count != 0;
                    locatedTitle |= verified;
                    if (!verified)
                    {
                        for (var turn = 0; turn < 4; turn++)
                        foreach (var template in templates)
                        {
                            var (w, h) = (turn & 1) == 0 ? (page.Width, page.Height) : (page.Height, page.Width);
                            if (TitleLocator.Guess(template, w, h, turn) is { } guess) found.Add(guess);
                        }
                    }
                    // A cell or group of cells inside a larger stamp cannot identify the document.
                    foreach (var location in found)
                    {
                        token.ThrowIfCancellationRequested();
                        var frame = location.PageFrame;
                        if (location.Verified && found.Any(other => ContainsSmallerStamp(other.PageFrame, frame)))
                            continue;
                        if (location.Verified && TitleTemplate.Defaults.Contains(location.Template) &&
                            found.Any(other => !TitleTemplate.Defaults.Contains(other.Template) &&
                                other.Turn == location.Turn && TitleLocator.SameFrame(other.Frame, location.Frame)))
                            continue;
                        var regions = location.Regions;
                        var candidate = ReadText(sheet, number, regions, location.Turn, location.Verified);
                        if (!location.Verified && location.Template.PageTurn is null)
                        {
                            if (LooksLikeGuessedPair(candidate)) matches.Add(candidate);
                            continue;
                        }

                        if (!HasBothFields(candidate) && options.OcrEnabled)
                        {
                            var recognized = ReadOcr(path, candidate, ocr, token);
                            if (recognized.OcrConfidence >= .35)
                                candidate = recognized;
                        }
                        if (HasBothFields(candidate))
                            matches.Add(RenameService.FileName(candidate.Designation, candidate.Name) is null
                                ? candidate with { Verified = false } : candidate);
                        else if (candidate.Designation.Length > 0 || candidate.Name.Length > 0)
                            incomplete.Add(candidate with { Verified = false });
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    failed++;
                    failure = Diagnostic(ex);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return new(RecognitionStatus.Error, pages, [], Diagnostic(ex));
        }
        var continuationOnly = matches.Count == 0 && incomplete.Count == 0 && continuations.Count != 0;
        var candidates = matches.Count != 0 ? matches : incomplete.Count != 0 ? incomplete : continuations;
        var pairs = candidates.GroupBy(c => (c.Designation, c.Name)).Select(g =>
            g.OrderByDescending(c => c.Verified).ThenByDescending(c => c.OcrConfidence).First()).ToArray();
        var status = pairs.Length switch
        {
            0 when failed == pages && failed > 0 => RecognitionStatus.Error,
            0 => RecognitionStatus.Missing,
            1 when pairs[0].Verified && failed == 0 => RecognitionStatus.Ready,
            _ => RecognitionStatus.Review
        };
        var diagnostic = status switch
        {
            RecognitionStatus.Ready => "Графы подтверждены геометрией штампа.",
            _ when failed > 0 => $"Страниц с ошибкой: {failed} из {pages}. {failure}",
            _ when pairs.Length > 1 => "Найдено несколько пар. Выберите правильный вариант.",
            _ when continuationOnly => "Найден штамп последующего листа. Введите наименование вручную.",
            RecognitionStatus.Review => "Проверьте предложенные значения перед переименованием.",
            _ when locatedTitle => "Штамп найден, но графы не прочитаны. Заполните пустые графы вручную или выделите текст на листе.",
            _ => "Графы не распознаны. Выделите их на листе или введите данные."
        };
        return new(status, pages, pairs, diagnostic) { SourceHash = sourceHash };
    }

    public Candidate ReadRegions(string path, int page, FieldRegions regions, int turn,
        RecognitionOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!regions.Designation.IsValid || !regions.Name.IsValid)
            throw new ArgumentException("Укажите обе графы.", nameof(regions));
        using var document = PdfDocument.Open(path);
        using var ocr = new OcrReader(options);
        var candidate = ReadText(new(document.GetPage(page)), page, regions, turn, false);
        return HasBothFields(candidate) || !options.OcrEnabled ? candidate : ReadOcr(path, candidate, ocr, token);
    }

    public TitleTemplate? CreateTemplate(string path, int number, FieldRegions regions, int turn,
        string name, RecognitionOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!regions.Designation.IsValid || !regions.Name.IsValid)
            throw new ArgumentException("Укажите обе графы.", nameof(regions));
        using var document = PdfDocument.Open(path);
        var page = document.GetPage(number);
        var (width, height) = (turn & 1) == 0 ? (page.Width, page.Height) : (page.Height, page.Width);
        var lines = new PdfSheet(page).Lines(token);
        var template = Create(lines);
        if (template is null && options.OcrEnabled)
        {
            var raster = renderer.Render(path, number, Area.Full, 2300, token);
            template = Create(raster.FindLines(Area.Full, token).ToArray());
        }
        if (template is not null || regions.TitleBlock is { IsValid: true }) return template;

        const double mmPerPoint = 25.4 / 72;
        return new TitleTemplate(name, width * mmPerPoint, height * mmPerPoint,
            regions.Designation.Rotate(turn), regions.Name.Rotate(turn), []) { PageTurn = turn };

        TitleTemplate? Create(IReadOnlyList<Segment> segments)
        {
            if (regions.TitleBlock is { IsValid: true } frame)
                return TitleLocator.CreateTemplate(segments, width, height, turn, regions, frame, name, token);

            var templates = options.Templates ?? TitleTemplate.Defaults;
            foreach (var location in TitleLocator.Find(segments, width, height, turn, templates, token))
            {
                if (location.IsContinuation) continue;
                var result = TitleLocator.CreateTemplate(segments, width, height, turn, regions, location.PageFrame, name, token);
                if (result is not null) return result;
            }
            return null;
        }
    }

    private static IEnumerable<LocatedTitle> Find(IEnumerable<Segment> lines, double width, double height,
        IReadOnlyList<TitleTemplate> templates, CancellationToken token)
    {
        for (var turn = 0; turn < 4; turn++)
        {
            var (w, h) = (turn & 1) == 0 ? (width, height) : (height, width);
            foreach (var item in TitleLocator.Find(lines, w, h, turn, templates, token))
                yield return item;
        }
    }

    private static Candidate ReadText(PdfSheet sheet, int page, FieldRegions regions, int turn, bool verified)
    {
        var designation = NormalizeDesignation(sheet.Read(regions.Designation, turn));
        var name = regions.Name.IsValid ? NormalizeName(sheet.Read(regions.Name, turn)) : "";
        return new(designation, name, page, turn, regions, "PDF", Verified: verified);
    }

    private Candidate ReadOcr(string path, Candidate candidate, OcrReader ocr, CancellationToken token)
    {
        var designation = candidate.Designation;
        var name = candidate.Name;
        var confidence = 1d;
        var englishFirst = false;
        if (designation.Length == 0)
        {
            var result = Read(candidate.Regions.Designation, isDesignation: true);
            designation = NormalizeDesignation(result.Text, ocr: true);
            confidence = result.Confidence;
            englishFirst = designation.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') &&
                !designation.Any(c => c is >= '\u0400' and <= '\u052f');
        }
        if (name.Length == 0 && candidate.Regions.Name.IsValid)
        {
            var result = Read(candidate.Regions.Name, preferEnglish: englishFirst);
            name = NormalizeName(result.Text);
            confidence = Math.Min(confidence, result.Confidence);
        }
        return candidate with { Designation = designation, Name = name, Method = "OCR",
            OcrConfidence = confidence, Verified = false };

        (string Text, double Confidence) Read(Area region, bool preferEnglish = false, bool isDesignation = false) =>
            ocr.Read(renderer.Render(path, candidate.Page, region.Inset(.017), 1450, token),
                candidate.Turn, token, preferEnglish, isDesignation);
    }

    public static string NormalizeName(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormC),
        @"\s+", " ").Trim();

    public static string NormalizeDesignation(string value, bool ocr = false)
    {
        var normalized = NormalizeName(value).Replace('‐', '-').Replace('–', '-').Replace('—', '-');
        normalized = Regex.Replace(normalized, @"\s*([.\-])\s*", "$1");
        return ocr ? Regex.Replace(normalized, @"(?<=\.)([OОoо0-9]+)(?=\.|-|\u2012|\s|$)",
            m => m.Value.Replace('O', '0').Replace('О', '0').Replace('o', '0').Replace('о', '0')) : normalized;
    }

    private static bool HasBothFields(Candidate candidate) => candidate.Designation.Length > 0 && candidate.Name.Length > 0;

    private static bool ContainsSmallerStamp(Area outer, Area inner)
    {
        var margin = outer.Inset(-.025);
        return outer.Width * outer.Height > inner.Width * inner.Height * 1.12 &&
            margin.Contains(new(inner.X, inner.Y)) && margin.Contains(new(inner.Right, inner.Bottom));
    }

    private static bool LooksLikeGuessedPair(Candidate candidate) =>
        Regex.IsMatch(candidate.Designation,
            @"^(?=.{3,80}$)(?=.*\d)[\p{L}\d]+(?:[.\u2012\-][\p{L}\d]+)+(?:\s[\p{L}\d]{1,6})?$") &&
        candidate.Name.Length is >= 2 and <= 160 && candidate.Name.Any(char.IsLetter) &&
        !candidate.Name.Contains('\uFFFD') && !candidate.Name.Any(char.IsControl) &&
        !Regex.IsMatch(candidate.Name, @"^(см\.?\s*лист|лист|наименование)\b", RegexOptions.IgnoreCase) &&
        !string.Equals(candidate.Designation, candidate.Name, StringComparison.OrdinalIgnoreCase);

    private static string Diagnostic(Exception ex) => ex.GetType().Name.Contains("Encrypted", StringComparison.Ordinal)
        ? "PDF защищён паролем." : ex switch
        {
            UnauthorizedAccessException => "Нет доступа к файлу.",
            DllNotFoundException or BadImageFormatException => "Нативная библиотека PDFium или OCR недоступна.",
            IOException => "Ошибка чтения файла.",
            _ => $"Ошибка обработки PDF или OCR ({ex.GetType().Name})."
        };

    private sealed class OcrReader(RecognitionOptions options) : IDisposable
    {
        private readonly bool russianEnabled = options.Languages.Split('+').Contains("rus");
        private Engine? engine;
        private Engine? englishFirstEngine;

        public (string Text, double Confidence) Read(Raster raster, int turn, CancellationToken token,
            bool preferEnglish, bool isDesignation)
        {
            token.ThrowIfCancellationRequested();
            var data = Path.Combine(AppContext.BaseDirectory, "tessdata");
            var reader = preferEnglish && options.Languages == "rus+eng"
                ? englishFirstEngine ??= new Engine(data, "eng+rus", EngineMode.LstmOnly)
                : engine ??= new Engine(data, options.Languages, EngineMode.LstmOnly);
            var best = Recognize(0);
            if (best.Confidence < .85)
            {
                // Drawing fonts are often inclined at about 15 degrees; scans can distort the slope.
                foreach (var shear in new[] { .20, .27 })
                {
                    var corrected = Recognize(shear);
                    if (corrected.Confidence > best.Confidence && !string.IsNullOrWhiteSpace(corrected.Text))
                        best = corrected;
                }
            }

            // A broad crop can enlarge thin strokes and scan noise. Retry a single designation line at two scales.
            if (isDesignation && best.Confidence < .90 && !best.Text.Trim().Contains('\n'))
            {
                token.ThrowIfCancellationRequested();
                using var upright = TesseractOCR.Pix.Image.LoadFromMemory(raster.ToBmp(turn, .25));
                using var gray = upright.ConvertRGBToGray();
                foreach (var scale in new[] { .60f, .75f })
                {
                    token.ThrowIfCancellationRequested();
                    using var scaled = gray.Scale(scale, scale);
                    var corrected = Process(scaled, PageSegMode.SingleLine);
                    var text = NormalizeDesignation(corrected.Text, ocr: true);
                    if (corrected.Confidence > best.Confidence + .01 && text.Length > 0 &&
                        text.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                        best = corrected;
                }
            }

            return russianEnabled ? (PreferCyrillic(best.Text).Replace('(', 'С'), best.Confidence) : best;

            (string Text, double Confidence) Recognize(double shear)
            {
                token.ThrowIfCancellationRequested();
                using var image = TesseractOCR.Pix.Image.LoadFromMemory(raster.ToBmp(turn, shear));
                return Process(image, PageSegMode.SingleBlock);
            }

            (string Text, double Confidence) Process(TesseractOCR.Pix.Image image, PageSegMode mode)
            {
                using var page = reader.Process(image, mode);
                token.ThrowIfCancellationRequested();
                return (page.Text, page.MeanConfidence);
            }
        }

        private static string PreferCyrillic(string text) => Regex.Replace(text, @"[\p{L}\p{M}\d]+", match =>
        {
            const string latin = "ABCEHKMOPTXYaceopxy";
            const string cyrillic = "АВСЕНКМОРТХУасеорху";
            if (match.Value.Any(c => char.IsLetter(c) && c is not (>= '\u0400' and <= '\u052f') && !latin.Contains(c)))
                return match.Value;

            return string.Concat(match.Value.Select(c =>
            {
                var index = latin.IndexOf(c);
                return index >= 0 ? cyrillic[index] : c;
            }));
        });

        public void Dispose()
        {
            engine?.Dispose();
            englishFirstEngine?.Dispose();
        }
    }
}
