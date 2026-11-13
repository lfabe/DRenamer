using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
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
    private DocumentRow? current;
    private int page = 1;
    private int pageCount;
    private Area? designation;
    private Area? name;
    private PointD? dragStart;
    private PointD? dragEnd;
    private bool markingDesignation;
    private bool marking;
    private bool closing;
    private bool renaming;
    private bool choosingDocuments;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Drawing Renamer";
        AppWindow.Resize(new(1320, 820));
        ViewModel.PropertyChanged += ViewModelChanged;
        ViewModel.AnalyzeCommand.PropertyChanged += (_, _) => UpdateCommands();
        ViewModel.Rows.CollectionChanged += (_, _) => UpdateCommands();
        Activated += async (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                await RefreshDocumentsAsync();
        };
        Closed += (_, _) =>
        {
            closing = true;
            preview?.Cancel();
            ViewModel.CancelCommand.Execute(null);
        };
        UpdateCommands();
    }

    private async void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.Busy)) UpdateCommands();
        if (e.PropertyName == nameof(ViewModel.Recursive)) await RefreshDocumentsAsync();
    }

    private async void RefreshDocuments(object sender, RoutedEventArgs e) => await RefreshDocumentsAsync();

    private Task RefreshDocumentsAsync() => closing || renaming || choosingDocuments
        ? Task.CompletedTask : ViewModel.RefreshDocumentsAsync();

    private void UpdateCommands()
    {
        var available = !ViewModel.Busy && !renaming && !choosingDocuments;
        AddFilesButton.IsEnabled = available;
        AddFolderButton.IsEnabled = available;
        RecursiveCheckBox.IsEnabled = available;
        RefreshButton.IsEnabled = available && ViewModel.HasDocumentSources;
        AnalyzeButton.IsEnabled = available && ViewModel.Rows.Count > 0;
        CancelButton.Visibility = ViewModel.AnalyzeCommand.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        RenameButton.IsEnabled = available && ViewModel.Rows.Count > 0;
        UndoButton.IsEnabled = available;
        Editor.IsHitTestVisible = available;
        Documents.IsEnabled = available;
        ReadRegionsButton.IsEnabled = available && designation.HasValue && name.HasValue;
        EmptyQueue.Visibility = ViewModel.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            if (!closing) ViewModel.Add(files.Select(file => file.Path));
        }
        catch (Exception ex) { ViewModel.Message = $"Не удалось открыть выбор файлов ({ex.GetType().Name})."; }
        finally { choosingDocuments = false; if (!closing) UpdateCommands(); }
    }

    private async void PickFolder(object sender, RoutedEventArgs e)
    {
        choosingDocuments = true;
        UpdateCommands();
        try
        {
            if (await PickFolderPathAsync() is { } path && !closing)
                await ViewModel.AddFolderAsync(path);
        }
        catch (Exception ex) { ViewModel.Message = $"Не удалось выбрать папку ({ex.GetType().Name})."; }
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or System.Runtime.InteropServices.COMException)
        {
            ViewModel.Message = $"Не удалось прочитать шаблоны ({ex.GetType().Name}).";
        }
    }

    private void ResetTemplates(object sender, RoutedEventArgs e) => ViewModel.SetTemplates(null);

    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Root is null) return;
        Root.RequestedTheme = ((ComboBox)sender).SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private void LanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is ComboBoxItem { Tag: string value }) ViewModel.Languages = value;
    }

    private async void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (current is not null) current.PropertyChanged -= RowChanged;
        current = Documents.SelectedItem as DocumentRow;
        if (current is not null) current.PropertyChanged += RowChanged;
        Editor.DataContext = current;
        Editor.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
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
    }

    private void RefreshCandidates()
    {
        var candidates = current?.Candidates ?? [];
        CandidatePicker.ItemsSource = candidates.Select(candidate =>
            $"Лист {candidate.Page} · {candidate.Designation} - {candidate.Name}").ToArray();
        CandidatePicker.Visibility = candidates.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CandidateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (current is not { } row || CandidatePicker.SelectedIndex < 0 ||
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
        using var operation = new CancellationTokenSource();
        preview = operation;
        var row = current;
        page = Math.Max(1, number);
        designation = name = null;
        marking = false;
        dragStart = dragEnd = null;
        RegionActions.Visibility = RegionReadActions.Visibility = RegionControls.Visibility = Visibility.Collapsed;
        PageImage.Source = null;
        Sheet.Width = Sheet.Height = 0;
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
                var count = knownCount.GetValueOrDefault();
                if (count < 1)
                {
                    using var document = UglyToad.PdfPig.PdfDocument.Open(path);
                    count = document.NumberOfPages;
                }
                return (count, raster: renderer.Render(path, number, Area.Full, 1800, operation.Token));
            });
            operation.Token.ThrowIfCancellationRequested();
            var pixels = result.raster.Pixels;
            using var writer = new DataWriter();
            writer.WriteBytes(pixels);
            using var buffer = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8,
                result.raster.Width, result.raster.Height, BitmapAlphaMode.Ignore);
            var bitmap = new SoftwareBitmapSource();
            await bitmap.SetBitmapAsync(buffer);
            operation.Token.ThrowIfCancellationRequested();
            var scale = Math.Min(Math.Max(100, PreviewScroll.ActualWidth - 16) / result.raster.Width,
                Math.Max(100, PreviewScroll.ActualHeight - 58) / result.raster.Height);
            Sheet.Width = PageImage.Width = result.raster.Width * scale;
            Sheet.Height = PageImage.Height = result.raster.Height * scale;
            PageImage.Source = bitmap;
            PreviewScroll.ChangeView(0, 0, 1, true);
            pageCount = result.count;
            PageLabel.Text = $"{page}/{pageCount}";
            PreviousButton.IsEnabled = page > 1;
            NextButton.IsEnabled = page < pageCount;
            PreviewMessage.Visibility = Visibility.Collapsed;
            RegionControls.Visibility = Visibility.Visible;
            if (row.SelectedCandidate is { } candidate && candidate.Page == page)
                Orientation.SelectedIndex = candidate.Turn;
            else Orientation.SelectedIndex = 0;
            PaintRegions();
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

    private void ToggleRegions(object sender, RoutedEventArgs e)
    {
        var open = RegionActions.Visibility == Visibility.Collapsed;
        RegionActions.Visibility = RegionReadActions.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        marking = false;
        RegionHint.Text = "";
    }

    private void MarkDesignation(object sender, RoutedEventArgs e) => Mark(true);
    private void MarkName(object sender, RoutedEventArgs e) => Mark(false);

    private void Mark(bool forDesignation)
    {
        marking = true;
        markingDesignation = forDesignation;
        RegionHint.Text = "Обведите графу на листе.";
    }

    private void RegionPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!marking || ViewModel.Busy) return;
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
            if (markingDesignation) designation = region;
            else name = region;
            marking = false;
            RegionHint.Text = designation.HasValue && name.HasValue ? "Выделены обе графы." : "Выделите вторую графу.";
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
        var point = e.GetCurrentPoint(Sheet).Position;
        return new(Math.Clamp(point.X / Sheet.Width, 0, 1), Math.Clamp(point.Y / Sheet.Height, 0, 1));
    }

    private void PaintRegions()
    {
        Overlays.Children.Clear();
        var recognized = current?.SelectedCandidate;
        var fields = recognized?.Page == page ? recognized.Regions : null;
        if (fields?.TitleBlock is { } block) Draw(block, Colors.DodgerBlue, true);
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
                StrokeThickness = 2,
                StrokeDashArray = dashed ? new DoubleCollection { 4, 3 } : null
            };
            Canvas.SetLeft(rect, area.X * Sheet.Width);
            Canvas.SetTop(rect, area.Y * Sheet.Height);
            Overlays.Children.Add(rect);
        }
    }

    private async void ReadRegions(object sender, RoutedEventArgs e)
    {
        if (current is not { } row || designation is not { } first || name is not { } second) return;
        await ViewModel.ReadAsync(row, page, new(first, second), Orientation.SelectedIndex);
        PaintRegions();
    }

    private void ConfirmValues(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.Busy && current?.Confirm() == true) ViewModel.Message = "Значения подтверждены.";
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
        var selected = ViewModel.Rows.Where(row => row.Included).ToArray();
        if (selected.Length == 0)
        {
            ViewModel.Message = "Отметьте файлы и подтвердите распознанные значения.";
            return;
        }

        var requests = selected.Where(row => row.Confirmed).Select(row => row.Request).ToArray();
        var unconfirmed = selected.Where(row => !row.Confirmed).Select(row =>
            new MoveResult(row.Source, null, MoveStatus.Skipped,
                row.NameError ?? "Нажмите «Подтвердить значения» после проверки обеих граф.")).ToArray();
        string? destination = null;
        string? pickerError = null;
        while (!closing)
        {
            var plans = await Task.Run(() => RenameService.Preview(requests, destination));
            if (closing) return;
            var pending = plans.Where(plan => plan.Status == MoveStatus.Pending).ToArray();
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
                var approved = pending.Select(plan => plan.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
                await ViewModel.MoveAsync(requests.Where(request => approved.Contains(request.Source)).ToArray(), skipped,
                    destination, removeOriginals.IsChecked == true);
            }
            return;
        }
    }

    private async void Undo(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Busy || renaming) return;
        if (await ConfirmDialog("Отменить последнюю операцию",
            "Перемещённые файлы вернутся в исходные папки под прежними именами. Созданные копии будут удалены, если исходные PDF и копии не изменились.",
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
