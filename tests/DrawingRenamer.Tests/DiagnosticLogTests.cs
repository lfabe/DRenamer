using System.Text.Json;

namespace DrawingRenamer.Tests;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("drawing-logs-").FullName;

    [Fact]
    public void WritesStructuredUtf8EntriesWithExceptionAndConcurrentMessages()
    {
        var log = new DiagnosticLog(folder);
        Exception failure;
        try { throw new IOException("Нет доступа\nк PDF", new InvalidOperationException("inner")); }
        catch (IOException ex) { failure = ex; }
        log.Error("Recognition", "Чертёж.pdf", failure);
        Parallel.For(0, 50, i => log.Info("Import", $"file-{i}"));

        var lines = File.ReadAllLines(log.FilePath);
        Assert.Equal(51, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        var entry = first.RootElement;
        Assert.Equal("Error", entry.GetProperty("Level").GetString());
        Assert.Equal("Recognition", entry.GetProperty("Operation").GetString());
        Assert.Equal("Чертёж.pdf", entry.GetProperty("Message").GetString());
        Assert.Contains("Нет доступа\nк PDF", entry.GetProperty("Exception").GetString());
        Assert.Contains(nameof(InvalidOperationException), entry.GetProperty("Exception").GetString());
        Assert.Contains(nameof(WritesStructuredUtf8EntriesWithExceptionAndConcurrentMessages), entry.GetProperty("Exception").GetString());
        Assert.True(entry.GetProperty("Time").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
        Assert.Equal(Environment.ProcessId, entry.GetProperty("ProcessId").GetInt32());
        foreach (var line in lines) using (JsonDocument.Parse(line)) { }
        Assert.Equal(50, lines.Skip(1).Distinct().Count());
    }

    [Fact]
    public void RotatesFilesAndKeepsTheNewestEntries()
    {
        var log = new DiagnosticLog(folder, maxFileBytes: 512);
        for (var i = 0; i < 30; i++) log.Info("Test", new string('x', 100) + i);
        var files = Directory.GetFiles(folder);
        Assert.Equal(3, files.Length);
        Assert.All(files, path => Assert.InRange(new FileInfo(path).Length, 1, 512));
        Assert.Contains(new string('x', 100) + "29", File.ReadAllText(log.FilePath));
    }

    [Fact]
    public void SessionsUseDifferentFilesAndOldLogsAreCleanedWithoutTouchingOtherFiles()
    {
        var stale = Path.Combine(folder, "drawing-renamer-old.log");
        var unrelated = Path.Combine(folder, "keep.txt");
        File.WriteAllText(stale, "old");
        File.WriteAllText(unrelated, "keep");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-8));
        var first = new DiagnosticLog(folder);
        var second = new DiagnosticLog(folder);
        first.Info("Startup", "first");
        second.Info("Startup", "second");
        Assert.NotEqual(first.FilePath, second.FilePath);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(unrelated));
        Assert.Contains("first", File.ReadAllText(first.FilePath));
        Assert.Contains("second", File.ReadAllText(second.FilePath));
    }

    [Fact]
    public void TotalSizeLimitAppliesAcrossSessionsAndAgeCleanupContinuesDuringASession()
    {
        var stale = Path.Combine(folder, "drawing-renamer-expired.log.1");
        var logs = Enumerable.Range(0, 8).Select(_ => new DiagnosticLog(folder, 512, 1024)).ToArray();
        foreach (var log in logs) log.Info("Test", new string('x', 100));
        Assert.InRange(Directory.GetFiles(folder).Sum(path => new FileInfo(path).Length), 1, 1024);
        Assert.True(File.Exists(logs[^1].FilePath));

        File.WriteAllText(stale, "expired");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-8));
        logs[^1].Info("Test", "next write");
        Assert.False(File.Exists(stale));
        Assert.InRange(Directory.GetFiles(folder).Sum(path => new FileInfo(path).Length), 1, 1024);
    }

    [Fact]
    public void HugeMessagesAreTruncatedAndLockedFilesDoNotThrow()
    {
        var log = new DiagnosticLog(folder);
        log.Error("Test", new string('я', 1_000_000), new IOException(new string('я', 1_000_000)));
        Assert.InRange(new FileInfo(log.FilePath).Length, 1, 2 * 1024 * 1024);
        Assert.Contains("[truncated]", File.ReadAllText(log.FilePath));
        using (File.Open(log.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            log.Info("Test", "locked");
        log.Info("Test", "available again");
        Assert.Contains("available again", File.ReadAllText(log.FilePath));
    }

    [Fact]
    public void LockedExpiredFilesStillCountTowardTheTotalLimit()
    {
        var stale = Path.Combine(folder, "drawing-renamer-locked.log");
        File.WriteAllBytes(stale, new byte[1024]);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-8));
        var log = new DiagnosticLog(folder, 512, 1024);
        using (File.Open(stale, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            log.Info("Test", "no room");
            Assert.False(File.Exists(log.FilePath));
        }
        log.Info("Test", "room available again");
        Assert.False(File.Exists(stale));
        Assert.Contains("room available again", File.ReadAllText(log.FilePath));
    }

    [Fact]
    public async Task UnwritableLogDoesNotBreakSettingsOrFileOperations()
    {
        var blocked = Path.Combine(folder, "blocked");
        File.WriteAllText(blocked, "not a directory");
        var log = new DiagnosticLog(blocked);
        log.Error("Test", "failure", new IOException("disk"));
        var settings = Path.Combine(folder, "settings.json");
        var model = new MainViewModel(settings, log);
        await model.SaveSettingsAsync();
        Assert.True(File.Exists(settings));
        var source = Path.Combine(folder, "source.pdf");
        File.WriteAllText(source, "content");
        using var renamer = new RenameService(Path.Combine(folder, "history.json"), log);
        Assert.Equal(MoveStatus.Moved, Assert.Single(renamer.Execute([new(source, "A", "Part")])).Status);
        Assert.Equal(MoveStatus.Undone, Assert.Single(renamer.Undo()).Status);
    }

    [Fact]
    public async Task LogsSettingsFallbackRecognitionErrorsAndFileResults()
    {
        var log = new DiagnosticLog(Path.Combine(folder, "logs"));
        var settings = Path.Combine(folder, "settings.json");
        File.WriteAllText(settings, "{broken");
        var model = new MainViewModel(settings, log);
        await model.LoadSettingsAsync();
        Assert.Contains("по умолчанию", model.SettingsMessage);
        await model.SaveSettingsAsync();
        var source = Path.Combine(folder, "source.pdf");
        File.WriteAllText(source, "content");
        using var renamer = new RenameService(Path.Combine(folder, "history.json"), log);
        Assert.Equal(MoveStatus.Moved, Assert.Single(renamer.Execute([new(source, "A", "Part")])).Status);
        Assert.Equal(MoveStatus.Undone, Assert.Single(renamer.Undo()).Status);
        Assert.Equal(MoveStatus.Skipped, Assert.Single(renamer.Execute([new(source, "", "")])).Status);
        var result = new DrawingAnalyzer(log).Analyze(Path.Combine(folder, "missing.pdf"), new(OcrEnabled: false), default);
        Assert.Equal(RecognitionStatus.Error, result.Status);
        var entries = File.ReadAllText(log.FilePath);
        Assert.Contains("JsonReaderException", entries);
        Assert.Contains("Settings", entries);
        Assert.Contains("Moved", entries);
        Assert.Contains("Undone", entries);
        Assert.Contains("Skipped", entries);
        Assert.Contains("Recognition", entries);
        Assert.Contains("FileNotFoundException", entries);
    }

    public void Dispose() => Directory.Delete(folder, true);
}
