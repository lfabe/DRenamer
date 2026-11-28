using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DrawingRenamer;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly string settingsPath;
    private readonly DiagnosticLog? log;
    public MainViewModel(string? settingsPath = null, DiagnosticLog? log = null)
    {
        this.settingsPath = settingsPath ?? Path.Combine(DataFolder, "settings.json");
        this.log = log;
        analyzer = new(log);
        Renamer = new(Path.Combine(DataFolder, "history.json"), log);
    }
    [ObservableProperty] public partial int ThemeIndex { get; set; }
    [ObservableProperty] public partial string SettingsMessage { get; set; } = "";
    [ObservableProperty] public partial bool ScannerWatchEnabled { get; set; }
    [ObservableProperty] public partial bool SplitScanBatches { get; set; }
    [ObservableProperty] public partial bool AutoPrepareScans { get; set; }
    [ObservableProperty] public partial DateTimeOffset? ScannerCreatedAfter { get; set; }
    public DateTimeOffset ScannerStartDate
    {
        get => (ScannerCreatedAfter ?? DateTimeOffset.Now).ToLocalTime();
        set => SetScannerStart(value.Date + ScannerStartTime);
    }
    public TimeSpan ScannerStartTime
    {
        get => ScannerStartDate.TimeOfDay;
        set => SetScannerStart(ScannerStartDate.Date + value);
    }
    public bool HasScannerFolder => scannerInbox is not null;
    public string ScannerFolderLabel => scannerInbox?.Folder ?? "Папка не выбрана";
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
            var settings = new AppSettings(ThemeIndex, SeparatorInput, OcrEnabled, Languages, TemplatePath,
                scannerInbox?.Folder, ScannerWatchEnabled, ScannerCreatedAfter, SplitScanBatches, AutoPrepareScans);
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
            log?.Warning("Settings", $"Load failed; using defaults. Path={settingsPath}", ex);
            ApplySettings(new());
            ReportSettings("Не удалось загрузить настройки. Используются значения по умолчанию.");
            return;
        }
        ApplySettings(settings);
        try { SetTemplates(settings.TemplatePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            log?.Warning("Templates", $"Saved template unavailable; using defaults. Path={settings.TemplatePath}", ex);
            SetTemplates(null);
            ReportSettings("Сохранённый файл шаблонов недоступен. Используются встроенные шаблоны.");
            return;
        }
        if (File.Exists(settingsPath)) ReportSettings("Настройки загружены.");
        log?.Info("Settings", $"Loaded. Path={settingsPath}; OCR={OcrEnabled}; Languages={Languages}; ScannerWatch={ScannerWatchEnabled}; SplitBatches={SplitScanBatches}");
    });

    private void ApplySettings(AppSettings settings)
    {
        ThemeIndex = settings.ThemeIndex;
        SeparatorInput = settings.Separator;
        OcrEnabled = settings.OcrEnabled;
        Languages = settings.Languages;
        SetTemplates(null);
        SetScannerFolder(settings.ScannerFolder);
        ScannerWatchEnabled = HasScannerFolder && settings.ScannerWatchEnabled;
        if (settings.ScannerCreatedAfter is not null || !ScannerWatchEnabled)
            ScannerCreatedAfter = settings.ScannerCreatedAfter;
        SplitScanBatches = settings.SplitScanBatches;
        AutoPrepareScans = settings.AutoPrepareScans;
    }

    private async Task<bool> SaveSettingsFileAsync(AppSettings settings)
    {
        try
        {
            await Task.Run(() => AppSettingsStore.Save(settingsPath, settings));
            log?.Info("Settings", $"Saved. Path={settingsPath}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log?.Error("Settings", $"Save failed. Path={settingsPath}", ex);
            ReportSettings("Не удалось сохранить настройки. Проверьте доступ к файлу настроек.");
            return false;
        }
    }

    private void ReportSettings(string message) => SettingsMessage = Message = message;
    partial void OnThemeIndexChanged(int value) => SettingsMessage = "";
    partial void OnOcrEnabledChanged(bool value) => SettingsMessage = "";
    partial void OnLanguagesChanged(string value) => SettingsMessage = "";
    partial void OnAutoPrepareScansChanged(bool value) => SettingsMessage = "";
    partial void OnSplitScanBatchesChanged(bool value)
    {
        SettingsMessage = "";
        if (!value)
        {
            manualMergeAssignments.Clear();
            foreach (var row in Rows) row.MergeEligible = false;
            RefreshMergeProposals();
        }
    }
    partial void OnScannerWatchEnabledChanged(bool value)
    {
        if (value) ScannerCreatedAfter = DateTimeOffset.Now;
        SettingsMessage = "";
    }

    private void SetScannerStart(DateTime local)
    {
        if (TimeZoneInfo.Local.IsInvalidTime(local)) return;
        ScannerCreatedAfter = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        SettingsMessage = "";
    }

    private readonly DrawingAnalyzer analyzer;
    private readonly HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> individualFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> excludedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScannerInbox.FileVersion> handledBatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DocumentRow, string> manualMergeAssignments = [];
    private ScannerInbox? scannerInbox;
    private bool checkingScanner;
    private bool scannerUnavailable;
    private CancellationTokenSource? cancellation;
    private string separator = "_";
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DrawingRenamer");
    public RenameService Renamer { get; internal init; }
    public ObservableCollection<DocumentRow> Rows { get; } = [];
    [ObservableProperty] internal partial IReadOnlyList<MergeProposal> MergeProposals { get; set; } = [];
    public bool HasDocumentSources => folders.Count > 0 || individualFiles.Count > 0;
    public bool CanUndo { get; private set; }
    public bool IsAnalyzing => cancellation is not null;

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
        log?.Info("Templates", $"Selected. Path={path ?? "built-in"}");
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
            log?.Warning("Templates", $"Load failed; using defaults. Path={path}", ex);
            return options;
        }
    }

    public Task AddDocumentsAsync(IEnumerable<string> files, params string[] directories)
    {
        if (Busy) return Task.CompletedTask;
        var pdfs = files.Where(IsPdf).Select(path => Path.GetFullPath(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = directories.Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).ToArray();
        log?.Info("Import", $"Requested. Files={pdfs.Count}; Folders={sources.Length}; Recursive={Recursive}; SplitBatches={SplitScanBatches}");
        excludedFiles.ExceptWith(pdfs);
        if (SplitScanBatches && sources.Length == 0)
            return WorkAsync(async () =>
            {
                var (prepared, warning) = await PrepareBatchFilesAsync(pdfs, explicitlyAdded: pdfs);
                individualFiles.UnionWith(prepared);
                var count = AddRows(prepared);
                Message = $"Добавлено: {count}. В очереди: {Rows.Count}. {warning}".TrimEnd();
            });
        individualFiles.UnionWith(pdfs);
        if (sources.Length > 0)
        {
            folders.UnionWith(sources);
            return RefreshDocumentsAsync(pdfs);
        }

        var count = AddRows(pdfs);
        log?.Info("Import", $"Added={count}; Queue={Rows.Count}");
        Message = pdfs.Count == 0 ? "Добавьте PDF-файлы или папки."
            : $"Добавлено: {count}. В очереди: {Rows.Count}.";
        return Task.CompletedTask;
    }

    public void SetScannerFolder(string? path)
    {
        if (!Path.IsPathFullyQualified(path ?? "")) path = null;
        if (string.Equals(scannerInbox?.Folder, path, StringComparison.OrdinalIgnoreCase)) return;
        scannerInbox = path is null ? null : new(path);
        scannerUnavailable = false;
        log?.Info("Scanner", $"Folder selected. Path={path}");
        SettingsMessage = "";
        OnPropertyChanged(nameof(HasScannerFolder));
        OnPropertyChanged(nameof(ScannerFolderLabel));
    }

    internal async Task CheckScannerFolderAsync(Func<bool> canStart)
    {
        if (checkingScanner || scannerInbox is not { } inbox || !CanScan()) return;
        checkingScanner = true;
        try
        {
            var split = SplitScanBatches;
            var autoPrepare = AutoPrepareScans;
            var cutoff = ScannerCreatedAfter?.UtcDateTime;
            var handled = split ? new Dictionary<string, ScannerInbox.FileVersion>(handledBatches, StringComparer.OrdinalIgnoreCase) : null;
            var ignored = Rows.Where(row => split && !autoPrepare || !IsPending(row)).Select(row => row.Source)
                .Concat(excludedFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var paths = await Task.Run(() => inbox.FindReadyFiles(ignored, cutoff, handled));
            if (scannerUnavailable) log?.Info("Scanner", $"Folder available again. Path={inbox.Folder}");
            scannerUnavailable = false;
            if (!CanScan() || scannerInbox != inbox || split != SplitScanBatches || autoPrepare != AutoPrepareScans ||
                cutoff != ScannerCreatedAfter?.UtcDateTime || paths.Length == 0) return;

            var ready = paths.Where(path => !excludedFiles.Contains(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            log?.Info("Scanner", $"Ready files={ready.Count}; Folder={inbox.Folder}");
            if (split)
            {
                string[] prepared = [];
                await WorkAsync(async () =>
                {
                    var (pages, warning) = await PrepareBatchFilesAsync(ready);
                    prepared = pages;
                    individualFiles.UnionWith(prepared);
                    var count = AddRows(prepared);
                    if (count > 0 || warning is not null)
                        Message = $"Добавлено: {count}. Нажмите «Распознать». {warning}".TrimEnd();
                });
                if (autoPrepare)
                    await AnalyzeRowsAsync(Rows.Where(row => prepared.Contains(row.Source, StringComparer.OrdinalIgnoreCase) &&
                        IsPending(row)).ToArray(), force: false, requireReview: true, proposeMerges: true, prepareScans: true);
                return;
            }
            AddRows(ready);
            var batch = Rows.Where(row => ready.Contains(row.Source) && IsPending(row)).ToArray();
            individualFiles.UnionWith(batch.Select(row => row.Source));
            await AnalyzeRowsAsync(batch, force: false, requireReview: true, prepareScans: autoPrepare);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            if (!scannerUnavailable) log?.Warning("Scanner", $"Folder unavailable. Path={inbox.Folder}", ex);
            scannerUnavailable = true;
            if (CanScan() && scannerInbox == inbox) Message = "Папка сканера недоступна. Ожидание подключения…";
        }
        finally { checkingScanner = false; }

        bool CanScan() => ScannerWatchEnabled && (OcrEnabled || SplitScanBatches || AutoPrepareScans) && !Busy && canStart();
        static bool IsPending(DocumentRow row) => row.Included && row.Status == "В очереди";
    }

    public void RemoveDocuments(IEnumerable<DocumentRow> selection)
    {
        if (Busy) return;
        var targets = selection.SelectMany(row => row.MergeProposal is { } header
            ? MergeProposals.FirstOrDefault(group => SameMergeKey(group.Key, header.Key))?.Pages ?? [] : [row]).ToHashSet();
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
        RefreshMergeProposals();
        if (activeIndex >= 0 && active is not null && targets.Contains(active))
            Selected = Rows.Count == 0 ? null : Rows[Math.Min(nextIndex, Rows.Count - 1)];
        Message = count == 1 ? $"Удалено из списка: {removed.OriginalName}." : $"Удалено из списка: {count}.";
        log?.Info("Queue", $"Removed={count}; Queue={Rows.Count}");
    }

    internal Task SplitDocumentAsync(DocumentRow row) => WorkAsync(async () =>
    {
        if (!Rows.Contains(row)) return;
        var (paths, warning) = await PrepareBatchFilesAsync([row.Source], manual: true);
        if (paths.Length < 2)
        {
            Message = warning ?? "В PDF только одна страница.";
            return;
        }

        individualFiles.Remove(row.Source);
        excludedFiles.Add(row.Source);
        excludedFiles.ExceptWith(paths);
        individualFiles.UnionWith(paths);
        var index = Rows.IndexOf(row);
        Rows.RemoveAt(index);
        var existing = Rows.Select(item => item.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        DocumentRow? first = null;
        foreach (var path in paths)
        {
            if (!existing.Add(path)) continue;
            var page = new DocumentRow(path) { Separator = separator, Included = row.Included };
            Rows.Insert(index++, page);
            first ??= page;
        }
        RefreshMergeProposals();
        if (first is not null) Selected = first;
        Message = $"Разбито на страницы: {paths.Length}. {warning}".TrimEnd();
    });

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

    public Task RefreshDocumentsAsync() => RefreshDocumentsAsync(null);

    private async Task RefreshDocumentsAsync(IReadOnlySet<string>? explicitlyAdded)
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
                    catch (DirectoryNotFoundException ex) { log?.Warning("Import", $"Folder missing. Path={folder}", ex); }
                }
                return discovered;
            });
            string? warning = null;
            if (SplitScanBatches)
            {
                var existing = Rows.Select(row => row.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var originals = paths.Where(path => !existing.Contains(path)).ToArray();
                var prepared = await PrepareBatchFilesAsync(originals, explicitlyAdded: explicitlyAdded);
                paths.ExceptWith(originals);
                individualFiles.ExceptWith(originals);
                paths.UnionWith(prepared.Paths);
                individualFiles.UnionWith(prepared.Paths);
                warning = prepared.Warning;
            }
            var selected = Selected;
            foreach (var row in Rows.Where(row => !paths.Contains(row.Source)).ToArray())
                Rows.Remove(row);
            var reviewed = Rows.Where(row => row.SourceHash is not null).ToArray();
            var versions = await Task.Run(() => reviewed.Select(row => ReadVersion(row.Source)).ToArray());
            for (var i = 0; i < reviewed.Length; i++) reviewed[i].ObserveVersion(versions[i]);
            AddRows(paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            RefreshMergeProposals();
            Selected = selected is not null && Rows.Contains(selected) ? selected : Rows.FirstOrDefault();
            Message = $"Найдено документов: {Rows.Count}. {warning}".TrimEnd();
            log?.Info("Import", $"Refreshed. Queue={Rows.Count}; Recursive={recursiveSearch}");
        });
    }

    private async Task<(string[] Paths, string? Warning)> PrepareBatchFilesAsync(IEnumerable<string> paths,
        bool manual = false, IReadOnlySet<string>? explicitlyAdded = null)
    {
        var files = paths.ToArray();
        var result = await Task.Run(() =>
        {
            var ready = new List<string>();
            string? warning = null;
            foreach (var path in files)
            {
                var keepOriginal = explicitlyAdded?.Contains(path) == true;
                try
                {
                    using var input = File.OpenRead(path);
                    var version = new ScannerInbox.FileVersion(new FileInfo(path));
                    if (!manual)
                    {
                        var alreadySplit = handledBatches.TryGetValue(path, out var previous) && previous == version;
                        if (!alreadySplit)
                        {
                            var receipt = BatchReceipt(path);
                            alreadySplit = File.Exists(receipt) && File.ReadAllText(receipt) == RenameService.Hash(input);
                        }
                        if (alreadySplit)
                        {
                            handledBatches[path] = version;
                            if (keepOriginal) ready.Add(path);
                            continue;
                        }
                    }
                    log?.Info("Split", $"Started. Source={path}; Manual={manual}");
                    var pages = PdfRenderer.SplitPages(path);
                    log?.Info("Split", $"Completed. Source={path}; Pages={pages.Length}");
                    ready.AddRange(pages);
                    if (pages.Length > 1)
                    {
                        try { RememberBatchFile(path, input); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            log?.Warning("Split", $"Receipt save failed. Source={path}", ex);
                            warning = $"Не удалось сохранить отметку о разделении {Path.GetFileName(path)} для следующего сеанса.";
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    log?.Error("Split", $"Failed. Source={path}", ex);
                    if (keepOriginal) ready.Add(path);
                    warning = $"Не удалось разделить {Path.GetFileName(path)}. {ex.Message}";
                }
            }
            return (ready, warning);
        });
        return (result.ready.Where(path => manual || !excludedFiles.Contains(path)).ToArray(), result.warning);
    }

    private string BatchReceipt(string path)
    {
        var key = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "batch-receipts", Convert.ToHexString(key));
    }

    private void RememberBatchFile(string path, Stream input)
    {
        handledBatches[path] = new(new FileInfo(path));
        var receipt = BatchReceipt(path);
        Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
        input.Position = 0;
        File.WriteAllText(receipt, RenameService.Hash(input));
    }

    internal void RefreshMergeProposals()
    {
        var eligible = Rows.Where(row => row.MergeEligible && row.SourceHash is not null).ToArray();
        var natural = eligible.Where(row => !manualMergeAssignments.ContainsKey(row) && !string.IsNullOrWhiteSpace(row.Designation))
            .ToLookup(NaturalMergeKey, StringComparer.OrdinalIgnoreCase);
        foreach (var (row, key) in manualMergeAssignments.ToArray())
            if (!Rows.Contains(row) || !natural.Contains(key)) manualMergeAssignments.Remove(row);
        var attached = eligible.Where(manualMergeAssignments.ContainsKey)
            .ToLookup(row => manualMergeAssignments[row], StringComparer.OrdinalIgnoreCase);
        var proposals = natural.Select(group =>
            {
                var first = group.First();
                return new MergeProposal(Path.GetDirectoryName(first.Source)!, first.Designation.Trim(),
                    [..group, ..attached[group.Key]]);
            }).Where(group => group.Pages.Length > 1).ToArray();
        var grouped = proposals.SelectMany(proposal => proposal.Pages).ToHashSet();
        foreach (var row in Rows)
        {
            row.InMergeProposal = grouped.Contains(row);
            row.ManuallyAssigned = manualMergeAssignments.ContainsKey(row);
        }
        if (proposals.Length > 0 || MergeProposals.Count > 0) MergeProposals = proposals;
    }

    private static string NaturalMergeKey(DocumentRow row) =>
        MergeProposal.MakeKey(Path.GetDirectoryName(row.Source)!, row.Designation);

    internal void DetachMergePage(DocumentRow row)
    {
        if (Busy || !manualMergeAssignments.Remove(row)) return;
        RefreshMergeProposals();
        Message = "Лист исключён из ручного состава группы.";
    }

    internal Task AttachMergePagesAsync(MergeProposal proposal, IEnumerable<DocumentRow> selection) => WorkAsync(async () =>
    {
        var current = MergeProposals.FirstOrDefault(group => SameMergeKey(group.Key, proposal.Key));
        var pages = selection.Distinct().ToArray();
        if (current is null || pages.Length == 0 || pages.Any(row => !Rows.Contains(row) || row.InMergeProposal)) return;

        foreach (var row in pages)
        {
            string? hash;
            try
            {
                hash = await Task.Run(() =>
                {
                    using var input = File.OpenRead(row.Source);
                    return new PdfRenderer().CountPages(input, default) == 1 ? RenameService.Hash(input) : null;
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                log?.Error("Merge", $"Cannot attach page. Source={row.Source}", ex);
                Message = $"Не удалось добавить {row.OriginalName}. {ex.Message}";
                return;
            }
            if (hash is null)
            {
                Message = $"{row.OriginalName} содержит несколько страниц. В группу можно добавить отдельные листы.";
                return;
            }
            if (!row.ObserveVersion(hash))
            {
                Message = $"{row.OriginalName} изменился. Распознайте его повторно.";
                return;
            }
        }

        foreach (var row in pages)
        {
            row.MergeEligible = true;
            manualMergeAssignments[row] = current.Key;
        }
        RefreshMergeProposals();
        Message = $"Добавлено листов: {pages.Length}. Проверьте порядок объединения.";
    });

    private static bool SameMergeKey(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    internal Task MergeAsync(MergeProposal proposal) => WorkAsync(async () =>
    {
        var current = MergeProposals.FirstOrDefault(group => SameMergeKey(group.Key, proposal.Key));
        if (current is null || !current.CanMerge || current.FileName is not { } fileName || SeparatorError is not null)
        {
            Message = "Укажите корректное имя и номера листов без пропусков или повторов.";
            return;
        }
        if (current.Pages.Any(row => !Rows.Contains(row) || !row.MergeEligible || row.SourceHash is null ||
            !SameMergeKey(manualMergeAssignments.TryGetValue(row, out var key) ? key : NaturalMergeKey(row), current.Key)))
        {
            RefreshMergeProposals();
            Message = "Состав группы изменился. Проверьте листы и выберите предложение заново.";
            return;
        }

        var ordered = current.Ordered;
        var output = Path.Combine(current.Folder, fileName);
        var inputs = ordered.Select(row => (row.Source, row.SourceHash!, row.PageRotations.GetValueOrDefault(1),
            row.PageAlignments.TryGetValue(1, out var alignment) ? (PageAlignment?)alignment : null)).ToArray();
        log?.Info("Merge", $"Started. Target={output}; Pages={inputs.Length}; Sources={string.Join("; ", ordered.Select(row => row.Source))}");
        try { await Task.Run(() => PdfRenderer.MergePages(output, inputs)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log?.Error("Merge", $"Failed. Target={output}", ex);
            Message = $"Не удалось объединить листы. {ex.Message}";
            return;
        }

        string? warning = null;
        try
        {
            await Task.Run(() =>
            {
                using var input = File.OpenRead(output);
                RememberBatchFile(output, input);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Warning("Merge", $"Receipt save failed. Target={output}", ex);
            warning = "Не удалось сохранить отметку о готовом PDF для следующего сеанса.";
        }

        foreach (var row in ordered)
        {
            excludedFiles.Add(row.Source);
            individualFiles.Remove(row.Source);
            Rows.Remove(row);
        }
        individualFiles.Add(output);
        var merged = new DocumentRow(output)
        {
            Designation = current.Designation,
            DocumentName = current.NamedSheet!.DocumentName,
            Included = false,
            Status = "Объединён",
            Diagnostic = "Итоговый PDF сохранён."
        };
        Rows.Add(merged);
        RefreshMergeProposals();
        Selected = merged;
        Message = $"Объединено листов: {ordered.Length}. Создан файл {fileName}. {warning}".TrimEnd();
        log?.Info("Merge", $"Completed. Target={output}; Pages={ordered.Length}");
    });

    private static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private Task AnalyzeAsync() => AnalyzeRowsAsync(Rows.Where(r => r.Included).ToArray(), false,
        proposeMerges: SplitScanBatches, prepareScans: AutoPrepareScans);

    public Task AnalyzeDocumentAsync(DocumentRow row) => Rows.Contains(row)
        ? AnalyzeRowsAsync([row], true, proposeMerges: SplitScanBatches && row.InMergeProposal,
            prepareScans: AutoPrepareScans) : Task.CompletedTask;

    private async Task AnalyzeRowsAsync(DocumentRow[] batch, bool force, bool requireReview = false,
        bool proposeMerges = false, bool prepareScans = false)
    {
        if (Busy || batch.Length == 0) return;
        log?.Info("Analysis", $"Started. Files={batch.Length}; Force={force}; OCR={OcrEnabled}; Languages={Languages}; Templates={TemplatePath ?? "built-in"}");
        using var operation = new CancellationTokenSource();
        cancellation = operation;
        analysisClock.Restart();
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
                    log?.Error("Templates", $"Analysis cannot load templates. Path={TemplatePath}", ex);
                    Message = "Не удалось загрузить выбранный JSON. Проверьте файл или сбросьте шаблоны в настройках.";
                    return;
                }
                var savedPageAlignments = await Task.Run(Renamer.SavedPageAlignments, operation.Token);
                var processed = 0;
                for (var index = 0; index < batch.Length; index++)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    var row = batch[index];
                    var keepConfirmed = !force && row.Confirmed &&
                        row.ObserveVersion(await Task.Run(() => ReadVersion(row.Source)));
                    if (keepConfirmed && row.AvailableAlignments is not null)
                    {
                        Progress = 100d * (index + 1) / batch.Length;
                        continue;
                    }
                    Message = $"Анализ {index + 1} из {batch.Length} · {row.OriginalName}";
                    var result = await Task.Run(() => analyzer.Analyze(row.Source,
                        options with { ReadSheetNumber = proposeMerges, DetectPageAlignment = true,
                            SavedPageAlignments = savedPageAlignments }, operation.Token));
                    (Dictionary<int, PageAlignment> Edits, string Message)? prepared =
                        prepareScans && result.Status != RecognitionStatus.Error && result.SourceHash is not null ? await Task.Run(() =>
                        PrepareScanPages(row.Source, result, operation.Token), operation.Token) : null;
                    operation.Token.ThrowIfCancellationRequested();
                    if (requireReview && result.Status == RecognitionStatus.Ready)
                        result = result with { Status = RecognitionStatus.Review, Diagnostic = "Проверьте обе графы и подтвердите значения." };
                    if (keepConfirmed && (result.SourceHash is null || result.SourceHash == row.SourceHash))
                        row.SetAvailableAlignments(result.PageAlignments);
                    else row.Apply(result);
                    if (prepared is { } preparation)
                    {
                        row.PendingPageCrops.Clear();
                        foreach (var (number, alignment) in preparation.Edits)
                        {
                            var leveling = alignment with { Crop = null };
                            if (leveling == PageAlignment.Original) row.PageAlignments.Remove(number);
                            else row.PageAlignments[number] = leveling;
                            if (alignment.Crop is { } crop)
                            {
                                row.PendingPageCrops[number] = crop;
                                if (crop.Turns == 0) row.PageRotations.Remove(number);
                                else row.PageRotations[number] = crop.Turns;
                            }
                        }
                        row.Confirmed = false;
                        row.Status = "Проверить";
                        row.Diagnostic = preparation.Message;
                        row.RefreshSavedPreview();
                    }
                    processed++;
                    Progress = 100d * (index + 1) / batch.Length;
                }
                if (proposeMerges)
                    foreach (var row in batch) row.MergeEligible = row.Result?.PageCount == 1;
                RefreshMergeProposals();
                analysisClock.Stop();
                var detail = MergeProposals.Count == 0 ? $"Сохранено подтверждённых: {batch.Length - processed}"
                    : $"Предложения к объединению: {MergeProposals.Count}";
                Message = $"Распознано: {processed} · Потраченное время {AnalysisElapsed} · {detail}";
                log?.Info("Analysis", $"Completed. Processed={processed}; Preserved={batch.Length - processed}; ElapsedMs={analysisClock.ElapsedMilliseconds}");
            });
        }
        finally
        {
            analysisClock.Stop();
            cancellation = null;
            if (operation.IsCancellationRequested) RefreshMergeProposals();
        }
    }

    private (Dictionary<int, PageAlignment> Edits, string Message) PrepareScanPages(
        string path, RecognitionResult result, CancellationToken token)
    {
        var edits = new Dictionary<int, PageAlignment>();
        var cropped = 0;
        try
        {
            using var input = File.OpenRead(path);
            if (result.SourceHash is null || RenameService.Hash(input) != result.SourceHash)
                return (edits, "PDF изменился. Распознайте его повторно.");
            var renderer = new PdfRenderer();
            for (var number = 1; number <= result.PageCount; number++)
            {
                token.ThrowIfCancellationRequested();
                if (result.PageAlignments?.TryGetValue(number, out var alignment) != true) continue;
                var candidates = result.Candidates.Where(candidate => candidate.Page == number).ToArray();
                var turns = candidates.Select(candidate => candidate.Turn).Distinct().ToArray();
                if (turns.Length > 1) continue;
                var turn = turns.FirstOrDefault();
                var stamp = candidates.FirstOrDefault()?.Regions.TitleBlock;
                if (alignment.Angle != 0) edits[number] = alignment;
                try
                {
                    var crop = renderer.FindPageCrop(input, number, turn, alignment, stamp, token);
                    if (crop is null || crop.HasOutsideInk) continue;
                    edits[number] = alignment with { Crop = crop };
                    cropped++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                {
                    log?.Warning("ScanPreparation", $"Cannot crop. Source={path}; Page={number}", ex);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log?.Warning("ScanPreparation", $"Cannot prepare. Source={path}", ex);
            return ([], "Не удалось подготовить скан. Проверьте доступ к PDF и распознайте его повторно.");
        }
        var review = result.PageCount - cropped;
        log?.Info("ScanPreparation", $"Prepared. Source={path}; Cropped={cropped}; Review={review}");
        return (edits, $"Автоподготовка · Предложена обрезка листов: {cropped} · Проверьте обрезку вручную: {review}. " +
            "Проверьте контур и графы. Обрезка применится после подтверждения и сохранится при переименовании или объединении.");
    }

    private readonly Stopwatch analysisClock = new();
    internal string AnalysisElapsed => FormatAnalysisTime(analysisClock.Elapsed);
    internal static string FormatAnalysisTime(TimeSpan elapsed) => $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";

    [RelayCommand]
    private void Cancel() => cancellation?.Cancel();

    public async Task ReadAsync(DocumentRow row, int page, FieldRegions regions, int turn)
    {
        var options = Options;
        await WorkAsync(async () =>
        {
            log?.Info("ReadRegions", $"Started. Source={row.Source}; Page={page}; Turn={turn}; OCR={options.OcrEnabled}");
            var (hash, candidate) = await Task.Run(() =>
            {
                using var input = File.OpenRead(row.Source);
                var version = RenameService.Hash(input);
                return (version, row.SourceHash is { } previous && previous != version ? null :
                    analyzer.ReadRegions(row.Source, page, regions, turn, options, default));
            });
            if (!row.ObserveVersion(hash) || candidate is null) { Message = row.Diagnostic; return; }
            row.Use(candidate);
            log?.Info("ReadRegions", $"Completed. Source={row.Source}; Page={page}; OCR={candidate.OcrConfidence is not null}");
            RefreshMergeProposals();
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

    internal Task ConfirmIncludedAsync() => ConfirmSelectedAsync(Rows.Where(row => row.Included));

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

    private async Task<bool> ConfirmRowAsync(DocumentRow row)
    {
        var hash = await Task.Run(() => ReadVersion(row.Source));
        var namedSheet = MergeProposals.FirstOrDefault(group => group.Pages.Contains(row))?.NamedSheet;
        return hash is null ? row.ObserveVersion(null) : row.Confirm(hash, namedSheet);
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
                        var edited = result.ResultHash is not null &&
                            (result.PageRotations?.Count > 0 || result.PageAlignments?.Count > 0);
                        if (edited) row.ApplySavedPageEdits(result.PageRotations, result.PageAlignments, result.ResultHash!);
                        var sameName = string.Equals(row.Source, newPath, StringComparison.Ordinal);
                        row.Source = newPath;
                        if (edited && sameName) row.RefreshSavedPreview();
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
                {
                    row.Included = false;
                    row.MergeEligible = false;
                }
            }
            RefreshMergeProposals();
            Message = results.Count == 0 ? "Нет выполненных операций." :
                $"Выполнено: {results.Count(r => r.Status is MoveStatus.Moved or MoveStatus.Undone or MoveStatus.Copied or MoveStatus.CopyRemoved)} из {results.Count}.";
            if (journalWarning is not null) Message = $"{journalWarning} {Message}";
        });
    }

    private async Task WorkAsync(Func<Task> work, [CallerMemberName] string operation = "")
    {
        if (Busy) return;
        Busy = true;
        var clock = Stopwatch.StartNew();
        log?.Info(operation, "Started.");
        try { await work(); }
        catch (OperationCanceledException)
        {
            log?.Info(operation, "Cancelled; completed results retained.");
            Message = "Анализ отменён. Готовые результаты сохранены в списке.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or InvalidOperationException or ArgumentException or DllNotFoundException or TypeInitializationException)
        {
            log?.Error(operation, "Operation interrupted.", ex);
            Message = $"Операция прервана ({ex.GetType().Name}). Проверьте доступ к файлам и журнал.";
        }
        finally
        {
            log?.Info(operation, $"Finished. ElapsedMs={clock.ElapsedMilliseconds}");
            Busy = false;
        }
    }
}
