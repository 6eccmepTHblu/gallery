using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Gallery;

/// <summary>Одна выполненная операция — элемент стека отмены.</summary>
sealed record SlotOp(int Slot, string Source, string Target, long Size, DateTime WrittenUtc);

/// <summary>Результат постановки в очередь: что показать пользователю немедленно.</summary>
enum CopyVerdict { Queued, AlreadyThere, NoTarget }

/// <summary>
/// Девять целевых папок (SPEC.md §5). Модель данных — массив вместо скаляра,
/// цена восьми дополнительных слотов равна нулю, а цена неверно выбранной
/// единственной папки — второй проход по всей папке.
/// </summary>
sealed class Slots
{
    public const int Count = 9;

    readonly Channel<SlotOp> _queue = Channel.CreateUnbounded<SlotOp>(
        new UnboundedChannelOptions { SingleReader = true });

    readonly List<SlotOp> _undo = new();          // стек на 50, в пределах сессии
    readonly HashSet<string>[] _copied = new HashSet<string>[Count];
    readonly CancellationTokenSource[] _scan = new CancellationTokenSource[Count];

    // Уже запланированные, но ещё не записанные цели: имя → размер источника.
    // Без этого два быстрых нажатия по одному файлу дают один и тот же target,
    // и вторая копия падает на File.Move.
    readonly Dictionary<string, long>[] _planned = new Dictionary<string, long>[Count];

    /// <summary>Отметки отключены: в папке больше 50 000 файлов.</summary>
    public readonly bool[] MarksOff = new bool[Count];

    /// <summary>Папка слота исчезла. Привязку не стираем — диск мог отвалиться временно.</summary>
    public readonly bool[] Broken = new bool[Count];

    public string?[] Paths => SettingsStore.Current.Slots;
    public int Pending { get; private set; }
    public int UndoDepth => _undo.Count;

    /// <summary>Сообщение об ошибке операции — показывается тостом.</summary>
    public event Action<int, string>? Failed;
    public event Action? Changed;

    public Slots()
    {
        for (int i = 0; i < Count; i++)
        {
            _copied[i] = new(StringComparer.OrdinalIgnoreCase);
            _planned[i] = new(StringComparer.OrdinalIgnoreCase);
        }
        _ = ConsumeAsync();
        for (int i = 0; i < Count; i++)
            if (!string.IsNullOrEmpty(Paths[i])) RescanSlot(i);
    }

    public string? FolderOf(int slot) => slot is >= 0 and < Count ? Paths[slot] : null;

    public string NameOf(int slot)
    {
        var p = FolderOf(slot);
        if (string.IsNullOrEmpty(p)) return "";
        var name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? p : name;
    }

    /// <summary>
    /// Содержимое целевой папки СО ВСЕМИ ПОДПАПКАМИ: размер → пути.
    ///
    /// Индекс именно по размеру, а не по хешу: размер уже лежит в записи
    /// каталога, и ради него не открывается ни один файл. Совпадение размера —
    /// редкость, поэтому читать содержимое приходится в единичных случаях, а
    /// ответ «такого нет» даётся вообще без чтения диска (§5.9).
    /// </summary>
    readonly Dictionary<long, List<string>>[] _bySize = new Dictionary<long, List<string>>[Count];

    /// <summary>Где нашёлся дубликат последнего Enqueue. Для сообщения человеку.</summary>
    public string? LastDuplicate { get; private set; }

    public int CopiedCount(int slot) => _copied[slot].Count;

    /// <summary>
    /// Путь такого же файла в дереве слота — или null.
    ///
    /// «Такой же» = побайтово. Пересжатую или уменьшенную копию это не поймает
    /// и не пытается: для неё нужен перцептивный хеш, то есть распаковка каждого
    /// файла, а это другой порядок цены (§5.9).
    /// </summary>
    public string? DuplicateOf(int slot, Entry source)
    {
        var index = _bySize[slot];
        if (index is null) return null;
        return FindSame(index, source.Path, source.Size, c => SameContent(c, source.Path));
    }

    /// <summary>
    /// Выбор кандидата — отдельно от диска, чтобы проверялся числом.
    /// Сам файл-источник пропускаем: слот вполне может смотреть на ту же папку,
    /// и тогда картинка «найдёт сама себя».
    /// </summary>
    internal static string? FindSame(
        IReadOnlyDictionary<long, List<string>> bySize, string sourcePath, long size,
        Func<string, bool> sameAsSource, int maxProbe = 20)
    {
        if (!bySize.TryGetValue(size, out var candidates)) return null;

        var probed = 0;
        foreach (var c in candidates)
        {
            if (string.Equals(c, sourcePath, StringComparison.OrdinalIgnoreCase)) continue;

            // Потолок на случай тысячи файлов одинакового размера: лучше не
            // заметить дубликат, чем подвесить нажатие на чтении диска
            if (++probed > maxProbe) break;
            if (sameAsSource(c)) return c;
        }
        return null;
    }

    /// <summary>Побайтовое сравнение потоком: целиком в память ничего не берём.</summary>
    static bool SameContent(string a, string b)
    {
        try
        {
            using var fa = File.OpenRead(a);
            using var fb = File.OpenRead(b);
            if (fa.Length != fb.Length) return false;

            var ba = new byte[64 * 1024];
            var bb = new byte[64 * 1024];
            while (true)
            {
                var na = fa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
                var nb = fb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
                if (na != nb) return false;
                if (na == 0) return true;
                if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            }
        }
        catch (Exception)
        {
            // Файл исчез или занят — считаем, что дубликата нет: лишняя копия
            // безобиднее пропущенной картинки
            return false;
        }
    }

    static void Index(Dictionary<long, List<string>> index, string path, long size)
    {
        if (!index.TryGetValue(size, out var list)) index[size] = list = new List<string>();
        list.Add(path);
    }

    /// <summary>Уже лежит ли файл с таким именем в целевой папке слота.</summary>
    public bool IsCopied(int slot, string fileName) => _copied[slot].Contains(fileName);

    public IEnumerable<int> SlotsContaining(string fileName)
    {
        for (int i = 0; i < Count; i++)
            if (!string.IsNullOrEmpty(Paths[i]) && _copied[i].Contains(fileName))
                yield return i;
    }

    public void Bind(int slot, string? folder)
    {
        Paths[slot] = folder;
        Broken[slot] = false;
        SettingsStore.Touch();
        RescanSlot(slot);
        Changed?.Invoke();
    }

    /// <summary>
    /// Фоновое сканирование целевой папки И ЕЁ ПОДПАПОК: слот может указывать на
    /// архив с сотнями тысяч файлов на NAS. Кап 50 000 — дальше отметки для слота
    /// отключаются.
    ///
    /// Обход рекурсивный, потому что сохраняют по главам: картинка, уже лежащая
    /// в подпапке, обязана считаться сохранённой (§5.9).
    /// </summary>
    public void RescanSlot(int slot)
    {
        var folder = Paths[slot];
        _scan[slot]?.Cancel();
        _copied[slot].Clear();
        _bySize[slot] = null!;
        if (string.IsNullOrEmpty(folder)) return;

        MarksOff[slot] = false;
        var cts = new CancellationTokenSource();
        _scan[slot] = cts;
        var ct = cts.Token;

        _ = Task.Run(() =>
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = new Dictionary<long, List<string>>();
            try
            {
                var opts = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = FolderList.MaxDepth,
                };
                // Размер берём из записи каталога — она уже прочитана обходом,
                // отдельного обращения к файлу не делаем
                foreach (var fi in new DirectoryInfo(folder).EnumerateFiles("*", opts))
                {
                    if (ct.IsCancellationRequested) return;
                    // Слот может смотреть на архив с сотнями тысяч файлов на NAS.
                    // Тогда отметки отключаются — но об этом надо сказать (§5.7).
                    if (set.Count >= 50_000)
                    {
                        MarksOff[slot] = true;
                        set.Clear();
                        index.Clear();
                        break;
                    }
                    set.Add(fi.Name);
                    try { Index(index, fi.FullName, fi.Length); }
                    catch (Exception) { /* запись исчезла между обходом и чтением */ }
                }
            }
            catch (Exception) { return; }

            // Не ct, а поколение: между проверкой токена и публикацией мог
            // стартовать новый скан, и мы бы затёрли его результат старым
            if (!ReferenceEquals(_scan[slot], cts)) return;
            _copied[slot] = set;
            _bySize[slot] = index;
            Changed?.Invoke();
        }, ct);
    }

    /// <summary>
    /// Куда писать и надо ли писать вообще. Чистая функция — единственное место,
    /// где можно потерять данные пользователя, поэтому она тестируется (§9.5).
    ///
    /// Диалог на горячем пути недопустим, перезапись — тихая потеря данных.
    /// Поэтому: совпал размер — считаем, что файл уже там; не совпал — «имя (2).ext».
    /// </summary>
    public static (string path, bool alreadyThere) ResolveTarget(
        string dir, string fileName, long sourceSize, Func<string, long?> sizeOf)
    {
        var direct = Path.Combine(dir, fileName);
        var s = sizeOf(direct);
        if (s is null) return (direct, false);
        if (s == sourceSize) return (direct, true);

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int n = 2; n < 1000; n++)
        {
            var cand = Path.Combine(dir, $"{stem} ({n}){ext}");
            var cs = sizeOf(cand);
            if (cs is null) return (cand, false);
            if (cs == sourceSize) return (cand, true);
        }
        // Тысяча одноимённых файлов разного размера — сдаёмся честно
        return (Path.Combine(dir, $"{stem} ({Guid.NewGuid():N}){ext}"), false);
    }

    static long? SizeOnDisk(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? fi.Length : null; }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Ставит копию в очередь. Возвращает вердикт немедленно — UI оптимистичен,
    /// диск ждать не должен (§5.4).
    /// </summary>
    public CopyVerdict Enqueue(int slot, Entry source)
    {
        var dir = FolderOf(slot);
        if (string.IsNullOrEmpty(dir)) return CopyVerdict.NoTarget;

        // Дубликат ищем ДО выбора имени: та же картинка могла быть сохранена
        // под другим именем или в подпапку, и по имени её не найти
        LastDuplicate = DuplicateOf(slot, source);
        if (LastDuplicate is not null) return CopyVerdict.AlreadyThere;

        var name = Path.GetFileName(source.Path);

        // Учитываем и то, что уже в очереди: иначе повторное нажатие по тому же
        // кадру даёт тот же target, и вторая копия падает на File.Move.
        var (target, already) = ResolveTarget(dir, name, source.Size,
            p => _planned[slot].TryGetValue(Path.GetFileName(p), out var sz) ? sz : SizeOnDisk(p));

        if (already)
        {
            _copied[slot].Add(Path.GetFileName(target));
            return CopyVerdict.AlreadyThere;    // NO-OP в стек отмены не кладём
        }

        _planned[slot][Path.GetFileName(target)] = source.Size;
        _copied[slot].Add(Path.GetFileName(target));

        // Сразу в индекс: второе нажатие по тому же кадру обязано увидеть копию,
        // не дожидаясь, пока она долетит до диска
        if (_bySize[slot] is { } idx) Index(idx, target, source.Size);

        Pending++;
        _queue.Writer.TryWrite(new SlotOp(slot, source.Path, target, source.Size, default));
        Changed?.Invoke();
        return CopyVerdict.Queued;
    }

    async Task ConsumeAsync()
    {
        await foreach (var op in _queue.Reader.ReadAllAsync())
        {
            try
            {
                var written = await Task.Run(() => CopyAtomic(op.Source, op.Target));
                PushUndo(op with { WrittenUtc = written });
                Broken[op.Slot] = false;
            }
            catch (Exception ex)
            {
                _copied[op.Slot].Remove(Path.GetFileName(op.Target));
                // «Недоступен» — только про исчезнувшую папку. У «нет прав» и
                // «нет места» свой текст, подменять его нельзя (§5.9).
                Broken[op.Slot] = ex is DirectoryNotFoundException;
                Failed?.Invoke(op.Slot, Explain(ex));
            }
            finally
            {
                _planned[op.Slot].Remove(Path.GetFileName(op.Target));
                Pending--;
                Changed?.Invoke();
            }
        }
    }

    /// <summary>
    /// Копия во временное имя рядом с целью, затем переименование. В целевой папке
    /// никогда не появляется полуфайл, который другой процесс прочитает недописанным.
    /// </summary>
    static DateTime CopyAtomic(string source, string target)
    {
        var dir = Path.GetDirectoryName(target)!;
        var tmp = Path.Combine(dir, $".gallery-{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(source, tmp, overwrite: false);
            File.Move(tmp, target, overwrite: false);
            return File.GetLastWriteTimeUtc(target);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    void PushUndo(SlotOp op)
    {
        _undo.Add(op);
        if (_undo.Count > 50) _undo.RemoveAt(0);
    }

    /// <summary>
    /// Отмена удаляет созданный нами файл — но только если он совпадает с тем, что
    /// мы записали. Не совпал → не трогаем и говорим об этом. Данные пользователя
    /// не теряются никогда.
    /// </summary>
    public (bool ok, string message, string? source) Undo()
    {
        if (_undo.Count == 0) return (false, "Отменять нечего", null);

        var op = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        try
        {
            var fi = new FileInfo(op.Target);
            if (!fi.Exists)
                return (false, $"Файл в «{NameOf(op.Slot)}» уже удалён", op.Source);

            if (fi.Length != op.Size || fi.LastWriteTimeUtc != op.WrittenUtc)
                return (false, $"Файл в «{NameOf(op.Slot)}» изменён — не трогаю", op.Source);

            FileOps.Recycle(op.Target);
            _copied[op.Slot].Remove(Path.GetFileName(op.Target));
            Changed?.Invoke();
            return (true, $"Отменено: {Path.GetFileName(op.Target)}", op.Source);
        }
        catch (Exception ex)
        {
            return (false, Explain(ex), op.Source);
        }
    }

    static string Explain(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Нет прав на запись",
        DirectoryNotFoundException => "Папка недоступна",
        PathTooLongException => "Слишком длинный путь",
        IOException io when (uint)io.HResult == 0x80070070 => "Недостаточно места",
        IOException io => io.Message,
        _ => ex.Message,
    };

    public async Task DrainAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Pending > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }
}
