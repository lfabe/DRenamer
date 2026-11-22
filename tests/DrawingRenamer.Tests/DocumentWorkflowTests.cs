namespace DrawingRenamer.Tests;

public sealed class DocumentWorkflowTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("drawing-workflow-").FullName;

    private DocumentRow ConfirmedRow(string file)
    {
        var path = Path.Combine(folder, file);
        File.WriteAllText(path, "%PDF-invalid");
        var row = new DocumentRow(path) { Designation = "A", DocumentName = "PART" };
        Assert.True(row.Confirm(RenameService.Hash(path)));
        return row;
    }

    [Fact]
    public void ChangingSeparatorUpdatesNamesAndRequestsWithoutLosingValidConfirmation()
    {
        var model = new MainViewModel();
        var row = ConfirmedRow("separator.pdf");
        model.Rows.Add(row);
        var originalRequest = row.Request;
        model.SeparatorInput = " - ";
        Assert.Null(model.SeparatorError);
        Assert.Equal("A - PART.pdf", row.NewName);
        Assert.True(row.Confirmed);
        Assert.Equal(" - ", row.Request.Separator);
        Assert.Equal("_", originalRequest.Separator);
        Assert.Equal("A - PART.pdf", Path.GetFileName(Assert.Single(RenameService.Preview([row.Request])).Target));
    }

    [Fact]
    public async Task NewDocumentsUseTheCurrentSeparatorIncludingSpaceOnlyValues()
    {
        var model = new MainViewModel { SeparatorInput = "   " };
        var file = Path.Combine(folder, "later.pdf");
        File.WriteAllText(file, "%PDF-original");
        await model.AddDocumentsAsync([file]);
        var row = Assert.Single(model.Rows);
        row.Designation = "A";
        row.DocumentName = "PART";
        Assert.Equal("A   PART.pdf", row.NewName);
        Assert.True(row.Confirm(RenameService.Hash(file)));
        Assert.Equal("   ", row.Request.Separator);
    }

    [Fact]
    public async Task InvalidSeparatorDraftKeepsTheLastValidNamesAndBlocksSaving()
    {
        var model = new MainViewModel();
        var row = ConfirmedRow("invalid-separator.pdf");
        model.Rows.Add(row);
        model.SeparatorInput = " - ";
        model.SeparatorInput = "A";
        Assert.NotNull(model.SeparatorError);
        Assert.Equal("A - PART.pdf", row.NewName);
        Assert.True(row.Confirmed);
        await model.MoveAsync([row.Request]);
        Assert.True(File.Exists(row.Source));
        Assert.Equal(model.SeparatorError, model.Message);
        model.SeparatorInput = "#";
        Assert.Null(model.SeparatorError);
        Assert.Equal("A#PART.pdf", row.NewName);
    }

    [Fact]
    public void SeparatorThatProducesAReservedWindowsNameRequiresCorrection()
    {
        var row = ConfirmedRow("reserved-separator.pdf");
        row.Designation = "CON";
        Assert.True(row.Confirm(row.SourceHash!));
        row.Separator = ".";
        Assert.False(row.Confirmed);
        Assert.Empty(row.NewName);
        Assert.Contains("зарезервированного", row.Diagnostic);
        row.Separator = "_";
        Assert.Equal("CON_PART.pdf", row.NewName);
        Assert.True(row.Confirm(row.SourceHash!));
    }

    [Fact]
    public async Task BatchAnalysisPreservesConfirmedValuesForUnchangedFiles()
    {
        var model = new MainViewModel { OcrEnabled = false };
        var row = ConfirmedRow("confirmed.pdf");
        model.Rows.Add(row);
        await model.AnalyzeCommand.ExecuteAsync(null);
        Assert.True(row.Confirmed);
        Assert.Equal("PART", row.DocumentName);
        Assert.Null(row.Result);
    }

    [Fact]
    public async Task BatchAnalysisInvalidatesConfirmationWhenPdfChanged()
    {
        var model = new MainViewModel { OcrEnabled = false };
        var row = ConfirmedRow("changed.pdf");
        model.Rows.Add(row);
        File.AppendAllText(row.Source, "changed");
        await model.AnalyzeCommand.ExecuteAsync(null);
        Assert.False(row.Confirmed);
        Assert.Equal(RecognitionStatus.Error, row.Result?.Status);
    }

    [Fact]
    public async Task ExplicitAnalysisReprocessesOnlyTheRequestedDocumentEvenIfConfirmedAndExcluded()
    {
        var model = new MainViewModel { OcrEnabled = false };
        var target = ConfirmedRow("target.pdf");
        var other = ConfirmedRow("other.pdf");
        target.Included = false;
        model.Rows.Add(target);
        model.Rows.Add(other);
        await model.AnalyzeDocumentAsync(target);
        Assert.Equal(RecognitionStatus.Error, target.Result?.Status);
        Assert.False(target.Confirmed);
        Assert.True(other.Confirmed);
        Assert.Null(other.Result);
        Assert.Equal("PART", other.DocumentName);
    }

    [Fact]
    public async Task BatchConfirmationUsesSelectedRowsAndReportsFilesThatNeedReview()
    {
        var model = new MainViewModel();
        var missing = ConfirmedRow("missing.pdf");
        var changed = ConfirmedRow("changed.pdf");
        var invalid = ConfirmedRow("invalid.pdf");
        var valid = ConfirmedRow("valid.pdf");
        var unselected = ConfirmedRow("unselected.pdf");
        foreach (var row in new[] { missing, changed, invalid, valid, unselected }) model.Rows.Add(row);
        File.Delete(missing.Source);
        File.AppendAllText(changed.Source, "changed");
        invalid.DocumentName = "PART/01";
        valid.DocumentName = "PART 02";
        valid.Included = false;
        unselected.DocumentName = "PART 03";

        await model.ConfirmSelectedAsync([missing, changed, invalid, valid, valid]);

        Assert.True(valid.Confirmed);
        Assert.False(unselected.Confirmed);
        Assert.False(missing.Confirmed);
        Assert.Contains("версию исходного PDF", missing.Diagnostic);
        Assert.False(changed.Confirmed);
        Assert.Contains("PDF изменился", changed.Diagnostic);
        Assert.False(invalid.Confirmed);
        Assert.Contains("U+002F", invalid.Diagnostic);
        Assert.Equal("Подтверждено: 1 из 4. Требуют проверки: 3.", model.Message);
        Assert.Equal(100, model.Progress);
        Assert.False(model.Busy);
    }

    [Fact]
    public void SelectingRecognitionOrientsThePreviewAndRenameRequest()
    {
        var row = ConfirmedRow("rotated.pdf");
        row.Apply(new(RecognitionStatus.Review, 1,
            [new("A", "PART", 1, 3, new(new(.1, .2, .3, .1), new(.1, .4, .3, .1)), "OCR", .8)], "Review")
            { SourceHash = row.SourceHash });
        Assert.Equal(3, row.PageRotations[1]);
        Assert.Equal(3, row.Request.PageRotations![1]);
        Assert.False(row.Confirmed);
    }

    [Fact]
    public void ShowsProductNameInTheRowAndRecognitionChoices()
    {
        var row = ConfirmedRow("assembly.pdf");
        var candidate = new Candidate("A СБ", "Корпус Сборочный чертёж", 1, 0, new(Area.Full, Area.Full), "OCR");
        row.Apply(new(RecognitionStatus.Review, 1, [candidate], "Review") { SourceHash = row.SourceHash });

        Assert.Equal("Корпус", row.DocumentName);
        Assert.Equal("Лист 1 · A СБ - Корпус", DocumentRow.CandidateLabel(Assert.Single(row.Candidates)));
        Assert.Equal("A СБ_Корпус.pdf", row.NewName);
        Assert.True(row.Confirm(row.SourceHash!));
        Assert.Equal(row.NewName, Path.GetFileName(Assert.Single(RenameService.Preview([row.Request])).Target));
    }

    [Fact]
    public void TypeOnlyRecognitionCanBeConfirmedWithAnEmptyVisibleName()
    {
        var row = ConfirmedRow("specification.pdf");
        var typeOnly = new Candidate("A", "Спецификация", 1, 0, new(Area.Full, Area.Full), "OCR");
        var missing = typeOnly with { Name = "", Page = 2 };
        row.Apply(new(RecognitionStatus.Review, 2, [typeOnly, missing], "Review") { SourceHash = row.SourceHash });

        row.Use(typeOnly);
        Assert.Empty(row.DocumentName);
        Assert.Equal("Лист 1 · A", DocumentRow.CandidateLabel(typeOnly));
        Assert.True(row.HasDocumentName);
        Assert.Contains("используется обозначение", row.Diagnostic);
        Assert.True(row.Confirm(row.SourceHash!));
        Assert.Equal("A.pdf", row.NewName);
        Assert.Equal(row.NewName, Path.GetFileName(Assert.Single(RenameService.Preview([row.Request])).Target));

        row.Use(missing);
        Assert.Empty(row.DocumentName);
        Assert.False(row.HasDocumentName);
        Assert.Empty(row.NewName);
        Assert.False(row.Confirm(row.SourceHash!));
    }

    [Fact]
    public void ConfirmationExplainsForbiddenCharactersAndAllowsCorrection()
    {
        var row = ConfirmedRow("invalid-name.pdf");
        row.DocumentName = "PART/01";
        Assert.False(row.Confirm(row.SourceHash!));
        Assert.Contains("U+002F", row.Diagnostic);
        Assert.Contains("Наименование", row.Diagnostic);
        row.DocumentName = "PART_01";
        Assert.True(row.Confirm(row.SourceHash!));
        Assert.Equal("A_PART_01.pdf", row.NewName);
    }

    public void Dispose() => Directory.Delete(folder, true);
}
