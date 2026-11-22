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
