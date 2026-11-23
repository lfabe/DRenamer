using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DrawingRenamer;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly string settingsPath;
    public MainViewModel(string? settingsPath = null) =>
        this.settingsPath = settingsPath ?? Path.Combine(DataFolder, "settings.json");
    [ObservableProperty] public partial int ThemeIndex { get; set; }
    [ObservableProperty] public partial string SettingsMessage { get; set; } = "";
    public int LanguageIndex
    {
        get => Languages switch { "eng" => 2, "rus+eng" => 1, _ => 0 };
        set => Languages = value switch { 1 => "rus+eng", 2 => "eng", _ => "rus" };
    }

    public Task SaveSettingsAsync()
    {
        if (SeparatorError is { } error)
        {
            ReportSettings(error);
            return Task.CompletedTask;
        }
        return WorkAsync(async () =>
        {
            var settings = new AppSettings(ThemeIndex, SeparatorInput, OcrEnabled, Languages, TemplatePath);
            if (await SaveSettingsFileAsync(settings)) ReportSettings("Настройки сохранены.");
        });
    }

    public Task ResetSettingsAsync() => WorkAsync(async () =>
    {
        var defaults = new AppSettings();
        if (!await SaveSettingsFileAsync(defaults)) return;
        ApplySettings(defaults);
        ReportSettings("Настройки сброшены к значениям по умолчанию и сохранены.");
    });

    public Task LoadSettingsAsync() => WorkAsync(async () =>
    {
        AppSettings settings;
        try { settings = await Task.Run(() => AppSettingsStore.Load(settingsPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            ApplySettings(new());
            ReportSettings("Не удалось загрузить настройки. Используются значения по умолчанию.");
            return;
        }
        ApplySettings(settings);
        try { SetTemplates(settings.TemplatePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            SetTemplates(null);
            ReportSettings("Сохранённый файл шаблонов недоступен. Используются встроенные шаблоны.");
            return;
        }
        if (File.Exists(settingsPath)) ReportSettings("Настройки загружены.");
    });

    private void ApplySettings(AppSettings settings)
    {
        ThemeIndex = settings.ThemeIndex;
        SeparatorInput = settings.Separator;
        OcrEnabled = settings.OcrEnabled;
        Languages = settings.Languages;
        SetTemplates(null);
    }

    private async Task<bool> SaveSettingsFileAsync(AppSettings settings)
    {
        try
        {
            await Task.Run(() => AppSettingsStore.Save(settingsPath, settings));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ReportSettings("Не удалось сохранить настройки. Проверьте доступ к файлу настроек.");
            return false;
        }
    }

    private void ReportSettings(string message) => SettingsMessage = Message = message;
    partial void OnThemeIndexChanged(int value) => SettingsMessage = "";
    partial void OnOcrEnabledChanged(bool value) => SettingsMessage = "";
    partial void OnLanguagesChanged(string value) => SettingsMessage = "";
    private readonly DrawingAnalyzer analyzer = new();
    private readonly HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> individualFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> excludedFiles = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? cancellation;
    private string separator = "_";
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrawingRenamer");
    public RenameService Renamer { get; } = new(Path.Combine(DataFolder, "history.json"));
    public ObservableCollection<DocumentRow> Rows { get; } = [];
    public bool HasDocumentSources => folders.Count > 0 || individualFiles.Count > 0;
    public bool CanUndo { get; private set; }

    [ObservableProperty] public partial DocumentRow? Selected { get; set; }
    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial bool Recursive { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string Message { get; set; } = "Добавьте PDF-файлы или папку.";
    [ObservableProperty] public partial bool OcrEnabled { get; set; } = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageIndex))]
    public partial string Languages { get; set; } = "rus";
    [ObservableProperty] public partial string TemplateLabel { get; set; } = "Встроенные шаблоны ЕСКД";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SeparatorError))]
    public partial string SeparatorInput { get; set; } = "_";
    public string? SeparatorError => RenameService.SeparatorError(SeparatorInput);

    partial void OnSeparatorInputChanged(string value)
    {
        SettingsMessage = "";
        if (SeparatorError is not null) return;
        separator = value;
        foreach (var row in Rows) row.Separator = value;
    }
    public string? TemplatePath { get; private set; }
    private RecognitionOptions Options => new(OcrEnabled, Languages);

    public void SetTemplates(string? path)
    {
        if (path is not null)
        {
            path = Path.GetFullPath(path);
            TitleTemplate.Load(path);
        }
        TemplatePath = path;
        TemplateLabel = path is null ? "Встроенные шаблоны ЕСКД" : Path.GetFileName(path);
        SettingsMessage = "";
    }

    public async Task<RecognitionOptions> LoadRecognitionOptionsAsync(bool useDefaultsOnError = false)
    {
        var options = Options;
        if (TemplatePath is not { } path) return options;
        try
        {
            var custom = await Task.Run(() => TitleTemplate.Load(path));
            return options with { Templates = [.. TitleTemplate.Defaults, .. custom] };
        }
        catch (Exception ex) when (useDefaultsOnError && (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or System.Text.Json.JsonException))
        {
            return options;
        }
    }

    public Task AddDocumentsAsync(IEnumerable<string> files, params string[] directories)
    {
        if (Busy) return Task.CompletedTask;
        var pdfs = files.Where(IsPdf).Select(path => Path.GetFullPath(path)).ToArray();
        var sources = directories.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).ToArray();
        excludedFiles.ExceptWith(pdfs);
        individualFiles.UnionWith(pdfs);
        if (sources.Length > 0)
        {
            folders.UnionWith(sources);
            return RefreshDocumentsAsync();
        }

        var count = AddRows(pdfs);
        Message = pdfs.Length == 0 ? "Добавьте PDF-файлы или папки."
            : $"Добавлено: {count}. В очереди: {Rows.Count}.";
        return Task.CompletedTask;
    }

    public void RemoveDocuments(IEnumerable<DocumentRow> selection)
    {
        if (Busy) return;
        var targets = selection.ToHashSet();
        var active = Selected;
        var activeIndex = active is null ? -1 : Rows.IndexOf(active);
        var nextIndex = activeIndex;
        DocumentRow? removed = null;
        var count = 0;
        for (var index = Rows.Count - 1; index >= 0; index--)
        {
            var row = Rows[index];
            if (!targets.Contains(row)) continue;
            if (index < activeIndex) nextIndex--;
            individualFiles.Remove(row.Source);
            excludedFiles.Add(row.Source);
            Rows.RemoveAt(index);
            removed = row;
            count++;
        }

        if (removed is null) return;
        if (activeIndex >= 0 && active is not null && targets.Contains(active))
            Selected = Rows.Count == 0 ? null : Rows[Math.Min(nextIndex, Rows.Count - 1)];
        Message = count == 1 ? $"Удалено из списка: {removed.OriginalName}." : $"Удалено из списка: {count}.";
    }

    private int AddRows(IEnumerable<string> paths)
    {
        var existing = Rows.Select(r => r.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var path in paths)
            if (existing.Add(path))
            {
                Rows.Add(new(path) { Separator = separator });
                count++;
            }
        Selected ??= Rows.FirstOrDefault();
        return count;
    }

    public async Task RefreshDocumentsAsync()
    {
        if (Busy) return;
        var recursiveSearch = Recursive;
        await WorkAsync(async () =>
        {
            CanUndo = await Task.Run(Renamer.HasUndoableOperations);
            if (!HasDocumentSources) return;
            Message = "Обновление списка…";
            var paths = await Task.Run(() =>
            {
                var discovered = individualFiles.Where(File.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = recursiveSearch,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                };
                foreach (var folder in folders)
                {
                    try
                    {
                        discovered.UnionWith(Directory.EnumerateFiles(folder, "*", options)
                            .Where(path => IsPdf(path) && !excludedFiles.Contains(path)));
                    }
                    catch (DirectoryNotFoundException) { }
                }
                return discovered;
            });
            var selected = Selected;
            foreach (var row in Rows.Where(row => !paths.Contains(row.Source)).ToArray())
                Rows.Remove(row);
            var reviewed = Rows.Where(row => row.SourceHash is not null).ToArray();
            var versions = await Task.Run(() => reviewed.Select(row => ReadVersion(row.Source)).ToArray());
            for (var i = 0; i < reviewed.Length; i++) reviewed[i].ObserveVersion(versions[i]);
            AddRows(paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            Selected = selected is not null && Rows.Contains(selected) ? selected : Rows.FirstOrDefault();
            Message = $"Найдено документов: {Rows.Count}.";
        });
    }

    private static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private Task AnalyzeAsync() => AnalyzeRowsAsync(Rows.Where(r => r.Included).ToArray(), false);

    public Task AnalyzeDocumentAsync(DocumentRow row) => Rows.Contains(row)
        ? AnalyzeRowsAsync([row], true) : Task.CompletedTask;

    private async Task AnalyzeRowsAsync(DocumentRow[] batch, bool force)
    {
        if (Busy || batch.Length == 0) return;
        using var operation = new CancellationTokenSource();
        cancellation = operation;
        Progress = 0;
        try
        {
            await WorkAsync(async () =>
            {
                RecognitionOptions options;
                try { options = await LoadRecognitionOptionsAsync(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    InvalidDataException or System.Text.Json.JsonException)
                {
                    Message = "Не удалось загрузить выбранный JSON. Проверьте файл или сбросьте шаблоны в настройках.";
                    return;
                }
                var processed = 0;
                for (var index = 0; index < batch.Length; index++)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    var row = batch[index];
                    if (!force && row.Confirmed && row.ObserveVersion(await Task.Run(() => ReadVersion(row.Source))))
                    {
                        Progress = 100d * (index + 1) / batch.Length;
                        continue;
                    }
                    Message = $"Анализ {index + 1} из {batch.Length} · {row.OriginalName}";
                    var result = await Task.Run(() => analyzer.Analyze(row.Source, options, operation.Token));
                    row.Apply(result);
                    processed++;
                    Progress = 100d * (index + 1) / batch.Length;
                }
                Message = $"Распознано: {processed}. Сохранено подтверждённых: {batch.Length - processed}.";
            });
        }
        finally { cancellation = null; }
    }

    [RelayCommand]
    private void Cancel() => cancellation?.Cancel();

    public async Task ReadAsync(DocumentRow row, int page, FieldRegions regions, int turn)
    {
        var options = Options;
        await WorkAsync(async () =>
        {
            var (hash, candidate) = await Task.Run(() =>
            {
                using var input = File.OpenRead(row.Source);
                var version = RenameService.Hash(input);
                return (version, row.SourceHash is { } previous && previous != version ? null :
                    analyzer.ReadRegions(row.Source, page, regions, turn, options, default));
            });
            if (!row.ObserveVersion(hash) || candidate is null) { Message = row.Diagnostic; return; }
            row.Use(candidate);
            Message = "Проверьте результат и подтвердите значения.";
        });
    }

    private static string? ReadVersion(string path)
    {
        try { return RenameService.Hash(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public Task ConfirmAsync(DocumentRow row) => WorkAsync(async () =>
        Message = await ConfirmRowAsync(row) ? "Значения подтверждены." : row.Diagnostic);

    public Task ConfirmSelectedAsync(IEnumerable<DocumentRow> selection)
    {
        var selected = selection.ToHashSet();
        var batch = Rows.Where(selected.Contains).ToArray();
        if (batch.Length == 0) return Task.CompletedTask;

        return WorkAsync(async () =>
        {
            var confirmed = 0;
            Progress = 0;
            for (var i = 0; i < batch.Length; i++)
            {
                var row = batch[i];
                Message = $"Подтверждение {i + 1} из {batch.Length} · {row.OriginalName}";
                if (await ConfirmRowAsync(row)) confirmed++;
                Progress = 100d * (i + 1) / batch.Length;
            }
            Message = $"Подтверждено: {confirmed} из {batch.Length}. Требуют проверки: {batch.Length - confirmed}.";
        });
    }

    private static async Task<bool> ConfirmRowAsync(DocumentRow row)
    {
        var hash = await Task.Run(() => ReadVersion(row.Source));
        return hash is null ? row.ObserveVersion(null) : row.Confirm(hash);
    }

    public async Task MoveAsync(IReadOnlyList<RenameRequest>? requests, IReadOnlyList<MoveResult>? skipped = null,
        string? destination = null, bool removeOriginals = false)
    {
        if (requests is not null && SeparatorError is { } error)
        {
            Message = error;
            return;
        }
        await WorkAsync(async () =>
        {
            IReadOnlyList<MoveResult> results;
            string? journalWarning = null;
            try
            {
                results = await Task.Run(() => requests is null
                    ? Renamer.Undo() : Renamer.Execute(requests, skipped, destination, removeOriginals));
            }
            catch (JournalWriteException ex)
            {
                results = ex.Completed;
                journalWarning = ex.Message;
            }
            finally
            {
                CanUndo = await Task.Run(Renamer.HasUndoableOperations);
            }
            foreach (var result in results)
            {
                var oldPath = result.Status == MoveStatus.Undone ? result.Target : result.Source;
                var row = Rows.FirstOrDefault(r => string.Equals(r.Source, oldPath, StringComparison.OrdinalIgnoreCase));
                if (row is null && requests is null)
                    row = Rows.FirstOrDefault(r => string.Equals(r.Source, result.Target, StringComparison.OrdinalIgnoreCase));
                if (result.Status is MoveStatus.Moved or MoveStatus.Undone)
                {
                    var newPath = result.Status == MoveStatus.Undone ? result.Source : result.Target!;
                    if (oldPath is not null) individualFiles.Remove(oldPath);
                    individualFiles.Add(newPath);
                    if (row is not null)
                    {
                        if (result.PageRotations is { Count: > 0 } rotations && result.ResultHash is { } hash)
                            row.ApplySavedRotations(rotations, hash);
                        row.Source = newPath;
                    }
                }
                if (row is null) continue;
                if (result.SourceChanged) row.InvalidateVersion();
                row.Diagnostic = result.Message;
                row.Status = result.Status switch
                {
                    MoveStatus.Moved => "Переименован",
                    MoveStatus.Copied => "Копия сохранена",
                    MoveStatus.Undone => "Восстановлен",
                    MoveStatus.CopyRemoved => "Копия удалена",
                    MoveStatus.Skipped => "Пропущен",
                    _ => "Ошибка"
                };
                if (result.Status is MoveStatus.Moved or MoveStatus.Undone or MoveStatus.Copied or MoveStatus.CopyRemoved)
                    row.Included = false;
            }
            Message = results.Count == 0 ? "Нет выполненных операций." :
                $"Выполнено: {results.Count(r => r.Status is MoveStatus.Moved or MoveStatus.Undone or MoveStatus.Copied or MoveStatus.CopyRemoved)} из {results.Count}.";
            if (journalWarning is not null) Message = $"{journalWarning} {Message}";
        });
    }

    private async Task WorkAsync(Func<Task> work)
    {
        if (Busy) return;
        Busy = true;
        try { await work(); }
        catch (OperationCanceledException) { Message = "Анализ отменён. Готовые результаты сохранены в списке."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidOperationException or ArgumentException or DllNotFoundException or TypeInitializationException)
        {
            Message = $"Операция прервана ({ex.GetType().Name}). Проверьте доступ к файлам и журнал.";
        }
        finally { Busy = false; }
    }
}
