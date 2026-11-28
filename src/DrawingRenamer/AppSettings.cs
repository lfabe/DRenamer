using System.Text.Json;

namespace DrawingRenamer;

internal sealed record AppSettings(int ThemeIndex = 0, string Separator = "_", bool OcrEnabled = true,
    string Languages = "rus", string? TemplatePath = null, string? ScannerFolder = null, bool ScannerWatchEnabled = false,
    DateTimeOffset? ScannerCreatedAfter = null, bool SplitScanBatches = false, bool AutoPrepareScans = false);

internal static class AppSettingsStore
{
    internal static AppSettings Load(string path)
    {
        if (!File.Exists(path)) return new();
        using var stream = File.OpenRead(path);
        if (stream.Length > 64 * 1024) throw new InvalidDataException("Файл настроек слишком велик.");
        var settings = JsonSerializer.Deserialize<AppSettings>(stream) ?? new();
        return settings with
        {
            ThemeIndex = settings.ThemeIndex is >= 0 and <= 2 ? settings.ThemeIndex : 0,
            Separator = RenameService.SeparatorError(settings.Separator) is null ? settings.Separator : "_",
            Languages = settings.Languages is "rus" or "rus+eng" or "eng" ? settings.Languages : "rus"
        };
    }

    internal static void Save(string path, AppSettings settings)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
