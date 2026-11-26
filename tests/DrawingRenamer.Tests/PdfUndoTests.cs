using System.Text.Json;

namespace DrawingRenamer.Tests;

public sealed class PdfUndoTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("drawing-renamer-pdf-undo-").FullName;
    private string Journal => Path.Combine(folder, "history.json");
    private string UndoRoot => Path.Combine(folder, "Undo");
    private string[] Backups => Directory.Exists(UndoRoot)
        ? Directory.GetFiles(UndoRoot, "*.pdf", SearchOption.AllDirectories) : [];

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void EditedPdfUsesAnExternalOriginalAndUndoRemovesIt(bool inPlace, bool crop)
    {
        var request = Request(inPlace, crop);
        var original = File.ReadAllBytes(request.Source);
        var service = new RenameService(Journal);
        var result = Assert.Single(service.Execute([request]));
        Assert.Equal(MoveStatus.Moved, result.Status);
        Assert.Equal(original, File.ReadAllBytes(Assert.Single(Backups)));
        Assert.True(new FileInfo(result.Target!).Length < original.Length * 1.1);
        using (var before = UglyToad.PdfPig.PdfDocument.Open(original))
        using (var after = UglyToad.PdfPig.PdfDocument.Open(result.Target!))
        {
            var originalImage = Assert.Single(before.GetPage(1).GetImages());
            var savedImage = Assert.Single(after.GetPage(1).GetImages());
            Assert.Equal(originalImage.RawMemory.ToArray(), savedImage.RawMemory.ToArray());
            Assert.Equal(originalImage.WidthInSamples, savedImage.WidthInSamples);
            Assert.Equal(originalImage.HeightInSamples, savedImage.HeightInSamples);
            Assert.Equal(originalImage.BitsPerComponent, savedImage.BitsPerComponent);
        }

        Assert.Equal(MoveStatus.Undone, Assert.Single(service.Undo()).Status);
        Assert.Equal(original, File.ReadAllBytes(request.Source));
        Assert.Empty(Backups);
        Close(service);
    }

    [Fact]
    public void EditedCopyUsesTheRetainedSourceWithoutAnExtraBackup()
    {
        var request = Request();
        var original = File.ReadAllBytes(request.Source);
        var destination = Directory.CreateDirectory(Path.Combine(folder, "output")).FullName;
        var service = new RenameService(Journal);
        var result = Assert.Single(service.Execute([request], destination: destination));
        Assert.Equal(MoveStatus.Copied, result.Status);
        Assert.Empty(Backups);
        Assert.Equal(original, File.ReadAllBytes(request.Source));
        Assert.Equal(MoveStatus.CopyRemoved, Assert.Single(service.Undo()).Status);
        Assert.False(File.Exists(result.Target));
        Close(service);
    }

    [Fact]
    public void ClosingTheSessionRemovesCompletedBackupsAndPreservesTheSavedPdf()
    {
        var service = new RenameService(Journal);
        var result = Assert.Single(service.Execute([Request()]));
        var saved = File.ReadAllBytes(result.Target!);
        Assert.Single(Backups);
        Close(service);
        Close(service);
        Assert.Empty(Backups);
        Assert.False(service.HasUndoableOperations());
        Assert.Equal(saved, File.ReadAllBytes(result.Target!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndoPreservesTheResultIfTheBackupIsMissingOrCorrupted(bool missing)
    {
        var service = new RenameService(Journal);
        var result = Assert.Single(service.Execute([Request(inPlace: true)]));
        var saved = File.ReadAllBytes(result.Target!);
        var backup = Assert.Single(Backups);
        if (missing) File.Delete(backup);
        else File.WriteAllText(backup, "damaged original");
        var undo = Assert.Single(service.Undo());
        Assert.Contains(undo.Status, new[] { MoveStatus.Skipped, MoveStatus.Failed });
        Assert.Equal(saved, File.ReadAllBytes(result.Target!));
        Close(service);
    }

    [Fact]
    public void JournalFailureAfterPublishingKeepsTheBackupAvailableForUndo()
    {
        var request = Request(inPlace: true);
        var original = File.ReadAllBytes(request.Source);
        var fail = false;
        var service = new RenameService(Journal, (temporary, journal) =>
        {
            fail |= RenameService.Hash(request.Source) != request.ExpectedHash;
            if (fail) throw new IOException("journal unavailable");
            File.Move(temporary, journal, overwrite: true);
        });
        var error = Assert.Throws<JournalWriteException>(() => service.Execute([request]));
        Assert.Equal(MoveStatus.Moved, Assert.Single(error.Completed).Status);
        Assert.Single(Backups);
        Assert.True(service.HasUndoableOperations());
        var undone = Assert.Throws<JournalWriteException>(() => service.Undo());
        Assert.Equal(MoveStatus.Undone, Assert.Single(undone.Completed).Status);
        Assert.Equal(original, File.ReadAllBytes(request.Source));
        Assert.Empty(Backups);
        Close(service);
    }

    [Fact]
    public void ASecondSessionDoesNotRemoveAnActiveSessionsOriginals()
    {
        var first = new RenameService(Journal);
        var result = Assert.Single(first.Execute([Request()]));
        var backup = Assert.Single(Backups);
        var second = new RenameService(Journal);
        Assert.False(second.HasUndoableOperations());
        Assert.True(File.Exists(backup));
        Close(second);
        Assert.True(File.Exists(backup));
        Assert.Equal(MoveStatus.Undone, Assert.Single(first.Undo()).Status);
        Assert.False(File.Exists(result.Target));
        Close(first);
    }

    [Fact]
    public void UnavailableBackupStorageDoesNotChangeTheOriginalAndCanBeRetried()
    {
        var request = Request(inPlace: true);
        var original = File.ReadAllBytes(request.Source);
        File.WriteAllText(UndoRoot, "occupied");
        using var service = new RenameService(Journal);
        Assert.Equal(MoveStatus.Failed, Assert.Single(service.Execute([request])).Status);
        Assert.Equal(original, File.ReadAllBytes(request.Source));
        Assert.False(service.HasUndoableOperations());
        File.Delete(UndoRoot);
        Assert.Equal(MoveStatus.Moved, Assert.Single(service.Execute([request])).Status);
        Assert.Equal(MoveStatus.Undone, Assert.Single(service.Undo()).Status);
        Assert.Equal(original, File.ReadAllBytes(request.Source));
    }

    [Fact]
    public void UndoCannotStartAnInPlaceWriteUntilRecoveryMetadataIsUpdated()
    {
        using var service = new RenameService(Journal);
        var request = Request(inPlace: true);
        var result = Assert.Single(service.Execute([request]));
        var saved = File.ReadAllBytes(result.Target!);
        var backup = Assert.Single(Backups);
        using (var marker = File.OpenRead(Path.ChangeExtension(backup, ".done")))
        {
            Assert.Equal(MoveStatus.Failed, Assert.Single(service.Undo()).Status);
            Assert.Equal(saved, File.ReadAllBytes(result.Target!));
            Assert.True(File.Exists(backup));
        }
        Assert.Equal(MoveStatus.Undone, Assert.Single(service.Undo()).Status);
    }

    [Fact]
    public void InterruptedInPlaceUndoKeepsItsOriginalAcrossCloseAndStartup()
    {
        var request = Request(inPlace: true);
        var original = File.ReadAllBytes(request.Source);
        string backup;
        using (var store = new PdfUndoStore(Journal))
        {
            using (var input = File.OpenRead(request.Source))
                backup = store.Save(input, request.Source, request.Source, request.ExpectedHash!, "edited-hash");
            store.Complete(backup);
            store.BeginRestore(backup);
            File.WriteAllText(request.Source, "partial restore after an interrupted undo");
        }
        Assert.Equal(original, File.ReadAllBytes(backup));
        using var next = new RenameService(Journal);
        Assert.False(next.HasUndoableOperations());
        Assert.Equal(original, File.ReadAllBytes(backup));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void StartupCleansOnlyCompletedOrVerifiedInterruptedBackups(bool completed, bool sourceIntact)
    {
        var request = Request();
        var directory = Directory.CreateDirectory(Path.Combine(UndoRoot, Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(directory, "session.lock"), "");
        var backup = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".pdf");
        File.Copy(request.Source, backup);
        File.WriteAllText(Path.ChangeExtension(backup, ".json"), JsonSerializer.Serialize(new
        {
            Source = request.Source, Target = Path.Combine(folder, "saved.pdf"),
            SourceHash = RenameService.Hash(request.Source), TargetHash = "not-published"
        }));
        if (completed) File.WriteAllText(Path.ChangeExtension(backup, ".done"), "");
        if (!sourceIntact) File.WriteAllText(request.Source, "interrupted write");

        var service = new RenameService(Journal);
        Assert.False(service.HasUndoableOperations());
        Assert.Equal(!completed && !sourceIntact, File.Exists(backup));
        Close(service);
    }

    private RenameRequest Request(bool inPlace = false, bool crop = false)
    {
        var source = Path.Combine(folder, inPlace ? "A_COVER.pdf" : "source.pdf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "scan-form1.pdf"), source);
        var alignment = new PageAlignment(2.5, .98);
        if (crop) alignment = alignment with { Crop = new("А4", 210, 297, new(.02, .02, .96, .96), 0, false) };
        return new(source, "A", "COVER")
        {
            ExpectedHash = RenameService.Hash(source),
            PageAlignments = new Dictionary<int, PageAlignment> { [1] = alignment }
        };
    }

    private static void Close(RenameService service) => Assert.IsAssignableFrom<IDisposable>(service).Dispose();
    public void Dispose() => Directory.Delete(folder, recursive: true);
}
