using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DrawingRenamer;

public enum MoveStatus { Skipped, Failed, Pending, Moved, UndoPending, Undone, Copied, CopyRemoved }
public sealed record RenameRequest(string Source, string Designation, string Name)
{
    public string Separator { get; init; } = "_";
    public string? ExpectedHash { get; init; }
    public IReadOnlyDictionary<int, int>? PageRotations { get; init; }
}
public sealed record MoveResult(string Source, string? Target, MoveStatus Status, string Message)
{
    [System.Text.Json.Serialization.JsonIgnore] public bool SourceChanged { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public string? ResultHash { get; init; }
    public IReadOnlyDictionary<int, int>? PageRotations { get; init; }
}

public sealed class JournalWriteException(IReadOnlyList<MoveResult> completed, Exception cause)
    : Exception("Запись журнала не удалась. Обработка остановлена. Результаты выполненных операций сохранены в этом сеансе.", cause)
{
    public IReadOnlyList<MoveResult> Completed { get; } = completed;
}

public sealed class RenameService(string journalPath)
{
    private readonly Action<string, string> replaceJournal = (from, to) => File.Move(from, to, overwrite: true);
    internal RenameService(string journalPath, Action<string, string> replaceJournal) : this(journalPath) =>
        this.replaceJournal = replaceJournal;
    // Keep the open session independent when another instance replaces the journal file.
    private Entry[]? sessionHistory;
    private sealed record RotationRevision(string TargetHash, long OriginalLength, bool KeepSource);
    private sealed record Entry(Guid Batch, DateTimeOffset Time, MoveResult Result, string? Hash, RotationRevision? Rotation = null);

    public static string? FileName(string designation, string name) => FileName(designation, name, out _);

    public static string? FileName(string designation, string name, out string? error) =>
        FileName(designation, name, "_", out error);

    public static string? FileName(string designation, string name, string separator, out string? error)
    {
        error = SeparatorError(separator);
        if (error is not null) return null;
        var title = DocumentTypeName.ForFileName(name);
        var fieldErrors = new[] { FieldError(designation, "Обозначение"),
            title is null ? null : FieldError(title, "Наименование", allowLowercase: true) };
        if (fieldErrors.Any(message => message is not null))
        {
            error = string.Join(" ", fieldErrors.Where(message => message is not null));
            return null;
        }

        var titlePart = title is null ? "" : $"{separator}{DrawingAnalyzer.NormalizeName(title)}";
        var fileName = $"{DrawingAnalyzer.NormalizeName(designation)}{titlePart}.pdf";
        error = fileName switch
        {
            { Length: > 255 } => "Сократите обозначение или наименование. Имя файла с расширением должно содержать не более 255 символов.",
            _ when Regex.IsMatch(fileName, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])\s*\.", RegexOptions.IgnoreCase)
                => "Измените обозначение. Имя файла начинается с зарезервированного имени Windows.",
            _ => null
        };
        return error is null ? fileName : null;
    }

    public static string? SeparatorError(string? separator)
    {
        if (separator is null || separator.Length is < 1 or > 6)
            return "Разделитель: введите от одного до трёх знаков или пробелов.";
        var symbols = separator.EnumerateRunes().ToArray();
        if (symbols.Length > 3 || string.Concat(symbols.Select(symbol => symbol.ToString())) != separator)
            return "Разделитель: введите от одного до трёх корректных символов.";
        foreach (var symbol in symbols)
        {
            if (symbol.Value < 32 || symbol.Value <= char.MaxValue && "<>:\"/\\|?*".Contains((char)symbol.Value))
                return $"Разделитель содержит запрещённый Windows символ U+{symbol.Value:X4}.";
            if (Rune.IsLetter(symbol) || Rune.IsNumber(symbol))
                return "Разделитель не может содержать буквы или цифры.";
            if (symbol.Value != ' ' && !Rune.IsPunctuation(symbol) && !Rune.IsSymbol(symbol))
                return "Разделитель может содержать только видимые знаки и обычные пробелы.";
        }
        return null;
    }

    private static string? FieldError(string value, string field, bool allowLowercase = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"Заполните графу «{field}».";
        var forbidden = value.EnumerateRunes()
            .Select(symbol => symbol.Value switch
            {
                >= 'A' and <= 'Z' or >= 'А' and <= 'Я' or 'Ё' or >= '0' and <= '9' or '.' or ' ' or '‒' => null,
                >= 'a' and <= 'z' or >= 'а' and <= 'я' or 'ё' => allowLowercase ? null : "строчные буквы",
                '_' or '№' when allowLowercase => null,
                _ when Rune.IsWhiteSpace(symbol) || Rune.GetUnicodeCategory(symbol) is UnicodeCategory.Control or UnicodeCategory.Format
                    => $"U+{symbol.Value:X4}",
                _ => $"«{symbol}» (U+{symbol.Value:X4})"
            })
            .OfType<string>()
            .Distinct()
            .ToArray();
        return forbidden.Length == 0 ? null : $"Недопустимые символы в графе «{field}» — {string.Join(", ", forbidden)}.";
    }

    public static IReadOnlyList<MoveResult> Preview(IReadOnlyList<RenameRequest> requests, string? destination = null)
    {
        if (destination is not null) destination = Path.GetFullPath(destination);
        var destinationMissing = destination is not null && !Directory.Exists(destination);
        var items = requests.Select(r =>
        {
            var name = FileName(r.Designation, r.Name, r.Separator, out var error);
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
        foreach (var (plan, request) in Preview(requests, destination).Zip(requests))
        {
            string? hash = null;
            if (plan.Status == MoveStatus.Pending)
            {
                try { hash = request.ExpectedHash ?? Hash(plan.Source); }
                catch (Exception ex) when (IsFileError(ex))
                {
                    Record(plan with { Status = MoveStatus.Failed, Message = Error(ex) }, null);
                    continue;
                }
                var started = plan with { Message = copy ? "Копирование начато." : "Переименование начато.",
                    PageRotations = request.PageRotations };
                if (request.PageRotations is { Count: > 0 } turns)
                {
                    RotationRevision? rotation = null;
                    var result = SaveWithRotation(plan with { PageRotations = turns }, hash, turns, copy, revision =>
                    {
                        rotation = revision;
                        Record(started, hash, addResult: false, rotation: revision);
                    });
                    Record(result, hash, replace: rotation is not null, rotation: rotation);
                }
                else
                {
                    Record(started, hash, addResult: false);
                    Record(copy ? Copy(plan, hash) : Move(plan, hash, undo: false), hash, replace: true);
                }
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
            Persist(history, results);
        }
        return results;

        void Record(MoveResult result, string? hash, bool replace = false, bool addResult = true, RotationRevision? rotation = null)
        {
            var entry = new Entry(batch, DateTimeOffset.Now, result, hash, rotation);
            if (replace) history[^1] = entry;
            else history.Add(entry);
            if (addResult) results.Add(result);
            Persist(history, results, completed: addResult);
        }
    }

    public bool HasUndoableOperations()
    {
        try
        {
            using var lease = LockJournal();
            return Load().FindLast(CanUndo) is not null;
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            return false;
        }
    }

    public IReadOnlyList<MoveResult> Undo()
    {
        using var lease = LockJournal();
        var history = Load();
        var last = history.FindLast(CanUndo);
        if (last is null) return [];
        var results = new List<MoveResult>();
        for (var index = history.Count - 1; index >= 0; index--)
        {
            var entry = history[index];
            if (entry.Batch != last.Batch || !CanUndo(entry)) continue;
            var plan = entry.Result;
            MoveResult result;
            if (entry.Rotation is { } rotation)
            {
                result = UndoRotation(plan, entry.Hash!, rotation);
            }
            else
            {
                var copied = plan.Status == MoveStatus.Copied;
                if (!copied && (File.Exists(plan.Source) || Directory.Exists(plan.Source)))
                {
                    RecordFailure(plan with { Status = MoveStatus.Skipped, Message = "Исходный путь занят." });
                    continue;
                }
                result = copied ? RemoveCopy(plan, entry.Hash!) : Move(plan, entry.Hash!, undo: true);
            }
            var undone = result.Status is MoveStatus.Undone or MoveStatus.CopyRemoved;
            history[index] = entry with { Result = undone ? result : plan };
            if (!undone) RecordFailure(result);
            else
            {
                results.Add(result);
                Persist(history, results);
            }
        }
        return results;

        void RecordFailure(MoveResult result)
        {
            results.Add(result);
            history.Add(new(last.Batch, DateTimeOffset.Now, result, null));
            Persist(history, results);
        }
    }

    private static bool CanUndo(Entry entry) =>
        (entry.Result.Status is MoveStatus.Moved or MoveStatus.Copied ||
            entry.Result.Status == MoveStatus.Pending && entry.Rotation is not null) &&
        File.Exists(entry.Result.Target);

    private static MoveResult Copy(MoveResult plan, string hash)
    {
        try
        {
            using var input = File.OpenRead(plan.Source);
            WriteStaged(plan.Target!, output =>
            {
                input.CopyTo(output);
                if (Hash(output) != hash) throw new InvalidDataException("Исходный PDF изменился.");
            });
            return plan with { Status = MoveStatus.Copied, Message = "Копия сохранена." };
        }
        catch (InvalidDataException) { return Changed(plan); }
        catch (Exception ex) when (IsFileError(ex))
        {
            return plan with { Status = MoveStatus.Failed, Message = Error(ex) };
        }
    }

    private static MoveResult SaveWithRotation(MoveResult plan, string hash, IReadOnlyDictionary<int, int> turns,
        bool copy, Action<RotationRevision> recordPending)
    {
        try
        {
            using var input = File.OpenRead(plan.Source);
            if (Hash(input) != hash) return Changed(plan);
            input.Position = 0;
            string? rotatedHash = null;
            WriteStaged(plan.Target!, output =>
            {
                PdfRenderer.SaveRotated(input, output, turns);
                // Undo restores the original bytes from the saved PDF's previous revision.
                if (output.Length <= input.Length || CopyPrefix(output, Stream.Null, input.Length) != hash)
                    throw new InvalidDataException("Не удалось сохранить исходную версию PDF для отмены.");
                rotatedHash = Hash(output);
                recordPending(new(rotatedHash, input.Length, copy));
            });
            if (copy) return plan with { Status = MoveStatus.Copied, Message = "Копия сохранена с поворотом листов." };

            try
            {
                input.Dispose();
                using var source = WindowsFile.OpenForMove(plan.Source);
                using var saved = File.OpenRead(plan.Target!);
                if (Hash(saved) != rotatedHash)
                    return plan with { Status = MoveStatus.Failed, Message = "Сохранённый PDF изменился. Исходный файл оставлен на месте." };
                if (Hash(source) != hash)
                    return plan with { Status = MoveStatus.Copied, SourceChanged = true,
                        Message = "PDF с поворотом сохранён. Исходный файл изменился и оставлен на месте." };
                WindowsFile.Delete(source);
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                return plan with { Status = MoveStatus.Copied, Message = "PDF с поворотом сохранён. Не удалось удалить исходный файл." };
            }
            return plan with { Status = MoveStatus.Moved, Message = "Файл переименован с поворотом листов.", ResultHash = rotatedHash };
        }
        catch (Exception ex) when (IsFileError(ex) || ex is InvalidDataException or DllNotFoundException or EntryPointNotFoundException)
        {
            return plan with { Status = MoveStatus.Failed, Message = Error(ex) };
        }
    }

    private static MoveResult UndoRotation(MoveResult plan, string hash, RotationRevision rotation)
    {
        var copied = rotation.KeepSource || plan.Status == MoveStatus.Copied;
        var skipped = plan with { Status = MoveStatus.Skipped, Message = "Исходный путь занят, исходный PDF отсутствует или PDF изменён." };
        try
        {
            using var original = File.Exists(plan.Source) ? File.OpenRead(plan.Source) : null;
            if (original is not null ? Hash(original) != hash : copied || Directory.Exists(plan.Source)) return skipped;

            if (File.Exists(plan.Target) || original is null)
            {
                using var target = WindowsFile.OpenForMove(plan.Target!);
                if (Hash(target) != rotation.TargetHash) return skipped;
                if (original is null)
                {
                    WriteStaged(plan.Source, output =>
                    {
                        if (CopyPrefix(target, output, rotation.OriginalLength) != hash)
                            throw new InvalidDataException("Исходную версию PDF не удалось восстановить.");
                    });
                }
                using var restored = original is null ? File.OpenRead(plan.Source) : null;
                if (restored is not null && Hash(restored) != hash) return skipped;
                WindowsFile.Delete(target);
            }
            return plan with { Status = copied ? MoveStatus.CopyRemoved : MoveStatus.Undone,
                Message = copied ? "Созданная копия удалена." : "Исходный PDF восстановлен.", ResultHash = hash,
                PageRotations = copied ? null : plan.PageRotations?.ToDictionary(item => item.Key, item => 4 - item.Value) };
        }
        catch (Exception ex) when (IsFileError(ex) || ex is InvalidDataException)
        {
            return plan with { Status = MoveStatus.Failed, Message = Error(ex) };
        }
    }

    private static string CopyPrefix(Stream input, Stream output, long length)
    {
        if (length <= 0 || length >= input.Length)
            throw new InvalidDataException("Длина исходной версии PDF недействительна.");
        input.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (length > 0)
        {
            var block = buffer.AsSpan(0, (int)Math.Min(length, buffer.Length));
            input.ReadExactly(block);
            output.Write(block);
            hash.AppendData(block);
            length -= block.Length;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void WriteStaged(string target, Action<FileStream> write)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".drawing-renamer-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                created = true;
                write(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: false);
            created = false;
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
            using var copy = WindowsFile.OpenForMove(plan.Target!);
            if (Hash(original) != hash || Hash(copy) != hash)
                return plan with { Status = MoveStatus.Skipped, Message = "Исходный PDF или копия изменены." };
            WindowsFile.Delete(copy);
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

    private static MoveResult Changed(MoveResult plan) => plan with
    {
        Status = MoveStatus.Skipped, SourceChanged = true,
        Message = "PDF изменился. Проверьте файл и подтвердите значения заново."
    };

    private static MoveResult Move(MoveResult plan, string hash, bool undo)
    {
        try
        {
            var from = undo ? plan.Target! : plan.Source;
            var to = undo ? plan.Source : plan.Target!;
            using var input = WindowsFile.OpenForMove(from);
            if (Hash(input) != hash) return undo
                ? plan with { Status = MoveStatus.Skipped, Message = "Перемещённый PDF изменён." } : Changed(plan);
            if (!WindowsFile.TryRenameOnSameVolume(input, to))
            {
                File.Copy(from, to, overwrite: false);
                using var saved = File.OpenRead(to);
                if (Hash(saved) != hash)
                    return plan with { Status = MoveStatus.Failed, Message = "Сохранённый PDF изменился. Исходный файл оставлен на месте." };
                try { WindowsFile.Delete(input); }
                catch (Exception ex) when (!undo && IsFileError(ex))
                {
                    return plan with { Status = MoveStatus.Copied, Message = "Копия сохранена. Не удалось удалить исходный файл." };
                }
            }
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

    private List<Entry> Load()
    {
        if (sessionHistory is null && File.Exists(journalPath))
            Save([]);
        sessionHistory ??= [];
        return [.. sessionHistory];
    }

    private void Save(List<Entry> history)
    {
        var pending = journalPath + ".tmp";
        using (var output = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(output, history);
            output.Flush(flushToDisk: true);
        }
        replaceJournal(pending, journalPath);
    }

    private void Persist(List<Entry> history, List<MoveResult> results, bool completed = true)
    {
        if (completed) sessionHistory = [.. history];
        try { Save(history); }
        catch (Exception ex) when (IsFileError(ex)) { throw new JournalWriteException(results, ex); }
    }

    public static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Hash(file);
    }

    public static string Hash(Stream stream)
    {
        stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool IsFileError(Exception ex) => ex is IOException or UnauthorizedAccessException;
    private static string Error(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "Файл или папка отсутствует.",
        UnauthorizedAccessException => "Нет доступа к файлу.",
        InvalidDataException => ex.Message,
        DllNotFoundException or EntryPointNotFoundException => "Компонент PDFium для сохранения поворота недоступен.",
        _ => "Файл занят или целевой путь недоступен."
    };
}
