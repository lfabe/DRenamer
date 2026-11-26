using System.Text;
using System.Text.Json;

namespace DrawingRenamer;

/// <summary>Best-effort diagnostic JSON lines, independent of the undo journal.</summary>
public sealed class DiagnosticLog
{
    private readonly object gate = new();
    private readonly string folder;
    private readonly long maxFileBytes;
    private readonly long maxTotalBytes;
    private DateOnly fileDate = DateOnly.FromDateTime(DateTime.UtcNow);

    public DiagnosticLog(string folder) : this(folder, 2 * 1024 * 1024) { }

    internal DiagnosticLog(string folder, long maxFileBytes, long maxTotalBytes = 20 * 1024 * 1024)
    {
        this.folder = folder;
        this.maxFileBytes = maxFileBytes;
        this.maxTotalBytes = maxTotalBytes;
        // Separate sessions (including simultaneous instances) never rotate each other's files.
        FilePath = Path.Combine(folder, $"drawing-renamer-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
    }

    public string FilePath { get; }
    public void Info(string operation, string message) => Write("Info", operation, message);
    public void Warning(string operation, string message, Exception? exception = null) => Write("Warning", operation, message, exception);
    public void Error(string operation, string message, Exception exception) => Write("Error", operation, message, exception);

    private void Write(string level, string operation, string message, Exception? exception = null)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var line = JsonSerializer.Serialize(new
                {
                    Time = DateTimeOffset.UtcNow, Level = level, Operation = Limit(operation, 128),
                    Message = Limit(message, 16 * 1024), Exception = exception is null ? null : Limit(exception.ToString(), 32 * 1024),
                    ProcessId = Environment.ProcessId
                }) + "\n";
                var bytes = Encoding.UTF8.GetBytes(line);
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                if (File.Exists(FilePath) && (fileDate != today || new FileInfo(FilePath).Length + bytes.Length > maxFileBytes))
                {
                    File.Delete(FilePath + ".2");
                    if (File.Exists(FilePath + ".1")) File.Move(FilePath + ".1", FilePath + ".2");
                    File.Move(FilePath, FilePath + ".1");
                }
                // Reserve room before writing. If old files cannot be removed, stop logging
                // instead of letting diagnostics grow beyond the directory budget.
                if (!Cleanup(bytes.Length)) return;
                using (var output = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    output.Write(bytes);
                fileDate = today;
            }
            catch (Exception ex) when (IsLogError(ex))
            {
                // Diagnostics must never change a file operation's outcome or hide its original error.
            }
        }
    }

    private bool Cleanup(long reserveBytes)
    {
        var files = new List<(string Path, long Length, DateTime LastWrite)>();
        foreach (var path in Directory.EnumerateFiles(folder, "drawing-renamer-*.log*"))
        {
            try
            {
                if (!(path.EndsWith(".log", StringComparison.Ordinal) || path.EndsWith(".log.1", StringComparison.Ordinal)
                    || path.EndsWith(".log.2", StringComparison.Ordinal))) continue;
                var file = new FileInfo(path);
                var length = file.Length;
                var lastWrite = file.LastWriteTimeUtc;
                if (lastWrite < DateTime.UtcNow.AddDays(-7))
                {
                    try { file.Delete(); continue; }
                    catch (Exception ex) when (IsLogError(ex)) { }
                }
                // An expired but locked file still consumes disk space.
                files.Add((file.FullName, length, lastWrite));
            }
            catch (Exception ex) when (IsLogError(ex)) { }
        }
        var total = files.Sum(file => file.Length);
        foreach (var file in files.OrderBy(file => file.LastWrite))
        {
            if (total + reserveBytes <= maxTotalBytes) break;
            if (string.Equals(file.Path, Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                File.Delete(file.Path);
                total -= file.Length;
            }
            catch (Exception ex) when (IsLogError(ex)) { }
        }
        return total + reserveBytes <= maxTotalBytes;
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length] + " [truncated]";

    private static bool IsLogError(Exception ex) => ex is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException;
}
