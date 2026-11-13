namespace DrawingRenamer.Tests;

public sealed class RenameTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("drawing-renamer-").FullName;
    private string Journal => Path.Combine(root, "history.json");
    private string Source(string name, string contents = "%PDF-original")
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    [Theory]
    [InlineData("A/B", "Part")]
    [InlineData("CON", "")]
    [InlineData("NUL.txt", "Part")]
    [InlineData("COM¹.details", "Part")]
    [InlineData("A", "Part?")]
    [InlineData("A\tB", "Part")]
    public void RejectsInvalidWindowsNames(string designation, string title) =>
        Assert.Null(RenameService.FileName(designation, title));

    [Fact]
    public void ChecksLengthOfCompleteWindowsFileName()
    {
        var designation = new string('A', 247);
        Assert.Equal(255, RenameService.FileName(designation, "7")!.Length);
        Assert.Null(RenameService.FileName(designation, "77"));
    }

    [Fact]
    public void AllowsUnicodeOutsideTheWindowsForbiddenControlRange() =>
        Assert.Equal("A\u0080 - Part.pdf", RenameService.FileName("A\u0080", "Part"));

    [Fact]
    public void PreviewExplainsWhichFieldsContainForbiddenWindowsCharacters()
    {
        var source = Source("a.pdf");
        var result = Assert.Single(RenameService.Preview([new(source, "A\"\"\"B", "Part?")]));

        Assert.Equal(MoveStatus.Skipped, result.Status);
        Assert.Null(result.Target);
        Assert.Contains("«Обозначение»", result.Message);
        Assert.Contains("«\"»", result.Message);
        Assert.Contains("«Наименование»", result.Message);
        Assert.Contains("«?»", result.Message);
    }

    [Fact]
    public void SelectedFolderCopiesByDefaultAndUndoRemovesTheCopy()
    {
        var source = Source("a.pdf");
        var destination = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        var target = Path.Combine(destination, "A - Cover.pdf");
        var service = new RenameService(Journal);

        var result = Assert.Single(service.Execute([new(source, "A", "Cover")], destination: destination));
        Assert.Equal(MoveStatus.Copied, result.Status);
        Assert.Equal("%PDF-original", File.ReadAllText(source));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));

        Assert.Equal(MoveStatus.CopyRemoved, Assert.Single(new RenameService(Journal).Undo()).Status);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void CopyUndoKeepsTheCopyIfTheOriginalOrCopyHasChanged()
    {
        var source = Source("a.pdf");
        var destination = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        var service = new RenameService(Journal);
        var target = Assert.Single(service.Execute([new(source, "A", "Cover")], destination: destination)).Target!;
        File.WriteAllText(source, "changed");

        Assert.Equal(MoveStatus.Skipped, Assert.Single(service.Undo()).Status);
        Assert.Equal("%PDF-original", File.ReadAllText(target));
        File.WriteAllText(source, "%PDF-original");
        File.WriteAllText(target, "edited copy");
        Assert.Equal(MoveStatus.Skipped, Assert.Single(service.Undo()).Status);
        Assert.Equal("edited copy", File.ReadAllText(target));
        File.Delete(source);
        Assert.Equal(MoveStatus.Skipped, Assert.Single(service.Undo()).Status);
        Assert.Equal("edited copy", File.ReadAllText(target));
    }

    [Fact]
    public void SelectedFolderPreviewMoveAndUndoUseTheSamePaths()
    {
        var source = Source("a.pdf");
        var destination = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        var target = Path.Combine(destination, "A - Cover.pdf");
        var requests = new RenameRequest[] { new(source, "A", "Cover") };
        var service = new RenameService(Journal);

        Assert.Equal(target, Assert.Single(RenameService.Preview(requests, destination)).Target);
        Assert.Equal(MoveStatus.Moved, Assert.Single(service.Execute(requests, destination: destination, removeOriginals: true)).Status);
        Assert.False(File.Exists(source));
        Assert.Equal("%PDF-original", File.ReadAllText(target));

        var restored = Assert.Single(new RenameService(Journal).Undo());
        Assert.Equal(MoveStatus.Undone, restored.Status);
        Assert.Equal("%PDF-original", File.ReadAllText(source));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void SelectedFolderChecksConflictsAcrossSourceFolders()
    {
        var first = Source("a.pdf");
        var secondFolder = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
        var second = Source(Path.Combine(secondFolder, "b.pdf"));
        var destination = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        var service = new RenameService(Journal);
        var requests = new RenameRequest[] { new(first, "A", "Cover"), new(second, "a", "COVER") };

        var duplicates = service.Execute(requests, destination: destination);
        Assert.All(duplicates, result =>
        {
            Assert.Equal(MoveStatus.Skipped, result.Status);
            Assert.Contains("Повторяющееся", result.Message);
        });
        var existing = Source(Path.Combine(destination, "A - Cover.pdf"), "existing");
        Assert.Equal(MoveStatus.Skipped, Assert.Single(service.Execute([requests[0]], destination: destination)).Status);
        Assert.Equal("existing", File.ReadAllText(existing));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void RemovedDestinationDoesNotMoveOrRenameFiles()
    {
        var source = Source("a.pdf");
        var destination = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        var requests = new RenameRequest[] { new(source, "A", "Cover") };
        Assert.Equal(MoveStatus.Pending, Assert.Single(RenameService.Preview(requests, destination)).Status);
        Directory.Delete(destination);

        var result = Assert.Single(new RenameService(Journal).Execute(requests, destination: destination));
        Assert.Equal(MoveStatus.Skipped, result.Status);
        Assert.Contains("папка назначения недоступна", result.Message);
        Assert.Equal("%PDF-original", File.ReadAllText(source));
        Assert.False(File.Exists(Path.Combine(root, "A - Cover.pdf")));
    }

    [Fact]
    public void DuplicateAndExistingTargetsCannotOverwrite()
    {
        var first = Source("a.pdf");
        var second = Source("b.pdf");
        var target = Source("B - Корпус.pdf", "unrelated");
        var service = new RenameService(Journal);
        var results = service.Execute(
            [new(first, "A", "Корпус"), new(second, "a", "КОРПУС"), new(first, "B", "Корпус")]);
        Assert.All(results, result => Assert.Equal(MoveStatus.Skipped, result.Status));
        Assert.Equal("unrelated", File.ReadAllText(target));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void BatchIsolatesMissingSourceAndUndoRestoresExactBytesAfterRestart()
    {
        var path = Source("a.pdf");
        var bytes = File.ReadAllBytes(path);
        var service = new RenameService(Journal);
        var results = service.Execute([new(Path.Combine(root, "missing.pdf"), "B", "Cover"),
            new(path, "A", "Корпус")]);
        Assert.Equal(MoveStatus.Failed, results[0].Status);
        Assert.Equal(MoveStatus.Moved, results[1].Status);
        Assert.False(File.Exists(path));
        Assert.Equal(bytes, File.ReadAllBytes(results[1].Target!));
        var undo = Assert.Single(new RenameService(Journal).Undo());
        Assert.Equal(MoveStatus.Undone, undo.Status);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void UndoKeepsBothFilesWhenOriginalPathIsOccupied()
    {
        var path = Source("a.pdf");
        var service = new RenameService(Journal);
        var moved = Assert.Single(service.Execute([new(path, "A", "Cover")]));
        Source("a.pdf", "replacement");
        Assert.Equal(MoveStatus.Skipped, Assert.Single(service.Undo()).Status);
        Assert.Equal("replacement", File.ReadAllText(path));
        Assert.True(File.Exists(moved.Target!));
        File.Delete(path);
        Assert.Equal(MoveStatus.Undone, Assert.Single(service.Undo()).Status);
    }

    [Fact]
    public void UndoRefusesModifiedDestination()
    {
        var service = new RenameService(Journal);
        var moved = Assert.Single(service.Execute([new(Source("a.pdf"), "A", "Cover")]));
        File.WriteAllText(moved.Target!, "modified");
        Assert.Equal(MoveStatus.Skipped, Assert.Single(service.Undo()).Status);
        Assert.Equal("modified", File.ReadAllText(moved.Target!));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
