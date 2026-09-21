using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace Gallery;

public sealed partial class MainWindow : Window
{
    readonly FolderList _list = new();
    readonly ImagePipeline _pipe;

    // Источник истины — путь, индекс производен от него (SPEC.md §6.1)
    string? _currentPath;

    /// <summary>
    /// Папка, которую ОТКРЫВАЛИ. Не то же, что папка текущего кадра: обход
    /// рекурсивный, и кадр обычно лежит в главе, а открывали том. Сюда же
    /// человек возвращается на следующем запуске.
    /// </summary>
    string? _root;

    CancellationTokenSource? _cts;

    // Дебаунс 120 мс: декод стартует только для «осевшего» индекса.
    // Skim-режим получается сам собой — при автоповторе 30 Гц таймер не срабатывает
    // ни разу, и отдельной сущности для него не нужно (§6.6).
    readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _resize = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>
    /// Оседание изменений в папке. Копирование сотни файлов — это сотня событий
    /// подряд, а перечитать надо один раз, когда всё улеглось.
    /// </summary>
    readonly DispatcherTimer _watchSettle = new() { Interval = TimeSpan.FromMilliseconds(600) };

    // Живая миниатюра-заглушка: источник и битмап освобождаются только парой
    SoftwareBitmapSource? _thumb;

    // Показанный путь. Пока он не совпал с текущим, файловые клавиши игнорируются:
    // иначе копируется не то, что человек видит на экране (§6.1).
    string? _shown;

    // Строка для скринридера и троттлинг её объявления
    string _a11yText = "";
    AutomationPeer? _peer;
    long _lastAnnounce;
    bool _pendingAnnounce;

    readonly List<double> _hitLatency = new();
    readonly List<double> _missLatency = new();
    bool _overlayOn;
    string _lastCmd = "—";
    string _rawKey = "—";

    public MainWindow()
    {
        InitializeComponent();
        Title = "Gallery";

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);

        _pipe = new ImagePipeline(DispatcherQueue.GetForCurrentThread());
        InitZoom();
        InitCopying();
        InitChrome();
        InitGoto();
        InitStrip();
        InitTranslate();
        InitApiLog();
        InitHistory();
        InitClean();
        InitOcrHover();
        _settle.Tick += OnSettle;
        _watchSettle.Tick += OnWatchSettle;
        _resize.Tick += OnResizeSettled;
        Root.SizeChanged += (_, __) => { _resize.Stop(); _resize.Start(); };
    }

    // ---------- открытие ----------

    public void OpenFromArgs(string[] argv)
    {
        foreach (var a in argv[1..])
        {
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            var path = a.Trim().Trim('"');
            if (Directory.Exists(path)) { OpenFolder(path, null); return; }
            if (File.Exists(path)) { OpenFolder(Path.GetDirectoryName(path)!, path); return; }
        }

        // Аргумент был, но такого пути нет — сказать об этом, а не молча
        // подменить контекст последней сессией
        var given = argv.Skip(1).FirstOrDefault(
            a => !a.StartsWith("--", StringComparison.Ordinal));
        if (given is not null)
            ShowToast($"Файл не найден: {Path.GetFileName(given.Trim().Trim('"'))}", error: true);

        // Без аргументов — возвращаемся туда, где закончили. Инструмент триажа,
        // который каждый запуск швыряет на первый файл папки в 3000 кадров,
        // неюзабелен (§4.5).
        // Цепочка, а не одна проверка: «последний файл» — самое хрупкое из того,
        // что мы помним. Его могли переименовать, удалить или унести на диск,
        // который сейчас не подключён. Показать в этом случае пустой холст значит
        // соврать, что открывать нечего, — а папка почти всегда ещё на месте.
        var last = SettingsStore.Current.LastPath;
        if (!string.IsNullOrEmpty(last))
        {
            // Возвращаемся в ТУ ЖЕ папку, которую открывали, а не в папку кадра:
            // у тома, разложенного по главам, это разные вещи
            var home = SettingsStore.Current.LastFolder;
            var dir = Directory.Exists(home) ? FolderList.Home(last, home)
                                             : Path.GetDirectoryName(last);

            if (File.Exists(last) && dir is not null) { OpenFolder(dir, last); return; }
            if (dir is not null && Directory.Exists(dir)) { OpenFolder(dir, null); return; }
        }

        // Нет и папки — берём самую свежую из запомненных, которая ещё существует
        foreach (var folder in SettingsStore.RecentFolders())
            if (Directory.Exists(folder)) { OpenFolder(folder, null); return; }
    }

    async void OpenFolder(string folder, string? focus)
    {
        _root = folder;
        Img.Source = null;          // Clear() освободит кэш — нельзя держать ссылку

        // Открыли конкретный файл — показываем его немедленно, не дожидаясь
        // перечисления: на папке в 10 000 файлов первый кадр не должен ждать (§4.5)
        if (focus is not null)
        {
            _list.LoadOne(focus);
            _pipe.SetList(_list);
            ClearThumbs();
            UpdateFolderLine();
            ApplyTier();
            if (_list.Count > 0) Show(0);
        }

        // Перечисление — вне UI-потока: пустой холст лучше замороженного окна (§6.2)
        // Целевые папки копирования из обхода исключаем: брошенная внутрь
        // исходной, она показывала бы скопированное вторым экземпляром
        var walls = _slots.Paths.ToArray();
        var (items, error) = await Task.Run(() => FolderList.Scan(folder, walls));

        var keep = _currentPath;
        _folderShown = null;            // список сменился — строку считаем заново
        _list.Adopt(folder, items, error);
        _pipe.SetList(_list);
        ClearThumbs();
        WatchFolder(folder);
        UpdateFolderLine();
        ApplyTier();
        ApplyStrip();

        if (_list.Count == 0)
        {
            _currentPath = null;
            _shown = null;
            ClearFrame();
            Meta.Text = "";
            ShowEmptyFolder(folder, error);
            Render();
            return;
        }

        // Позиция восстанавливается по пути, а не по индексу (§6.1)
        var remembered = focus is null && keep is null
                      && SettingsStore.Current.FolderLast.TryGetValue(folder, out var last)
            ? last : null;

        var i = keep is not null ? _list.IndexOf(keep)
              : focus is not null ? _list.IndexOf(focus)
              : remembered is not null ? _list.IndexOf(remembered) : 0;

        // Одно место на все три источника: аргумент, drop и пикер. Молчание здесь
        // выглядит как «приложение не отреагировало».
        if (focus is not null && i < 0)
            ShowToast($"{Path.GetFileName(focus)}: формат не поддерживается", error: true);

        Show(i < 0 ? 0 : i);
    }

    /// <summary>
    /// Три режима одного контейнера: не прочиталась / пуста / не задана.
    /// «Нет доступа» неотличимо от «пустая папка» — это ложь пользователю (§7.8).
    /// </summary>
    void ShowEmptyFolder(string folder, string? error)
    {
        if (error is not null)
        {
            ShowEmpty("Не удалось прочитать папку", $"{SafeName(folder)}\n{error}");
            EmptyPickText("Выбрать другую");
        }
        else
        {
            ShowEmpty("В папке нет изображений",
                SafeName(folder) +
                "\nПоддерживаются JPEG, PNG, WEBP, HEIC, AVIF, GIF, BMP, TIFF и RAW");
            EmptyPickText("Выбрать другую");
        }
        EmptyReveal.Visibility = Visibility.Visible;
        _emptyFolder = folder;
    }

    string? _emptyFolder;
    void EmptyPickText(string text) => EmptyPick.Content = text;

    int CurrentIndex => _currentPath is null ? -1 : _list.IndexOf(_currentPath);

    // ---------- папка меняется снаружи ----------

    FileSystemWatcher? _watch;

    /// <summary>
    /// Следим за открытой папкой. Перечисление делается ОДИН раз при открытии,
    /// и без слежения любая правка снаружи остаётся невидимой: положил файлы в
    /// пустую папку — на экране по-прежнему «в папке нет изображений», и починка
    /// только через F5, о котором надо ещё догадаться.
    /// </summary>
    void WatchFolder(string? folder)
    {
        _watch?.Dispose();
        _watch = null;
        if (folder is null) return;

        try
        {
            var w = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,       // список тоже с подпапками (§4.6.1)
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                             | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            void Bump(object? s, object e) =>
                DispatcherQueue.TryEnqueue(() => { _watchSettle.Stop(); _watchSettle.Start(); });

            w.Created += Bump; w.Deleted += Bump; w.Changed += Bump; w.Renamed += Bump;
            w.Error += Bump;                        // переполнение буфера — тоже повод перечитать
            w.EnableRaisingEvents = true;
            _watch = w;
        }
        catch (Exception ex)
        {
            // Сетевые и съёмные тома могут не поддерживать слежение — это не беда,
            // остаётся F5
            Log.Warn($"следить за папкой не вышло: {ex.Message}");
        }
    }

    /// <summary>
    /// Папка изменилась. Пока список НЕ пуст, вид не трогаем: перечитываем
    /// список, ленту и счётчик, но кадр и его масштаб оставляем как есть —
    /// иначе чужая копия файла в соседнюю подпапку сбрасывала бы увеличение
    /// под руками.
    /// </summary>
    async void OnWatchSettle(object? sender, object e)
    {
        _watchSettle.Stop();
        var folder = _list.Folder;
        if (folder is null) return;

        var walls = _slots.Paths.ToArray();
        var (items, error) = await Task.Run(() => FolderList.Scan(folder, walls));
        if (!string.Equals(folder, _list.Folder, StringComparison.OrdinalIgnoreCase)) return;

        var wasEmpty = _list.Count == 0;
        var keep = _currentPath;
        _list.Adopt(folder, items, error);
        _pipe.SetList(_list);
        ClearThumbs();
        _folderShown = null;
        UpdateFolderLine();
        ApplyStrip();

        if (_list.Count == 0)
        {
            _currentPath = null;
            _shown = null;
            ClearFrame();
            Meta.Text = "";
            ShowEmptyFolder(folder, error);
            Render();
            return;
        }

        // Папка была пуста, а теперь нет — показываем первый кадр: карточка
        // «нет изображений» должна уйти сама, без F5
        if (wasEmpty) { Show(0); return; }

        var i = keep is null ? -1 : _list.IndexOf(keep);
        if (i < 0) Show(0); else Render();
    }

    // ---------- горячий путь ----------

    void Show(int index)
    {
        if (_list.Count == 0) return;
        index = Math.Clamp(index, 0, _list.Count - 1);

        var t0 = Stopwatch.GetTimestamp();
        var entry = _list[index];
        _shown = null;                     // кадр ещё не показан — файловые клавиши ждут
        _currentPath = entry.Path;
        _pipe.Track(index);

        // Счётчик и имя обновляются немедленно, до всякого декода: при автоповторе
        // 30 Гц человек видит, что летит, и где он.
        Render();
        UpdateFolderLine();

        // Смена индекса: Cancel → пересоздать CTS → перерисовать → взвести таймер
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        var hit = _pipe.Peek(index);
        if (hit is not null)
        {
            Img.Source = hit.Source;           // мгновенно: без SetBitmapAsync
            Img.Opacity = 1.0;
            LayoutFrame(hit.SrcW, hit.SrcH, hit.BmpW);
            HideOverlays();
            _shown = entry.Path;
            ShowMeta(hit, entry.Size);
            DropThumb();
            _pipe.HitCount++;
            _pipe.LastSource = "cache";
            _pipe.LastHitMs = Ms(t0);
            Track(_hitLatency, _pipe.LastHitMs);
        }
        else
        {
            _pipe.MissCount++;
            _pipe.LastSource = "—";
            // Признак «на экране ещё не этот кадр»: имя и счётчик уже новые.
            // Гасить в ноль хуже — мигание серым прямоугольником заметнее.
            Img.Opacity = 0.35;
            if (entry.Size == 0)
            {
                Fail(entry.Name, "Файл пустой или не докачан", 0);
            }
            else
            {
                _ = ShowThumbAsync(entry, _cts.Token);
            }
        }

        AnnounceFrameThrottled();

        // Единая точка оседания: и добор промаха, и префетч
        _settle.Stop();
        _settle.Start();
        UpdateOverlay();
    }

    /// <summary>Заглушка из встроенной миниатюры: 1–2 мс против ~150 мс полного декода.</summary>
    async Task ShowThumbAsync(Entry entry, CancellationToken ct)
    {
        var t0 = Stopwatch.GetTimestamp();
        var bmp = await Decoder.ThumbnailAsync(entry.Path, ct);
        if (bmp is null || ct.IsCancellationRequested) { bmp?.Dispose(); return; }
        if (!ReferenceEquals(_currentPath, entry.Path) && _currentPath != entry.Path)
        {
            bmp.Dispose();
            return;
        }

        try
        {
            // Новый источник на каждую миниатюру: перекрывающиеся SetBitmapAsync на
            // одном SoftwareBitmapSource роняют XAML stowed-исключением.
            // SetBitmapAsync забирает владение битмапом — сами его не освобождаем.
            var src = new SoftwareBitmapSource();
            await src.SetBitmapAsync(bmp);
            if (ct.IsCancellationRequested || _currentPath != entry.Path) { src.Dispose(); return; }

            Img.Source = src;
            Img.Opacity = 1.0;
            // Заглушка: пропорции берём у самой миниатюры и разрешаем растянуть —
            // настоящие размеры оригинала будут известны через ~150 мс.
            LayoutFrame((uint)bmp.PixelWidth, (uint)bmp.PixelHeight,
                        (uint)bmp.PixelWidth, allowUpscale: true);
            HideOverlays();
            _shown = entry.Path;
            _pipe.LastSource = "thumb";
            _pipe.LastMissMs = Ms(t0);

            DropThumb();
            _thumb = src;
        }
        catch (Exception) { }
    }

    async void OnSettle(object? sender, object e)
    {
        _settle.Stop();
        if (_pendingAnnounce) AnnounceFrameThrottled();

        // Панель чистки идёт за кадром: сохранил, нажал стрелку — она уже на
        // следующем. Здесь, а не в Show(): при автоповторе Show зовётся 30 раз
        // в секунду, и декодировать страницу на каждый шаг незачем
        if (CleanPanel.Visibility == Visibility.Visible && _currentPath is not null
            && !string.Equals(_clnPath, _currentPath, StringComparison.OrdinalIgnoreCase))
        {
            if (_clnDirty)
                ShowToast("Правки не сохранены — панель ушла на новый кадр", error: false, warn: true);
            LoadClean();
        }
        var index = CurrentIndex;
        if (index < 0 || _cts is null) return;
        var ct = _cts.Token;
        var entry = _list[index];

        // Позиция сохраняется здесь, а не в Show(): при автоповторе Show зовётся
        // 30 раз в секунду, и каждый вызов дёргал бы таймер записи настроек.
        SaveSession();

        // Миниатюры заказываем только с осевшего индекса: при автоповторе 30 Гц
        // заказ на каждый щелчок утопил бы очередь в заведомо устаревшей работе
        LoadStripThumbs();

        // Карточку для пустого файла уже показал Show() с точной причиной.
        // Декодировать ноль байт незачем, а результат затёр бы сообщение общим.
        if (entry.Size == 0) return;

        var t0 = Stopwatch.GetTimestamp();

        try
        {
            var ready = _pipe.Peek(index) ?? await _pipe.GetAsync(index, ct);
            if (ct.IsCancellationRequested) return;
            if (CurrentIndex != index) return;
            if (ready is null)
            {
                // Отмену уже отсеяли выше: сюда попадает настоящий отказ,
                // и молчаливый выход оставил бы пустой холст навсегда
                Fail(entry.Name, "Не удалось декодировать", entry.Size);
                return;
            }

            Img.Source = ready.Source;
            Img.Opacity = 1.0;
            LayoutFrame(ready.SrcW, ready.SrcH, ready.BmpW);
            HideOverlays();
            _shown = entry.Path;
            ShowMeta(ready, entry.Size);
            DropThumb();
            if (_pipe.LastSource != "cache")
            {
                _pipe.LastSource = "decode";
                _pipe.LastMissMs = Ms(t0);
                Track(_missLatency, _pipe.LastMissMs);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested && CurrentIndex == index)
                Fail(entry.Name, Why(entry.Name, ex), entry.Size);
        }

        UpdateOverlay();

        // Префетч — после того, как текущий кадр показан, и только на осевшем индексе
        try { await _pipe.PrefetchAsync(index, ct); }
        catch (OperationCanceledException) { }
        UpdateOverlay();
    }

    void DropThumb()
    {
        if (_thumb is null) return;
        if (ReferenceEquals(Img.Source, _thumb)) return;   // ещё на экране
        try { _thumb.Dispose(); } catch { }
        _thumb = null;
    }

    /// <summary>
    /// Позиция сессии: и глобальная, и по текущей папке. Одна точка на оседание
    /// и на закрытие.
    ///
    /// ПОЧЕМУ ВМЕСТЕ: закрытие раньше писало только LastPath, а FolderLast
    /// обновлялся лишь на оседании через 120 мс. Кто закрывал приложение сразу
    /// после шага — а так и закрывают, досмотрев кадр, — оставлял в FolderLast
    /// предыдущий кадр. Запуск без аргументов подхватывал верный LastPath, но
    /// стоило открыть ту же папку явно, и человек попадал не туда, где кончил.
    /// </summary>
    void SaveSession()
    {
        SettingsStore.Current.LastPath = _currentPath;

        // Корень обхода, а не _list.Folder: пока идёт перечисление, там лежит
        // папка одного показанного кадра — и закрывшийся в эту секунду получил
        // бы на запуске главу вместо тома
        if (_root is not null) SettingsStore.Current.LastFolder = _root;
        if (_currentPath is not null && (_root ?? _list.Folder) is { } folder)
            SettingsStore.Remember(folder, _currentPath);

        // Пишем сразу, без секунды покоя. Оседание уже само по себе дебаунс: при
        // автоповторе 30 Гц оно не наступает ни разу, а наступает ровно тогда,
        // когда человек остановился на кадре. Секунда сверху записей не экономила,
        // зато теряла позицию, если процесс снимали не штатно — диспетчером задач
        // или выключением питания.
        SettingsStore.Touch();
        SettingsStore.FlushNow();
    }

    void ShowMeta(Ready r, long fileSize) =>
        Meta.Text = $"{r.SrcW}×{r.SrcH} · {Human(fileSize)}";

    /// <summary>
    /// Папка под размером кадра. Обновляется при смене папки, а не в Render():
    /// тот зовётся до 30 раз в секунду при автоповторе, а папка за это время
    /// измениться не может — считать сокращение на каждом кадре было бы
    /// выброшенной работой на горячем пути (§6.6).
    /// </summary>
    /// <summary>Папка, для которой уже посчитана строка. Пустая — ещё ни разу.</summary>
    string? _folderShown;

    void UpdateFolderLine()
    {
        // Показываем папку ТЕКУЩЕГО кадра, а не корень обхода: со вложенными
        // папками это разные вещи, и по кнопке человек ждёт ту, где лежит
        // картинка. Поэтому же строка обновляется на каждом кадре — но СЧИТАЕТСЯ
        // только при смене папки: при автоповторе 30 Гц сокращать путь заново
        // было бы выброшенной работой на горячем пути (§6.6)
        var folder = _currentPath is not null
            ? System.IO.Path.GetDirectoryName(_currentPath) ?? _list.Folder
            : _list.Folder;

        if (string.Equals(folder, _folderShown, StringComparison.OrdinalIgnoreCase)) return;
        _folderShown = folder;

        if (string.IsNullOrEmpty(folder))
        {
            FolderButton.Visibility = Visibility.Collapsed;
            return;
        }

        FolderText.Text = ShortPath(folder);
        FolderButton.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Путь внутри целевой папки: «глава 2\\стр_07.jpg» вместо полного пути.
    /// Корень человек и так выбирал сам, а строка тоста коротка.
    /// </summary>
    internal static string Relative(string? root, string? full)
    {
        if (string.IsNullOrEmpty(full)) return "";
        if (string.IsNullOrEmpty(root)) return System.IO.Path.GetFileName(full);

        var r = root.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                             System.IO.Path.AltDirectorySeparatorChar)
              + System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(r, StringComparison.OrdinalIgnoreCase)
            ? full.Substring(r.Length)
            : System.IO.Path.GetFileName(full);
    }

    /// <summary>
    /// Путь в одну строку статус-бара. Режем СЕРЕДИНУ и только по разделителям:
    /// хвост — имя папки, ради которого строка и нужна, а корень отвечает на
    /// «тот ли это диск». Обрезка по символам дала бы «D:\…ects\Personal».
    /// </summary>
    internal static string ShortPath(string? path, int max = 44)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (path.Length <= max) return path;

        var sep = Path.DirectorySeparatorChar;
        var root = Path.GetPathRoot(path) ?? "";
        var parts = path[root.Length..].Split(sep, StringSplitOptions.RemoveEmptyEntries);

        // Выбрасываем сегменты с начала, пока не влезет
        for (var skip = 1; skip < parts.Length; skip++)
        {
            var s = root + "…" + sep + string.Join(sep, parts[skip..]);
            if (s.Length <= max) return s;
        }

        // Не влезло даже одно последнее имя — только его и показываем
        var last = parts.Length > 0 ? parts[^1] : path;
        return last.Length <= max - 1 ? "…" + last : "…" + last[^Math.Max(1, max - 1)..];
    }

    /// <summary>
    /// Карточка ошибки вместо кадра. Навигация стрелками работает поверх неё —
    /// застрять на битом файле невозможно (§7.8).
    /// </summary>
    void Fail(string name, string why, long size)
    {
        ClearFrame();               // иначе за карточкой остаётся серый прямоугольник
        DropFull();                 // иначе _full навсегда блокирует полный декод
        DropThumb();
        EmptyState.Visibility = Visibility.Collapsed;
        ErrCaption.Text = $"Не удалось открыть {SafeName(name)}";
        ErrWhy.Text = why;
        ErrStore.Visibility = StoreQuery(name) is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorCard.Visibility = Visibility.Visible;
        Meta.Text = Human(size);

        // Показанного кадра нет, но действовать над файлом можно: копирование —
        // операция над байтами, отрисовка превью ей не нужна (§7.8)
        _shown = _currentPath;
        Announce($"{ErrCaption.Text}. {why}", important: true);
    }

    void ShowEmpty(string title, string hint)
    {
        ErrorCard.Visibility = Visibility.Collapsed;
        EmptyPick.Content = "Выбрать папку";
        EmptyReveal.Visibility = Visibility.Collapsed;
        _emptyFolder = null;
        EmptyTitle.Text = title;
        EmptyHint.Text = hint;
        EmptyState.Visibility = Visibility.Visible;
        Announce($"{title}. {hint}", important: true);
    }

    void HideOverlays()
    {
        EmptyState.Visibility = Visibility.Collapsed;
        ErrorCard.Visibility = Visibility.Collapsed;
    }

    /// <summary>Запрос в Store для форматов, которым нужен отдельный пакет кодека (§8.3).</summary>
    internal static string? StoreQuery(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".heic" or ".heif" or ".hif" => "HEIF Image Extensions",
        ".avif" or ".avifs" => "AV1 Video Extension",
        ".webp" => "Webp Image Extensions",
        ".jxl" => "JPEG XL Image Extension",
        ".cr2" or ".cr3" or ".nef" or ".arw" or ".dng" or ".raf"
            or ".orf" or ".rw2" or ".pef" or ".srw" => "Raw Image Extension",
        _ => null,
    };

    // ---------- тир по вьюпорту ----------

    void ApplyTier()
    {
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var longSide = Math.Max(Root.ActualWidth, Root.ActualHeight) * scale;
        if (longSide < 1) return;
        _pipe.SetTier(ImagePipeline.QuantizeTier(longSide));
    }

    void OnResizeSettled(object? sender, object e)
    {
        _resize.Stop();
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var longSide = Math.Max(Root.ActualWidth, Root.ActualHeight) * scale;
        if (longSide < 1) return;

        var tier = ImagePipeline.QuantizeTier(longSide);
        if (tier != _pipe.Tier)
        {
            // Кэш выбрасывается, значит показанный источник вот-вот освободят
            Img.Source = null;
            _pipe.SetTier(tier);
            if (CurrentIndex >= 0) Show(CurrentIndex);
        }
        else if (CurrentIndex >= 0 && _pipe.Peek(CurrentIndex) is { } cur)
        {
            // Тир тот же — кадр на экране валиден, гасить его нельзя, просто перевписать
            LayoutFrame(cur.SrcW, cur.SrcH, cur.BmpW);
        }
        UpdateOverlay();
    }

    // ---------- отображение состояния ----------

    void Render()
    {
        var i = CurrentIndex;
        Counter.Text = _list.Count == 0 ? "—" : $"{i + 1} / {_list.Count}";
        FileNameText.Text = _currentPath is null ? "" : SafeName(Path.GetFileName(_currentPath));
        UpdateFrameBadges();

        var w = Root.ActualWidth;
        PositionRail.Width = _list.Count > 0 && w > 0 ? w * (i + 1) / _list.Count : 0;

        UpdateStrip();
        SyncTranslate();
        SyncOcrLayer();
    }

    /// <summary>
    /// Имя с U+202E (RLO) переворачивает отображение и может вывернуть весь
    /// заголовок — классический bidi-спуфинг. Чистим ТОЛЬКО при показе:
    /// на диске файл называется как называется (§8.7).
    /// </summary>
    internal static string SafeName(string name) =>
        BidiControls.Replace(name, "");

    static readonly System.Text.RegularExpressions.Regex BidiControls =
        new("[‎‏‪-‮⁦-⁩]",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Объявление скринридеру. Троттлинг 350 мс обязателен: без него быстрое
    /// листание превращается в неразборчивый поток речи (§7.9).
    /// Ранний выход по ListenerExists — чтобы на горячем пути не поднимать
    /// AutomationPeer впустую тридцать раз в секунду.
    /// </summary>
    void Announce(string text, bool important)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            // Пир поднимаем один раз: на горячем пути (30 Гц автоповтор) создавать
            // его заново тридцать раз в секунду — выброшенная работа.
            _peer ??= FrameworkElementAutomationPeer.FromElement(Img)
                      ?? FrameworkElementAutomationPeer.CreatePeerForElement(Img);
            _peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                important ? AutomationNotificationProcessing.ImportantAll
                          : AutomationNotificationProcessing.MostRecent,
                text, "gallery");
        }
        catch (Exception) { }
    }

    void AnnounceFrameThrottled()
    {
        var now = Stopwatch.GetTimestamp();
        var sinceMs = (now - _lastAnnounce) * 1000.0 / Stopwatch.Frequency;
        if (sinceMs < 350) { _pendingAnnounce = true; return; }
        _lastAnnounce = now;
        _pendingAnnounce = false;
        Announce(_a11yText, important: false);
    }

    /// <summary>Переход к кадру по номеру. Поле в статус-баре, не диалог (§4.6).</summary>
    void InitGoto()
    {
        CounterButton.Click += (_, __) => OpenGoto();

        GotoBox.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                if (int.TryParse(GotoBox.Text.Trim(), out var num) &&
                    num >= 1 && num <= _list.Count)
                {
                    CloseGoto();
                    Show(num - 1);
                }
                else
                {
                    ShowToast($"Введите номер от 1 до {_list.Count}", error: true);
                }
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                CloseGoto();
            }
        };
        GotoBox.LostFocus += (_, __) => CloseGoto();
    }

    void OpenGoto()
    {
        if (_list.Count == 0) return;
        GotoBox.Text = (CurrentIndex + 1).ToString();
        CounterButton.Visibility = Visibility.Collapsed;
        GotoBox.Visibility = Visibility.Visible;
        GotoBox.Focus(FocusState.Programmatic);
        GotoBox.SelectAll();
    }

    void CloseGoto()
    {
        if (GotoBox.Visibility != Visibility.Visible) return;
        GotoBox.Visibility = Visibility.Collapsed;
        CounterButton.Visibility = Visibility.Visible;
    }

    void UpdateOverlay()
    {
        if (!_overlayOn) return;
        var total = _pipe.HitCount + _pipe.MissCount;
        var hitPct = total == 0 ? 0 : 100.0 * _pipe.HitCount / total;
        var cancelPct = _pipe.DecodeStarted == 0
            ? 0 : 100.0 * _pipe.DecodeCancelled / _pipe.DecodeStarted;

        OverlayText.Text = string.Join('\n', new[]
        {
            $"кадр     hit {_pipe.LastHitMs,6:F1} мс   miss {_pipe.LastMissMs,6:F1} мс",
            $"         p50 {P(_hitLatency, 50),6:F1} / p99 {P(_hitLatency, 99),6:F1}  (попадания)",
            $"источник {_pipe.LastSource}",
            $"декоды   старт {_pipe.DecodeStarted}  отменено {_pipe.DecodeCancelled} ({cancelPct:F0}%)",
            $"кэш      {_pipe.CacheCount} шт · {_pipe.CacheBytes / 1024.0 / 1024.0:F0} МБ · попадания {hitPct:F0}%",
            $"тир      {_pipe.Tier}   RSS {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024} МБ",
            $"зум      x{Zoom.ZoomFactor:F2}  ({NativePercent:F0}%)  min {Zoom.MinZoomFactor:F2} max {Zoom.MaxZoomFactor:F0}",
            $"вписать  {_fitW:F0}x{_fitH:F0}  ориг {_srcW}x{_srcH}  кадр {_bmpW}px",
            $"вьюпорт  {Zoom.ViewportWidth:F0}x{Zoom.ViewportHeight:F0}  экстент {Zoom.ExtentWidth:F0}x{Zoom.ExtentHeight:F0}",
            $"лента    {(StripBox.Visibility == Visibility.Visible ? "видна" : "скрыта")}" +
                $"  ячеек {_cells.Count}  миниатюр {_thumbs.Count}" +
                $"  без миниатюры {_thumbBad.Count}  в работе {_thumbLoading.Count}",
            $"запись   {SettingsStore.LastFlushMs:F2} мс",
            $"команда  {_lastCmd}",
            $"клавиша  {_rawKey}",
        });

        // Дублируем в файл: читать оверлей со скриншота при отладке невозможно
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gallery");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "overlay.txt"), OverlayText.Text);
        }
        catch { }
    }

    static double P(List<double> xs, int pct)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        return s[Math.Clamp((int)(s.Count * pct / 100.0), 0, s.Count - 1)];
    }

    static void Track(List<double> xs, double v)
    {
        xs.Add(v);
        if (xs.Count > 200) xs.RemoveAt(0);
    }

    static double Ms(long t0) => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

    static readonly string[] NeedsCodec =
    {
        ".heic", ".heif", ".avif", ".jxl", ".webp",
        ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".rw2", ".pef", ".srw",
    };

    /// <summary>Формула сообщения: что случилось → почему (§7.3).</summary>
    string Why(string name, Exception ex)
    {
        // §8.10: OOM приходит из конкретной крупной аллокации декодера — ловим,
        // освобождаем кэш и живём дальше
        if (ex is OutOfMemoryException)
        {
            _pipe.Clear();
            return "Файл слишком большой для показа";
        }
        Log.Warn($"{name}: 0x{(uint)ex.HResult:X8} {ex.GetType().Name} {ex.Message}");
        return WhyText(name, ex);
    }

    static string WhyText(string name, Exception ex) => (uint)ex.HResult switch
    {
        // COMPONENTNOTFOUND неоднозначен: и «нет кодека», и «поток не опознан».
        // Замерено: файл в 0 байт отдаёт именно его, и советовать по нему Store — враньё.
        0x88982F50 => NeedsCodec.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
            ? "Нужен кодек — установите расширение из Microsoft Store"
            : "Формат не распознан или файл повреждён",
        0x88982F07 => "Это не изображение или файл повреждён",
        0x88982F60 or 0x88982F61 => "Файл повреждён",
        0x88982F0C => "Файл пустой или не докачан",
        0x88982F8C => "Файл слишком большой",
        0x88982F0B => "Версия формата не поддерживается",
        0x88982F04 => "Внутренняя ошибка декодера",
        // Сырой HRESULT пользователю ничего не говорит — он ушёл в лог выше
        _ => "Не удалось прочитать файл",
    };

    static string Human(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} КБ",
        _ => $"{bytes / 1024.0 / 1024.0:F1} МБ",
    };

    // ---------- команды ----------

    // Границы папки не цикличатся: конец папки — сигнал «сессия разбора закончена» (§4.6)
    void OnNext(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; Step(+1); }

    void OnPrev(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; Step(-1); }

    void Step(int delta)
    {
        var i = CurrentIndex;
        if (i < 0) return;
        var next = i + delta;
        if (next < 0 || next >= _list.Count) { BounceEdge(delta > 0); return; }
        Show(next);
    }

    void OnFirst(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; Show(0); }

    void OnLast(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; Show(_list.Count - 1); }

    void OnRescan(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        RescanFolder();
    }

    /// <summary>
    /// Перечитать папку и выбросить всё, что помнилось о её файлах. Зовётся и
    /// с F5, и после сохранения: файл на диске сменился, а в кэше кадров и
    /// миниатюр лежит прежний — без сброса приложение показывало бы старую
    /// картинку под новым именем.
    /// </summary>
    void RescanFolder()
    {
        if (_list.Folder is null) return;
        var keep = _currentPath;
        Img.Source = null;
        _list.Load(_list.Folder, _slots.Paths.ToArray());
        _pipe.SetList(_list);
        ClearThumbs();
        WatchFolder(_list.Folder);
        // F5 обновляет и целевые папки: иначе счётчики и бейджи устаревают (§5.7)
        for (int slot = 0; slot < Slots.Count; slot++) _slots.RescanSlot(slot);

        // позиция восстанавливается по пути, а не по индексу (§6.1)
        var i = keep is null ? 0 : _list.IndexOf(keep);
        Show(i < 0 ? 0 : i);
    }

    void OnToggleOverlay(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        _overlayOn = !_overlayOn;
        Overlay.Visibility = _overlayOn ? Visibility.Visible : Visibility.Collapsed;
        if (TransSource.Visibility == Visibility.Visible) ShowTransSource(TransSource.Text);
        UpdateOverlay();
    }

    // ---------- бенч без UI-автоматизации (§6.10) ----------

    public async Task RunBenchAsync(int steps, int intervalMs)
    {
        // Гоняем ту же команду, что и клавиша — никакого SendInput
        await Task.Delay(1500);
        var rows = new StringBuilder("i,index,ms,source\n");

        for (int n = 0; n < steps; n++)
        {
            var i = CurrentIndex;
            var next = i + 1 >= _list.Count ? 0 : i + 1;
            var t0 = Stopwatch.GetTimestamp();
            Show(next);
            var ms = Ms(t0);
            rows.Append(CultureInfo.InvariantCulture,
                $"{n},{next},{ms:F2},{_pipe.LastSource}\n");
            await Task.Delay(intervalMs);
        }

        var hits = _hitLatency;
        var summary =
            $"\n# шагов {steps}, интервал {intervalMs} мс\n" +
            $"# попаданий {_pipe.HitCount}, промахов {_pipe.MissCount}, " +
            $"hit-rate {(100.0 * _pipe.HitCount / Math.Max(1, _pipe.HitCount + _pipe.MissCount)):F0}%\n" +
            $"# смена кадра из кэша: p50 {P(hits, 50):F2} мс, p99 {P(hits, 99):F2} мс\n" +
            $"# декодов {_pipe.DecodeStarted}, отменено {_pipe.DecodeCancelled}\n" +
            $"# тир {_pipe.Tier}, кадр {_pipe.LastFrameBytes / 1024 / 1024} МБ\n" +
            $"# кэш {_pipe.CacheCount} шт / {_pipe.CacheBytes / 1024 / 1024} МБ, " +
            $"RSS {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024} МБ\n";

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gallery");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "bench.csv"), rows + summary);
        Environment.Exit(0);
    }
}
