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
    public Candidate? SelectedCandidate { get; private set; }
    public IReadOnlyList<Candidate> Candidates => Result?.Candidates ?? [];
    public RenameRequest Request => new(Source, Designation, DocumentName);
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
        Result = result;
        SelectedCandidate = null;
        Designation = "";
        DocumentName = "";
        if (result.Candidates.Count == 1) Use(result.Candidates[0]);
        var error = result.Candidates.Count == 1 ? NameError : null;
        Confirmed = result.Status == RecognitionStatus.Ready && result.Candidates.Count == 1 && error is null;
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
        Designation = candidate.Designation;
        DocumentName = candidate.Name;
        Diagnostic = NameError ?? (candidate.OcrConfidence is { } confidence
            ? $"Лист {candidate.Page} · OCR {confidence:P0}. Проверьте обе графы."
            : $"Лист {candidate.Page} · Проверьте обе графы.");
    }

    public bool Confirm()
    {
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
