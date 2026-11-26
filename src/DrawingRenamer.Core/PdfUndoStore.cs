using System.Text.Json;

namespace DrawingRenamer;

// Each live session holds its own lease. Other instances may only clean unlocked sessions.
internal sealed class PdfUndoStore(string journalPath) : IDisposable
{
    private readonly string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(journalPath))!, "Undo");
    private string? session;
    private FileStream? lease;
    private readonly HashSet<string> completed = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Original(string Source, string Target, string SourceHash, string TargetHash);

    internal void CleanupStaleSessions()
    {
        try
        {
            if (!Directory.Exists(root) || IsLink(root)) return;
            foreach (var directory in Directory.GetDirectories(root))
            {
                if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _) && !IsLink(directory))
                    Cleanup(directory);
            }
        }
        catch (Exception ex) when (IsFileError(ex)) { }
    }

    internal string Save(Stream input, string source, string target, string sourceHash, string targetHash)
    {
        if (session is null)
        {
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var opened = new FileStream(Path.Combine(directory, "session.lock"), FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete);
            session = directory;
            lease = opened;
        }
        var path = Path.Combine(session, Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                input.Position = 0;
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
                if (RenameService.Hash(output) != sourceHash)
                    throw new InvalidDataException("Исходный PDF изменился при сохранении копии для отмены.");
            }
            using var metadata = new FileStream(Path.ChangeExtension(path, ".json"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(metadata, new Original(source, target, sourceHash, targetHash));
            metadata.Flush(flushToDisk: true);
            return path;
        }
        catch
        {
            Remove(path);
            throw;
        }
    }

    internal void Complete(string? path)
    {
        if (path is null) return;
        completed.Add(path);
        try
        {
            using var marker = new FileStream(Path.ChangeExtension(path, ".done"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            marker.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (IsFileError(ex)) { }
    }

    internal void BeginRestore(string path)
    {
        // A crash during undo is another interrupted write. Its original must survive cleanup.
        // Failure to remove the durable completion marker must stop undo before changing the PDF.
        File.Delete(Path.ChangeExtension(path, ".done"));
        completed.Remove(path);
    }

    internal static void Remove(string? path)
    {
        if (path is null) return;
        try
        {
            File.Delete(path);
            File.Delete(Path.ChangeExtension(path, ".json"));
            File.Delete(Path.ChangeExtension(path, ".done"));
        }
        catch (Exception ex) when (IsFileError(ex)) { }
    }

    private static void Cleanup(string directory)
    {
        try
        {
            using (var guard = new FileStream(Path.Combine(directory, "session.lock"), FileMode.Open,
                FileAccess.ReadWrite, FileShare.None))
            {
                foreach (var metadata in Directory.GetFiles(directory, "*.json"))
                {
                    if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(metadata), "N", out _) || IsLink(metadata)) continue;
                    try
                    {
                        var path = Path.ChangeExtension(metadata, ".pdf");
                        if (File.Exists(Path.ChangeExtension(path, ".done")))
                            Remove(path);
                        else
                        {
                            var original = JsonSerializer.Deserialize<Original>(File.ReadAllText(metadata));
                            // An interrupted write with neither a valid source nor a valid result needs recovery.
                            if (original is not null && (Matches(original.Source, original.SourceHash) ||
                                Matches(original.Target, original.TargetHash))) Remove(path);
                        }
                    }
                    catch (Exception ex) when (IsFileError(ex) || ex is JsonException) { }
                }
                if (Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path) != "session.lock")) return;
            }
            File.Delete(Path.Combine(directory, "session.lock"));
            Directory.Delete(directory);
        }
        catch (Exception ex) when (IsFileError(ex)) { }
    }

    private static bool Matches(string path, string hash)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(hash)) return false;
        try { return RenameService.Hash(path) == hash; }
        catch (Exception ex) when (IsFileError(ex) || ex is ArgumentException or NotSupportedException) { return false; }
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException;

    public void Dispose()
    {
        // Even if a completion marker could not be written, successful operations are known in this process.
        foreach (var path in completed) Remove(path);
        completed.Clear();
        lease?.Dispose();
        lease = null;
        if (session is not null) Cleanup(session);
    }
}
