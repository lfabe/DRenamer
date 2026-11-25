using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
using WinRT.Interop;
using Path = System.IO.Path;

namespace DrawingRenamer;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();
    private readonly PdfRenderer renderer = new();
    private CancellationTokenSource? preview;
    private CancellationTokenSource? deskew;
    private DocumentRow? current;
    private int page = 1;
    private int pageCount;
    private (int Width, int Height) sheetSize;
    private Area? designation;
    private Area? name;
    private Area? stamp;
    private PointD? dragStart;
    private PointD? dragEnd;
    private enum RegionTarget { Designation, Name, Stamp }
    private RegionTarget? marking;
    private bool closing;
    private bool renaming;
    private bool choosingDocuments;
    private bool refreshingCandidates;
    private bool settingsLoaded;
    private bool syncingScannerCutoff;
    private bool refreshingDocumentRows;
    private readonly ObservableCollection<DocumentRow> visibleRows = [];
    private readonly Dictionary<string, DocumentRow> mergeHeaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<DocumentRow> animatingGroups = [];
    private readonly HashSet<DocumentRow> pendingGroupToggles = [];
    private readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    private bool updatingAllChecks;
    private Border? movingHeader;
    private DocumentRow? queueDragSource;
    private DocumentRow? queueDropTarget;
    private Windows.Foundation.Point queueDragStart;
    private uint queuePointerId;
    private DocumentRow[] draggedQueueRows = [];
    private bool CanChangeDocuments => !closing && !ViewModel.Busy && !renaming && !choosingDocuments;
    private int PreviewTurn => current?.PageRotations.GetValueOrDefault(page) ?? 0;
    private PageAlignment Alignment => current?.PageAlignments.GetValueOrDefault(page, PageAlignment.Original)
        ?? PageAlignment.Original;
    private int RegionTurn => (PreviewTurn + Orientation.SelectedIndex) & 3;
    private double RegionStrokeThickness => 1.25 / PreviewScroll.ZoomFactor;

    public MainWindow()
    {
        InitializeComponent();
        Documents.ItemsSource = visibleRows;
        UpdateScannerCutoff();
        ApplyTheme();
        PreviewScroll.ViewChanged += (_, _) =>
        {
            var thickness = RegionStrokeThickness;
            foreach (var outline in Overlays.Children.OfType<Rectangle>())
                outline.StrokeThickness = thickness;
        };
        Root.AddHandler(UIElement.DragOverEvent, new DragEventHandler(DocumentsDragOver), true);
        Root.AddHandler(UIElement.DropEvent, new DragEventHandler(DocumentsDropped), true);
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(QueuePointerPressed), true);
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(QueuePointerMoved), true);
        Root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(QueuePointerReleased), true);
        Root.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(QueueCaptureLost), true);
        var version = typeof(MainWindow).Assembly.GetName().Version;
        Title = version is null ? "Drawing Renamer" : $"Drawing Renamer v{version.ToString(3)}";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "DrawingRenamer.ico"));
        AppWindow.Resize(new(1320, 820));
        ViewModel.PropertyChanged += ViewModelChanged;
        ViewModel.AnalyzeCommand.PropertyChanged += (_, _) => UpdateCommands();
        ViewModel.Rows.CollectionChanged += QueueRowsChanged;
        var analysisTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        analysisTimer.Tick += (_, _) => UpdateAnalysisClock();
        analysisTimer.Start();
        var scannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        scannerTimer.Tick += async (_, _) => await ViewModel.CheckScannerFolderAsync(() => CanChangeDocuments);
        scannerTimer.Start();
        Activated += async (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                if (!settingsLoaded)
                {
                    settingsLoaded = true;
                    await ViewModel.LoadSettingsAsync();
                }
                await RefreshDocumentsAsync();
            }
        };
        Closed += (_, _) =>
        {
            closing = true;
            scannerTimer.Stop();
            analysisTimer.Stop();
            preview?.Cancel();
            deskew?.Cancel();
            ViewModel.CancelCommand.Execute(null);
        };
        UpdateCommands();
    }

    private async void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.Busy) or nameof(ViewModel.SeparatorError)) UpdateCommands();
        if (e.PropertyName == nameof(ViewModel.ThemeIndex)) ApplyTheme();
        if (e.PropertyName == nameof(ViewModel.Recursive)) await RefreshDocumentsAsync();
        if (e.PropertyName == nameof(ViewModel.ScannerCreatedAfter)) UpdateScannerCutoff();
        if (e.PropertyName == nameof(ViewModel.MergeProposals)) RefreshDocumentRows();
        if (e.PropertyName == nameof(ViewModel.Message)) UpdateAnalysisClock();
    }

    private void UpdateAnalysisClock() => AnalysisClockText.Text = ViewModel.IsAnalyzing &&
        ViewModel.Message.StartsWith("Анализ ", StringComparison.Ordinal) ? $" · {ViewModel.AnalysisElapsed}" : "";

    private void QueueRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (DocumentRow row in e.OldItems) row.PropertyChanged -= QueueRowChanged;
        if (e.NewItems is not null)
            foreach (DocumentRow row in e.NewItems) row.PropertyChanged += QueueRowChanged;
        RefreshDocumentRows();
    }

    private void QueueRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentRow.Included) && !updatingAllChecks) UpdateCommands();
    }

    private void ToggleAllFiles(object sender, RoutedEventArgs e)
    {
        if (!CanChangeDocuments) return;
        var include = !ViewModel.Rows.All(row => row.Included);
        updatingAllChecks = true;
        try { foreach (var row in ViewModel.Rows) row.Included = include; }
        finally { updatingAllChecks = false; }
        UpdateCommands();
    }

    private async void RefreshDocuments(object sender, RoutedEventArgs e) => await RefreshDocumentsAsync();

    private Task RefreshDocumentsAsync() => closing || renaming || choosingDocuments
        ? Task.CompletedTask : ViewModel.RefreshDocumentsAsync();

    private void UpdateCommands()
    {
        var available = CanChangeDocuments;
        AddFilesButton.IsEnabled = available;
        AddFolderButton.IsEnabled = available;
        RecursiveCheckBox.IsEnabled = available;
        RefreshButton.IsEnabled = available && ViewModel.HasDocumentSources;
        var included = ViewModel.Rows.Count(row => row.Included);
        AllFilesCheckBox.IsEnabled = available && ViewModel.Rows.Count > 0;
        AllFilesCheckBox.IsChecked = included == 0 ? false : included == ViewModel.Rows.Count ? true : null;
        AnalyzeButton.IsEnabled = available && included > 0;
        ConfirmButton.IsEnabled = available && included > 0;
        foreach (var header in mergeHeaders.Values)
            header.MergeEnabled = available && ViewModel.SeparatorError is null && header.MergeProposal?.CanMerge == true;
        CancelButton.Visibility = ViewModel.Busy && ViewModel.IsAnalyzing ? Visibility.Visible : Visibility.Collapsed;
        RenameButton.IsEnabled = available && ViewModel.Rows.Any(row => !row.InMergeProposal) && ViewModel.SeparatorError is null;
        SeparatorInput.IsEnabled = available;
        SettingsContainer.IsEnabled = available;
        SaveSettingsButton.IsEnabled = available && ViewModel.SeparatorError is null;
        UndoButton.IsEnabled = available && ViewModel.CanUndo;
        Editor.IsHitTestVisible = available;
        Documents.IsEnabled = available;
        RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled = available && PageImage.Source is not null;
        FitPageButton.IsEnabled = PageImage.Source is not null;
        FitStampButton.IsEnabled = PageImage.Source is not null && CurrentStamp is { IsValid: true };
        UpdateDeskewButton();
        ToggleRegionsButton.IsEnabled = StartManualRegionsButton.IsEnabled = available;
        MarkDesignationButton.IsEnabled = MarkNameButton.IsEnabled = MarkStampButton.IsEnabled = available;
        ReadRegionsButton.IsEnabled = SaveRegionsButton.IsEnabled = available && designation.HasValue && name.HasValue;
        EmptyQueue.Visibility = ViewModel.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private QueueColumns FileQueueColumns => (QueueColumns)Root.Resources["FileQueueColumns"];

    private void QueueSizeChanged(object sender, SizeChangedEventArgs e) => FileQueueColumns.Fit(e.NewSize.Width - 2);

    private void ResizeQueueColumn(object sender, DragDeltaEventArgs e)
    {
        if (sender is Thumb { Tag: string column }) FileQueueColumns.Resize(column, e.HorizontalChange);
    }

    private void ResizeQueueColumnKey(object sender, KeyRoutedEventArgs e)
    {
        var change = e.Key switch { Windows.System.VirtualKey.Left => -12, Windows.System.VirtualKey.Right => 12, _ => 0 };
        if (change == 0 || sender is not Thumb { Tag: string column }) return;
        FileQueueColumns.Resize(column, change);
        e.Handled = true;
    }

    private void BeginColumnMove(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border header ||
            !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed || !header.CapturePointer(e.Pointer)) return;
        movingHeader = header;
        e.Handled = true;
    }

    private void TrackColumnMove(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string column } header || header != movingHeader) return;
        var from = FileQueueColumns.IndexOf(column);
        var target = FileQueueColumns.DropIndex(column, e.GetCurrentPoint(QueueHeader).Position.X);
        ColumnMoveGuide.Visibility = from == target ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetLeft(ColumnMoveGuide, FileQueueColumns.DropGuide(from, target));
        e.Handled = true;
    }

    private void FinishColumnMove(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string column } header || header != movingHeader) return;
        var target = FileQueueColumns.DropIndex(column, e.GetCurrentPoint(QueueHeader).Position.X);
        CancelColumnMove(header, e);
        header.ReleasePointerCapture(e.Pointer);
        FileQueueColumns.Move(column, target);
        e.Handled = true;
    }

    private void CancelColumnMove(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(movingHeader, sender)) return;
        movingHeader = null;
        ColumnMoveGuide.Visibility = Visibility.Collapsed;
    }

    private void AutoFitQueueColumn(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not Thumb { Tag: string column }) return;
        var (label, values) = column switch
        {
            nameof(QueueColumns.Source) => ("Исходное имя", ViewModel.Rows.Select(row => row.InMergeProposal ? "    " + row.OriginalName : row.OriginalName)),
            nameof(QueueColumns.Designation) => ("Обозначение", ViewModel.Rows.Select(row => row.Designation)),
            nameof(QueueColumns.Title) => ("Наименование", ViewModel.Rows.Select(row => row.DocumentName)),
            nameof(QueueColumns.Sheet) => ("Лист", ViewModel.Rows.Select(row => row.SheetNumberText)),
            nameof(QueueColumns.Target) => ("Новое имя", ViewModel.Rows.Select(row => row.QueueNewName)),
            _ => ("Статус", ViewModel.Rows.Select(row => row.QueueStatus).Append("Объединить"))
        };
        var measure = new TextBlock { FontSize = 14 };
        var width = values.Append(label).Distinct().Max(value =>
        {
            measure.Text = value;
            measure.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            return measure.DesiredSize.Width;
        }) + 16;
        FileQueueColumns.Resize(column, width - FileQueueColumns.WidthOf(column).Value);
        e.Handled = true;
    }

    private FileOpenPicker FilePicker(string extension)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(extension);
        return picker;
    }

    private async void PickFiles(object sender, RoutedEventArgs e)
    {
        choosingDocuments = true;
        UpdateCommands();
        try
        {
            var files = await FilePicker(".pdf").PickMultipleFilesAsync();
            if (!closing && files.Count > 0)
                await ViewModel.AddDocumentsAsync(files.Select(file => file.Path));
        }
        catch (Exception ex) { ViewModel.Message = $"Не удалось открыть выбор файлов ({ex.GetType().Name})."; }
        finally { choosingDocuments = false; if (!closing) UpdateCommands(); }
    }

    private async void PickFolder(object sender, RoutedEventArgs e) => await PickDocumentFolderAsync(scanner: false);

    private async void PickScannerFolder(object sender, RoutedEventArgs e) => await PickDocumentFolderAsync(scanner: true);

    private async Task PickDocumentFolderAsync(bool scanner)
    {
        if (!CanChangeDocuments) return;
        choosingDocuments = true;
        UpdateCommands();
        try
        {
            if (await PickFolderPathAsync() is not { } path || closing) return;
            if (scanner) ViewModel.SetScannerFolder(path);
            else await ViewModel.AddDocumentsAsync([], path);
        }
        catch (Exception ex) { ViewModel.Message = $"Не удалось выбрать папку ({ex.GetType().Name})."; }
        finally { choosingDocuments = false; if (!closing) UpdateCommands(); }
    }

    private void DocumentsDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = CanChangeDocuments ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (CanChangeDocuments) e.DragUIOverride.Caption = "Добавить PDF и папки";
        e.Handled = true;
    }

    private DocumentRow? QueueRowAt(PointerRoutedEventArgs e) =>
        VisualTreeHelper.FindElementsInHostCoordinates(e.GetCurrentPoint(null).Position, Documents)
            .OfType<ListViewItem>().Select(item => Documents.ItemFromContainer(item) as DocumentRow)
            .FirstOrDefault(row => row is not null);

    private void QueuePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!CanChangeDocuments || ViewModel.MergeProposals.Count == 0 ||
            e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        var point = e.GetCurrentPoint(Root);
        if (!point.Properties.IsLeftButtonPressed || QueueRowAt(e) is not { IsFile: true, InMergeProposal: false } row) return;
        for (var element = e.OriginalSource as DependencyObject; element is not null && element is not ListViewItem;
            element = VisualTreeHelper.GetParent(element))
            if (element is ButtonBase) return;
        queueDragSource = row;
        queueDragStart = point.Position;
        queuePointerId = e.Pointer.PointerId;
    }

    private void QueuePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (queueDragSource is not { } row || e.Pointer.PointerId != queuePointerId) return;
        var point = e.GetCurrentPoint(Root);
        if (!CanChangeDocuments || !point.Properties.IsLeftButtonPressed)
        {
            ClearQueueDrag();
            return;
        }
        if (draggedQueueRows.Length == 0)
        {
            if (Math.Abs(point.Position.X - queueDragStart.X) < 6 && Math.Abs(point.Position.Y - queueDragStart.Y) < 6) return;
            draggedQueueRows = Documents.SelectedItems.Contains(row)
                ? Documents.SelectedItems.OfType<DocumentRow>().Where(item => item.IsFile && !item.InMergeProposal).ToArray()
                : [row];
            Root.CapturePointer(e.Pointer);
        }
        HighlightQueueDrop(QueueRowAt(e) is { IsMergeHeader: true } header ? header : null);
        e.Handled = true;
    }

    private async void QueuePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (queueDragSource is null || e.Pointer.PointerId != queuePointerId) return;
        var pages = draggedQueueRows;
        var target = pages.Length > 0 ? QueueRowAt(e) : null;
        ClearQueueDrag();
        if (pages.Length == 0) return;
        Root.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (!CanChangeDocuments || target is not { MergeProposal: { } proposal }) return;
        await ViewModel.AttachMergePagesAsync(proposal, pages);
        if (!closing) RefreshDocumentRows(target);
    }

    private void QueueCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, Root)) ClearQueueDrag();
    }

    private void ClearQueueDrag()
    {
        queueDragSource = null;
        draggedQueueRows = [];
        HighlightQueueDrop(null);
    }

    private void HighlightQueueDrop(DocumentRow? target)
    {
        if (ReferenceEquals(queueDropTarget, target)) return;
        if (queueDropTarget is not null) queueDropTarget.IsQueueDropTarget = false;
        queueDropTarget = target;
        if (target is not null) target.IsQueueDropTarget = true;
    }

    private void DetachMergePage(object sender, RoutedEventArgs e)
    {
        if (CanChangeDocuments && sender is MenuFlyoutItem { Tag: DocumentRow row }) ViewModel.DetachMergePage(row);
    }

    private async void DocumentsDropped(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        e.AcceptedOperation = CanChangeDocuments ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (!CanChangeDocuments) return;

        choosingDocuments = true;
        UpdateCommands();
        try
        {
            IReadOnlyList<IStorageItem> items;
            var deferral = e.GetDeferral();
            try { items = await e.DataView.GetStorageItemsAsync(); }
            finally { deferral.Complete(); }
            if (closing) return;

            var files = items.OfType<StorageFile>().Select(file => file.Path).Where(Path.IsPathFullyQualified);
            var folders = items.OfType<StorageFolder>().Select(folder => folder.Path).Where(Path.IsPathFullyQualified);
            await ViewModel.AddDocumentsAsync(files, folders.ToArray());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!closing) ViewModel.Message = $"Не удалось добавить перетащенные файлы и папки ({ex.GetType().Name}).";
        }
        finally { choosingDocuments = false; if (!closing) UpdateCommands(); }
    }

    private async Task<string?> PickFolderPathAsync()
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async void PickTemplates(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await FilePicker(".json").PickSingleFileAsync() is { } file)
            {
                ViewModel.SetTemplates(file.Path);
                ViewModel.Message = "Шаблоны загружены для следующего анализа.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException
            or System.Runtime.InteropServices.COMException)
        {
            ViewModel.Message = $"Не удалось прочитать шаблоны ({ex.GetType().Name}).";
        }
    }

    private async void SaveSettings(object sender, RoutedEventArgs e)
    {
        if (CanChangeDocuments) await ViewModel.SaveSettingsAsync();
    }

    private async void ResetSettings(object sender, RoutedEventArgs e)
    {
        if (CanChangeDocuments) await ViewModel.ResetSettingsAsync();
    }

    private void UpdateScannerCutoff()
    {
        syncingScannerCutoff = true;
        try
        {
            ScannerStartDatePicker.Date = ViewModel.ScannerStartDate;
            ScannerStartTimePicker.Time = ViewModel.ScannerStartTime;
        }
        finally { syncingScannerCutoff = false; }
    }

    private void ScannerStartDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs e)
    {
        if (syncingScannerCutoff) return;
        if (e.NewDate is { } date) ViewModel.ScannerStartDate = date;
        else UpdateScannerCutoff();
    }

    private void ScannerStartTimeChanged(object sender, TimePickerValueChangedEventArgs e)
    {
        if (!syncingScannerCutoff) ViewModel.ScannerStartTime = e.NewTime;
    }

    private void ApplyTheme() => Root.RequestedTheme = ViewModel.ThemeIndex switch
    {
        1 => ElementTheme.Light,
        2 => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    private async void AnalyzeDocumentFromMenu(object sender, RoutedEventArgs e)
    {
        if (CanChangeDocuments && sender is MenuFlyoutItem { Tag: DocumentRow row })
            await ViewModel.AnalyzeDocumentAsync(row);
    }

    private async void SplitFromMenu(object sender, RoutedEventArgs e)
    {
        if (CanChangeDocuments && sender is MenuFlyoutItem { Tag: DocumentRow { IsFile: true } row })
            await ViewModel.SplitDocumentAsync(row);
    }

    private void RemoveDocumentFromMenu(object sender, RoutedEventArgs e)
    {
        if (CanChangeDocuments && sender is MenuFlyoutItem { Tag: DocumentRow row })
            ViewModel.RemoveDocuments(row.IsFile && Documents.SelectedItems.Contains(row)
                ? Documents.SelectedItems.OfType<DocumentRow>() : [row]);
    }

    private void DocumentsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Delete || !CanChangeDocuments ||
            Documents.SelectedItems.Count == 0) return;
        e.Handled = true;
        ViewModel.RemoveDocuments(Documents.SelectedItems.OfType<DocumentRow>());
        Documents.Focus(FocusState.Keyboard);
    }

    private MergeProposal? SelectedMergeGroup()
    {
        if (Documents.SelectedItem is not DocumentRow selected) return null;
        return selected.MergeProposal ?? ViewModel.MergeProposals.FirstOrDefault(proposal => proposal.Pages.Contains(selected));
    }

    private async void RefreshDocumentRows(DocumentRow? preferred = null)
    {
        if (refreshingDocumentRows) return;
        var selected = preferred ?? Documents.SelectedItem as DocumentRow ?? ViewModel.Selected;
        var wasFlat = mergeHeaders.Count == 0;
        var activeKeys = ViewModel.MergeProposals.Select(proposal => proposal.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in mergeHeaders.Keys.Where(key => !activeKeys.Contains(key)).ToArray()) mergeHeaders.Remove(key);
        var members = new Dictionary<DocumentRow, MergeProposal>();
        foreach (var proposal in ViewModel.MergeProposals)
            foreach (var row in proposal.Pages) members[row] = proposal;
        var addedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actualRows = ViewModel.Rows.ToHashSet();
        var visible = new List<DocumentRow>();
        foreach (var row in ViewModel.Rows)
        {
            if (!members.TryGetValue(row, out var proposal))
            {
                visible.Add(row);
                continue;
            }
            if (!addedHeaders.Add(proposal.Key)) continue;
            if (!mergeHeaders.TryGetValue(proposal.Key, out var header))
            {
                header = new DocumentRow(proposal.Label) { MergeProposal = proposal, MergeExpanded = true };
                mergeHeaders.Add(proposal.Key, header);
            }
            header.UpdateMergeHeader(proposal);
            visible.Add(header);
            if (!header.MergeExpanded) continue;
            var pages = proposal.Ordered.Where(actualRows.Contains).ToArray();
            for (var i = 0; i < pages.Length; i++)
            {
                pages[i].IsGroupTail = i == pages.Length - 1;
                visible.Add(pages[i]);
            }
        }

        var visibleSet = visible.ToHashSet();
        var next = selected is not null && visibleSet.Contains(selected) ? selected : null;
        if ((next is null || (wasFlat && Documents.SelectedItems.Count < 2)) && selected is not null &&
            members.TryGetValue(selected, out var selectedGroup))
            mergeHeaders.TryGetValue(selectedGroup.Key, out next);
        refreshingDocumentRows = true;
        try
        {
            // Update the same ItemsSource so WinUI keeps the scroll anchor and animates only changed rows.
            if (next is not null && visibleRows.Contains(next) && !ReferenceEquals(Documents.SelectedItem, next))
                Documents.SelectedItem = next;
            for (var i = visibleRows.Count - 1; i >= 0; i--)
                if (!visibleSet.Contains(visibleRows[i])) visibleRows.RemoveAt(i);
            for (var i = 0; i < visible.Count; i++)
            {
                if (i < visibleRows.Count && ReferenceEquals(visibleRows[i], visible[i])) continue;
                var oldIndex = visibleRows.IndexOf(visible[i]);
                if (oldIndex >= 0) visibleRows.Move(oldIndex, i);
                else visibleRows.Insert(i, visible[i]);
            }
            next ??= visible.FirstOrDefault();
            if (!ReferenceEquals(Documents.SelectedItem, next)) Documents.SelectedItem = next;
        }
        finally { refreshingDocumentRows = false; }
        await UpdateDocumentSelectionAsync();
    }

    private async void ToggleMergeGroup(object sender, RoutedEventArgs e)
    {
        if (!CanChangeDocuments || sender is not Button { Tag: DocumentRow { IsMergeHeader: true } header }) return;
        if (!animatingGroups.Add(header))
        {
            if (!pendingGroupToggles.Add(header)) pendingGroupToggles.Remove(header);
            return;
        }
        try
        {
            do
            {
                if (!CanChangeDocuments || !visibleRows.Contains(header)) break;
                header.MergeExpanded = !header.MergeExpanded;
                RefreshDocumentRows(header);
                if (uiSettings.AnimationsEnabled) await Task.Delay(300);
            } while (pendingGroupToggles.Remove(header));
        }
        finally
        {
            pendingGroupToggles.Remove(header);
            animatingGroups.Remove(header);
        }
    }

    private async void MergeSelected(object sender, RoutedEventArgs e)
    {
        if (CanChangeDocuments && sender is Button { Tag: DocumentRow { MergeProposal: { } proposal } })
            await ViewModel.MergeAsync(proposal);
    }

    private void MergeFieldsChanged(object sender, RoutedEventArgs e)
    {
        if (CanChangeDocuments && current?.MergeEligible == true) ViewModel.RefreshMergeProposals();
    }

    private async void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!refreshingDocumentRows) await UpdateDocumentSelectionAsync();
    }

    private async Task UpdateDocumentSelectionAsync()
    {
        UpdateCommands();
        var selected = Documents.SelectedItem as DocumentRow;
        var group = SelectedMergeGroup();
        MergeDetails.Visibility = group is null ? Visibility.Collapsed : Visibility.Visible;
        if (group is not null)
        {
            MergeOrder.Text = string.Join(Environment.NewLine, group.Ordered.Select(row =>
                $"Лист {row.SheetNumber?.ToString() ?? "—"}   {row.OriginalName}"));
            MergeDestination.Text = $"Итоговый файл: {group.Label}";
        }
        SheetNumberInput.Visibility = selected is { IsFile: true, MergeEligible: true } ? Visibility.Visible : Visibility.Collapsed;
        Editor.Visibility = selected is { IsFile: true } ? Visibility.Visible : Visibility.Collapsed;
        RegionControls.Visibility = PageImage.Source is not null ? Editor.Visibility : Visibility.Collapsed;
        UpdateRegionGuidance();
        if (selected is { IsMergeHeader: true }) selected = group?.Ordered.FirstOrDefault();
        if (current == selected) return;
        if (current is not null) current.PropertyChanged -= RowChanged;
        current = selected;
        if (current is not null) current.PropertyChanged += RowChanged;
        Editor.DataContext = current;
        RefreshCandidates();
        await SelectPageAsync(current?.SelectedCandidate?.Page ?? 1);
    }

    private async void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentRow.Candidates))
        {
            RefreshCandidates();
            await SelectPageAsync(current?.SelectedCandidate?.Page ?? 1);
        }
        if (e.PropertyName == nameof(DocumentRow.Source)) await SelectPageAsync(page);
        if (e.PropertyName == nameof(DocumentRow.SheetNumberText)) UpdateCommands();
        if (e.PropertyName is nameof(DocumentRow.Designation) or nameof(DocumentRow.DocumentName) or nameof(DocumentRow.HasDocumentName))
            UpdateRegionGuidance();
    }

    private void RefreshCandidates()
    {
        refreshingCandidates = true;
        try
        {
            var candidates = current?.Candidates ?? [];
            CandidatePicker.ItemsSource = candidates.Select(DocumentRow.CandidateLabel).ToArray();
            CandidatePicker.Visibility = candidates.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            CandidatePicker.SelectedIndex = current?.SelectedCandidate is { } selected
                ? candidates.ToList().IndexOf(selected) : -1;
        }
        finally { refreshingCandidates = false; }
    }

    private async void CandidateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (refreshingCandidates || current is not { } row || CandidatePicker.SelectedIndex < 0 ||
            CandidatePicker.SelectedIndex >= row.Candidates.Count) return;
        var candidate = row.Candidates[CandidatePicker.SelectedIndex];
        row.Use(candidate);
        await SelectPageAsync(candidate.Page);
    }

    private async void PreviousPage(object sender, RoutedEventArgs e) => await SelectPageAsync(page - 1);
    private async void NextPage(object sender, RoutedEventArgs e) => await SelectPageAsync(page + 1);

    private async Task SelectPageAsync(int number)
    {
        if (closing) return;
        preview?.Cancel();
        deskew?.Cancel();
        using var operation = new CancellationTokenSource();
        preview = operation;
        var row = current;
        page = Math.Max(1, number);
        designation = name = stamp = null;
        dragStart = dragEnd = null;
        RegionControls.Visibility = Visibility.Collapsed;
        ShowRegionActions(false);
        PageImage.Source = null;
        Sheet.Width = Sheet.Height = 0;
        SheetExtent.Width = SheetExtent.Height = 0;
        UpdateCommands();
        Overlays.Children.Clear();
        PreviousButton.IsEnabled = NextButton.IsEnabled = false;
        PageLabel.Text = "—";
        PreviewMessage.Visibility = Visibility.Visible;
        PreviewMessage.Text = row is null ? "Выберите PDF в списке." : "Открытие листа…";
        try
        {
            if (row is null) return;
            var path = row.Source;
            var knownCount = row.Result?.PageCount;
            var result = await Task.Run(() =>
            {
                using var input = File.OpenRead(path);
                var hash = RenameService.Hash(input);
                var count = knownCount is > 0 ? knownCount.Value : renderer.CountPages(input, operation.Token);
                return (count, hash, raster: renderer.Render(path, number, Area.Full, 1800, operation.Token));
            });
            operation.Token.ThrowIfCancellationRequested();
            if (!row.ObserveVersion(result.hash)) return;
            var pixels = result.raster.Pixels;
            using var writer = new DataWriter();
            writer.WriteBytes(pixels);
            using var buffer = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8,
                result.raster.Width, result.raster.Height, BitmapAlphaMode.Ignore);
            var bitmap = new SoftwareBitmapSource();
            await bitmap.SetBitmapAsync(buffer);
            operation.Token.ThrowIfCancellationRequested();
            sheetSize = (result.raster.Width, result.raster.Height);
            UpdateSheetTransforms();
            PageImage.Source = bitmap;
            UpdateCommands();
            pageCount = result.count;
            PageLabel.Text = $"{page}/{pageCount}";
            PreviousButton.IsEnabled = page > 1;
            NextButton.IsEnabled = page < pageCount;
            PreviewMessage.Visibility = Visibility.Collapsed;
            RegionControls.Visibility = Editor.Visibility;
            UpdateRegionGuidance();
            Area? frame = null;
            if (row.SelectedCandidate is { } candidate && candidate.Page == page)
            {
                Orientation.SelectedIndex = (candidate.Turn - PreviewTurn) & 3;
                if (row.Status == "Проверить") frame = candidate.Regions.TitleBlock;
            }
            else Orientation.SelectedIndex = 0;
            FitSheet(frame);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!operation.IsCancellationRequested) PreviewMessage.Text = $"Лист недоступен ({ex.GetType().Name}).";
        }
        finally
        {
            if (ReferenceEquals(preview, operation)) preview = null;
        }
    }

    private void ZoomIn(object sender, RoutedEventArgs e) => Zoom(1.25f);
    private void ZoomOut(object sender, RoutedEventArgs e) => Zoom(.8f);
    private void Zoom(float factor) => PreviewScroll.ChangeView(null, null,
        Math.Clamp(PreviewScroll.ZoomFactor * factor, .25f, 5f));

    private void RotateLeft(object sender, RoutedEventArgs e) => RotateSheet(-90);
    private void RotateRight(object sender, RoutedEventArgs e) => RotateSheet(90);
    private void FitPage(object sender, RoutedEventArgs e) => FitSheet();
    private Area? CurrentStamp => stamp ?? (current?.SelectedCandidate is { } candidate && candidate.Page == page
        ? candidate.Regions.TitleBlock : null);

    private void FitStamp(object sender, RoutedEventArgs e)
    {
        if (CurrentStamp is { IsValid: true } frame) FitSheet(frame);
    }

    private void UpdateDeskewButton()
    {
        DeskewButton.IsEnabled = CanChangeDocuments && PageImage.Source is not null && deskew is null;
        var applied = current?.PageAlignments.ContainsKey(page) == true;
        DeskewButton.IsChecked = applied;
        var label = applied ? "Отменить выравнивание листа" : "Выровнять лист";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DeskewButton, label);
        ToolTipService.SetToolTip(DeskewButton, label);
        SaveAlignedPdfButton.Visibility = current?.PageAlignments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveAlignedPdfButton.IsEnabled = DeskewButton.IsEnabled;
    }

    private async void ToggleDeskew(object sender, RoutedEventArgs e)
    {
        if (current is not { } row || !CanChangeDocuments || PageImage.Source is null ||
            dragStart.HasValue || deskew is not null)
        {
            UpdateDeskewButton();
            return;
        }
        if (row.PageAlignments.Remove(page))
        {
            UpdateSheetTransforms();
            UpdateDeskewButton();
            FitSheet();
            ViewModel.Message = "Выравнивание листа отменено.";
            return;
        }

        var number = page;
        var source = row.Source;
        var expectedHash = row.SourceHash;
        using var operation = new CancellationTokenSource();
        deskew = operation;
        UpdateDeskewButton();
        try
        {
            var options = await ViewModel.LoadRecognitionOptionsAsync(useDefaultsOnError: true);
            var correction = await Task.Run(() =>
            {
                using var input = File.OpenRead(source);
                if (RenameService.Hash(input) != expectedHash)
                    throw new InvalidDataException("PDF изменился. Откройте лист заново.");
                return renderer.FindPageAlignment(input, number, options.Templates, operation.Token);
            }, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (correction is not { } alignment)
                ViewModel.Message = "Не удалось определить наклон по рамке листа или штампа.";
            else if (alignment.Angle == 0)
                ViewModel.Message = "Лист уже выровнен.";
            else
            {
                row.PageAlignments[number] = alignment;
                UpdateSheetTransforms();
                UpdateDeskewButton();
                FitSheet();
                var scale = alignment.Scale < 1 ? $" · Масштаб содержимого {alignment.Scale:P1}" : "";
                ViewModel.Message = $"Скан выровнен на {Math.Abs(alignment.Angle):0.0}°{scale}.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            if (!operation.IsCancellationRequested) ViewModel.Message = $"Не удалось выровнять лист. {ex.Message}";
        }
        finally
        {
            deskew = null;
            if (!closing) UpdateDeskewButton();
        }
    }

    private async void SaveAlignedPdf(object sender, RoutedEventArgs e)
    {
        if (!CanChangeDocuments || deskew is not null || current is not { SourceHash: { } hash } row ||
            row.PageAlignments.Count == 0) return;
        var source = row.Source;
        var pages = row.PageAlignments.ToDictionary(item => item.Key,
            item => (item.Value, row.PageRotations.GetValueOrDefault(item.Key)));
        choosingDocuments = true;
        UpdateCommands();
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = $"{Path.GetFileNameWithoutExtension(source)} (выровнено)",
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeChoices.Add("Документ PDF", [".pdf"]);
            var file = await picker.PickSaveFileAsync();
            if (file is null || closing) return;
            await Task.Run(() => PdfRenderer.SaveAlignedCopy(source, file.Path, hash, pages));
            if (!closing) ViewModel.Message = $"Сохранена выровненная копия PDF · {file.Path}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!closing) ViewModel.Message = $"Не удалось сохранить выровненный PDF. {ex.Message}";
        }
        finally
        {
            choosingDocuments = false;
            if (!closing) UpdateCommands();
        }
    }

    private void ScrollPreviewHorizontally(object sender, PointerRoutedEventArgs e)
    {
        if ((e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) == 0) return;
        var input = e.GetCurrentPoint(PreviewScroll).Properties;
        if (input.IsHorizontalMouseWheel) return;
        var offset = PreviewScroll.HorizontalOffset - input.MouseWheelDelta * .8;
        PreviewScroll.ChangeView(Math.Clamp(offset, 0, PreviewScroll.ScrollableWidth), null, null, true);
        e.Handled = true;
    }

    private void RotateSheet(int degrees)
    {
        if (current is not { } row || PageImage.Source is null || dragStart.HasValue) return;
        var turns = (PreviewTurn + degrees / 90 + 4) & 3;
        if (turns == 0) row.PageRotations.Remove(page);
        else row.PageRotations[page] = turns;
        UpdateSheetTransforms();
        Orientation.SelectedIndex = 0;
        FitSheet();
    }

    private void UpdateSheetTransforms()
    {
        SheetRotation.Angle = PreviewTurn * 90;
        var alignment = Alignment;
        ScanTransform.Rotation = alignment.Angle;
        ScanTransform.ScaleX = ScanTransform.ScaleY = alignment.Scale;
    }

    private void FitSheet(Area? frame = null)
    {
        if (PageImage.Source is null) return;
        PreviewScroll.ChangeView(0, 0, 1, true);
        Root.UpdateLayout();
        var (width, height) = sheetSize;
        var turn = PreviewTurn;
        var (extentWidth, extentHeight) = (turn & 1) == 0 ? (width, height) : (height, width);
        var focus = frame is { IsValid: true } block ? Alignment.Bounds(block, width, height).Rotate(turn) : Area.Full;
        if (!focus.IsValid) focus = Area.Full;
        var scale = Math.Min(Math.Max(1, PreviewScroll.ActualWidth - 16) / (extentWidth * focus.Width),
            Math.Max(1, PreviewScroll.ActualHeight - 16) / (extentHeight * focus.Height));
        SheetExtent.Width = extentWidth * scale;
        SheetExtent.Height = extentHeight * scale;
        Sheet.Width = ScanContent.Width = PageImage.Width = width * scale;
        Sheet.Height = ScanContent.Height = PageImage.Height = height * scale;
        SheetClip.Rect = new(0, 0, Sheet.Width, Sheet.Height);
        // Keep the rotated sheet centered inside the scrollable bounds.
        Canvas.SetLeft(Sheet, (SheetExtent.Width - Sheet.Width) / 2);
        Canvas.SetTop(Sheet, (SheetExtent.Height - Sheet.Height) / 2);
        PaintRegions();
        if (focus == Area.Full) return;

        PreviewScroll.UpdateLayout();
        var x = (focus.X + focus.Width / 2) * SheetExtent.Width - PreviewScroll.ViewportWidth / 2;
        var y = (focus.Y + focus.Height / 2) * SheetExtent.Height - PreviewScroll.ViewportHeight / 2;
        PreviewScroll.ChangeView(Math.Clamp(x, 0, PreviewScroll.ScrollableWidth),
            Math.Clamp(y, 0, PreviewScroll.ScrollableHeight), null, true);
    }

    private void ToggleRegions(object sender, RoutedEventArgs e)
    {
        var open = RegionActions.Visibility == Visibility.Collapsed;
        ShowRegionActions(open);
        if (!open && ManualRegionsNotice.IsOpen) StartManualRegionsButton.Focus(FocusState.Programmatic);
    }

    private void StartManualRegions(object sender, RoutedEventArgs e)
    {
        if (current is not { } row) return;
        ShowRegionActions(true);
        var target = string.IsNullOrWhiteSpace(row.Designation) ? RegionTarget.Designation : RegionTarget.Name;
        Mark(target);
        (target == RegionTarget.Designation ? MarkDesignationButton : MarkNameButton).Focus(FocusState.Programmatic);
    }

    private void ShowRegionActions(bool open)
    {
        RegionActions.Visibility = RegionReadActions.Visibility = RegionHint.Visibility =
            open ? Visibility.Visible : Visibility.Collapsed;
        ToggleRegionsButton.Content = open ? "Свернуть" : "Выделить графы";
        Mark(null);
        UpdateRegionGuidance();
    }

    private void UpdateRegionGuidance()
    {
        var needsFields = current is { Result: { } result } row &&
            (result.Status == RecognitionStatus.Missing ||
                result.Candidates is [{ } candidate] &&
                (string.IsNullOrWhiteSpace(candidate.Designation) || string.IsNullOrWhiteSpace(candidate.Name))) &&
            (string.IsNullOrWhiteSpace(row.Designation) || (!row.HasDocumentName &&
                !(row.InMergeProposal && row.SelectedCandidate?.Regions.Name.IsValid == false)));
        ManualRegionsNotice.IsOpen = RegionControls.Visibility == Visibility.Visible &&
            RegionActions.Visibility == Visibility.Collapsed && needsFields;
        ToggleRegionsButton.Visibility = ManualRegionsNotice.IsOpen ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MarkDesignation(object sender, RoutedEventArgs e) =>
        Mark(MarkDesignationButton.IsChecked == true ? RegionTarget.Designation : null);
    private void MarkName(object sender, RoutedEventArgs e) =>
        Mark(MarkNameButton.IsChecked == true ? RegionTarget.Name : null);
    private void MarkStamp(object sender, RoutedEventArgs e) =>
        Mark(MarkStampButton.IsChecked == true ? RegionTarget.Stamp : null);

    private void Mark(RegionTarget? target)
    {
        marking = target;
        MarkDesignationButton.IsChecked = target == RegionTarget.Designation;
        MarkNameButton.IsChecked = target == RegionTarget.Name;
        MarkStampButton.IsChecked = target == RegionTarget.Stamp;
        RegionHint.Text = target switch
        {
            RegionTarget.Designation => "Обведите графу «Обозначение» на листе.",
            RegionTarget.Name => "Обведите графу «Наименование» на листе.",
            RegionTarget.Stamp => "Обведите штамп целиком.",
            _ => ""
        };
    }

    private void RegionPressed(object sender, PointerRoutedEventArgs e)
    {
        if (marking is null || ViewModel.Busy || choosingDocuments || deskew is not null) return;
        dragStart = dragEnd = PointerPosition(e);
        Sheet.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void RegionMoved(object sender, PointerRoutedEventArgs e)
    {
        if (dragStart is null) return;
        dragEnd = PointerPosition(e);
        PaintRegions();
    }

    private void RegionReleased(object sender, PointerRoutedEventArgs e)
    {
        if (dragStart is not { } start) return;
        var region = Area.Between(start, PointerPosition(e));
        dragStart = dragEnd = null;
        Sheet.ReleasePointerCapture(e.Pointer);
        if (region.Width * Sheet.Width > 5 && region.Height * Sheet.Height > 5)
        {
            switch (marking)
            {
                case RegionTarget.Designation: designation = region; break;
                case RegionTarget.Name: name = region; break;
                case RegionTarget.Stamp: stamp = region; break;
            }
            var markedStamp = marking == RegionTarget.Stamp;
            Mark(null);
            RegionHint.Text = markedStamp ? "Рамка выделена."
                : (designation.HasValue, name.HasValue) switch
                {
                    (true, true) => "Выделены обе графы. Нажмите «Прочитать графы».",
                    (true, false) => "Выберите «Наименование» и обведите графу.",
                    _ => "Выберите «Обозначение» и обведите графу."
                };
        }
        UpdateCommands();
        PaintRegions();
        e.Handled = true;
    }

    private void RegionCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        dragStart = dragEnd = null;
        PaintRegions();
    }

    private PointD PointerPosition(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ScanContent).Position;
        return new(Math.Clamp(point.X / ScanContent.Width, 0, 1), Math.Clamp(point.Y / ScanContent.Height, 0, 1));
    }

    private void PaintRegions()
    {
        Overlays.Children.Clear();
        var recognized = current?.SelectedCandidate;
        var fields = recognized?.Page == page ? recognized.Regions : null;
        if ((stamp ?? fields?.TitleBlock) is { } block) Draw(block, Colors.DodgerBlue, true);
        if ((designation ?? fields?.Designation) is { } first) Draw(first, Colors.DarkOrange);
        if ((name ?? fields?.Name) is { } second) Draw(second, Colors.MediumSeaGreen);
        if (dragStart is { } start && dragEnd is { } end) Draw(Area.Between(start, end), Colors.DodgerBlue);

        void Draw(Area area, Windows.UI.Color color, bool dashed = false)
        {
            if (!area.IsValid) return;
            var rect = new Rectangle
            {
                Width = area.Width * Sheet.Width,
                Height = area.Height * Sheet.Height,
                Stroke = new SolidColorBrush(color),
                StrokeThickness = RegionStrokeThickness,
                StrokeDashArray = dashed ? new DoubleCollection { 4, 3 } : null
            };
            Canvas.SetLeft(rect, area.X * Sheet.Width);
            Canvas.SetTop(rect, area.Y * Sheet.Height);
            Overlays.Children.Add(rect);
        }
    }

    private async void ReadRegions(object sender, RoutedEventArgs e)
    {
        if (!CanChangeDocuments || current is not { } row || designation is not { } first || name is not { } second) return;
        var number = page;
        var selected = row.SelectedCandidate;
        await ViewModel.ReadAsync(row, number, new(first, second), RegionTurn);
        if (current == row && page == number && !ReferenceEquals(selected, row.SelectedCandidate))
            await SelectPageAsync(number);
    }

    private async void ConfirmValues(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Busy || renaming || choosingDocuments || current is not { } row) return;
        await ViewModel.ConfirmAsync(row);
    }

    private async void ConfirmSelected(object sender, RoutedEventArgs e)
    {
        if (CanChangeDocuments)
            await ViewModel.ConfirmIncludedAsync();
    }

    private async void SaveRegions(object sender, RoutedEventArgs e) => await ExportRegionsAsync();

    private async Task ExportRegionsAsync()
    {
        if (ViewModel.Busy || renaming || choosingDocuments || current is not { } row ||
            designation is not { } first || name is not { } second) return;

        choosingDocuments = true;
        UpdateCommands();
        var path = row.Source;
        var number = page;
        var turn = RegionTurn;
        var regions = new FieldRegions(first, second, stamp);
        try
        {
            var options = await ViewModel.LoadRecognitionOptionsAsync(useDefaultsOnError: true);
            var template = await Task.Run(() => new DrawingAnalyzer().CreateTemplate(path, number, regions, turn,
                "Шаблон штампа", options, default));
            if (closing || current != row || page != number) return;
            if (template is null)
            {
                ShowRegionActions(true);
                RegionHint.Text = "Уточните границы штампа.";
                return;
            }

            await SaveTemplateAsync(template, Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            ViewModel.Message = $"Не удалось сохранить шаблон. {ex.Message}";
        }
        finally
        {
            choosingDocuments = false;
            if (!closing) UpdateCommands();
        }
    }

    private async Task SaveTemplateAsync(TitleTemplate template, string suggestedName)
    {
        var templateName = new TextBox { Header = "Имя шаблона", Text = suggestedName };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(templateName);
        content.Children.Add(new TextBlock
        {
            Text = "Выберите JSON для сохранения. В существующем файле одноимённый шаблон будет обновлён.",
            TextWrapping = TextWrapping.Wrap
        });
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = template.PageTurn is null ? "Сохранить шаблон штампа" : "Сохранить шаблон для страниц того же размера",
            Content = content,
            PrimaryButtonText = "Новый JSON", SecondaryButtonText = "Существующий JSON",
            CloseButtonText = "Пропустить", DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(suggestedName),
            IsSecondaryButtonEnabled = !string.IsNullOrWhiteSpace(suggestedName)
        };
        templateName.TextChanged += (_, _) =>
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = !string.IsNullOrWhiteSpace(templateName.Text);
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None || closing) return;
        var newFile = choice == ContentDialogResult.Primary;
        var file = newFile ? await PickNewTemplateFileAsync() : await FilePicker(".json").PickSingleFileAsync();
        if (file is null || closing) return;

        template = template with { Name = templateName.Text.Trim() };
        await Task.Run(() => template.Save(file.Path, newFile));
        var active = string.Equals(Path.GetFullPath(file.Path), ViewModel.TemplatePath, StringComparison.OrdinalIgnoreCase);
        ViewModel.Message = active ? "Шаблон сохранён и будет применён при следующем распознавании."
            : "Шаблон сохранён. Его можно загрузить в настройках.";
    }

    private async Task<Windows.Storage.StorageFile?> PickNewTemplateFileAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = "Шаблоны штампов" };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add("Шаблоны JSON", [".json"]);
        return await picker.PickSaveFileAsync();
    }

    private async void Rename(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Busy || renaming) return;
        renaming = true;
        UpdateCommands();
        try { await RenameSelectedAsync(); }
        finally
        {
            renaming = false;
            if (!closing) UpdateCommands();
        }
    }

    private async Task RenameSelectedAsync()
    {
        var selected = ViewModel.Rows.Where(row => row.Included && !row.InMergeProposal).ToArray();
        if (selected.Length == 0)
        {
            ViewModel.Message = "Отметьте файлы и подтвердите распознанные значения.";
            return;
        }

        var requests = selected.Where(row => row.Confirmed).Select(row => row.Request).ToArray();
        var unconfirmed = selected.Where(row => !row.Confirmed).Select(row =>
            new MoveResult(row.Source, null, MoveStatus.Skipped,
                row.NameError ?? "После проверки граф выделите строку и нажмите «Подтвердить».")).ToArray();
        string? destination = null;
        string? pickerError = null;
        var preserveRotations = true;
        while (!closing)
        {
            var plans = await Task.Run(() => RenameService.Preview(requests, destination));
            if (closing) return;
            var pending = plans.Where(plan => plan.Status == MoveStatus.Pending).ToArray();
            var approved = pending.Select(plan => plan.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var skipped = plans.Where(plan => plan.Status == MoveStatus.Skipped).Concat(unconfirmed).ToArray();
            var summary = $"Будет обработано файлов: {pending.Length}. Будет пропущено: {skipped.Length}.";
            var details = skipped.Select(plan => $"{Path.GetFileName(plan.Source)}\n{plan.Message}")
                .Concat(pending.Select(plan => $"{Path.GetFileName(plan.Source)} → {Path.GetFileName(plan.Target)}"));
            var location = destination is null ? "Сохранение в исходных папках." : $"Папка назначения\n{destination}";
            var message = $"{location}\n\n{summary}\n\n" + string.Join("\n\n", details);
            if (pickerError is not null) message = $"{pickerError}\n\n{message}";
            var content = new StackPanel { Spacing = 8 };
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "Переименование PDF",
                PrimaryButtonText = pending.Length == 0 ? "Записать пропуски" : destination is null ? "Переименовать" : "Скопировать",
                SecondaryButtonText = requests.Length == 0 ? "" : "Выбрать папку",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close,
                Content = content
            };
            content.Children.Add(DialogMessage(message));
            var saveRotations = new CheckBox { Content = "Сохранить поворот листов в PDF", IsChecked = preserveRotations };
            if (requests.Any(request => request.PageRotations is not null && approved.Contains(request.Source)))
                content.Children.Add(saveRotations);
            var reset = false;
            var removeOriginals = new CheckBox { Content = "Удалять исходные файлы после сохранения" };
            if (destination is not null)
            {
                removeOriginals.IsEnabled = pending.Length > 0;
                if (pending.Length > 0)
                {
                    removeOriginals.Checked += (_, _) => dialog.PrimaryButtonText = "Переместить";
                    removeOriginals.Unchecked += (_, _) => dialog.PrimaryButtonText = "Скопировать";
                }
                content.Children.Add(removeOriginals);
                var original = new Button { Content = "Сохранять в исходных папках" };
                original.Click += (_, _) => { reset = true; dialog.Hide(); };
                content.Children.Add(original);
            }

            var choice = await dialog.ShowAsync();
            preserveRotations = saveRotations.IsChecked == true;
            if (reset)
            {
                destination = null;
                continue;
            }
            if (choice == ContentDialogResult.Secondary)
            {
                try
                {
                    destination = await PickFolderPathAsync() ?? destination;
                    pickerError = null;
                }
                catch (Exception ex) { pickerError = $"Не удалось выбрать папку ({ex.GetType().Name})."; }
                continue;
            }
            if (choice == ContentDialogResult.Primary)
            {
                var accepted = requests.Where(request => approved.Contains(request.Source))
                    .Select(request => preserveRotations ? request : request with { PageRotations = null }).ToArray();
                await ViewModel.MoveAsync(accepted, skipped, destination, removeOriginals.IsChecked == true);
            }
            return;
        }
    }

    private async void Undo(object sender, RoutedEventArgs e)
    {
        if (!CanChangeDocuments || !ViewModel.CanUndo) return;
        if (await ConfirmDialog("Отменить последнюю операцию",
            "Перемещённые PDF вернутся в исходные папки с прежними именами и ориентацией. Созданные копии будут удалены, если исходные PDF и копии не изменились.",
            "Отменить операцию"))
            await ViewModel.MoveAsync(null);
    }

    private async void ShowHistory(object sender, RoutedEventArgs e)
    {
        try
        {
            var entries = await Task.Run(ViewModel.Renamer.History);
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = "Журнал переименований", CloseButtonText = "Закрыть",
                Content = new ListView { ItemsSource = entries.Count == 0 ? new[] { "Журнал пуст." } : entries,
                    MaxHeight = 440, MinWidth = 380 }
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ViewModel.Message = "Журнал занят, повреждён или недоступен.";
        }
    }

    private async Task<bool> ConfirmDialog(string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = title, PrimaryButtonText = primary, CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
            Content = DialogMessage(message)
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static ScrollViewer DialogMessage(string message) => new()
    {
        MaxHeight = 320,
        Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
    };
}
