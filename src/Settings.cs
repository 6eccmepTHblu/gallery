using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;

namespace Gallery;

sealed class Settings
{
    public string?[] Slots { get; set; } = new string?[9];
    public bool AdvanceAfterCopy { get; set; } = true;
    public bool SpaceIsNext { get; set; }
    public string Theme { get; set; } = "dark";
    public string? WindowPlacement { get; set; }

    /// <summary>
    /// Последний кадр в каждой папке. Одного LastPath мало: вернувшись во вчерашнюю
    /// съёмку, человек должен попасть туда, где остановился в ней, а не туда,
    /// где закрыл приложение сегодня.
    /// </summary>
    public Dictionary<string, string> FolderLast { get; set; } = new();

    /// <summary>
    /// Те же папки в порядке обращения, свежая — последняя.
    ///
    /// Отдельным списком, потому что на порядок перечисления Dictionary опираться
    /// нельзя: после Remove повторная вставка того же ключа переиспользует
    /// освобождённый слот и возвращает ключ на прежнее место, то есть «поднять
    /// папку наверх» удалением и вставкой не выходит. Поймано самопроверкой.
    /// </summary>
    public List<string> FolderOrder { get; set; } = new();
    public string? LastPath { get; set; }

    /// <summary>Лента миниатюр: видимость и непрозрачность в процентах (§4.8).</summary>
    public bool StripVisible { get; set; } = true;
    public int StripOpacity { get; set; } = 90;

    /// <summary>
    /// Размеры кисти и ластика в панели очистки — ПОРОЗНЬ. Инструменты делают
    /// разное: кистью дорисовывают пропущенный хвост буквы, ластиком сметают
    /// лишнее пятно, и удобные размеры у них разные. Помнятся между запусками:
    /// человек подбирает их под свой размер букв один раз.
    /// </summary>
    public int BrushSize { get; set; } = 10;
    public int EraserSize { get; set; } = 20;
    /// <summary>Перо: толщина и цвет. Цвет помнится — его берут пипеткой раз.</summary>
    public int PenSize { get; set; } = 3;
    public uint PenColor { get; set; } = 0xFF000000;

    /// <summary>Где лента: "bottom" или "right" (§4.8).</summary>
    public string StripSide { get; set; } = "bottom";

    /// <summary>
    /// Чем переводить: "local" — модель на этом компьютере, "api" — внешняя.
    /// По умолчанию локально: работает без сети, без денег и без отправки
    /// страниц наружу. API включает человек, осознанно.
    /// </summary>
    public string TranslateBackend { get; set; } = "local";

    /// <summary>Совместимый с OpenAI адрес chat completions — не привязка к поставщику.</summary>
    public string ApiUrl { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string ApiModel { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Отправлять модели саму страницу, а не только распознанный текст (§4.13).
    /// По умолчанию выключено: картинка покидает компьютер и стоит дороже текста.
    /// </summary>
    public bool ApiSendImage { get; set; }

    // Ключ здесь НЕ хранится — он в отдельном зашифрованном файле (ApiKeyStore)
    public int ToastQuietCount { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
partial class SettingsContext : JsonSerializerContext { }

/// <summary>
/// Хранилище настроек (SPEC.md §9.1). Запись атомарная и с дебаунсом.
/// Битый файл никогда не роняет приложение — это самый обидный класс отказа.
/// </summary>
static class SettingsStore
{
    static readonly DispatcherTimer Debounce = new() { Interval = TimeSpan.FromSeconds(1) };
    static Settings _current = new();
    static bool _dirty;

    public static Settings Current => _current;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gallery");

    static string FilePath => Path.Combine(Dir, "settings.json");

    public static void Load()
    {
        Debounce.Tick += (_, __) => { Debounce.Stop(); Flush(); };
        try
        {
            if (!File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            _current = JsonSerializer.Deserialize(json, SettingsContext.Default.Settings) ?? new Settings();
        }
        catch (Exception)
        {
            // Битый JSON — уносим в сторону и стартуем с умолчаний, но не падаем
            try { File.Move(FilePath, FilePath + ".bad", overwrite: true); } catch { }
            _current = new Settings();
        }

        if (_current.Slots is null || _current.Slots.Length != 9)
        {
            var fixedSlots = new string?[9];
            for (int i = 0; i < Math.Min(9, _current.Slots?.Length ?? 0); i++)
                fixedSlots[i] = _current.Slots![i];
            _current.Slots = fixedSlots;
        }
    }

    /// <summary>
    /// Запомнить кадр в папке.
    ///
    /// Ключ переставляется в конец, а не просто присваивается: порядок вставки
    /// словаря — это и есть наша «свежесть», и от него зависят обе вещи, которые
    /// им пользуются. Возврат к последней живой папке при запуске брал бы иначе
    /// не последнюю, а первую когда-либо открытую; вытеснение при переполнении
    /// выбрасывало бы самую давно добавленную папку, даже если человек ходит в
    /// неё каждый день.
    /// </summary>
    public static void Remember(string folder, string path) =>
        Remember(_current, folder, path);

    /// <summary>Папки от самой свежей к самой давней.</summary>
    public static IEnumerable<string> RecentFolders() => RecentFolders(_current);

    /// <summary>Забыть папку: и кадр, и место в порядке. Диск не трогается.</summary>
    public static void Forget(string folder)
    {
        Forget(_current, folder);
        Touch();
        FlushNow();
    }

    /// <summary>
    /// Выбросить папки, которых больше нет. Проверка передаётся снаружи:
    /// самопроверке ходить на диск нельзя, а список без неё не проверить.
    /// </summary>
    public static List<string> PruneMissing(Func<string, bool> exists)
    {
        var gone = PruneMissing(_current, exists);
        if (gone.Count > 0) { Touch(); FlushNow(); }
        return gone;
    }

    // Чистые версии: состояние снаружи, чтобы самопроверка не трогала настоящие
    // настройки пользователя (§9.5)
    internal static void Remember(Settings s, string folder, string path)
    {
        s.FolderLast[folder] = path;
        s.FolderOrder.Remove(folder);
        s.FolderOrder.Add(folder);
    }

    internal static void Forget(Settings s, string folder)
    {
        s.FolderLast.Remove(folder);
        s.FolderOrder.Remove(folder);
    }

    internal static List<string> PruneMissing(Settings s, Func<string, bool> exists)
    {
        // Список материализуем ДО удаления: RecentFolders — ленивый перебор по
        // тем самым коллекциям, которые мы сейчас будем править
        var gone = new List<string>();
        foreach (var f in RecentFolders(s).ToList())
            if (!exists(f)) gone.Add(f);

        foreach (var f in gone) Forget(s, f);
        return gone;
    }

    internal static IEnumerable<string> RecentFolders(Settings s)
    {
        for (var i = s.FolderOrder.Count - 1; i >= 0; i--)
            if (s.FolderLast.ContainsKey(s.FolderOrder[i])) yield return s.FolderOrder[i];

        // Настройки прошлых версий порядка не знали — отдаём остаток как есть
        foreach (var f in s.FolderLast.Keys)
            if (!s.FolderOrder.Contains(f)) yield return f;
    }

    /// <summary>
    /// Оставить не больше <paramref name="keep"/> папок, выбрасывая самые давние.
    /// Начало списка порядка — и есть самые давние.
    /// </summary>
    internal static void Trim(Settings s, int keep)
    {
        // Папки без записи в порядке — наследие старых настроек; считаем их
        // самыми давними, иначе они никогда не вытеснятся
        foreach (var f in s.FolderLast.Keys.ToList())
            if (!s.FolderOrder.Contains(f)) s.FolderOrder.Insert(0, f);

        while (s.FolderOrder.Count > keep)
        {
            s.FolderLast.Remove(s.FolderOrder[0]);
            s.FolderOrder.RemoveAt(0);
        }
    }

    /// <summary>Пометить изменённым. Реальная запись — через секунду покоя.</summary>
    public static void Touch()
    {
        _dirty = true;
        Debounce.Stop();
        Debounce.Start();
    }

    /// <summary>
    /// Изменение настройки человеком: пометить и тут же записать.
    ///
    /// Отдельно от Touch() потому, что дебаунс защищает ровно один случай —
    /// ползунок, который на перетаскивании шлёт события десятками в секунду.
    /// Для галки, выпадающего списка или поля ввода он не экономит ничего, зато
    /// теряет настройку, если приложение завершится нештатно в ближайшую секунду.
    /// А человек, переключивший тумблер, считает дело сделанным.
    /// </summary>
    public static void Commit()
    {
        Touch();
        FlushNow();
    }

    /// <summary>
    /// Записать немедленно, не дожидаясь секунды покоя. Таймер при этом гасим:
    /// иначе он выстрелит вхолостую и оставит впечатление, что запись отложена.
    /// </summary>
    public static void FlushNow()
    {
        Debounce.Stop();
        Flush();
    }

    /// <summary>Сколько заняла последняя запись — для оверлея F12 (§6.9).</summary>
    public static double LastFlushMs { get; private set; }

    /// <summary>Немедленная запись — на закрытии окна.</summary>
    public static void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        var t0 = Stopwatch.GetTimestamp();
        try
        {
            Directory.CreateDirectory(Dir);

            Trim(_current, 100);

            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_current, SettingsContext.Default.Settings));
            // Атомарно: прямая запись поверх оставит обрезанный JSON при падении,
            // и приложение больше не запустится
            File.Move(tmp, FilePath, overwrite: true);
            LastFlushMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
        catch (Exception)
        {
            // Некуда писать (например, папка только для чтения) — не наша забота на горячем пути
        }
    }
}
