using CommunityToolkit.Mvvm.ComponentModel;

namespace DrawingRenamer;

public sealed partial class DocumentRow : ObservableObject
{
    public DocumentRow(string path) => Source = path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OriginalName))]
    public partial string Source { get; set; }

    [ObservableProperty] public partial bool Included { get; set; } = true;
    [ObservableProperty] public partial string Designation { get; set; } = "";
    [ObservableProperty] public partial string DocumentName { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueStatus))]
    public partial string Status { get; set; } = "В очереди";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueNewName))]
    public partial string NewName { get; set; } = "";
    [ObservableProperty] public partial string Diagnostic { get; set; } = "";
    [ObservableProperty] public partial bool Confirmed { get; set; }
    [ObservableProperty] public partial string Separator { get; set; } = "_";
    [ObservableProperty] public partial string SheetNumberText { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueNewName), nameof(QueueStatus), nameof(ShowBranchContinuation))]
    public partial bool InMergeProposal { get; set; }
    [ObservableProperty] public partial bool ManuallyAssigned { get; set; }
    [ObservableProperty] public partial bool IsQueueDropTarget { get; set; }
    [ObservableProperty] public partial bool MergeEnabled { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBranchContinuation))]
    public partial bool IsGroupTail { get; set; }

    public bool IsMergeHeader => MergeProposal is not null;
    public bool IsFile => !IsMergeHeader;
    public string QueueStatus => IsFile && !InMergeProposal ? Status : "";
    public string QueueNewName => InMergeProposal ? "" : NewName;
    public bool ShowBranchContinuation => InMergeProposal && !IsGroupTail;
    public string RemoveQueueLabel => IsMergeHeader ? "Удалить группу из списка" : "Удалить из списка";
    public string GroupLabel => $"{(MergeExpanded ? "▾" : "▸")}  {MergeProposal?.Label}";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GroupLabel))]
    internal partial bool MergeExpanded { get; set; }
    internal MergeProposal? MergeProposal { get; set; }
    internal void UpdateMergeHeader(MergeProposal proposal)
    {
        MergeProposal = proposal;
        Source = proposal.Label;
        Diagnostic = proposal.CanMerge ? "Сохранить листы этой группы в один PDF" : "Проверьте имя и номера листов группы";
        OnPropertyChanged(nameof(GroupLabel));
    }
    internal bool MergeEligible { get; set; }
    internal int? SheetNumber => int.TryParse(SheetNumberText.Trim(), out var number) && number > 0 ? number : null;

    public string OriginalName => Path.GetFileName(Source);
    public RecognitionResult? Result { get; private set; }
    public string? SourceHash { get; private set; }
    public Candidate? SelectedCandidate { get; private set; }
    public IReadOnlyList<Candidate> Candidates => Result?.Candidates ?? [];
    public Dictionary<int, int> PageRotations { get; } = [];
    internal Dictionary<int, PageAlignment> PageAlignments { get; } = [];
    internal Dictionary<int, PageCrop> PendingPageCrops { get; } = [];
    internal IReadOnlyDictionary<int, PageAlignment>? AvailableAlignments { get; private set; }
    internal void SetAvailableAlignments(IReadOnlyDictionary<int, PageAlignment>? alignments)
    {
        AvailableAlignments = alignments;
        OnPropertyChanged(nameof(AvailableAlignments));
    }
    internal bool HasDocumentName => !string.IsNullOrWhiteSpace(NameForRename);
    public RenameRequest Request => new(Source, Designation, NameForRename)
    {
        Separator = Separator,
        ExpectedHash = SourceHash ?? throw new InvalidOperationException("Сначала подтвердите версию PDF."),
        PageRotations = PageRotations.Count == 0 ? null : new Dictionary<int, int>(PageRotations),
        PageAlignments = PageAlignments.Count == 0 ? null : new Dictionary<int, PageAlignment>(PageAlignments)
    };
    public string? NameError
    {
        get
        {
            RenameService.FileName(Designation, NameForRename, Separator, out var error);
            return error;
        }
    }

    // Rename validation uses the original title to recognize a document whose title contains only its type.
    private string NameForRename => SelectedCandidate is { } candidate &&
        DocumentName == (candidate.NameWithoutDocumentType ?? "") ? candidate.Name : DocumentName;

    public void Apply(RecognitionResult result)
    {
        if (SourceHash != result.SourceHash)
        {
            PageRotations.Clear();
            PageAlignments.Clear();
            PendingPageCrops.Clear();
        }
        MergeEligible = false;
        SheetNumberText = "";
        SourceHash = result.SourceHash;
        Result = result;
        SetAvailableAlignments(result.PageAlignments);
        SelectedCandidate = null;
        Designation = "";
        DocumentName = "";
        if (result.Candidates.Count == 1) Use(result.Candidates[0]);
        var error = result.Candidates.Count == 1 ? NameError : null;
        Confirmed = SourceHash is not null && result.Status == RecognitionStatus.Ready && result.Candidates.Count == 1 && error is null;
        if (result.Candidates is [{ Regions.Name.IsValid: false }])
            Diagnostic = result.Diagnostic;
        else if (error is not null)
            Diagnostic = error;
        else if (result.Candidates.Count != 1 || result.Candidates[0].OcrConfidence is null)
            Diagnostic = result.Diagnostic;
        Status = result.Status switch
        {
            RecognitionStatus.Ready when Confirmed => "Готово",
            RecognitionStatus.Error => "Ошибка",
            RecognitionStatus.Missing => "Не распознано",
            _ => "Проверить"
        };
        OnPropertyChanged(nameof(Candidates));
    }

    public void Use(Candidate candidate)
    {
        if (candidate.Turn != PageRotations.GetValueOrDefault(candidate.Page))
            PendingPageCrops.Remove(candidate.Page);
        if (candidate.SheetNumber is { } number) SheetNumberText = number.ToString();
        SelectedCandidate = candidate;
        if (candidate.Turn == 0) PageRotations.Remove(candidate.Page);
        else PageRotations[candidate.Page] = candidate.Turn;
        Designation = candidate.Designation;
        DocumentName = candidate.NameWithoutDocumentType ?? "";
        Edited();
        var source = candidate.OcrConfidence is { } confidence ? $"Лист {candidate.Page} · OCR {confidence:P0}." : $"Лист {candidate.Page} ·";
        var guidance = DocumentName.Length == 0 && HasDocumentName
            ? "Для имени файла используется обозначение."
            : "Проверьте обе графы.";
        Diagnostic = NameError ?? $"{source} {guidance}";
        OnPropertyChanged(nameof(HasDocumentName));
    }

    internal static string CandidateLabel(Candidate candidate)
    {
        var label = $"Лист {candidate.Page} · {candidate.Designation}";
        return candidate.NameWithoutDocumentType is { Length: > 0 } name ? $"{label} - {name}" : label;
    }

    internal void ApplySavedPageEdits(IReadOnlyDictionary<int, int>? rotations,
        IReadOnlyDictionary<int, PageAlignment>? alignments, string hash)
    {
        SourceHash = hash;
        if (rotations is not null) PageRotations.Clear();
        if (alignments is not null) PageAlignments.Clear();
        PendingPageCrops.Clear();
        SetAvailableAlignments(null);
        if (Result is { } result) Result = result with { SourceHash = hash, PageAlignments = null,
            Candidates = result.Candidates.Where(candidate => alignments?.ContainsKey(candidate.Page) != true).Select(Rotate).ToArray() };
        if (SelectedCandidate is { } selected)
            SelectedCandidate = alignments?.ContainsKey(selected.Page) == true ? null : Rotate(selected);

        Candidate Rotate(Candidate candidate)
        {
            if (rotations?.TryGetValue(candidate.Page, out var turns) != true) return candidate;
            var regions = candidate.Regions;
            return candidate with
            {
                Turn = (candidate.Turn + 4 - turns) % 4,
                Regions = new(regions.Designation.Rotate(turns), regions.Name.Rotate(turns), regions.TitleBlock?.Rotate(turns))
            };
        }
    }

    internal void RefreshSavedPreview() => OnPropertyChanged(nameof(Source));

    public bool ObserveVersion(string? hash)
    {
        if (hash is null)
        {
            Confirmed = false;
            Status = "Проверить";
            Diagnostic = "Не удалось проверить версию исходного PDF.";
            return false;
        }
        if (SourceHash is null || SourceHash == hash)
        {
            SourceHash = hash;
            return true;
        }
        InvalidateVersion(hash);
        return false;
    }

    public void InvalidateVersion(string? hash = null) =>
        Apply(new(RecognitionStatus.Review, 0, [], "PDF изменился. Проверьте графы заново.") { SourceHash = hash });

    public bool Confirm(string hash)
    {
        if (!ObserveVersion(hash)) return false;
        var error = NameError;
        Confirmed = error is null;
        Status = Confirmed ? "Подтверждено" : "Проверить";
        Diagnostic = error ?? "Значения проверены.";
        if (Confirmed && PendingPageCrops.Count > 0)
        {
            foreach (var (page, crop) in PendingPageCrops)
                PageAlignments[page] = PageAlignments.GetValueOrDefault(page, PageAlignment.Original) with { Crop = crop };
            PendingPageCrops.Clear();
            RefreshSavedPreview();
        }
        return Confirmed;
    }

    partial void OnDesignationChanged(string value) => Edited();
    partial void OnDocumentNameChanged(string value) => Edited();

    partial void OnSeparatorChanged(string value)
    {
        NewName = RenameService.FileName(Designation, NameForRename, value, out var error) ?? "";
        if (Confirmed && error is not null)
        {
            Confirmed = false;
            Status = "Проверить";
        }
        Diagnostic = error ?? (Confirmed ? "Значения проверены." : "Подтвердите введённые значения.");
    }

    private void Edited()
    {
        Confirmed = false;
        var name = RenameService.FileName(Designation, NameForRename, Separator, out var error);
        NewName = name ?? "";
        Status = "Проверить";
        Diagnostic = error ?? "Подтвердите введённые значения.";
    }
}

internal sealed class MergeProposal(string folder, string designation, DocumentRow[] pages)
{
    public string Folder { get; } = folder;
    public string Designation { get; } = designation;
    public string Key => MakeKey(Folder, Designation);
    public DocumentRow[] Pages { get; } = pages;
    public DocumentRow[] Ordered => Pages.OrderBy(page => page.SheetNumber ?? int.MaxValue).ToArray();
    public DocumentRow? NamedSheet => Ordered.FirstOrDefault(page => page.NewName.Length > 0 &&
        string.Equals(page.Designation.Trim(), Designation, StringComparison.OrdinalIgnoreCase));
    public string? FileName => NamedSheet?.NewName;
    public string Label => FileName ?? $"{Designation}.pdf";
    public bool CanMerge => FileName is not null && Ordered.Select(page => page.SheetNumber)
        .SequenceEqual(Enumerable.Range(1, Pages.Length).Select(number => (int?)number));

    internal static string MakeKey(string folder, string designation) => $"{folder}\0{designation.Trim()}";
}
