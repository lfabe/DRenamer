using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DrawingRenamer;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DrawingAnalyzer analyzer = new();
    private readonly HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> individualFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> excludedFiles = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? cancellation;
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
    [ObservableProperty] public partial string Languages { get; set; } = "rus";
    [ObservableProperty] public partial string TemplateLabel { get; set; } = "Встроенные шаблоны ЕСКД";
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

    public void RemoveDocument(DocumentRow row)
    {
        if (Busy) return;
        var index = Rows.IndexOf(row);
        if (index < 0) return;

        var wasSelected = Selected == row;
        individualFiles.Remove(row.Source);
        excludedFiles.Add(row.Source);
        Rows.RemoveAt(index);
        if (wasSelected)
            Selected = Rows.Count == 0 ? null : Rows[Math.Min(index, Rows.Count - 1)];
        Message = $"Удалено из списка: {row.OriginalName}.";
    }

    private int AddRows(IEnumerable<string> paths)
    {
        var existing = Rows.Select(r => r.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var path in paths)
            if (existing.Add(path))
            {
                Rows.Add(new(path));
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
            AddRows(paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            Selected = selected is not null && Rows.Contains(selected) ? selected : Rows.FirstOrDefault();
            Message = $"Найдено документов: {Rows.Count}.";
        });
    }

    private static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        if (Busy || Rows.Count == 0) return;
        using var operation = new CancellationTokenSource();
        cancellation = operation;
        var batch = Rows.Where(r => r.Included).ToArray();
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
                for (var index = 0; index < batch.Length; index++)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    var row = batch[index];
                    Message = $"Анализ {index + 1} из {batch.Length} · {row.OriginalName}";
                    var result = await Task.Run(() => analyzer.Analyze(row.Source, options, operation.Token));
                    row.Apply(result);
                    Progress = 100d * (index + 1) / batch.Length;
                }
                Message = batch.Length == 0 ? "Отметьте файлы для анализа." :
                    $"Обработано: {batch.Length}. Готово: {batch.Count(r => r.Confirmed)}.";
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
            var candidate = await Task.Run(() => analyzer.ReadRegions(row.Source, page, regions, turn, options, default));
            row.Use(candidate);
            Message = "Проверьте результат и подтвердите значения.";
        });
    }

    public async Task MoveAsync(IReadOnlyList<RenameRequest>? requests, IReadOnlyList<MoveResult>? skipped = null,
        string? destination = null, bool removeOriginals = false)
    {
        await WorkAsync(async () =>
        {
            IReadOnlyList<MoveResult> results;
            try
            {
                results = await Task.Run(() => requests is null
                    ? Renamer.Undo() : Renamer.Execute(requests, skipped, destination, removeOriginals));
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
                        if (result.PageRotations is { } rotations) row.ApplySavedRotations(rotations);
                        row.Source = newPath;
                    }
                }
                if (row is null) continue;
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
