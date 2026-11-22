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
    [ObservableProperty] public partial string Status { get; set; } = "В очереди";
    [ObservableProperty] public partial string NewName { get; set; } = "";
    [ObservableProperty] public partial string Diagnostic { get; set; } = "";
    [ObservableProperty] public partial bool Confirmed { get; set; }

    public string OriginalName => Path.GetFileName(Source);
    public RecognitionResult? Result { get; private set; }
    public string? SourceHash { get; private set; }
    public Candidate? SelectedCandidate { get; private set; }
    public IReadOnlyList<Candidate> Candidates => Result?.Candidates ?? [];
    public Dictionary<int, int> PageRotations { get; } = [];
    public RenameRequest Request => new(Source, Designation, DocumentName)
    {
        ExpectedHash = SourceHash ?? throw new InvalidOperationException("Сначала подтвердите версию PDF."),
        PageRotations = PageRotations.Count == 0 ? null : new Dictionary<int, int>(PageRotations)
    };
    public string? NameError
    {
        get
        {
            RenameService.FileName(Designation, DocumentName, out var error);
            return error;
        }
    }

    public void Apply(RecognitionResult result)
    {
        if (SourceHash != result.SourceHash) PageRotations.Clear();
        SourceHash = result.SourceHash;
        Result = result;
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
        Confirmed = false;
        Status = "Проверить";
        SelectedCandidate = candidate;
        if (candidate.Turn == 0) PageRotations.Remove(candidate.Page);
        else PageRotations[candidate.Page] = candidate.Turn;
        Designation = candidate.Designation;
        DocumentName = candidate.Name;
        Diagnostic = NameError ?? (candidate.OcrConfidence is { } confidence
            ? $"Лист {candidate.Page} · OCR {confidence:P0}. Проверьте обе графы."
            : $"Лист {candidate.Page} · Проверьте обе графы.");
    }

    public void ApplySavedRotations(IReadOnlyDictionary<int, int> rotations, string hash)
    {
        SourceHash = hash;
        PageRotations.Clear();
        if (Result is { } result) Result = result with { SourceHash = hash, Candidates = result.Candidates.Select(Rotate).ToArray() };
        if (SelectedCandidate is { } selected) SelectedCandidate = Rotate(selected);

        Candidate Rotate(Candidate candidate)
        {
            if (!rotations.TryGetValue(candidate.Page, out var turns)) return candidate;
            var regions = candidate.Regions;
            return candidate with
            {
                Turn = (candidate.Turn + 4 - turns) % 4,
                Regions = new(regions.Designation.Rotate(turns), regions.Name.Rotate(turns), regions.TitleBlock?.Rotate(turns))
            };
        }
    }

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
        return Confirmed;
    }

    partial void OnDesignationChanged(string value) => Edited();
    partial void OnDocumentNameChanged(string value) => Edited();

    private void Edited()
    {
        Confirmed = false;
        var name = RenameService.FileName(Designation, DocumentName, out var error);
        NewName = name ?? "";
        Status = "Проверить";
        Diagnostic = error ?? "Подтвердите введённые значения.";
    }
}
