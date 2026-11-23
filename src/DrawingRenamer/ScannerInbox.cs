namespace DrawingRenamer;

internal sealed class ScannerInbox(string folder)
{
    private readonly record struct FileVersion(long Length, DateTime LastWriteTimeUtc);

    private Dictionary<string, FileVersion> previous = new(StringComparer.OrdinalIgnoreCase);
    internal string Folder { get; } = folder;

    internal string[] FindReadyFiles(IReadOnlySet<string> ignored)
    {
        var current = new Dictionary<string, FileVersion>(StringComparer.OrdinalIgnoreCase);
        var ready = new List<string>();
        try
        {
            var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
            foreach (var path in Directory.EnumerateFiles(Folder, "*.pdf", options))
            {
                if (ignored.Contains(path)) continue;
                try
                {
                    var file = new FileInfo(path);
                    var version = new FileVersion(file.Length, file.LastWriteTimeUtc);
                    if (version.Length == 0) continue;
                    current[path] = version;

                    // Wait for an unchanged observation and for the scanner to release any write handle.
                    if (!previous.TryGetValue(path, out var earlier) || earlier != version) continue;
                    using var input = File.OpenRead(path);
                    if (input.Length == version.Length && File.GetLastWriteTimeUtc(path) == version.LastWriteTimeUtc)
                        ready.Add(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { previous.Clear(); throw; }
        catch (UnauthorizedAccessException) { previous.Clear(); throw; }

        previous = current;
        return ready.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
