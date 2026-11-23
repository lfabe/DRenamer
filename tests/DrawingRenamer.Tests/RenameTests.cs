using System.Text.Json.Nodes;

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
    [InlineData("A/B", "PART")]
    [InlineData("CON", "")]
    [InlineData("NUL.TXT", "PART")]
    [InlineData("COM1.DETAILS", "PART")]
    [InlineData("A", "PART?")]
    [InlineData("A\tB", "PART")]
    public void RejectsInvalidWindowsNames(string designation, string title) =>
        Assert.Null(RenameService.FileName(designation, title));

    [Fact]
    public void SeparatorDefaultsToUnderscore()
    {
        Assert.Equal("_", new RenameRequest("a.pdf", "A", "PART").Separator);
        Assert.Equal("A_PART.pdf", RenameService.FileName("A", "PART"));
    }

    [Theory]
    [InlineData("_")]
    [InlineData(" - ")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("‒")]
    [InlineData("#+")]
    [InlineData("🙂🙂🙂")]
    public void FileNameAllowsOneToThreeSignsAndSpaces(string separator)
    {
        Assert.Equal($"A{separator}PART.pdf", RenameService.FileName("A", "PART", separator, out var error));
        Assert.Null(error);
        Assert.Equal("A.pdf", RenameService.FileName("A", "Спецификация", separator, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("____")]
    [InlineData("    ")]
    [InlineData("a")]
    [InlineData("А")]
    [InlineData("中")]
    [InlineData("1")]
    [InlineData("١")]
    [InlineData("Ⅳ")]
    [InlineData("²")]
    [InlineData("_a_")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData(":")]
    [InlineData("\"")]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData("|")]
    [InlineData("?")]
    [InlineData("*")]
    [InlineData("\0")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData("\u007f")]
    [InlineData("\u200b")]
    [InlineData("\u00a0")]
    [InlineData("\u0301")]
    public void InvalidSeparatorCannotReachAFileOperation(string separator)
    {
        var source = Source("separator.pdf");
        var request = new RenameRequest(source, "A", "PART") { Separator = separator };
        Assert.Null(RenameService.FileName("A", "PART", separator, out var error));
        Assert.Contains("Разделитель", error);
        var preview = Assert.Single(RenameService.Preview([request]));
        Assert.Equal(MoveStatus.Skipped, preview.Status);
        Assert.Null(preview.Target);
        Assert.Equal(MoveStatus.Skipped, Assert.Single(new RenameService(Journal).Execute([request])).Status);
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CustomSeparatorIsUsedForPreviewSavingAndUndo(bool chooseFolder, bool copy)
    {
        var source = Source("separator.pdf");
        var destination = chooseFolder ? Directory.CreateDirectory(Path.Combine(root, "destination")).FullName : null;
        var request = new RenameRequest(source, "A", "PART") { Separator = " - " };
        var target = Path.Combine(destination ?? root, "A - PART.pdf");
        Assert.Equal(target, Assert.Single(RenameService.Preview([request], destination)).Target);
        var service = new RenameService(Journal);
        var saved = Assert.Single(service.Execute([request], destination: destination, removeOriginals: !copy));
        Assert.Equal(copy ? MoveStatus.Copied : MoveStatus.Moved, saved.Status);
        Assert.Equal(target, saved.Target);
        Assert.True(File.Exists(target));
        Assert.Equal(copy ? MoveStatus.CopyRemoved : MoveStatus.Undone, Assert.Single(service.Undo()).Status);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void SeparatorRejectsNullAndMalformedUnicode()
    {
        Assert.Null(RenameService.FileName("A", "PART", null!, out var missing));
        Assert.Contains("Разделитель", missing);
        Assert.Null(RenameService.FileName("A", "PART", new string((char)0xD800, 1), out var malformed));
        Assert.Contains("Разделитель", malformed);
    }

    [Fact]
    public void SeparatorStillRespectsReservedNamesAndTheFullNameLength()
    {
        Assert.Null(RenameService.FileName("CON", "PART", ".", out _));
        Assert.Null(RenameService.FileName(new string('A', 249), "7", " - ", out _));
    }

    [Fact]
    public void AllowsTheSameDrawingCharactersInBothFields() =>
        Assert.Equal("АБВГ.123456.001 СБ ‒ A_ЧЕРТЁЖ 2.1 ‒ B.pdf",
            RenameService.FileName("АБВГ.123456.001 СБ ‒ A", "ЧЕРТЁЖ 2.1 ‒ B"));

    [Theory]
    [InlineData("AB/01")]
    [InlineData("AB-01")]
    [InlineData("AB–01")]
    [InlineData("AB\u00A001")]
    [InlineData("AB\u0080")]
    public void RejectsUnsupportedDrawingCharactersInEitherField(string value)
    {
        Assert.Null(RenameService.FileName(value, "PART", out var designationError));
        Assert.Contains("«Обозначение»", designationError);
        Assert.Null(RenameService.FileName("AB", value, out var titleError));
        Assert.Contains("«Наименование»", titleError);
    }

    [Theory]
    [InlineData("Assembly housing")]
    [InlineData("Схема чертёжа")]
    public void AllowsLowercaseInTheTitleOnly(string value)
    {
        Assert.Equal($"AB_{value}.pdf", RenameService.FileName("AB", value));
        Assert.Null(RenameService.FileName(value, "PART", out var designationError));
        Assert.Contains("«Обозначение» — строчные буквы", designationError);
    }

    [Theory]
    [InlineData("ПРОКЛАДКА_01", "_")]
    [InlineData("РАМА № 2", "№")]
    public void AllowsUnderscoreAndNumeroSignInTheTitleOnly(string value, string symbol)
    {
        Assert.Equal($"AB_{value}.pdf", RenameService.FileName("AB", value));
        Assert.Null(RenameService.FileName(value, "PART", out var error));
        Assert.Contains($"«Обозначение» — «{symbol}»", error);
    }

    [Theory]
    [InlineData("Соединитель Сборочный чертеж", "АБВГ.001 СБ_Соединитель.pdf")]
    [InlineData("Корпус. СБОРОЧНЫЙ\r\nЧЕРТЁЖ.", "АБВГ.001 СБ_Корпус.pdf")]
    [InlineData("Корпус — (сборочный чертёж)", "АБВГ.001 СБ_Корпус.pdf")]
    [InlineData("Корпус\u00A0Спецификация", "АБВГ.001 СБ_Корпус.pdf")]
    [InlineData("Сборочный чертёж Корпус", "АБВГ.001 СБ_Корпус.pdf")]
    [InlineData("Ведомость покупных изделий", "АБВГ.001 СБ.pdf")]
    [InlineData("Коробка передач. Программа и методика испытаний. Стендовые испытания", "АБВГ.001 СБ_Коробка передач. Стендовые испытания.pdf")]
    [InlineData("Коробка передач. Технические условия. Часть 2", "АБВГ.001 СБ_Коробка передач. Часть 2.pdf")]
    [InlineData("Компрессор Руководство по эксплуатации", "АБВГ.001 СБ_Компрессор.pdf")]
    [InlineData("Компрессор. Инструкция по монтажу, пуску, регулированию и обкатке изделия", "АБВГ.001 СБ_Компрессор.pdf")]
    [InlineData("Насос Технические условия на ремонт", "АБВГ.001 СБ_Насос.pdf")]
    [InlineData("Редуктор Ремонтный сборочный чертеж", "АБВГ.001 СБ_Редуктор.pdf")]
    [InlineData("Шкаф Схема электрическая принципиальная", "АБВГ.001 СБ_Шкаф.pdf")]
    [InlineData("Шкаф Электрическая принципиальная схема", "АБВГ.001 СБ_Шкаф.pdf")]
    [InlineData("Блок Схема электрогидравлическая принципиальная", "АБВГ.001 СБ_Блок.pdf")]
    [InlineData("Установка Схема гидравлическая структурная, принципиальная и соединений", "АБВГ.001 СБ_Установка.pdf")]
    [InlineData("Шкаф Схема электрическая принципиальная цепей питания", "АБВГ.001 СБ_Шкаф цепей питания.pdf")]
    [InlineData("Корпус монтажный", "АБВГ.001 СБ_Корпус монтажный.pdf")]
    [InlineData("Макет пояснительной записки", "АБВГ.001 СБ_Макет пояснительной записки.pdf")]
    [InlineData("Проверка на сборочный чертеж изделия", "АБВГ.001 СБ_Проверка на сборочный чертеж изделия.pdf")]
    public void FileNameOmitsDocumentTypeAndRetainsOtherTitleParts(string title, string expected) =>
        Assert.Equal(expected, RenameService.FileName("АБВГ.001 СБ", title));

    [Fact]
    public void TypeOnlyTitleStillChecksReservedNamesAndEmptyTitlesAreRejected()
    {
        Assert.Null(RenameService.FileName("CON", "Спецификация", out var reserved));
        Assert.Contains("зарезервированного", reserved);
        Assert.Null(RenameService.FileName("A", "", out var missing));
        Assert.Contains("«Наименование»", missing);
    }

    [Fact]
    public void PreviewRejectsTargetsThatCoincideAfterOmittingDocumentType()
    {
        var first = Source("first.pdf");
        var second = Source("second.pdf");
        var result = RenameService.Preview([new(first, "A СБ", "Корпус Сборочный чертёж"), new(second, "A СБ", "Корпус")]);

        Assert.All(result, item =>
        {
            Assert.Equal(MoveStatus.Skipped, item.Status);
            Assert.Equal(Path.Combine(root, "A СБ_Корпус.pdf"), item.Target);
            Assert.Equal("Повторяющееся целевое имя.", item.Message);
        });
    }

    [Theory]
    [InlineData("'", "U+0027")]
    [InlineData("‘", "U+2018")]
    public void ReportsTheActualForbiddenSymbolsInBothFields(string quote, string code)
    {
        var name = RenameService.FileName("ФПО.Т0-14.00.000 С1", $"Схема {quote}оборудования", out var error);

        Assert.Null(name);
        Assert.Contains("«Обозначение» — «-» (U+002D)", error);
        Assert.Contains($"«Наименование» — «{quote}» ({code})", error);
        Assert.DoesNotContain("строчные буквы", error);
    }

    [Fact]
    public void ChecksLengthOfCompleteWindowsFileName()
    {
        var designation = new string('A', 249);
        Assert.Equal(255, RenameService.FileName(designation, "7")!.Length);
        Assert.Null(RenameService.FileName(designation, "77"));
    }

    [Fact]
    public void PreviewExplainsWhichFieldsContainForbiddenWindowsCharacters()
    {
        var source = Source("a.pdf");
        var result = Assert.Single(RenameService.Preview([new(source, "A\"\"\"B", "PART?")]));

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
        var target = Path.Combine(destination, "A_COVER.pdf");
        var service = new RenameService(Journal);

        var result = Assert.Single(service.Execute([new(source, "A", "COVER")], destination: destination));
        Assert.Equal(MoveStatus.Copied, result.Status);
        Assert.Equal("%PDF-original", File.ReadAllText(source));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));

        Assert.Equal(MoveStatus.CopyRemoved, Assert.Single(service.Undo()).Status);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void CopyUndoKeepsTheCopyIfTheOriginalOrCopyHasChanged()
    {
        var source = Source("a.pdf");
        var destination = Directory.CreateDirectory(Path.Combine(root, "output")).FullName;
        var service = new RenameService(Journal);
        var target = Assert.Single(service.Execute([new(source, "A", "COVER")], destination: destination)).Target!;
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
        var target = Path.Combine(destination, "A_COVER.pdf");
        var requests = new RenameRequest[] { new(source, "A", "COVER") };
        var service = new RenameService(Journal);

        Assert.Equal(target, Assert.Single(RenameService.Preview(requests, destination)).Target);
        Assert.Equal(MoveStatus.Moved, Assert.Single(service.Execute(requests, destination: destination, removeOriginals: true)).Status);
        Assert.False(File.Exists(source));
        Assert.Equal("%PDF-original", File.ReadAllText(target));

        var restored = Assert.Single(service.Undo());
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
        var requests = new RenameRequest[] { new(first, "A", "COVER"), new(second, "A", "COVER") };

        var duplicates = service.Execute(requests, destination: destination);
        Assert.All(duplicates, result =>
        {
            Assert.Equal(MoveStatus.Skipped, result.Status);
            Assert.Contains("Повторяющееся", result.Message);
        });
        var existing = Source(Path.Combine(destination, "A_COVER.pdf"), "existing");
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
        var requests = new RenameRequest[] { new(source, "A", "COVER") };
        Assert.Equal(MoveStatus.Pending, Assert.Single(RenameService.Preview(requests, destination)).Status);
        Directory.Delete(destination);

        var result = Assert.Single(new RenameService(Journal).Execute(requests, destination: destination));
        Assert.Equal(MoveStatus.Skipped, result.Status);
        Assert.Contains("папка назначения недоступна", result.Message);
        Assert.Equal("%PDF-original", File.ReadAllText(source));
        Assert.False(File.Exists(Path.Combine(root, "A_COVER.pdf")));
    }

    [Fact]
    public void DuplicateAndExistingTargetsCannotOverwrite()
    {
        var first = Source("a.pdf");
        var second = Source("b.pdf");
        var target = Source("B_КОРПУС.pdf", "unrelated");
        var service = new RenameService(Journal);
        var results = service.Execute(
            [new(first, "A", "КОРПУС"), new(second, "A", "КОРПУС"), new(first, "B", "КОРПУС")]);
        Assert.All(results, result => Assert.Equal(MoveStatus.Skipped, result.Status));
        Assert.Equal("unrelated", File.ReadAllText(target));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public void BatchIsolatesMissingSourceAndUndoRestoresExactBytes()
    {
        var path = Source("a.pdf");
        var bytes = File.ReadAllBytes(path);
        var service = new RenameService(Journal);
        var results = service.Execute([new(Path.Combine(root, "missing.pdf"), "B", "COVER"),
            new(path, "A", "КОРПУС")]);
        Assert.Equal(MoveStatus.Failed, results[0].Status);
        Assert.Equal(MoveStatus.Moved, results[1].Status);
        Assert.False(File.Exists(path));
        Assert.Equal(bytes, File.ReadAllBytes(results[1].Target!));
        var undo = Assert.Single(service.Undo());
        Assert.Equal(MoveStatus.Undone, undo.Status);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void UndoAvailabilityTracksRemainingBatchesInCurrentSession()
    {
        var service = new RenameService(Journal);
        Assert.False(service.HasUndoableOperations());
        Assert.Equal(MoveStatus.Failed, Assert.Single(service.Execute([new(Path.Combine(root, "missing.pdf"), "A", "COVER")])).Status);
        Assert.False(service.HasUndoableOperations());

        Assert.Equal(MoveStatus.Moved, Assert.Single(service.Execute([new(Source("move.pdf"), "B", "COVER")])).Status);
        Assert.Equal(MoveStatus.Copied, Assert.Single(service.Execute([new(Source("copy.pdf"), "C", "COVER")], destination: root)).Status);

        Assert.True(service.HasUndoableOperations());
        Assert.Equal(MoveStatus.CopyRemoved, Assert.Single(service.Undo()).Status);
        Assert.True(service.HasUndoableOperations());
        Assert.Equal(MoveStatus.Undone, Assert.Single(service.Undo()).Status);
        Assert.False(service.HasUndoableOperations());
    }

    [Fact]
    public void NewSessionClearsHistoryAndCanOnlyUndoItsOwnOperations()
    {
        var previous = new RenameService(Journal);
        var earlier = Assert.Single(previous.Execute([new(Source("earlier.pdf"), "A", "COVER")]));
        Assert.True(previous.HasUndoableOperations());

        var current = new RenameService(Journal);
        Assert.False(current.HasUndoableOperations());
        Assert.Empty(JsonNode.Parse(File.ReadAllText(Journal))!.AsArray());
        Assert.Empty(current.Undo());
        Assert.Empty(current.History());
        Assert.True(previous.HasUndoableOperations());

        var source = Source("current.pdf");
        Assert.Equal(MoveStatus.Moved, Assert.Single(current.Execute([new(source, "B", "COVER")])).Status);
        Assert.True(current.HasUndoableOperations());
        var saved = Assert.Single(JsonNode.Parse(File.ReadAllText(Journal))!.AsArray());
        Assert.Equal(source, saved!["Result"]!["Source"]!.GetValue<string>());
        Assert.Equal(MoveStatus.Undone, Assert.Single(current.Undo()).Status);
        Assert.False(current.HasUndoableOperations());
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(earlier.Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingTargetDoesNotKeepUndoAvailableOrHideEarlierBatches(bool copy)
    {
        var service = new RenameService(Journal);
        var first = Source("first.pdf");
        Assert.Equal(MoveStatus.Moved, Assert.Single(service.Execute([new(first, "A", "COVER")])).Status);
        var latest = Assert.Single(service.Execute([new(Source("latest.pdf"), "B", "COVER")],
            destination: copy ? root : null));
        Assert.Equal(copy ? MoveStatus.Copied : MoveStatus.Moved, latest.Status);
        File.Delete(latest.Target!);

        Assert.True(service.HasUndoableOperations());
        Assert.Equal(MoveStatus.Undone, Assert.Single(service.Undo()).Status);
        Assert.True(File.Exists(first));
        Assert.False(service.HasUndoableOperations());
        Assert.Empty(service.Undo());
    }

    [Fact]
    public void NewSessionClearsAnUnreadableJournal()
    {
        File.WriteAllText(Journal, "invalid JSON");
        Assert.False(new RenameService(Journal).HasUndoableOperations());
        Assert.Empty(JsonNode.Parse(File.ReadAllText(Journal))!.AsArray());
    }

    [Fact]
    public void UndoKeepsBothFilesWhenOriginalPathIsOccupied()
    {
        var path = Source("a.pdf");
        var service = new RenameService(Journal);
        var moved = Assert.Single(service.Execute([new(path, "A", "COVER")]));
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
        var moved = Assert.Single(service.Execute([new(Source("a.pdf"), "A", "COVER")]));
        File.WriteAllText(moved.Target!, "modified");
        Assert.Equal(MoveStatus.Skipped, Assert.Single(service.Undo()).Status);
        Assert.Equal("modified", File.ReadAllText(moved.Target!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ReplacedSourceRequiresReviewEvenWithSameSizeAndTimestamp(bool copy, bool rotate)
    {
        var path = Source("source.pdf", "first");
        var stamp = File.GetLastWriteTimeUtc(path);
        var request = new RenameRequest(path, "A", "COVER")
        {
            ExpectedHash = RenameService.Hash(path),
            PageRotations = rotate ? new Dictionary<int, int> { [1] = 1 } : null
        };
        File.WriteAllText(path, "other");
        File.SetLastWriteTimeUtc(path, stamp);

        var service = new RenameService(Journal);
        var result = Assert.Single(service.Execute([request], destination: copy ? root : null));
        Assert.Equal(MoveStatus.Skipped, result.Status);
        Assert.True(result.SourceChanged);
        Assert.Equal("other", File.ReadAllText(path));
        Assert.False(File.Exists(result.Target));
        Assert.False(service.HasUndoableOperations());
    }

    [Fact]
    public void MoveChecksTheOpenedFileAfterRecordingIntent()
    {
        var source = Source("first.pdf");
        var request = new RenameRequest(source, "A", "COVER") { ExpectedHash = RenameService.Hash(source) };
        var service = new RenameService(Journal, (temporary, journal) =>
        {
            if (!File.Exists(journal)) File.WriteAllText(source, "updated before move");
            File.Move(temporary, journal, overwrite: true);
        });

        var result = Assert.Single(service.Execute([request]));
        Assert.True(result.SourceChanged);
        Assert.Equal(MoveStatus.Skipped, result.Status);
        Assert.Equal("updated before move", File.ReadAllText(source));
        Assert.False(File.Exists(result.Target));
    }

    [Fact]
    public void VerifiedHandlePreventsReplacementUntilDeleteCompletes()
    {
        var path = Source("target.pdf");
        var replacement = Source("replacement.pdf", "updated");
        var expected = RenameService.Hash(path);
        using (var verified = WindowsFile.OpenForMove(path))
        {
            Assert.Equal(expected, RenameService.Hash(verified));
            var denied = Record.Exception(() => File.Move(replacement, path, overwrite: true));
            Assert.True(denied is IOException or UnauthorizedAccessException);
            WindowsFile.Delete(verified);
        }
        Assert.False(File.Exists(path));
        Assert.Equal("updated", File.ReadAllText(replacement));
    }

    [Fact]
    public void JournalFailureReturnsCompletedMovesAndKeepsUndoInMemory()
    {
        var first = Source("first.pdf");
        var next = Source("next.pdf");
        var target = Path.Combine(root, "A_COVER.pdf");
        var failWrites = false;
        var service = new RenameService(Journal, (temporary, journal) =>
        {
            failWrites |= File.Exists(target);
            using var reader = failWrites ? File.OpenRead(journal) : null;
            File.Move(temporary, journal, overwrite: true);
        });

        var failure = Assert.Throws<JournalWriteException>(() => service.Execute(
            [new(first, "A", "COVER"), new(next, "B", "COVER")]));
        Assert.Equal(MoveStatus.Moved, Assert.Single(failure.Completed).Status);
        Assert.True(File.Exists(target));
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(next));
        Assert.True(service.HasUndoableOperations());

        var undo = Assert.Throws<JournalWriteException>(() => service.Undo());
        Assert.Equal(MoveStatus.Undone, Assert.Single(undo.Completed).Status);
        Assert.True(File.Exists(first));
        Assert.False(File.Exists(target));
        Assert.False(service.HasUndoableOperations());
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
