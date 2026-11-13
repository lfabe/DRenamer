using System.Text;

namespace DrawingRenamer.Tests;

public sealed class RecognitionTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("drawing-renamer-").FullName;

    [Theory]
    [InlineData(595, 842, 0)]
    [InlineData(842, 595, 90)]
    [InlineData(1191, 842, 180)]
    [InlineData(1684, 1191, 270)]
    [InlineData(2384, 1684, 0)]
    [InlineData(3370, 2384, 0)]
    [InlineData(900, 700, 90)]
    public void FindsFormOneOnDifferentSheets(int width, int height, int rotation)
    {
        var path = Pdf(Sheet(width, height, rotation));
        var result = new DrawingAnalyzer().Analyze(path, new(OcrEnabled: false), default);
        Assert.Equal(RecognitionStatus.Ready, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("ABCD.123456.001", candidate.Designation);
        Assert.Equal("Main Housing", candidate.Name);
        var manual = new DrawingAnalyzer().ReadRegions(path, 1, candidate.Regions, candidate.Turn,
            new(OcrEnabled: false), default);
        Assert.Equal(candidate.Name, manual.Name);
        var preview = new PdfRenderer().Render(path, 1, Area.Full, 640, default);
        Assert.Equal(rotation is 90 or 270 ? height > width : width > height, preview.Width > preview.Height);
    }

    [Fact]
    public void LaterTitleSheetsAreFoundAndDifferentPairsRequireReview()
    {
        var analyzer = new DrawingAnalyzer();
        var path = Pdf(new(595, 842, 0, ""), Sheet(595, 842, 0));
        Assert.Equal(2, Assert.Single(analyzer.Analyze(path, new(false), default).Candidates).Page);
        path = Pdf(Sheet(595, 842, 0), Sheet(595, 842, 0, "ABCD.123456.002"));
        var ambiguous = analyzer.Analyze(path, new(false), default);
        Assert.Equal(RecognitionStatus.Review, ambiguous.Status);
        Assert.Equal(2, ambiguous.Candidates.Count);
    }

    [Theory]
    [InlineData("scan-form1.pdf")]
    [InlineData("scan-rotated.pdf")]
    public void ScanUsesOfflineRussianOcrAndRequiresReview(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, file);
        var result = new DrawingAnalyzer().Analyze(path, new(), default);
        Assert.True(result.Status == RecognitionStatus.Review, result.Diagnostic);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("OCR", candidate.Method);
        Assert.Equal("АБВГ.123456.001", candidate.Designation);
        Assert.Equal("Корпус основной", candidate.Name, ignoreCase: true);
    }

    [Theory]
    [InlineData("eng")]
    [InlineData("rus+eng")]
    public void OutlineTextFallsBackToEnglishOcr(string languages)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "outline-form1.pdf");
        var analyzer = new DrawingAnalyzer();
        var disabled = analyzer.Analyze(path, new(false), default);
        Assert.True(disabled.Status == RecognitionStatus.Missing, disabled.Diagnostic);
        var result = analyzer.Analyze(path, new(Languages: languages), default);
        Assert.True(result.Status == RecognitionStatus.Review, result.Diagnostic);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("ABCD.123456.001", candidate.Designation);
        Assert.Equal("Main housing", candidate.Name, ignoreCase: true);
    }

    [Theory]
    [InlineData("AB.COVER", "Housing")]
    [InlineData("A", "A")]
    [InlineData("123", "7")]
    [InlineData("#_+ [A]", "Name")]
    public void WindowsCompatibleFieldsAreReadyWhenTitleBlockIsLocated(string designation, string name)
    {
        var sheet = Sheet(595, 842, 0, designation);
        var path = Pdf(sheet with { Content = sheet.Content.Replace("(Main) Tj 0 -14 Td (Housing) Tj", $"({name}) Tj") });
        var analyzer = new DrawingAnalyzer();
        var result = analyzer.Analyze(path, new(), default);

        Assert.Equal(RecognitionStatus.Ready, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(designation, candidate.Designation);
        Assert.Equal(name, candidate.Name);
        Assert.True(candidate.Verified);
        var manual = analyzer.ReadRegions(path, 1, candidate.Regions, candidate.Turn, new(), default);
        Assert.Equal("PDF", manual.Method);
        Assert.Equal(designation, manual.Designation);
        Assert.Equal(name, manual.Name);
    }

    [Theory]
    [InlineData("Test\"\"\"designation", "Housing?")]
    [InlineData("NUL.parts", "Housing")]
    [InlineData("ABCD.123456.001", "Housing?")]
    public void InvalidWindowsNamesStayVisibleForCorrection(string designation, string name)
    {
        var sheet = Sheet(595, 842, 0, designation);
        var path = Pdf(sheet with { Content = sheet.Content.Replace("(Main) Tj 0 -14 Td (Housing) Tj", $"({name}) Tj") });
        var result = new DrawingAnalyzer().Analyze(path, new(), default);

        Assert.Equal(RecognitionStatus.Review, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("PDF", candidate.Method);
        Assert.Equal(designation, candidate.Designation);
        Assert.Equal(name, candidate.Name);
        Assert.False(candidate.Verified);
    }

    [Fact]
    public void ProtectedPdfReportsPasswordError()
    {
        var result = new DrawingAnalyzer().Analyze(Path.Combine(AppContext.BaseDirectory, "protected.pdf"), new(), default);
        Assert.Equal(RecognitionStatus.Error, result.Status);
        Assert.Contains("паролем", result.Diagnostic);
    }

    [Fact]
    public void FormTwoUsesItsOwnFieldSizes()
    {
        var path = Pdf(new Page(595, 842, 0, """
            55 14 525 113 re S
            239 14 m 239 127 l S
            239 84 m 580 84 l S
            437 56 m 580 56 l S
            437 14 m 437 84 l S
            BT /F1 12 Tf 250 99 Td (ABCD.123456.001) Tj ET
            BT /F1 12 Tf 250 38 Td (Parts list) Tj ET
            """));
        var result = new DrawingAnalyzer().Analyze(path, new(false, Templates: [TitleTemplate.Defaults[1]]), default);
        Assert.Equal(RecognitionStatus.Ready, result.Status);
        Assert.Equal("Parts list", Assert.Single(result.Candidates).Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FormTwoRecognizesTitleBlockWithRuleSplitAtCellBorders(bool filled)
    {
        var path = Pdf(new Page(595, 842, 0, """
            55 14 m 580 14 l 580 827 l 55 827 l h S
            55 127 m 580 127 l S
            239 14 m 239 127 l S
            239 84 m 580 84 l S
            437 14 m 437 84 l S
            437 56 m 451 56 l S
            451 56 m 465 56 l S
            465 56 m 479 56 l S
            479 56 m 522 56 l S
            522 56 m 580 56 l S
            """ + "\n" + (filled ? """
            BT /F1 12 Tf 250 99 Td (ABCD.123456.001) Tj ET
            BT /F1 12 Tf 250 38 Td (Parts list) Tj ET
            """ : "")));

        var result = new DrawingAnalyzer().Analyze(path, new(OcrEnabled: false), default);
        if (filled)
        {
            Assert.Equal(RecognitionStatus.Ready, result.Status);
            var candidate = Assert.Single(result.Candidates);
            Assert.Equal("ABCD.123456.001", candidate.Designation);
            Assert.Equal("Parts list", candidate.Name);
        }
        else
        {
            Assert.Equal(RecognitionStatus.Missing, result.Status);
            Assert.Empty(result.Candidates);
            Assert.Contains("Штамп найден", result.Diagnostic);
        }
    }

    [Fact]
    public void GroupSpecificationUsesFirstSheetAndIgnoresPairsInContinuationTable()
    {
        var first = Sheet(842, 595, 0);
        var continuation = ContinuationSheet(842, 595, 0, left: false);
        var path = Pdf(first, continuation with { Content = continuation.Content + """

            62 66 525 113 re S
            246 136 m 587 136 l S
            444 108 m 587 108 l S
            BT /F1 12 Tf 260 152 Td (Another part) Tj ET
            BT /F1 12 Tf 260 90 Td (Table data) Tj ET
            """ });
        var result = new DrawingAnalyzer().Analyze(path, new(), default);

        Assert.Equal(RecognitionStatus.Ready, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(1, candidate.Page);
        Assert.Equal("ABCD.123456.001", candidate.Designation);
        Assert.Equal("Main Housing", candidate.Name);
    }

    [Theory]
    [InlineData(595, 842, 0, false)]
    [InlineData(842, 595, 90, false)]
    [InlineData(595, 842, 0, true)]
    [InlineData(595, 842, 270, true)]
    [InlineData(3370, 2384, 0, false)]
    public void ContinuationStampReadsDesignationAndLeavesNameForManualEntry(
        int width, int height, int rotation, bool left)
    {
        var path = Pdf(ContinuationSheet(width, height, rotation, left));
        var result = new DrawingAnalyzer().Analyze(path, new(false), default);

        Assert.Equal(RecognitionStatus.Review, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Group A", candidate.Designation);
        Assert.Empty(candidate.Name);
        Assert.False(candidate.Verified);
        Assert.True(candidate.Regions.Designation.IsValid);
        Assert.False(candidate.Regions.Name.IsValid);
    }

    [Fact]
    public void IgnoresPageBorderAndCellsNestedInsideTheDrawingStamp()
    {
        var sheet = Sheet(2384, 1684, 0);
        var path = Pdf(sheet with { Content = sheet.Content + """

            2 2 2380 1680 re S
            1844 14 184 42.5 re S
            1844 42 m 2028 42 l S
            1844 30 m 2028 30 l S
            BT /F1 9 Tf 1920 46 Td (Signatures) Tj ET
            BT /F1 9 Tf 1920 21 Td (Dates) Tj ET
            """ });
        var result = new DrawingAnalyzer().Analyze(path, new(false), default);

        Assert.Equal(RecognitionStatus.Ready, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Main Housing", candidate.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstructionDrawingReadsTheNameFromTheBottomLeftStampField(bool filled)
    {
        var path = Pdf(new Page(2384, 1684, 0, """
            q 1 0 0 1 1844 14 cm
            0.7 w
            0 0 m 525 0 l 525 156 l 0 156 l S
            0 156 m 0 85 l S
            0 85 m 0 42 l S
            0 42 m 0 0 l S
            184 0 m 184 156 l S
            184 128 m 525 128 l S
            184 85 m 525 85 l S
            184 43 m 525 43 l S
            382 0 m 382 85 l S
            382 71 m 525 71 l S
            BT /F1 12 Tf 222 137 Td (ABCD.123456.001) Tj ET
            BT /F1 12 Tf 222 100 Td (Complex) Tj ET
            BT /F1 12 Tf 222 55 Td (Building) Tj ET
            BT /F1 12 Tf 400 18 Td (Design office) Tj ET
            """ + "\n" + (filled ? "BT /F1 12 Tf 222 18 Td (Floor plan) Tj ET\nQ" : "Q")));

        var result = new DrawingAnalyzer().Analyze(path, new(), default);

        Assert.Equal(filled ? RecognitionStatus.Ready : RecognitionStatus.Review, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("ABCD.123456.001", candidate.Designation);
        Assert.Equal(filled ? "Floor plan" : "", candidate.Name);
    }

    [Fact]
    public void CustomTemplateLoadsAndRecognizesAScaledTitleBlock()
    {
        var templatePath = Path.Combine(folder, "template.json");
        File.WriteAllText(templatePath, """
            [{"name":"Custom","widthMm":185,"heightMm":55,
              "designation":{"x":0.35,"y":0,"width":0.60,"height":0.27},
              "title":{"x":0.35,"y":0.27,"width":0.38,"height":0.45},
              "horizontalRules":[0.269,0.635]}]
            """);
        var template = Assert.Single(TitleTemplate.Load(templatePath));
        Assert.Equal("Custom", template.Name);
        var sheet = Sheet(842, 595, 0);
        var path = Pdf(sheet with { Content = "q .78 0 0 .78 180 0 cm\n" + sheet.Content + "\nQ" });
        var result = new DrawingAnalyzer().Analyze(path, new(false, Templates: [template]), default);
        Assert.Equal("Main Housing", Assert.Single(result.Candidates).Name);
    }

    [Fact]
    public void CancellationStopsBeforeRecognition()
    {
        var path = Pdf(Sheet(595, 842, 0));
        Assert.Throws<OperationCanceledException>(() =>
            new DrawingAnalyzer().Analyze(path, new(), new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData(" АБВГ . 123456 . 001   СБ ", false, "АБВГ.123456.001 СБ")]
    [InlineData("ABC.O0123.O01", true, "ABC.00123.001")]
    [InlineData("ABC.O0123.O01", false, "ABC.O0123.O01")]
    public void DesignationNormalizationKeepsSourceCharactersUnlessOcr(string source, bool ocr, string expected) =>
        Assert.Equal(expected, DrawingAnalyzer.NormalizeDesignation(source, ocr));

    [Fact]
    public void BrokenPdfIsIsolated()
    {
        var path = Path.Combine(folder, "bad.pdf");
        File.WriteAllText(path, "%PDF-1.4\n");
        Assert.Equal(RecognitionStatus.Error, new DrawingAnalyzer().Analyze(path, new(), default).Status);
    }

    private record Page(int Width, int Height, int Rotation, string Content);

    private static Page ContinuationSheet(int width, int height, int rotation, bool left)
    {
        var x = left ? 14 : width - 539;
        var revision = left ? 341 : 0;
        var pageNumber = left ? 0 : 497;
        var text = left ? 45 : 250;
        return new(width, height, rotation, FormattableString.Invariant($"""
            q 1 0 0 1 {x} 14 cm
            0.7 w
            0 0 525 42.5 re S
            {revision} 0 184 42.5 re S
            {revision} 14 m {revision + 20} 14 l S
            {revision + 20} 14 m {revision + 48} 14 l S
            {revision + 48} 14 m {revision + 113} 14 l S
            {revision + 113} 14 m {revision + 156} 14 l S
            {revision + 156} 14 m {revision + 184} 14 l S
            {revision} 28 m {revision + 184} 28 l S
            {pageNumber} 0 28 42.5 re S
            {pageNumber} 23 m {pageNumber + 28} 23 l S
            BT /F1 12 Tf {text} 12 Td (Group A) Tj ET
            BT /F1 10 Tf {pageNumber + 5} 7 Td (2) Tj ET
            Q
            """));
    }

    private static Page Sheet(int width, int height, int rotation, string designation = "ABCD.123456.001")
    {
        var x = width - 540;
        return new(width, height, rotation, FormattableString.Invariant($"""
            q 1 0 0 1 {x} 14 cm
            0.7 w
            0 0 525 156 re S
            184 0 m 184 156 l S
            184 114 m 525 114 l S
            382 57 m 525 57 l S
            184 43 m 382 43 l S
            382 0 m 382 114 l S
            BT /F1 14 Tf 200 129 Td ({designation}) Tj ET
            BT /F1 13 Tf 200 96 Td (Main) Tj 0 -14 Td (Housing) Tj ET
            Q
            """));
    }

    private string Pdf(params Page[] pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Count {pages.Length} /Kids [{string.Join(" ", pages.Select((_, i) => $"{4 + i * 2} 0 R"))}] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        foreach (var page in pages)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page.Width} {page.Height}] /Rotate {page.Rotation} /Resources << /Font << /F1 3 0 R >> >> /Contents {objects.Count + 2} 0 R >>");
            objects.Add($"<< /Length {page.Content.Length} >>\nstream\n{page.Content}\nendstream");
        }
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var obj in objects)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{obj}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(folder, Guid.NewGuid() + ".pdf");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
        return path;
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
