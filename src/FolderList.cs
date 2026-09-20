using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Gallery;

readonly record struct Entry(string Path, long Size, DateTime MTime)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Список кадров папки ВМЕСТЕ С ПОДПАПКАМИ (SPEC.md §3.2).
/// Источник истины — путь, а не индекс (§6.1).
/// </summary>
sealed class FolderList
{
    /// <summary>
    /// Камерный RAW. Отдельным списком, потому что он же нужен для решения
    /// «предложить ли кодек из Store» (§8.2) — один источник, два потребителя.
    /// </summary>
    public static readonly string[] RawExts =
    {
        ".3fr", ".ari", ".arw", ".bay", ".cap", ".cr2", ".cr3", ".crw", ".dcs", ".dcr",
        ".drf", ".eip", ".erf", ".fff", ".iiq", ".k25", ".kdc", ".mef", ".mos", ".mrw",
        ".nef", ".nrw", ".orf", ".ori", ".pef", ".ptx", ".pxn", ".raf", ".raw", ".rw2",
        ".rwl", ".sr2", ".srf", ".srw", ".x3f", ".dng",
    };

    // Расширения — только для перечисления папки: нужна скорость, не точность.
    // Реальный ответ «откроется ли» даёт попытка декода (§8.3).
    internal static readonly HashSet<string> Exts = new(
        new[]
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib",
            ".tif", ".tiff", ".webp", ".heic", ".heif", ".hif", ".avif", ".avifs",
            ".jxl", ".ico", ".jxr", ".wdp", ".dds",
        }.Concat(RawExts),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Глубина обхода. Том комикса — это папка с главами, изредка с томами
    /// внутри; восемь уровней перекрывают любую разумную раскладку и при этом
    /// не дают уйти в бесконечность по символическим ссылкам.
    /// </summary>
    public const int MaxDepth = 8;

    /// <summary>
    /// Потолок числа кадров. Не защита от большой коллекции — защита от
    /// случайно брошенного корня диска: там перечисление идёт минутами,
    /// а показать столько всё равно нельзя.
    /// </summary>
    public const int MaxItems = 20_000;

    public string? Folder { get; private set; }
    public List<Entry> Items { get; } = new();
    public int Count => Items.Count;

    /// <summary>
    /// Почему папку не удалось прочитать. Без этого «нет доступа» неотличимо
    /// от «пустая папка», и человек ищет несуществующие картинки.
    /// </summary>
    public string? LoadError { get; private set; }

    public Entry this[int i] => Items[i];

    /// <summary>
    /// Чистое перечисление вне UI-потока: не трогает состояние, поэтому
    /// безопасно вызывается из Task.Run (§6.2 — ноль IO на UI-потоке).
    /// </summary>
    /// <param name="skip">
    /// Папки, которые не перечислять. Это целевые папки копирования: брошенная
    /// внутрь исходной, она иначе показывала бы скопированное второй раз — и
    /// список рос бы с каждым нажатием.
    /// </param>
    public static (List<Entry> items, string? error) Scan(
        string folder, IReadOnlyList<string?>? skip = null)
    {
        var items = new List<Entry>();
        var walls = Fence(skip, folder);
        var capped = false;
        try
        {
            // EnumerateFiles, а не GetFiles: ленивая. IgnoreInaccessible — иначе одна
            // недоступная запись убивает всё перечисление посреди итерации (§8.7).
            foreach (var fi in new DirectoryInfo(folder).EnumerateFiles("*",
                new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = MaxDepth,
                }))
            {
                // Length и LastWriteTimeUtc уже пришли из WIN32_FIND_DATA — отдельного
                // stat на файл не делаем, на SMB это секунды на сотню файлов (§8.7).
                if (!Exts.Contains(fi.Extension)) continue;
                if (walls.Count > 0 && Under(fi.DirectoryName, walls)) continue;

                if (items.Count >= MaxItems) { capped = true; break; }
                try { items.Add(new Entry(fi.FullName, fi.Length, fi.LastWriteTimeUtc)); }
                catch (Exception) { /* запись исчезла между перечислением и чтением */ }
            }
        }
        catch (Exception ex)
        {
            return (items, ex switch
            {
                UnauthorizedAccessException => "Нет доступа к папке",
                DirectoryNotFoundException => "Папка не найдена",
                IOException => "Папка недоступна",
                _ => "Не удалось прочитать папку",
            });
        }

        if (capped) Log.Warn($"в папке больше {MaxItems} кадров — показаны первые");

        // Порядок перечисления ФС не гарантирован — сортируем всегда явно (§4.6)
        SortEntries(items);

        // Пустой результат и исчезнувшая папка — разные вещи для пользователя
        if (items.Count == 0 && !Directory.Exists(folder))
            return (items, "Папка недоступна");

        return (items, null);
    }

    public void Load(string folder, IReadOnlyList<string?>? skip = null)
    {
        var (items, error) = Scan(folder, skip);
        Adopt(folder, items, error);
    }

    /// <summary>Один файл — чтобы показать кадр из argv, не дожидаясь перечисления.</summary>
    public void LoadOne(string file)
    {
        Folder = Path.GetDirectoryName(file);
        LoadError = null;
        Items.Clear();
        try
        {
            var fi = new FileInfo(file);
            if (fi.Exists) Items.Add(new Entry(fi.FullName, fi.Length, fi.LastWriteTimeUtc));
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Заменить содержимое того же объекта. Именно замена, а не новый экземпляр:
    /// на этот список уже держат ссылку окно и конвейер.
    /// </summary>
    public void Adopt(string folder, List<Entry> items, string? error)
    {
        Folder = folder;
        LoadError = error;
        Items.Clear();
        Items.AddRange(items);
    }

    /// <summary>
    /// Порядок кадров: сначала по папке, потом по имени внутри неё. Именно так
    /// читается том, разложенный по главам, — и именно так его показывает
    /// Проводник, если войти в каждую папку по очереди.
    ///
    /// Ключи считаются ОДИН РАЗ: разбирать путь внутри компаратора значит делать
    /// это n·log n раз, а на двадцати тысячах кадров это уже заметно.
    /// </summary>
    internal static void SortEntries(List<Entry> items)
    {
        if (items.Count < 2) return;

        var keyed = new (string Dir, string Name, Entry E)[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var e = items[i];
            keyed[i] = (Path.GetDirectoryName(e.Path) ?? "", e.Name, e);
        }

        Array.Sort(keyed, static (a, b) =>
        {
            var c = NaturalCompare(a.Dir, b.Dir);
            return c != 0 ? c : NaturalCompare(a.Name, b.Name);
        });

        for (var i = 0; i < keyed.Length; i++) items[i] = keyed[i].E;
    }

    /// <summary>Непустые папки-стены с разделителем на конце — для сравнения префиксом.</summary>
    /// <summary>
    /// Стены, которые реально применимы к ЭТОЙ папке. Стена, накрывающая саму
    /// открытую папку или её предка, отбрасывается: иначе человек открывает
    /// целевую папку слота и видит «в папке нет изображений» — при полной папке
    /// файлов. Стена нужна только для ПОДпапок, чтобы скопированное не
    /// показалось вторым экземпляром.
    /// </summary>
    internal static List<string> Fence(IReadOnlyList<string?>? skip, string folder)
    {
        var walls = Walls(skip);
        walls.RemoveAll(w => Under(folder, new[] { w }));
        return walls;
    }

    internal static List<string> Walls(IReadOnlyList<string?>? skip)
    {
        var walls = new List<string>();
        if (skip is null) return walls;
        foreach (var s in skip)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            walls.Add(s.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      + Path.DirectorySeparatorChar);
        }
        return walls;
    }

    /// <summary>
    /// Лежит ли папка в одной из стен — она сама или что-то под ней.
    /// Сравнение по префиксу С РАЗДЕЛИТЕЛЕМ: иначе «...\out2» попал бы в «...\out».
    /// </summary>
    internal static bool Under(string? dir, IReadOnlyList<string> walls)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        var probe = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
        foreach (var w in walls)
            if (probe.StartsWith(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Индекс по пути. −1, если файла в списке нет.</summary>
    public int IndexOf(string path)
    {
        for (int i = 0; i < Items.Count; i++)
            if (string.Equals(Items[i].Path, path, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    public void RemoveAt(int i)
    {
        if (i >= 0 && i < Items.Count) Items.RemoveAt(i);
    }

    /// <summary>
    /// Натуральная сортировка как в Проводнике: img_2 перед img_10.
    /// Своя реализация не нужна и вредна — порядок обязан совпадать с окном
    /// Проводника, из которого пользователь пришёл (§4.6).
    /// </summary>
    public static int NaturalCompare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        return StrCmpLogicalW(x, y);
    }

    // DllImport, а не LibraryImport: последний требует AllowUnsafeBlocks на весь
    // проект ради одного вызова. NativeAOT здесь всё равно недоступен (§2.3).
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);
}
