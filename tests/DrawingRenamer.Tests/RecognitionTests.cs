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
        Assert.Equal(RenameService.Hash(path), result.SourceHash);
        Assert.Equal(RecognitionStatus.Ready, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("ABCD.123456.001", candidate.Designation);
        Assert.Equal("MAIN HOUSING", candidate.Name);
        var manual = new DrawingAnalyzer().ReadRegions(path, 1, candidate.Regions, candidate.Turn,
            new(OcrEnabled: false), default);
        Assert.Equal(candidate.Name, manual.Name);
        var preview = new PdfRenderer().Render(path, 1, Area.Full, 640, default);
        Assert.Equal(rotation is 90 or 270 ? height > width : width > height, preview.Width > preview.Height);
    }

    [Fact]
    public void UnreadableMetadataFallsBackToRecognitionOfTheRenderedPage()
    {
        var path = WritePdf(metadataAsStream: true, Sheet(595, 842, 0));
        var analyzer = new DrawingAnalyzer();
        Assert.Equal(RecognitionStatus.Error, analyzer.Analyze(path, new(OcrEnabled: false), default).Status);

        var options = new RecognitionOptions(Languages: "eng");
        var result = analyzer.Analyze(path, options, default);
        Assert.Equal(1, result.PageCount);
        Assert.Equal(RecognitionStatus.Review, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("ABCD.123456.001", candidate.Designation);
        Assert.Equal("MAIN HOUSING", candidate.Name, ignoreCase: true);
        var manual = analyzer.ReadRegions(path, 1, candidate.Regions, candidate.Turn, options, default);
        Assert.Equal(candidate.Designation, manual.Designation);
        Assert.Equal(candidate.Name, manual.Name, ignoreCase: true);
    }

    [Fact]
    public async Task ScannerWaitsForTheWriterAndRequiresOperatorConfirmation()
    {
        var inbox = Directory.CreateDirectory(Path.Combine(folder, "scanner")).FullName;
        var path = Path.Combine(inbox, "incoming.pdf");
        var bytes = File.ReadAllBytes(Pdf(Sheet(595, 842, 0)));
        var model = new MainViewModel(Path.Combine(folder, "settings.json"));
        model.SetScannerFolder(inbox);
        using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
        {
            writer.Write(bytes[..32]);
            writer.Flush();
            await model.CheckScannerFolderAsync(() => true);
            Assert.Empty(model.Rows);

            model.ScannerWatchEnabled = true;
            model.ScannerCreatedAfter = DateTimeOffset.MinValue;
            await model.CheckScannerFolderAsync(() => true);
            await model.CheckScannerFolderAsync(() => true);
            Assert.Empty(model.Rows);
            writer.Write(bytes[32..]);
        }

        await model.CheckScannerFolderAsync(() => true);
        Assert.Empty(model.Rows);
        await model.CheckScannerFolderAsync(() => true);
        var row = Assert.Single(model.Rows);
        Assert.Equal("ABCD.123456.001", row.Designation);
        Assert.Equal("MAIN HOUSING", row.DocumentName);
        Assert.Equal("Проверить", row.Status);
        Assert.False(row.Confirmed);
        await model.ConfirmAsync(row);
        Assert.True(row.Confirmed);
    }

    [Fact]
    public async Task ScannerProcessesArrivalsAfterReenablingAndRespectsRemoval()
    {
        var model = new MainViewModel(Path.Combine(folder, "settings.json"));
        model.SetScannerFolder(folder);
        var path = Path.Combine(folder, "new.pdf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "scan-form1.pdf"), path);
        await model.CheckScannerFolderAsync(() => true);
        await model.CheckScannerFolderAsync(() => true);
        Assert.Empty(model.Rows);

        model.ScannerWatchEnabled = true;
        model.ScannerCreatedAfter = DateTimeOffset.MinValue;
        await model.CheckScannerFolderAsync(() => true);
        await model.CheckScannerFolderAsync(() => false);
        Assert.Empty(model.Rows);
        await model.CheckScannerFolderAsync(() => true);
        var row = Assert.Single(model.Rows);
        Assert.Equal("АБВГ.123456.001", row.Designation);
        Assert.Equal("Проверить", row.Status);
        await model.CheckScannerFolderAsync(() => true);
        Assert.Same(row, Assert.Single(model.Rows));

        model.RemoveDocuments([row]);
        await model.CheckScannerFolderAsync(() => true);
        await model.CheckScannerFolderAsync(() => true);
        Assert.Empty(model.Rows);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ScannerRecognizesASecondFileWithTheSameNameAfterRenaming()
    {
        var inbox = Directory.CreateDirectory(Path.Combine(folder, "scanner")).FullName;
        var path = Path.Combine(inbox, "incoming.pdf");
        File.Copy(Pdf(Sheet(595, 842, 0)), path);
        var lastWrite = File.GetLastWriteTimeUtc(path);
        var model = new MainViewModel { ScannerWatchEnabled = true, ScannerCreatedAfter = DateTimeOffset.MinValue,
            Renamer = new(Path.Combine(folder, "history.json")) };
        model.SetScannerFolder(inbox);
        await model.CheckScannerFolderAsync(() => true);
        await model.CheckScannerFolderAsync(() => true);
        var first = Assert.Single(model.Rows);
        Assert.Equal("ABCD.123456.001", first.Designation);
        await model.ConfirmAsync(first);
        await model.MoveAsync([first.Request]);
        Assert.Equal("Переименован", first.Status);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(first.Source));

        File.Copy(Pdf(Sheet(595, 842, 0, "ABCD.123456.002")), path);
        File.SetLastWriteTimeUtc(path, lastWrite);
        await model.CheckScannerFolderAsync(() => true);
        Assert.Single(model.Rows);
        await model.CheckScannerFolderAsync(() => true);
        var second = Assert.Single(model.Rows, row => row.Source == path);
        Assert.Equal("ABCD.123456.002", second.Designation);
        Assert.Equal("Проверить", second.Status);
        Assert.Equal("Переименован", first.Status);
    }

    [Fact]
    public async Task ScannerRetriesQueuedPdfAfterMissingTemplateIsReset()
    {
        var inbox = Directory.CreateDirectory(Path.Combine(folder, "scanner")).FullName;
        File.Copy(Pdf(Sheet(595, 842, 0)), Path.Combine(inbox, "incoming.pdf"));
        var model = new MainViewModel { ScannerWatchEnabled = true, ScannerCreatedAfter = DateTimeOffset.MinValue };
        model.SetScannerFolder(inbox);
        var templates = Path.Combine(folder, "templates.json");
        TitleTemplate.Defaults[0].Save(templates, newFile: true);
        model.SetTemplates(templates);
        File.Delete(templates);

        await model.CheckScannerFolderAsync(() => true);
        await model.CheckScannerFolderAsync(() => true);
        var row = Assert.Single(model.Rows);
        Assert.Equal("В очереди", row.Status);
        Assert.Null(row.Result);
        Assert.Contains("не удалось загрузить выбранный JSON", model.Message, StringComparison.OrdinalIgnoreCase);

        model.SetTemplates(null);
        await model.CheckScannerFolderAsync(() => true);
        await model.CheckScannerFolderAsync(() => true);
        Assert.Same(row, Assert.Single(model.Rows));
        Assert.Equal("ABCD.123456.001", row.Designation);
        Assert.Equal("Проверить", row.Status);
    }

    [Fact]
    public async Task ScannerRetriesOnlyUnfinishedFilesAfterCancellation()
    {
        var inbox = Directory.CreateDirectory(Path.Combine(folder, "scanner")).FullName;
        var firstPath = Path.Combine(inbox, "a.pdf");
        var secondPath = Path.Combine(inbox, "b.pdf");
        File.Copy(Pdf(Sheet(595, 842, 0)), firstPath);
        File.Copy(Pdf(Sheet(595, 842, 0, "ABCD.123456.002")), secondPath);
        var model = new MainViewModel { ScannerWatchEnabled = true, ScannerCreatedAfter = DateTimeOffset.MinValue };
        model.SetScannerFolder(inbox);
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(model.Progress) && model.Progress == 50)
                model.CancelCommand.Execute(null);
        };

        await model.CheckScannerFolderAsync(() => true);
        await model.CheckScannerFolderAsync(() => true);
        var first = Assert.Single(model.Rows, row => row.Source == firstPath);
        var completed = Assert.IsType<RecognitionResult>(first.Result);
        var second = Assert.Single(model.Rows, row => row.Source == secondPath);
        Assert.Equal("В очереди", second.Status);
        Assert.Null(second.Result);
        Assert.Contains("Анализ отменён", model.Message);

        await model.CheckScannerFolderAsync(() => true);
        await model.CheckScannerFolderAsync(() => true);
        Assert.Same(completed, first.Result);
        Assert.Equal("ABCD.123456.002", second.Designation);
        Assert.Equal("Проверить", second.Status);
    }

    [Fact]
    public async Task ScannerSplitsBeforeManualRecognitionAndMergesInStampOrder()
    {
        var inbox = Directory.CreateDirectory(Path.Combine(folder, "scanner")).FullName;
        var source = Path.Combine(inbox, "Пачка.pdf");
        var continuation = ContinuationSheet(595, 842, 0, left: false);
        continuation = continuation with { Content = continuation.Content.Replace("Group A", "ABCD.123456.001") };
        var firstSheet = NumberedSheet("ABCD.123456.001", 1);
        File.Copy(Pdf(continuation, firstSheet, NumberedSheet("ABCD.123456.002", 1)), source);
        var original = RenameService.Hash(source);
        var model = new MainViewModel(Path.Combine(folder, "settings.json"))
        {
            OcrEnabled = false, SplitScanBatches = true, ScannerWatchEnabled = true,
            ScannerCreatedAfter = DateTimeOffset.MinValue
        };
        model.SetScannerFolder(inbox);

        await model.CheckScannerFolderAsync(() => true);
        await model.CheckScannerFolderAsync(() => true);
        Assert.Equal(new[] { "Пачка_1.pdf", "Пачка_2.pdf", "Пачка_3.pdf" }, model.Rows.Select(row => row.OriginalName));
        Assert.All(model.Rows, row => { Assert.Null(row.Result); Assert.True(File.Exists(row.Source)); });
        Assert.Empty(model.MergeProposals);
        await model.CheckScannerFolderAsync(() => true);
        await model.CheckScannerFolderAsync(() => true);
        Assert.Equal(3, model.Rows.Count);

        await model.AnalyzeCommand.ExecuteAsync(null);
        var proposal = Assert.Single(model.MergeProposals);
        Assert.Equal(new[] { "Пачка_2.pdf", "Пачка_1.pdf" }, proposal.Ordered.Select(row => row.OriginalName));
        Assert.True(proposal.CanMerge);
        Assert.False(model.Rows[2].InMergeProposal);
        var second = proposal.Ordered[1];
        second.SheetNumberText = "1";
        Assert.False(proposal.CanMerge);
        var result = Path.Combine(inbox, "ABCD.123456.001_MAIN HOUSING.pdf");
        await model.MergeAsync(proposal);
        Assert.False(File.Exists(result));
        second.SheetNumberText = "2";
        await model.MergeAsync(proposal);

        using var merged = UglyToad.PdfPig.PdfDocument.Open(result);
        Assert.Equal(2, merged.NumberOfPages);
        Assert.Contains("HOUSING", merged.GetPage(1).Text);
        Assert.DoesNotContain("HOUSING", merged.GetPage(2).Text);
        Assert.Equal(original, RenameService.Hash(source));
        Assert.All(proposal.Pages, row => Assert.True(File.Exists(row.Source)));
        Assert.Empty(model.MergeProposals);

        var restarted = new MainViewModel(Path.Combine(folder, "settings.json"))
        {
            OcrEnabled = false, SplitScanBatches = true, ScannerWatchEnabled = true,
            ScannerCreatedAfter = DateTimeOffset.MinValue
        };
        restarted.SetScannerFolder(inbox);
        await restarted.CheckScannerFolderAsync(() => true);
        await restarted.CheckScannerFolderAsync(() => true);
        Assert.Equal(new[] { "Пачка_1.pdf", "Пачка_2.pdf", "Пачка_3.pdf" }, restarted.Rows.Select(row => row.OriginalName));
        Assert.Empty(restarted.MergeProposals);
        Assert.Equal(5, Directory.GetFiles(inbox, "*.pdf").Length);
    }

    [Fact]
    public async Task ScannerIdentifiesSheetNumbersOnRasterScans()
    {
        var batch = Path.Combine(folder, "raster.pdf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "scan-numbered-batch.pdf"), batch);
        var model = new MainViewModel(Path.Combine(folder, "settings.json")) { SplitScanBatches = true };
        await model.AddDocumentsAsync([batch]);
        Assert.Empty(model.MergeProposals);
        await model.AnalyzeCommand.ExecuteAsync(null);
        var proposal = Assert.Single(model.MergeProposals);
        Assert.Equal(new int?[] { 2, 1 }, proposal.Pages.Select(row => row.SheetNumber));
        Assert.True(proposal.CanMerge);
        Assert.Equal(new[] { "raster_2.pdf", "raster_1.pdf" }, proposal.Ordered.Select(row => row.OriginalName));
    }

    [Fact]
    public async Task AttachAndDetachKeepOriginalRecognitionAndMergeInTheTargetFolder()
    {
        var model = new MainViewModel(Path.Combine(folder, "settings.json")) { OcrEnabled = false, SplitScanBatches = true };
        var first = Pdf(NumberedSheet("ABCD.123456.001", 1));
        var second = Pdf(NumberedSheet("ABCD.123456.001", 2));
        var otherFolder = Directory.CreateDirectory(Path.Combine(folder, "different-folder"));
        var third = Path.Combine(otherFolder.FullName, "лист.pdf");
        File.Copy(Pdf(NumberedSheet("ABCD.123456.999", 3)), third);
        await model.AddDocumentsAsync([first, second, third]);
        await model.AnalyzeCommand.ExecuteAsync(null);
        var page = Assert.Single(model.Rows, row => row.Source == third);
        Assert.False(page.InMergeProposal);
        var original = Assert.Single(model.MergeProposals);
        await model.AttachMergePagesAsync(original, [page]);
        Assert.True(page.ManuallyAssigned);
        Assert.Equal("ABCD.123456.999", page.Designation);
        Assert.Equal(new[] { first, second, third }, Assert.Single(model.MergeProposals).Ordered.Select(row => row.Source));

        model.DetachMergePage(page);
        Assert.False(page.InMergeProposal);
        Assert.False(page.ManuallyAssigned);
        await model.AttachMergePagesAsync(Assert.Single(model.MergeProposals), [page]);
        await model.MergeAsync(Assert.Single(model.MergeProposals));

        var output = Path.Combine(folder, "ABCD.123456.001_MAIN HOUSING.pdf");
        using var result = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.Equal(3, result.NumberOfPages);
        Assert.True(File.Exists(third));
        Assert.Empty(model.MergeProposals);
    }

    [Fact]
    public async Task AGroupDropRejectsAllItemsIfOneIsNotASinglePage()
    {
        var model = new MainViewModel(Path.Combine(folder, "settings.json")) { OcrEnabled = false, SplitScanBatches = true };
        Page First(int number) => NumberedSheet("ABCD.123456.001", number);
        await model.AddDocumentsAsync([Pdf(First(1)), Pdf(First(2))]);
        await model.AnalyzeCommand.ExecuteAsync(null);
        var proposal = Assert.Single(model.MergeProposals);
        var single = new DocumentRow(Pdf(First(3)));
        var multiple = new DocumentRow(Pdf(First(3), First(4)));
        model.Rows.Add(single);
        model.Rows.Add(multiple);

        await model.AttachMergePagesAsync(proposal, [single, multiple]);

        Assert.Same(proposal, Assert.Single(model.MergeProposals));
        Assert.False(single.ManuallyAssigned);
        Assert.False(multiple.ManuallyAssigned);
        Assert.Contains("несколько страниц", model.Message);
    }

    [Fact]
    public void RasterNameCropKeepsAThinConnectedDescender()
    {
        const int width = 1650, height = 2340;
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, byte.MaxValue);
        Ink(158, 1868, 1612, 1872);
        Ink(158, 2297, 1612, 2301);
        Ink(158, 1868, 162, 2301);
        Ink(1608, 1868, 1612, 2301);
        Ink(668, 1868, 672, 2301);
        Ink(668, 1986, 1612, 1990);
        Ink(1218, 1986, 1222, 2301);
        Ink(1218, 2143, 1612, 2147);
        Ink(668, 2182, 1222, 2186);
        Ink(780, 1900, 1100, 1940);
        Ink(738, 2010, 774, 2050);
        Ink(805, 2010, 835, 2050);
        Ink(805, 2049, 811, 2060);
        Ink(960, 2061, 966, 2072);

        var scan = new Raster(width, height, pixels);
        var stamp = Assert.Single(ScanTitleLocator.Find(scan, 595, 842, [TitleTemplate.Defaults[0]], _ => scan, default),
            stamp => stamp.Turn == 0);
        Assert.NotNull(stamp.NameWithDescenders);
        Assert.True(stamp.Regions.Name.Bottom * height < 2060);
        Assert.InRange(stamp.NameWithDescenders.Value.Bottom * height, 2060, 2071);

        void Ink(int left, int top, int right, int bottom)
        {
            for (var y = top; y < bottom; y++)
                for (var x = left; x < right; x++) pixels.AsSpan(4 * (y * width + x), 3).Clear();
        }
    }

    [Theory]
    [InlineData("Певеход", "Переход", true)]
    [InlineData("Ось поиводная", "Ось приводная", true)]
    [InlineData("Переход", "с Переход", false)]
    [InlineData("Переход Сборочный чертеж", "Переход Сборочный черте", false)]
    [InlineData("Опора", "Опова", false)]
    public void ExtraPixelsCanRestoreRWithoutAcceptingOtherChanges(string initial, string extended, bool expected) =>
        Assert.Equal(expected, DrawingAnalyzer.RestoresCyrillicR(initial, extended));

    [Fact]
    public async Task ScannerUsesCreationTimeAndUpdatesCutoffWhenReenabled()
    {
        var input = Pdf(Sheet(595, 842, 0));
        var inbox = Directory.CreateDirectory(Path.Combine(folder, "scanner")).FullName;
        var oldFile = Path.Combine(inbox, "old.pdf");
        var newFile = Path.Combine(inbox, "new.pdf");
        var model = new MainViewModel { OcrEnabled = false, SplitScanBatches = true };
        model.SetScannerFolder(inbox);
        var before = DateTimeOffset.Now;
        model.ScannerWatchEnabled = true;
        Assert.InRange(model.ScannerCreatedAfter!.Value, before, DateTimeOffset.Now);
        var old = DateTimeOffset.Now.AddDays(-2);
        File.Copy(input, oldFile);
        File.SetCreationTimeUtc(oldFile, old.UtcDateTime);
        File.Copy(input, newFile);
        File.SetLastWriteTimeUtc(newFile, old.UtcDateTime);
        File.SetCreationTimeUtc(newFile, model.ScannerCreatedAfter.Value.AddMinutes(1).UtcDateTime);

        await model.CheckScannerFolderAsync(() => true);
        await model.CheckScannerFolderAsync(() => true);
        Assert.Equal(newFile, Assert.Single(model.Rows).Source);
        model.ScannerCreatedAfter = old.AddMinutes(-1);
        await model.CheckScannerFolderAsync(() => true);
        await model.CheckScannerFolderAsync(() => true);
        Assert.Contains(model.Rows, row => row.Source == oldFile);

        model.ScannerWatchEnabled = false;
        model.ScannerWatchEnabled = true;
        Assert.True(model.ScannerCreatedAfter > old);
    }

    [Fact]
    public void SplittingWithANameCollisionPreservesEveryExistingFile()
    {
        var source = Pdf(NumberedSheet("ABCD.123456.001", 1), NumberedSheet("ABCD.123456.001", 2));
        var first = Path.ChangeExtension(source, null) + "_1.pdf";
        var collision = Path.ChangeExtension(source, null) + "_2.pdf";
        var original = RenameService.Hash(source);
        File.WriteAllText(collision, "existing file");

        Assert.Throws<IOException>(() => PdfRenderer.SplitPages(source));
        Assert.False(File.Exists(first));
        Assert.Equal("existing file", File.ReadAllText(collision));
        Assert.Equal(original, RenameService.Hash(source));
    }

    private static Page NumberedSheet(string designation, int number)
    {
        var sheet = Sheet(595, 842, 0, designation);
        return sheet with { Content = sheet.Content + $"\nBT /F1 8 Tf 455 62 Td ({number}) Tj ET\n" };
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
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void RecognizedOrientationIsSavedAndUndoRestoresTheOriginalPdf(int rotation)
    {
        var path = Pdf(Sheet(595, 842, rotation));
        var original = File.ReadAllBytes(path);
        var row = new DocumentRow(path);
        row.Apply(new DrawingAnalyzer().Analyze(path, new(OcrEnabled: false), default));
        Assert.True(row.Confirmed);
        var service = new RenameService(Path.Combine(folder, "auto-rotation.json"));
        var renamed = Assert.Single(service.Execute([row.Request]));
        Assert.Equal(MoveStatus.Moved, renamed.Status);
        using (var document = UglyToad.PdfPig.PdfDocument.Open(renamed.Target!))
            Assert.Equal(0, document.GetPage(1).Rotation.Value);
        Assert.Equal(MoveStatus.Undone, Assert.Single(service.Undo()).Status);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SavesSelectedPageRotationsAndUndoesTheOperation(bool chooseFolder, bool copy)
    {
        var source = Pdf(Sheet(595, 842, 90), Sheet(595, 842, 180), Sheet(595, 842, 270));
        var original = File.ReadAllBytes(source);
        var destination = chooseFolder ? Directory.CreateDirectory(Path.Combine(folder, "output")).FullName : null;
        var journal = Path.Combine(folder, "history.json");
        var request = new RenameRequest(source, "A", "COVER")
        {
            PageRotations = new Dictionary<int, int> { [1] = 3, [2] = 1 }
        };

        var service = new RenameService(journal);
        var result = Assert.Single(service.Execute([request], destination: destination,
            removeOriginals: !copy));
        Assert.True(result.Status == (copy ? MoveStatus.Copied : MoveStatus.Moved), result.Message);
        Assert.Equal(copy, File.Exists(source));
        if (copy) Assert.Equal(original, File.ReadAllBytes(source));
        using (var saved = UglyToad.PdfPig.PdfDocument.Open(result.Target!))
        {
            Assert.Equal(new[] { 0, 270, 270 }, saved.GetPages().Select(page => page.Rotation.Value));
            Assert.Contains("MAIN", saved.GetPage(1).Text);
        }

        var rotated = File.ReadAllBytes(result.Target!);
        File.AppendAllText(result.Target!, "\n% Edited after saving");
        Assert.Equal(MoveStatus.Skipped, Assert.Single(service.Undo()).Status);
        Assert.Equal(copy, File.Exists(source));
        File.WriteAllBytes(result.Target!, rotated);

        var undo = Assert.Single(service.Undo());
        Assert.True(undo.Status == (copy ? MoveStatus.CopyRemoved : MoveStatus.Undone), undo.Message);
        if (!copy) Assert.Equal(1, undo.PageRotations![1]);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(File.Exists(result.Target));
    }

    [Fact]
    public void InvalidPageRotationLeavesTheSourceInPlace()
    {
        var source = Pdf(Sheet(595, 842, 0));
        var original = File.ReadAllBytes(source);
        var request = new RenameRequest(source, "A", "COVER")
        {
            PageRotations = new Dictionary<int, int> { [1] = 1, [2] = 2 }
        };

        var result = Assert.Single(new RenameService(Path.Combine(folder, "history.json")).Execute([request]));
        Assert.Equal(MoveStatus.Failed, result.Status);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(File.Exists(result.Target));
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

    [Fact]
    public void CustomScanTemplateTakesPrecedenceOverBuiltInFields()
    {
        var custom = TitleTemplate.Defaults[0] with { Name = "Top line", Title = new(65d / 185, 15d / 55, 70d / 185, 12d / 55) };
        var result = new DrawingAnalyzer().Analyze(Path.Combine(AppContext.BaseDirectory, "scan-form1.pdf"),
            new(Languages: "rus", Templates: [.. TitleTemplate.Defaults, custom]), default);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Корпус", candidate.Name, ignoreCase: true);
        Assert.False(candidate.Verified);
    }

    [Fact]
    public void ReadsDesignationSeparatorsWithoutFrameStrokes()
    {
        var result = new DrawingAnalyzer().Analyze(Path.Combine(AppContext.BaseDirectory, "scan-nut.pdf"),
            new(Languages: "rus"), default);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("КК ВМ 01.02.03.01.002", candidate.Designation);
        Assert.Equal("Гайка", candidate.Name);
    }

    [Theory]
    [InlineData("scan-assembly.pdf", "Соединитель", 0)]
    [InlineData("scan-specification.pdf", "Соединитель", 0)]
    [InlineData("scan-sideways.pdf", "компрессора", 3)]
    public void ReadsTheBottomStampAndItsOrientation(string file, string name, int turn)
    {
        var result = new DrawingAnalyzer().Analyze(Path.Combine(AppContext.BaseDirectory, file),
            new(Languages: "rus"), default);
        Assert.Equal(RecognitionStatus.Review, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Contains(name, candidate.Name);
        Assert.Equal(turn, candidate.Turn);
        Assert.NotNull(RenameService.FileName(candidate.Designation, candidate.Name));
    }

    [Fact]
    public void ReadsSingleStoreyALettersInAnInclinedScanTitle()
    {
        var result = new DrawingAnalyzer().Analyze(Path.Combine(AppContext.BaseDirectory, "scan-gasket.pdf"),
            new(Languages: "rus"), default);

        Assert.Equal(RecognitionStatus.Review, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Contains("Прокладка", candidate.Name);
        Assert.Equal("КК ВМ 01.02.03.01.001", candidate.Designation);
        Assert.NotNull(RenameService.FileName(candidate.Designation, candidate.Name));
        Assert.Equal("OCR", candidate.Method);
        Assert.False(candidate.Verified);
    }

    [Theory]
    [InlineData("rus")]
    [InlineData("rus+eng")]
    public void FindsAndReadsAnInclinedConnectorScanAutomatically(string languages)
    {
        var result = new DrawingAnalyzer().Analyze(Path.Combine(AppContext.BaseDirectory, "scan-connector.pdf"),
            new(Languages: languages), default);

        Assert.Equal(RecognitionStatus.Review, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Соединитель", candidate.Name);
        Assert.Equal(0, candidate.Turn);
        Assert.True(candidate.Regions.Designation.IsValid && candidate.Regions.Name.IsValid);
        Assert.False(candidate.Verified);
    }

    [Theory]
    [InlineData("rus")]
    [InlineData("rus+eng")]
    public void ReadsManuallySelectedFieldsInAnInclinedConnectorScan(string languages)
    {
        var regions = new FieldRegions(new(.391, .840, .513, .049), new(.405, .895, .305, .073));
        var candidate = new DrawingAnalyzer().ReadRegions(Path.Combine(AppContext.BaseDirectory, "scan-connector.pdf"),
            1, regions, 0, new(Languages: languages), default);

        Assert.Equal("Соединитель", candidate.Name);
        Assert.Equal("OCR", candidate.Method);
        Assert.False(candidate.Verified);
    }

    [Fact]
    public void TiltedSidewaysScanWithClippedBorderOffersAUsableName()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scan-skewed.pdf");
        var analyzer = new DrawingAnalyzer();
        Assert.Empty(analyzer.Analyze(path, new(OcrEnabled: false), default).Candidates);
        var result = analyzer.Analyze(path, new(Languages: "rus"), default);

        Assert.Equal(RecognitionStatus.Review, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.False(candidate.Verified);
        Assert.Equal("АБВГ.123456‒02", candidate.Designation);
        Assert.Equal("Корпус основной", candidate.Name, ignoreCase: true);
        Assert.NotNull(RenameService.FileName(candidate.Designation, candidate.Name));
        Assert.True(candidate.Regions.Designation.IsValid && candidate.Regions.Name.IsValid);
    }

    [Fact]
    public void ReadsAnInclinedDrawingFontInSelectedScanRegions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scan-italic.pdf");
        var regions = new FieldRegions(new(.12, .08, .80, .25), new(.035, .345, .91, .625));
        var candidate = new DrawingAnalyzer().ReadRegions(path, 1, regions, 0, new(), default);

        Assert.Equal("АБВГ.123456.001", candidate.Designation);
        Assert.Equal("Монтаж оборудования системы отопления", candidate.Name);
        Assert.Equal("OCR", candidate.Method);
        Assert.False(candidate.Verified);
    }

    [Fact]
    public void ReadsDesignationInAFadedInclinedScan()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scan-italic-faded.pdf");
        var regions = new FieldRegions(new(.12, .08, .80, .25), new(.035, .345, .91, .625));
        var candidate = new DrawingAnalyzer().ReadRegions(path, 1, regions, 0, new(), default);

        Assert.Equal("АБВГ.123456.001", candidate.Designation);
        Assert.False(candidate.Verified);
    }

    [Theory]
    [InlineData("rus+eng", "С1")]
    [InlineData("eng", "C1")]
    public void PrefersCyrillicForAmbiguousOcrGlyphsWhenRussianIsEnabled(string languages, string expected)
    {
        var path = Pdf(new Page(240, 122, 0, """
            3 w 1 J 1 j
            110 74 m 106 78 103 79 98 79 c 88 79 83 71 83 62 c
            83 53 88 45 98 45 c 103 45 106 46 110 50 c S
            122 71 m 131 79 l 131 45 l S
            """));
        var candidate = new DrawingAnalyzer().ReadRegions(path, 1, new(Area.Full, Area.Full), 0,
            new(Languages: languages), default);

        Assert.Equal(expected, candidate.Designation);
        Assert.Equal(expected, candidate.Name);
        Assert.Equal("OCR", candidate.Method);
    }

    [Theory]
    [InlineData("rus", "С1")]
    [InlineData("rus+eng", "С1")]
    [InlineData("eng", "C1")]
    public void CorrectsAnOcrOpeningParenthesisWhenRussianIsEnabled(string languages, string expected)
    {
        var path = Pdf(new Page(240, 122, 0, """
            3 w 1 J 1 j
            105 80 m 90 71 90 53 105 44 c S
            122 71 m 131 79 l 131 45 l S
            """));
        var candidate = new DrawingAnalyzer().ReadRegions(path, 1, new(Area.Full, Area.Full), 0,
            new(Languages: languages), default);

        Assert.Equal("OCR", candidate.Method);
        Assert.Equal(expected, candidate.Designation);
        Assert.Equal(expected, candidate.Name);
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
    [InlineData("AB.COVER", "HOUSING")]
    [InlineData("A", "A")]
    [InlineData("123", "7")]
    [InlineData("AB.001 SB", "MAIN HOUSING")]
    [InlineData("ABCD.123456.001", "Housing")]
    public void ValidDrawingFieldsAreReadyWhenTitleBlockIsLocated(string designation, string name)
    {
        var sheet = Sheet(595, 842, 0, designation);
        var path = Pdf(sheet with { Content = sheet.Content.Replace("(MAIN) Tj 0 -14 Td (HOUSING) Tj", $"({name}) Tj") });
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
    [InlineData("TEST\"\"\"DESIGNATION", "HOUSING")]
    [InlineData("NUL.PARTS", "HOUSING")]
    [InlineData("ABCD.123456.001", "HOUSING?")]
    [InlineData("#_+ [A]", "HOUSING")]
    [InlineData("AB(1)", "HOUSING")]
    [InlineData("AbCD.123456.001", "Housing")]
    public void InvalidDrawingFieldsStayVisibleForCorrection(string designation, string name)
    {
        var sheet = Sheet(595, 842, 0, designation);
        var path = Pdf(sheet with { Content = sheet.Content.Replace("(MAIN) Tj 0 -14 Td (HOUSING) Tj", $"({name}) Tj") });
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
            BT /F1 12 Tf 250 38 Td (PARTS LIST) Tj ET
            """));
        var result = new DrawingAnalyzer().Analyze(path, new(false, Templates: [TitleTemplate.Defaults[1]]), default);
        Assert.Equal(RecognitionStatus.Ready, result.Status);
        Assert.Equal("PARTS LIST", Assert.Single(result.Candidates).Name);
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
            BT /F1 12 Tf 250 38 Td (PARTS LIST) Tj ET
            """ : "")));

        var result = new DrawingAnalyzer().Analyze(path, new(OcrEnabled: false), default);
        if (filled)
        {
            Assert.Equal(RecognitionStatus.Ready, result.Status);
            var candidate = Assert.Single(result.Candidates);
            Assert.Equal("ABCD.123456.001", candidate.Designation);
            Assert.Equal("PARTS LIST", candidate.Name);
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
        Assert.Equal("MAIN HOUSING", candidate.Name);
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
        Assert.Equal("MAIN HOUSING", candidate.Name);
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
            """ + "\n" + (filled ? "BT /F1 12 Tf 222 18 Td (FLOOR PLAN) Tj ET\nQ" : "Q")));

        var result = new DrawingAnalyzer().Analyze(path, new(), default);

        Assert.Equal(filled ? RecognitionStatus.Ready : RecognitionStatus.Review, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("ABCD.123456.001", candidate.Designation);
        Assert.Equal(filled ? "FLOOR PLAN" : "", candidate.Name);
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
        Assert.Equal("MAIN HOUSING", Assert.Single(result.Candidates).Name);
    }

    [Fact]
    public void SavesManuallySelectedStampAndFindsItOnAnotherSheet()
    {
        var sheet = Sheet(842, 595, 0);
        var content = sheet.Content.Replace("382 57 m 525 57 l S", "382 67 m 525 67 l S");
        var sample = Pdf(sheet with { Content = content });
        var frame = new Area(302d / 842, 425d / 595, 525d / 842, 156d / 595);
        var regions = new FieldRegions(new Area(.36, .01, .57, .25).Inside(frame),
            new Area(.36, .28, .35, .43).Inside(frame), frame);
        var analyzer = new DrawingAnalyzer();
        var path = Path.Combine(folder, "saved.json");
        var created = analyzer.CreateTemplate(sample, 1, regions, 0, "Workshop", new(false), default);
        Assert.NotNull(created);
        created.Save(path);

        var template = Assert.Single(TitleTemplate.Load(path));
        Assert.Equal("Workshop", template.Name);
        var larger = Pdf(new Page(1191, 842, 90,
            "q 1 0 0 1 349 0 cm\n" + content.Replace("ABCD.123456.001", "ABCD.123456.002") + "\nQ"));
        var result = analyzer.Analyze(larger, new(false, Templates: [.. TitleTemplate.Defaults, template]), default);
        Assert.Equal(RecognitionStatus.Ready, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("ABCD.123456.002", candidate.Designation);
        Assert.Equal("MAIN HOUSING", candidate.Name);
    }

    [Fact]
    public void SavingIntoExistingJsonAddsTemplatesAndUpdatesOnlyTheMatchingName()
    {
        var path = Path.Combine(folder, "templates.json");
        var first = TitleTemplate.Defaults[0] with { Name = "Workshop" };
        var second = TitleTemplate.Defaults[1] with { Name = "Specification" };
        first.Save(path);
        second.Save(path);
        (first with { WidthMm = 190 }).Save(path);

        Assert.Collection(TitleTemplate.Load(path),
            template =>
            {
                Assert.Equal("Workshop", template.Name);
                Assert.Equal(190, template.WidthMm);
            },
            template => Assert.Equal("Specification", template.Name));
    }

    [Fact]
    public void SavingRejectsInvalidExistingJsonWithoutChangingIt()
    {
        var path = Path.Combine(folder, "invalid.json");
        const string original = "[{";
        File.WriteAllText(path, original);

        Assert.Throws<System.Text.Json.JsonException>(() => TitleTemplate.Defaults[0].Save(path));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void SavesPageRegionsWithoutAStampOrTextInBothFields()
    {
        const string designation = "BT /F1 14 Tf 60 750 Td (Hello world page 1) Tj ET";
        const string title = "\nBT /F1 14 Tf 410 55 Td (Example title) Tj ET";
        var sheet = new Page(595, 842, 0, designation);
        var analyzer = new DrawingAnalyzer();
        var options = new RecognitionOptions(OcrEnabled: false);
        var regions = new FieldRegions(new(.08, .08, .24, .05), new(.67, .90, .26, .05));
        Assert.Empty(analyzer.ReadRegions(Pdf(sheet), 1, regions, 0, options, default).Name);

        var template = analyzer.CreateTemplate(Pdf(sheet), 1, regions, 0, "Sheet layout", options, default);
        Assert.NotNull(template);
        var json = Path.Combine(folder, "page-layout.json");
        template.Save(json);
        options = options with { Templates = [.. TitleTemplate.Defaults, .. TitleTemplate.Load(json)] };

        var result = analyzer.Analyze(Pdf(sheet with { Content = designation + title }), options, default);
        Assert.Equal(RecognitionStatus.Review, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Hello world page 1", candidate.Designation);
        Assert.Equal("Example title", candidate.Name);
        Assert.Empty(analyzer.Analyze(Pdf(sheet with { Width = 650, Content = designation + title }),
            options, default).Candidates);
    }

    [Fact]
    public void FindsKnownStampForTemplateExportUsingOnlyTheTwoFields()
    {
        var path = Pdf(Sheet(595, 842, 0));
        var analyzer = new DrawingAnalyzer();
        var options = new RecognitionOptions(OcrEnabled: false);
        var candidate = Assert.Single(analyzer.Analyze(path, options, default).Candidates);
        var interior = new Area(.05, .05, .9, .9);
        var regions = new FieldRegions(interior.Inside(candidate.Regions.Designation),
            new Area(.05, .02, .9, .31).Inside(candidate.Regions.Name));
        var template = analyzer.CreateTemplate(path, 1, regions, 0, "Workshop", options, default);

        Assert.NotNull(template);
        Assert.InRange(template.WidthMm, 184, 186);
        Assert.InRange(template.HeightMm, 54, 56);
        var result = analyzer.Analyze(path, options with { Templates = [.. TitleTemplate.Defaults, template] }, default);
        Assert.Equal("MAIN", Assert.Single(result.Candidates).Name);
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
    [InlineData("AB.O1‒02", true, "AB.01‒02")]
    [InlineData("AB.O1‒02", false, "AB.O1‒02")]
    public void DesignationNormalizationKeepsSourceCharactersUnlessOcr(string source, bool ocr, string expected) =>
        Assert.Equal(expected, DrawingAnalyzer.NormalizeDesignation(source, ocr));

    [Theory]
    [InlineData("Соединитель Сдорочный чертеж", "Соединитель Сборочный чертеж")]
    [InlineData("сдорочная единица", "сборочная единица")]
    [InlineData("СДОРОЧНЫЙ ЧЕРТЁЖ", "СБОРОЧНЫЙ ЧЕРТЁЖ")]
    [InlineData("Сварочный узел", "Сварочный узел")]
    [InlineData("Установка Схема комбинировснная принципиальная", "Установка Схема комбинированная принципиальная")]
    [InlineData("СХЕМА КОМБИНИРОВСННАЯ ПРИНЦИПИАЛЬНАЯ", "СХЕМА КОМБИНИРОВАННАЯ ПРИНЦИПИАЛЬНАЯ")]
    [InlineData("Рама№1", "Рама № 1")]
    [InlineData("Рама № 2", "Рама № 2")]
    [InlineData("Рама №°3", "Рама № 3")]
    [InlineData("№1", "№ 1")]
    public void OcrNameCorrectsRecognizedTextWithoutChangingPdfText(string source, string expected)
    {
        Assert.Equal(expected, DrawingAnalyzer.NormalizeOcrName(source));
        Assert.Equal(source, DrawingAnalyzer.NormalizeName(source));
    }

    [Theory]
    [InlineData("СВ", "Соединитель Сборочный чертеж", true, "СБ")]
    [InlineData("СЕ", "Установка компрессора Сборочный чертёж", true, "СБ")]
    [InlineData("CE", "СБОРОЧНЫЙ ЧЕРТЁЖ.", true, "СБ")]
    [InlineData("СВ", "Соединитель", true, "СВ")]
    [InlineData("СЕ", "Для сборочного чертежа", true, "СЕ")]
    [InlineData("СЕ", "Сборочный чертёж", false, "СЕ")]
    [InlineData("СБ", "Сборочный чертёж", true, "СБ")]
    public void AssemblySuffixCorrectionRequiresOcrDesignationAndAssemblyTitle(
        string suffix, string title, bool designationFromOcr, string expected)
    {
        var candidate = new Candidate($"АБВГ.123456.001 {suffix}", title, 1, 0, new(Area.Full, Area.Full), "OCR")
            { DesignationFromOcr = designationFromOcr };
        Assert.Equal($"АБВГ.123456.001 {expected}", DrawingAnalyzer.CorrectAssemblySuffix(candidate).Designation);
    }

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
            BT /F1 13 Tf 200 96 Td (MAIN) Tj 0 -14 Td (HOUSING) Tj ET
            Q
            """));
    }

    private string Pdf(params Page[] pages) => WritePdf(false, pages);

    private string WritePdf(bool metadataAsStream, params Page[] pages)
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
        if (metadataAsStream) objects.Add("<< /Length 3 >>\nstream\nabc\nendstream");
        var info = metadataAsStream ? $" /Info {objects.Count} 0 R" : "";
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
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{info} >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(folder, Guid.NewGuid() + ".pdf");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
        return path;
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
