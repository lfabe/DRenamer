using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DrawingRenamer;

public enum MoveStatus { Skipped, Failed, Pending, Moved, UndoPending, Undone, Copied, CopyRemoved }
public sealed record RenameRequest(string Source, string Designation, string Name);
public sealed record MoveResult(string Source, string? Target, MoveStatus Status, string Message);

public sealed class RenameService(string journalPath)
{
    private sealed record Entry(Guid Batch, DateTimeOffset Time, MoveResult Result, string? Hash);

    public static string? FileName(string designation, string name) => FileName(designation, name, out _);

    public static string? FileName(string designation, string name, out string? error)
    {
        var fieldErrors = new[] { FieldError(designation, "Обозначение"), FieldError(name, "Наименование") };
        if (fieldErrors.Any(message => message is not null))
        {
            error = string.Join(" ", fieldErrors.Where(message => message is not null));
            return null;
        }

        var fileName = $"{DrawingAnalyzer.NormalizeName(designation)} - {DrawingAnalyzer.NormalizeName(name)}.pdf";
        error = fileName switch
        {
            { Length: > 255 } => "Сократите обозначение или наименование. Имя файла с расширением должно содержать не более 255 символов.",
            _ when Regex.IsMatch(fileName, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])\s*\.", RegexOptions.IgnoreCase)
                => "Измените обозначение. Имя файла начинается с зарезервированного имени Windows.",
            _ => null
        };
        return error is null ? fileName : null;
    }

    private static string? FieldError(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"Заполните графу «{field}».";
        var forbidden = value.Where(c => "<>:\"/\\|?*".Contains(c)).Distinct().Select(c => $"«{c}»").ToArray();
        if (forbidden.Length > 0)
            return $"Удалите из графы «{field}» запрещённые в именах файлов Windows символы {string.Join(", ", forbidden)}.";
        return value.Any(c => c < ' ') ? $"Удалите управляющие символы из графы «{field}»." : null;
    }

    public static IReadOnlyList<MoveResult> Preview(IReadOnlyList<RenameRequest> requests, string? destination = null)
    {
        if (destination is not null) destination = Path.GetFullPath(destination);
        var destinationMissing = destination is not null && !Directory.Exists(destination);
        var items = requests.Select(r =>
        {
            var name = FileName(r.Designation, r.Name, out var error);
            var source = Path.GetFullPath(r.Source);
            var target = name is null ? null : Path.Combine(destination ?? Path.GetDirectoryName(source)!, name);
            return (Source: source, Target: target, Error: error);
        }).ToArray();
        var comparer = StringComparer.OrdinalIgnoreCase;
        var targets = items.Where(i => i.Target != null).ToLookup(i => i.Target, comparer);
        var sources = items.ToLookup(i => i.Source, comparer);
        return items.Select(i =>
        {
            var reason = i.Target switch
            {
                null => i.Error!,
                _ when !string.Equals(Path.GetExtension(i.Source), ".pdf", StringComparison.OrdinalIgnoreCase) =>
                    "Допускаются только PDF.",
                _ when destinationMissing => "Выбранная папка назначения недоступна.",
                _ when comparer.Equals(i.Source, i.Target) => "Имя уже совпадает.",
                _ when targets[i.Target].Count() > 1 => "Повторяющееся целевое имя.",
                _ when sources[i.Source].Count() > 1 => "Исходный файл выбран несколько раз.",
                _ when File.Exists(i.Target) || Directory.Exists(i.Target) => "Целевой путь уже занят.",
                _ => ""
            };
            return new MoveResult(i.Source, i.Target, reason.Length == 0 ? MoveStatus.Pending : MoveStatus.Skipped, reason);
        }).ToArray();
    }

    public IReadOnlyList<MoveResult> Execute(IReadOnlyList<RenameRequest> requests,
        IReadOnlyList<MoveResult>? skipped = null, string? destination = null, bool removeOriginals = false)
    {
        using var lease = LockJournal();
        var history = Load();
        var batch = Guid.NewGuid();
        var results = new List<MoveResult>();
        var copy = destination is not null && !removeOriginals;
        foreach (var plan in Preview(requests, destination))
        {
            string? hash = null;
            if (plan.Status == MoveStatus.Pending)
            {
                try { hash = Hash(plan.Source); }
                catch (Exception ex) when (IsFileError(ex))
                {
                    Record(plan with { Status = MoveStatus.Failed, Message = Error(ex) }, null);
                    continue;
                }
                Record(plan with { Message = copy ? "Копирование начато." : "Переименование начато." }, hash, addResult: false);
                Record(copy ? Copy(plan, hash) : Move(plan, undo: false), hash, replace: true);
            }
            else Record(plan, hash);
        }
        if (skipped is { Count: > 0 })
        {
            foreach (var item in skipped)
            {
                var result = item with { Status = MoveStatus.Skipped };
                results.Add(result);
                history.Add(new(batch, DateTimeOffset.Now, result, null));
            }
            Save(history);
        }
        return results;

        void Record(MoveResult result, string? hash, bool replace = false, bool addResult = true)
        {
            var entry = new Entry(batch, DateTimeOffset.Now, result, hash);
            if (replace) history[^1] = entry;
            else history.Add(entry);
            Save(history);
            if (addResult) results.Add(result);
        }
    }

    public IReadOnlyList<MoveResult> Undo()
    {
        using var lease = LockJournal();
        var history = Load();
        var last = history.FindLast(e => e.Result.Status is MoveStatus.Moved or MoveStatus.Copied);
        if (last is null) return [];
        var results = new List<MoveResult>();
        for (var index = history.Count - 1; index >= 0; index--)
        {
            var entry = history[index];
            if (entry.Batch != last.Batch || entry.Result.Status is not (MoveStatus.Moved or MoveStatus.Copied)) continue;
            var plan = entry.Result;
            var copied = plan.Status == MoveStatus.Copied;
            MoveResult result;
            try
            {
                if (!copied && (File.Exists(plan.Source) || Directory.Exists(plan.Source) ||
                    !string.Equals(Hash(plan.Target!), entry.Hash, StringComparison.Ordinal)))
                {
                    result = plan with { Status = MoveStatus.Skipped, Message = "Исходный путь занят или PDF изменён." };
                    RecordFailure(result);
                    continue;
                }
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                RecordFailure(plan with { Status = MoveStatus.Failed, Message = Error(ex) });
                continue;
            }
            history[index] = entry with { Result = plan with { Status = MoveStatus.UndoPending, Message = "Отмена начата." } };
            Save(history);
            result = copied ? RemoveCopy(plan, entry.Hash!) : Move(plan, undo: true);
            var undone = result.Status is MoveStatus.Undone or MoveStatus.CopyRemoved;
            history[index] = entry with { Result = undone ? result : plan };
            if (!undone) RecordFailure(result);
            else
            {
                results.Add(result);
                Save(history);
            }
        }
        return results;

        void RecordFailure(MoveResult result)
        {
            results.Add(result);
            history.Add(new(last.Batch, DateTimeOffset.Now, result, null));
            Save(history);
        }
    }

    private static MoveResult Copy(MoveResult plan, string hash)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(plan.Target)!, $".drawing-renamer-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            using (var input = File.OpenRead(plan.Source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            if (!string.Equals(Hash(temporary), hash, StringComparison.Ordinal))
                return plan with { Status = MoveStatus.Failed, Message = "Исходный PDF изменился во время копирования." };
            File.Move(temporary, plan.Target!, overwrite: false);
            created = false;
            return plan with { Status = MoveStatus.Copied, Message = "Копия сохранена." };
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return plan with { Status = MoveStatus.Failed, Message = Error(ex) };
        }
        finally
        {
            try { if (created) File.Delete(temporary); }
            catch (Exception ex) when (IsFileError(ex)) { }
        }
    }

    private static MoveResult RemoveCopy(MoveResult plan, string hash)
    {
        try
        {
            if (!File.Exists(plan.Source))
                return plan with { Status = MoveStatus.Skipped, Message = "Исходный PDF отсутствует. Копия сохранена." };
            using var original = File.OpenRead(plan.Source);
            using var copy = new FileStream(plan.Target!, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (Convert.ToHexString(SHA256.HashData(original)) != hash || Convert.ToHexString(SHA256.HashData(copy)) != hash)
                return plan with { Status = MoveStatus.Skipped, Message = "Исходный PDF или копия изменены." };
            File.Delete(plan.Target!);
            return plan with { Status = MoveStatus.CopyRemoved, Message = "Созданная копия удалена." };
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return plan with { Status = MoveStatus.Failed, Message = Error(ex) };
        }
    }

    public IReadOnlyList<string> History()
    {
        using var lease = LockJournal();
        return Load().AsEnumerable().Reverse().Select(e =>
            $"{e.Time:g}   {Path.GetFileName(e.Result.Source)}   {e.Result.Message}").ToArray();
    }

    private static MoveResult Move(MoveResult plan, bool undo)
    {
        try
        {
            var from = undo ? plan.Target! : plan.Source;
            var to = undo ? plan.Source : plan.Target!;
            File.Move(from, to, overwrite: false);
            if (File.Exists(from))
                return plan with { Status = MoveStatus.Failed, Message = "Копия сохранена, но файл остался и по прежнему пути." };
            return plan with { Status = undo ? MoveStatus.Undone : MoveStatus.Moved,
                Message = undo ? "Исходное имя восстановлено." : "Файл переименован." };
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return plan with { Status = MoveStatus.Failed, Message = Error(ex) };
        }
    }

    private FileStream LockJournal()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(journalPath))!);
        return new(journalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private List<Entry> Load() => File.Exists(journalPath)
        ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllBytes(journalPath)) ??
          throw new InvalidDataException("Журнал операций повреждён.")
        : [];

    private void Save(List<Entry> history)
    {
        var pending = journalPath + ".tmp";
        using (var output = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(output, history);
            output.Flush(flushToDisk: true);
        }
        File.Move(pending, journalPath, overwrite: true);
    }

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException;
    private static string Error(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "Файл или папка отсутствует.",
        UnauthorizedAccessException => "Нет доступа к файлу.",
        _ => "Файл занят или целевой путь недоступен."
    };
}
