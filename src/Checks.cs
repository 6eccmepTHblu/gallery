using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Gallery;

/// <summary>
/// Самопроверка вместо тестового проекта (SPEC.md §9.5): gallery.exe --self-test
/// Проверяется только то, где ошибка стоит дорого. Апгрейд до xunit — когда
/// проверок станет больше ~30 или понадобится CI-репорт.
/// </summary>
static class Checks
{
    static int _failed, _total;
    static readonly StringBuilder Log = new();

    public static int Run()
    {
        PickScale();
        FitToViewport();
        ResolveTarget();
        NaturalSort();
        SafeNames();
        ThemeTokens();
        StripCells();
        ShortPaths();
        Duplicates();
        CleanupMath();
        CleanupBrush();
        CleanupBlocks();
        SubfolderScan();
        FolderMemory();
        HistoryList();
        Reading();
        ApiParsing();
        OcrCleanup();
        ApiCompleteness();
        DecodeLoop();
        BubbleMath();
        Overlaps();
        BubbleGrouping();
        FreeTextStitch();
        BubbleBackground();
        LinePitchCheck();
        PlateLayout();

        Log.AppendLine();
        Log.AppendLine(_failed == 0
            ? $"OK — {_total} проверок пройдено"
            : $"ПРОВАЛ — {_failed} из {_total}");

        Write(Log.ToString());
        return _failed == 0 ? 0 : 1;
    }

    static void Eq<T>(T actual, T expected, string what)
    {
        _total++;
        if (EqualityComparer<T>.Default.Equals(actual, expected)) return;
        _failed++;
        Log.AppendLine($"  ПРОВАЛ  {what}: получено {actual}, ожидалось {expected}");
    }

    static void True(bool cond, string what)
    {
        _total++;
        if (cond) return;
        _failed++;
        Log.AppendLine($"  ПРОВАЛ  {what}");
    }

    // Ошибка здесь = апскейл (видимое мыло) или лишние 70 МБ и 100 мс на кадр
    static void PickScale()
    {
        Log.AppendLine("Decoder.PickScale");

        // длинная сторона попадает точно в цель, пропорции сохраняются
        Eq(Decoder.PickScale(6000, 4000, 2560), (2560u, 1706u), "6000x4000 -> 2560");
        // граница тира: 4000 при цели 2048 обязано дать 2048, а не нативные 4000
        Eq(Decoder.PickScale(4000, 3000, 2048), (2048u, 1536u), "4000x3000 -> 2048 (не нативные)");
        // мелкое не увеличиваем
        Eq(Decoder.PickScale(300, 200, 2560), (300u, 200u), "300x200 -> 2560 (без апскейла)");
        // ровно в размер — тоже без изменений
        Eq(Decoder.PickScale(2560, 1440, 2560), (2560u, 1440u), "точное совпадение");
        // портрет: считаем по длинной стороне
        Eq(Decoder.PickScale(4000, 6000, 2560), (1706u, 2560u), "портрет 4000x6000");
        // вырожденные значения не делят на ноль
        Eq(Decoder.PickScale(6000, 4000, 0), (6000u, 4000u), "цель 0");
        Eq(Decoder.PickScale(0, 0, 2560), (1u, 1u), "нулевой размер");
        Eq(Decoder.PickScale(1, 1, 2560), (1u, 1u), "1x1");
        // экстремальные пропорции не схлопываются в ноль
        Eq(Decoder.PickScale(20000, 200, 2560), (2560u, 25u), "панорама 20000x200");
    }

    // Ошибка здесь = кадр не по центру, неверный процент зума и промах 1:1
    static void FitToViewport()
    {
        Log.AppendLine("MainWindow.Fit");

        // ландшафт в широкий вьюпорт: упирается в высоту
        Eq(MainWindow.Fit(4000, 3000, 1904, 942), (1256.0, 942.0), "4000x3000 в 1904x942");
        // портрет: упирается в высоту, ширина считается пропорционально
        Eq(MainWindow.Fit(1200, 1800, 1904, 942), (628.0, 942.0), "портрет 1200x1800");
        // мелкое не растягиваем
        Eq(MainWindow.Fit(320, 240, 1904, 942), (320.0, 240.0), "мелкое без апскейла");
        // ...кроме заглушки
        Eq(MainWindow.Fit(320, 240, 1904, 942, allowUpscale: true), (1256.0, 942.0), "заглушка растягивается");
        // панорама: высота не схлопывается в ноль
        True(MainWindow.Fit(20000, 200, 1904, 942).h >= 1, "панорама не даёт нулевую высоту");
        // вырожденные значения не роняют
        Eq(MainWindow.Fit(4000, 3000, 0, 0), (1.0, 1.0), "нулевой вьюпорт");
        Eq(MainWindow.Fit(0, 0, 1904, 942), (1.0, 1.0), "нулевой источник");
    }

    // ЕДИНСТВЕННОЕ место, где можно потерять данные пользователя
    static void ResolveTarget()
    {
        Log.AppendLine("Slots.ResolveTarget");

        // Модель диска: имя -> размер, null если файла нет
        static Func<string, long?> Disk(params (string name, long size)[] files) =>
            path =>
            {
                var n = System.IO.Path.GetFileName(path);
                foreach (var f in files) if (f.name == n) return f.size;
                return null;
            };

        // Прямые слэши и ожидания через Path.Combine: литералы с обратным слэшем
        // слишком легко испортить при генерации кода — уже наступал.
        const string Dir = "C:/out";
        static string At(string name) => System.IO.Path.Combine(Dir, name);

        Eq(Slots.ResolveTarget(Dir, "a.jpg", 100, Disk()),
           (At("a.jpg"), false), "пустая папка");

        // тот же размер = уже скопирован; повторный прогон не плодит дубли
        Eq(Slots.ResolveTarget(Dir, "a.jpg", 100, Disk(("a.jpg", 100))),
           (At("a.jpg"), true), "тот же размер -> уже там");

        // другой размер = чужой файл, перезаписывать нельзя
        Eq(Slots.ResolveTarget(Dir, "a.jpg", 100, Disk(("a.jpg", 999))),
           (At("a (2).jpg"), false), "другой размер -> (2)");

        // цепочка занятых имён
        Eq(Slots.ResolveTarget(Dir, "a.jpg", 100,
               Disk(("a.jpg", 1), ("a (2).jpg", 2), ("a (3).jpg", 3))),
           (At("a (4).jpg"), false), "цепочка -> (4)");

        // наша копия уже лежит под суффиксом — тоже no-op, а не (3)
        Eq(Slots.ResolveTarget(Dir, "a.jpg", 100,
               Disk(("a.jpg", 1), ("a (2).jpg", 100))),
           (At("a (2).jpg"), true), "копия под суффиксом -> уже там");

        // имя без расширения
        Eq(Slots.ResolveTarget(Dir, "README", 5, Disk(("README", 9))),
           (At("README (2)"), false), "без расширения");

        // точки в имени: расширение только последнее
        Eq(Slots.ResolveTarget(Dir, "a.b.jpg", 5, Disk(("a.b.jpg", 9))),
           (At("a.b (2).jpg"), false), "точки в имени");

        // размер 0 не должен случайно совпасть с несуществующим файлом
        Eq(Slots.ResolveTarget(Dir, "z.jpg", 0, Disk(("z.jpg", 0))),
           (At("z.jpg"), true), "нулевой размер тоже сравнивается");
    }

    // Порядок обязан совпадать с окном Проводника, из которого пользователь пришёл
    static void NaturalSort()
    {
        Log.AppendLine("FolderList.NaturalCompare");

        True(FolderList.NaturalCompare("img_2.jpg", "img_10.jpg") < 0, "img_2 < img_10");
        True(FolderList.NaturalCompare("img_10.jpg", "img_100.jpg") < 0, "img_10 < img_100");
        True(FolderList.NaturalCompare("a.jpg", "a.jpg") == 0, "равные строки");
        True(FolderList.NaturalCompare(null, "a") < 0, "null не роняет");
        True(FolderList.NaturalCompare("a", null) > 0, "null справа не роняет");
        True(FolderList.NaturalCompare("", "a") < 0, "пустая строка");

        var got = new[] { "img_100.jpg", "img_2.jpg", "img_20.jpg", "img_1.jpg", "img_10.jpg" };
        Array.Sort(got, FolderList.NaturalCompare);
        Eq(string.Join(",", got),
           "img_1.jpg,img_2.jpg,img_10.jpg,img_20.jpg,img_100.jpg",
           "полная сортировка");

        // кириллица не роняет нативный вызов
        var ru = new[] { "фото_10.jpg", "фото_2.jpg" };
        Array.Sort(ru, FolderList.NaturalCompare);
        Eq(string.Join(",", ru), "фото_2.jpg,фото_10.jpg", "кириллица");
    }

    // Bidi-спуфинг в имени файла не должен выворачивать интерфейс
    static void SafeNames()
    {
        Log.AppendLine("MainWindow.SafeName");

        Eq(MainWindow.SafeName("обычное.jpg"), "обычное.jpg", "обычное имя не трогаем");
        Eq(MainWindow.SafeName("a‮gpj.jpg"), "agpj.jpg", "RLO вырезан");
        Eq(MainWindow.SafeName("‎a⁦b⁩c"), "abc", "LRM и изоляты вырезаны");
        Eq(MainWindow.SafeName(""), "", "пустая строка");
        // Все сообщения приложения проходят через ShowToast, который зовёт SafeName.
        // Проверяем ровно ту строку, что собирает Fail().
        Eq(MainWindow.SafeName("Не удалось открыть a‮gpj.jpg"),
           "Не удалось открыть agpj.jpg", "имя внутри сообщения очищено");

        // эмодзи и суррогатные пары не ломаются
        Eq(MainWindow.SafeName("кадр_-.jpg".Replace("-", "🎞")), "кадр_🎞.jpg", "эмодзи цел");
    }

    /// <summary>
    /// Каждый токен палитры обязан существовать во ВСЕХ трёх словарях тем.
    /// Опечатка в одном из них не видна при разработке: она всплывёт только у
    /// пользователя, который включил высокую контрастность, — и обвалит окно
    /// исключением при резолве ThemeResource.
    /// </summary>
    static void ThemeTokens()
    {
        Log.AppendLine("App.xaml ThemeDictionaries");

        string[] tokens =
        {
            "GalleryCanvasBrush", "GallerySurfaceBrush", "GallerySurfaceElevatedBrush",
            "GalleryStrokeBrush", "GalleryTextPrimaryBrush", "GalleryTextSecondaryBrush",
            "GalleryTextTertiaryBrush", "GalleryAccentBrush", "GalleryDangerBrush",
            "GalleryWarningBrush", "GalleryOverlayBrush", "GalleryTransparencyBrush",
            "GalleryDropTintBrush", "GalleryBadgeTextBrush", "GallerySlotEmptyBrush",
        };

        var themes = Microsoft.UI.Xaml.Application.Current.Resources.ThemeDictionaries;
        var keySets = new Dictionary<string, HashSet<string>>();

        foreach (var theme in new[] { "Default", "Light", "HighContrast" })
        {
            if (!themes.TryGetValue(theme, out var raw) ||
                raw is not Microsoft.UI.Xaml.ResourceDictionary dict)
            {
                True(false, $"словарь темы {theme} отсутствует");
                continue;
            }

            foreach (var t in tokens)
                True(dict.ContainsKey(t), $"{theme}: нет токена {t}");

            var keys = new HashSet<string>();
            foreach (var k in dict.Keys)
            {
                var name = k?.ToString();
                if (name is null) continue;
                keys.Add(name);
                True(dict[k] is Microsoft.UI.Xaml.Media.Brush, $"{theme}: {name} — не кисть");
            }
            keySets[theme] = keys;
        }

        // Расхождение НАБОРОВ — то, что ломает тему незаметно: лишний ключ в одной
        // теме и его отсутствие в другой обвалят окно только у того, кто её включил
        if (keySets.Count == 3)
        {
            var baseline = keySets["Default"];
            foreach (var theme in new[] { "Light", "HighContrast" })
                True(baseline.SetEquals(keySets[theme]),
                     $"набор ключей {theme} не совпадает с Default");
        }
    }

    /// <summary>
    /// Лента миниатюр (§4.8). Вся её модель держится на одном утверждении:
    /// середина ряда — текущий кадр. Если оно перестанет выполняться, лента
    /// поедет вбок, и заметить это по коду будет трудно.
    /// </summary>
    static void StripCells()
    {
        Log.AppendLine();
        Log.AppendLine("MainWindow.CellCount / CellIndex");

        foreach (var w in new[] { 1.0, 320.0, 800.0, 1100.0, 1920.0, 3840.0 })
            True(MainWindow.CellCount(w, 86) % 2 == 1, $"ряд при ширине {w} нечётный");

        Eq(MainWindow.CellCount(1100, 86), 15, "1100 px -> 15 ячеек");
        Eq(MainWindow.CellCount(0, 86), 3, "нулевая ширина -> минимальный ряд");
        True(MainWindow.CellCount(3840, 86) > MainWindow.CellCount(1100, 86),
             "шире окно — длиннее ряд");

        // Главный инвариант: середина ряда — всегда текущий кадр
        foreach (var w in new[] { 320.0, 1100.0, 3840.0 })
        {
            var count = MainWindow.CellCount(w, 86);
            foreach (var cur in new[] { 0, 1, 7, 12345 })
                Eq(MainWindow.CellIndex(cur, count, count / 2), cur,
                   $"центр ряда {count} при кадре {cur}");
        }

        Eq(MainWindow.CellIndex(10, 15, 0), 3, "левый край ряда 15 при кадре 10");
        Eq(MainWindow.CellIndex(10, 15, 14), 17, "правый край ряда 15 при кадре 10");
        Eq(MainWindow.CellIndex(0, 15, 0), -7, "у начала папки слева выходим за список");
    }

    /// <summary>
    /// Строка папки в статус-баре (§7.4). Ошибка здесь либо ломает вёрстку полосы
    /// длинной строкой, либо срезает имя папки — то единственное, ради чего
    /// строка и нужна.
    /// </summary>
    static void SubfolderScan()
    {
        Log.AppendLine();
        Log.AppendLine("Paint");

        // Ровный фон 100 — на нём видно и что нарисовали, и чего не задели
        const int lw = 48, lh = 48;
        var img = new SkiaSharp.SKColor[lw * lh];
        for (var i = 0; i < img.Length; i++) img[i] = new SkiaSharp.SKColor(100, 100, 100);

        // Пипетка берёт МЕДИАНУ 3x3: одиночный отсчёт на сжатой картинке может
        // оказаться артефактом, а не цветом
        img[30 * lw + 30] = new SkiaSharp.SKColor(0, 0, 0);          // выброс
        var picked = Paint.Pick(img, lw, lh, 30, 30);
        Eq(picked.Red, (byte)100, "пипетка пережила одиночный выброс");
        Eq(Paint.Pick(img, lw, lh, 0, 0).Red, (byte)100, "пипетка работает в углу кадра");

        // Линия ложится выбранным цветом, рядом кадр не тронут
        var ink = new SkiaSharp.SKColor(10, 20, 30);
        var rect = Paint.Line(img, lw, lh, 5, 40, 40, 40, 4, ink);
        Eq(img[40 * lw + 20].Red, (byte)10, "линия легла цветом пера");
        Eq(img[44 * lw + 20].Red, (byte)100, "в стороне от линии кадр цел");
        Eq((rect.Left <= 5 && rect.Right >= 40 && rect.Top <= 38 && rect.Bottom >= 42), true,
           "задетая область накрывает линию");

        // Край мягкий: на границе линии полутон, а не ступенька. Резкая
        // граница поверх сжатой картинки читается как наклейка
        var edge = img[42 * lw + 20].Red;
        Eq(edge > 20 && edge < 90, true, $"край линии смягчён ({edge})");

        // Квадрат и круг рисуются КОНТУРОМ, середина остаётся нетронутой
        var frame = new SkiaSharp.SKRectI(4, 4, 30, 24);
        Paint.Rect(img, lw, lh, frame, 2, ink);
        Eq(img[4 * lw + 15].Red, (byte)10, "верхняя сторона квадрата");
        Eq(img[23 * lw + 15].Red, (byte)10, "нижняя сторона квадрата");
        Eq(img[14 * lw + 15].Red, (byte)100, "внутри квадрата пусто");

        var img2 = new SkiaSharp.SKColor[lw * lh];
        for (var i = 0; i < img2.Length; i++) img2[i] = new SkiaSharp.SKColor(100, 100, 100);
        Paint.Ellipse(img2, lw, lh, new SkiaSharp.SKRectI(8, 8, 40, 40), 2, ink);
        Eq(img2[23 * lw + 8].Red < 60, true, "левая точка круга");
        Eq(img2[8 * lw + 23].Red < 60, true, "верхняя точка круга");
        Eq(img2[23 * lw + 23].Red, (byte)100, "внутри круга пусто");
        Eq(img2[23 * lw + 45].Red, (byte)100, "снаружи круга пусто");

        Log.AppendLine();
        Log.AppendLine("Smooth.Run");

        // Кадр целиком в разметке: краевого цвета нет, и заливать НЕЧЕМ.
        // Молча вернуть чёрный лист — худший из возможных ответов
        using (var all = new SkiaSharp.SKBitmap(16, 16))
        {
            var px = all.Pixels;
            for (var i = 0; i < px.Length; i++) px[i] = new SkiaSharp.SKColor(200, 200, 200);
            all.Pixels = px;

            var full = new byte[16 * 16];
            Array.Fill(full, (byte)255);
            Eq(Smooth.Run(all, full) is null, true, "заливать нечего — отказ, а не чёрный лист");

            // А дыра с краями зарастает цветом краёв
            var spot = new byte[16 * 16];
            for (var y = 6; y < 10; y++)
                for (var x = 6; x < 10; x++) spot[y * 16 + x] = 255;
            using var got = Smooth.Run(all, spot);
            Eq(got is not null, true, "дыра с краями заливается");
            Eq(got!.GetPixel(8, 8).Red, (byte)200, "цвет взят с краёв");
        }

        Log.AppendLine();
        Log.AppendLine("Smooth.Harmonic");

        // Дыра в линейном градиенте обязана зарасти ТЕМ ЖЕ градиентом: в этом
        // весь смысл — не средний цвет пятном, а переход от края к краю
        const int gw = 64, gh = 48;
        var grad = new float[gw * gh];
        for (var y = 0; y < gh; y++)
            for (var x = 0; x < gw; x++)
                grad[y * gw + x] = 10 + 2.0f * x + 0.5f * y;

        var want = (float[])grad.Clone();
        var hole = new bool[gw * gh];
        for (var y = 12; y < 36; y++)
            for (var x = 16; x < 48; x++)
            {
                hole[y * gw + x] = true;
                grad[y * gw + x] = 255;          // мусор на месте дыры
            }

        Smooth.Harmonic(grad, hole, gw, gh);

        var worst = 0f;
        for (var i = 0; i < grad.Length; i++)
            if (hole[i]) worst = Math.Max(worst, Math.Abs(grad[i] - want[i]));
        Eq(worst < 1.0f, true, $"градиент восстановлен, худшая ошибка {worst:F2}");

        // Ровный фон остаётся ровным, а не уезжает
        var flat = new float[gw * gh];
        var fh = new bool[gw * gh];
        for (var i = 0; i < flat.Length; i++) flat[i] = 128;
        for (var y = 20; y < 30; y++)
            for (var x = 20; x < 30; x++) { fh[y * gw + x] = true; flat[y * gw + x] = 0; }
        Smooth.Harmonic(flat, fh, gw, gh);
        var off = 0f;
        for (var i = 0; i < flat.Length; i++) if (fh[i]) off = Math.Max(off, Math.Abs(flat[i] - 128));
        Eq(off < 0.01f, true, "ровный фон остаётся ровным");

        // Без дыр не трогает ничего
        var same = new float[] { 1, 2, 3, 4 };
        Smooth.Harmonic(same, new bool[4], 2, 2);
        Eq((same[0], same[3]), (1f, 4f), "без разметки кадр не меняется");

        Log.AppendLine();
        Log.AppendLine("Cleanup.Grip / Resize");

        var box = new SkiaSharp.SKRectI(100, 100, 200, 180);     // 100x80

        // Углы дают две стороны, края — одну, середина — ни одной
        Eq(Cleanup.Grip(box, 100, 100, 5), Cleanup.Side.Left | Cleanup.Side.Top, "левый верхний угол");
        Eq(Cleanup.Grip(box, 199, 179, 5), Cleanup.Side.Right | Cleanup.Side.Bottom, "правый нижний угол");
        Eq(Cleanup.Grip(box, 150, 100, 5), Cleanup.Side.Top, "верхний край");
        Eq(Cleanup.Grip(box, 100, 140, 5), Cleanup.Side.Left, "левый край");
        Eq(Cleanup.Grip(box, 150, 140, 5), Cleanup.Side.None, "середина — не край");
        Eq(Cleanup.Grip(box, 300, 300, 5), Cleanup.Side.None, "мимо рамки");

        // Снаружи край хватается так же, как изнутри: у края кадра места внутри
        // может не быть вовсе
        Eq(Cleanup.Grip(box, 97, 140, 5), Cleanup.Side.Left, "край хватается снаружи");

        // На мелкой рамке полоса ужимается — иначе рамку нельзя было бы выбрать
        var tiny = new SkiaSharp.SKRectI(0, 0, 9, 9);
        Eq(Cleanup.Grip(tiny, 4, 4, 9), Cleanup.Side.None, "в мелкой рамке середина осталась");

        // Сторона упирается в противоположную, а не выворачивает рамку
        var pulled = Cleanup.Resize(box, Cleanup.Side.Left, 400, 0, 8);
        Eq((pulled.Left, pulled.Right), (192, 200), "левый край упёрся в правый");
        var grown = Cleanup.Resize(box, Cleanup.Side.Right, 400, 0, 8);
        Eq((grown.Left, grown.Right), (100, 401), "правый край растянут");
        var corner = Cleanup.Resize(box, Cleanup.Side.Left | Cleanup.Side.Bottom, 120, 300, 8);
        Eq((corner.Left, corner.Top, corner.Right, corner.Bottom), (120, 100, 200, 301), "угол тянет две стороны");
        var kept = Cleanup.Resize(box, Cleanup.Side.Left, 150, 999, 8);
        Eq((kept.Top, kept.Bottom), (100, 180), "нетронутая ось не меняется");

        // Нарисованная ручка обязана хвататься ВСЯ: её край шире полосы захвата —
        // это ловушка, нажатие по нему рисовало бы новую рамку вместо правки
        foreach (var b in new[] { new SkiaSharp.SKRectI(100, 100, 220, 115),   // 120x15, строка
                                  new SkiaSharp.SKRectI(50, 50, 58, 58),       // 8x8, минимум
                                  new SkiaSharp.SKRectI(0, 0, 300, 300) })
        {
            var hs = Math.Min(6, Cleanup.Band(b, 9));
            int[] hx = { b.Left, b.MidX, b.Right - 1 }, hy = { b.Top, b.MidY, b.Bottom - 1 };
            var ok = true;
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                {
                    if (i == 1 && j == 1) continue;
                    for (var dy = -hs; dy <= hs; dy++)
                        for (var dx = -hs; dx <= hs; dx++)
                            if (Math.Abs(dx) == hs || Math.Abs(dy) == hs)
                                if (Cleanup.Grip(b, hx[i] + dx, hy[j] + dy, 9) == Cleanup.Side.None)
                                    ok = false;
                }
            Eq(ok, true, $"ручки рамки {b.Width}x{b.Height} хватаются целиком");
        }

        // Ворота справа и снизу считаются по последнему пикселю, а не по
        // исключающему краю — иначе там лишний пиксель слабины
        Eq(Cleanup.Grip(box, 199 + 5, 140, 5), Cleanup.Side.Right, "правый край на границе полосы");
        Eq(Cleanup.Grip(box, 199 + 6, 140, 5), Cleanup.Side.None, "за полосой справа — мимо");
        Eq(Cleanup.Grip(box, 150, 179 + 6, 5), Cleanup.Side.None, "за полосой снизу — мимо");
        Eq(Cleanup.Grip(box, 100 - 6, 140, 5), Cleanup.Side.None, "за полосой слева — мимо");

        Log.AppendLine();
        Log.AppendLine("FolderList.Fence");

        // Стена вокруг САМОЙ открытой папки — не стена, а слепота: так папка
        // слота показывала «нет изображений» при полной папке файлов
        var here = @"D:\фото\проверка";
        Eq(FolderList.Fence(new[] { here }, here).Count, 0, "стена вокруг самой папки снята");
        Eq(FolderList.Fence(new[] { @"D:\фото" }, here).Count, 0, "стена вокруг предка снята");
        Eq(FolderList.Fence(new[] { here + @"\готово" }, here).Count, 1, "стена в подпапке осталась");
        Eq(FolderList.Fence(new[] { @"D:\фото2" }, here).Count, 1, "чужая стена осталась");
        Eq(FolderList.Fence(new[] { here.ToUpperInvariant() }, here).Count, 0, "регистр пути не важен");

        Log.AppendLine();
        Log.AppendLine("FolderList: подпапки");

        static Entry E(string p) => new(p, 0, default);

        // Том, разложенный по главам: сначала корень, потом главы по порядку,
        // внутри главы — натуральная сортировка, чтобы 2 шла перед 10
        var items = new List<Entry>
        {
            E(@"D:\vol\ch10\p2.jpg"),
            E(@"D:\vol\ch2\p10.jpg"),
            E(@"D:\vol\cover.jpg"),
            E(@"D:\vol\ch2\p2.jpg"),
            E(@"D:\vol\ch10\p1.jpg"),
        };
        FolderList.SortEntries(items);
        Eq(string.Join(" ", items.Select(i => i.Path.Substring(7))),
           @"cover.jpg ch2\p2.jpg ch2\p10.jpg ch10\p1.jpg ch10\p2.jpg",
           "корень, затем главы по порядку");

        // Стены: целевая папка копирования внутри исходной не перечисляется
        var walls = FolderList.Walls(new string?[] { @"D:\vol\keep", null, "  " });
        Eq(walls.Count, 1, "пустые стены отброшены");
        True(FolderList.Under(@"D:\vol\keep", walls), "сама стена");
        True(FolderList.Under(@"D:\vol\keep\sub", walls), "папка под стеной");
        True(!FolderList.Under(@"D:\vol\keeper", walls), "похожее имя — не стена");
        True(!FolderList.Under(@"D:\vol\ch2", walls), "соседняя папка");
        True(!FolderList.Under(null, walls), "пустой путь");

        // Без стен ничего не отсекается
        Eq(FolderList.Walls(null).Count, 0, "стен нет");
    }

    static void CleanupBlocks()
    {
        Log.AppendLine();
        Log.AppendLine("Cleanup.Norm / Pick");

        // Рамку тянут в любую сторону — итог один
        var a = Cleanup.Norm(10, 10, 40, 30);
        var b = Cleanup.Norm(40, 30, 10, 10);
        Eq((a.Left, a.Top, a.Right, a.Bottom), (10, 10, 41, 31), "рамка из двух углов");
        Eq((b.Left, b.Top, b.Right, b.Bottom), (a.Left, a.Top, a.Right, a.Bottom),
           "направление протяжки роли не играет");

        // Щелчок по вложенной рамке обязан выбрать ВЛОЖЕННУЮ
        var boxes = new List<SkiaSharp.SKRectI>
        {
            new(0, 0, 200, 200),      // крупная
            new(50, 50, 90, 90),      // мелкая внутри неё
        };
        Eq(Cleanup.Pick(boxes, 60, 60), 1, "из вложенных выбрана тесная");
        Eq(Cleanup.Pick(boxes, 150, 150), 0, "вне мелкой — крупная");
        Eq(Cleanup.Pick(boxes, 500, 500), -1, "мимо всех");

        // Правый и нижний край исключающие — иначе соседние рамки спорят
        Eq(Cleanup.Pick(new List<SkiaSharp.SKRectI> { new(0, 0, 10, 10) }, 10, 5), -1, "правый край не входит");
        Eq(Cleanup.Pick(new List<SkiaSharp.SKRectI> { new(0, 0, 10, 10) }, 9, 9), 0, "последний пиксель входит");

        // Рамку тянут за край кадра — она обязана прижаться. Без этого
        // «Разметить блок» выходила за массив маски и роняла приложение
        var big = Cleanup.Clamp(Cleanup.Norm(80, 90, 400, 500), 100, 100);
        Eq((big.Left, big.Top, big.Right, big.Bottom), (80, 90, 100, 100), "рамка прижата к кадру");
        var neg = Cleanup.Clamp(Cleanup.Norm(-50, -70, 40, 30), 100, 100);
        Eq((neg.Left, neg.Top, neg.Right, neg.Bottom), (0, 0, 41, 31), "рамка прижата слева и сверху");
        var whole = Cleanup.Clamp(Cleanup.Norm(0, 0, 99, 99), 100, 100);
        Eq((whole.Left, whole.Top, whole.Right, whole.Bottom), (0, 0, 100, 100), "во весь кадр не урезана");

        // Самое главное: обход прижатой рамки не выходит за маску страницы
        var mask = new byte[100 * 100];
        var hit = 0;
        for (var y = big.Top; y < big.Bottom; y++)
            for (var x = big.Left; x < big.Right; x++) { mask[y * 100 + x] = 1; hit++; }
        Eq(hit, big.Width * big.Height, "обход рамки укладывается в маску");
    }

    static void CleanupBrush()
    {
        Log.AppendLine();
        Log.AppendLine("Cleanup.Group");

        static SkiaSharp.SKRectI R(int x, int y, int w, int h) => new(x, y, x + w, y + h);
        static List<SkiaSharp.SKRectI> G(params SkiaSharp.SKRectI[] r) => Cleanup.Group(r.ToList());

        // Слова одной строки: стоят рядом, просвет меньше высоты буквы
        Eq(G(R(10, 10, 50, 40), R(70, 12, 60, 38)).Count, 1, "слова строки сошлись");

        // Строки одной реплики: друг под другом, просвет меньше высоты строки
        Eq(G(R(10, 10, 200, 40), R(14, 58, 176, 40)).Count, 1, "строки реплики сошлись");

        // Две надписи в разных концах кадра остаются двумя
        Eq(G(R(10, 10, 200, 40), R(10, 400, 200, 40)).Count, 2, "далёкие врозь");
        Eq(G(R(10, 10, 100, 40), R(600, 10, 100, 40)).Count, 2, "и разнесённые по строке — тоже");

        // Два источника дают на одну надпись две рамки внахлёст — складываем
        var one = G(R(10, 10, 200, 60), R(20, 20, 100, 30));
        Eq(one.Count, 1, "вложенная поглощена");
        Eq((one[0].Left, one[0].Top, one[0].Right, one[0].Bottom), (10, 10, 210, 70), "рамка накрыла обе");

        // Ради чего всё затевалось: посредник между двумя надписями не имеет
        // права утянуть их в общую рамку. Старый Merge складывал пересечения
        // по очереди, рамка росла — и четыре реплики сходились в одну
        var chain = G(R(0, 0, 200, 40), R(150, 30, 250, 570), R(350, 560, 200, 40));
        True(chain.Count >= 2, "цепочка не слиплась в одну рамку");
        True(chain.Any(r => r.Right <= 220), "дальняя надпись осталась сама по себе");

        // Вырожденные выбрасываются, а не дают вырезку в два пикселя
        Eq(G(R(0, 0, 2, 2)).Count, 0, "крошечная отброшена");

        Log.AppendLine();
        Log.AppendLine("Cleanup.Absorb");

        static List<SkiaSharp.SKRectI> A(List<SkiaSharp.SKRectI> f, params SkiaSharp.SKRectI[] e)
            => Cleanup.Absorb(f, e);

        // Та же надпись, найденная распознаванием, ложится в готовую рамку
        var fit = A(new List<SkiaSharp.SKRectI> { R(0, 0, 200, 60) }, R(10, 10, 180, 40));
        Eq(fit.Count, 1, "совпавшая строка не даёт второй рамки");
        Eq(fit[0].Right, 200, "рамка осталась на месте");

        // Надпись, которую сегментатор не увидел, приходит своей рамкой
        Eq(A(new List<SkiaSharp.SKRectI> { R(0, 0, 200, 60) }, R(1000, 1000, 100, 40)).Count,
           2, "найденная только распознаванием добавлена");

        // Габарит строки по дуге задевает соседей — и не имеет права их сцепить
        Eq(A(new List<SkiaSharp.SKRectI> { R(0, 0, 200, 40), R(600, 0, 200, 40) }, R(150, 0, 500, 40)).Count,
           3, "широкая строка не сцепила две надписи");

        Log.AppendLine();
        Log.AppendLine("Cleanup.Separate");

        static List<SkiaSharp.SKRectI> S(params SkiaSharp.SKRectI[] r)
            => Cleanup.Separate(r.ToList());

        // Рамка внутри рамки — лишняя: её площадь и так накрыта
        Eq(S(R(0, 0, 400, 300), R(100, 100, 50, 50)).Count, 1, "вложенная убрана");

        // Налезли краем — подрезаем ту, что меньше, до края соседки
        var cut = S(R(400, 0, 1000, 560), R(0, 0, 640, 100));
        Eq(cut.Count, 2, "налезающие остались обе");
        var small = cut[0].Width * cut[0].Height < cut[1].Width * cut[1].Height ? cut[0] : cut[1];
        Eq((small.Left, small.Top, small.Right, small.Bottom), (0, 0, 400, 100), "меньшая обрезана по краю большей");

        // Общая закрашенная площадь от подрезки не меняется — режем только
        // то, что и так накрыто соседкой
        var big = cut[0].Width * cut[0].Height >= cut[1].Width * cut[1].Height ? cut[0] : cut[1];
        Eq((big.Left, big.Top, big.Right, big.Bottom), (400, 0, 1400, 560), "большая не тронута");

        // Сошлись углами — чистого реза нет, и резать нельзя: отрезалось бы
        // то, что не пересекается
        var corner = S(R(0, 0, 100, 100), R(50, 50, 100, 100));
        Eq(corner.Count, 2, "угловое перекрытие оставлено как есть");
        Eq(corner[0].Width * corner[0].Height + corner[1].Width * corner[1].Height,
           100 * 100 * 2, "и ни одна не урезана");

        // Рамки врозь не трогаем вовсе
        Eq(S(R(0, 0, 100, 100), R(500, 500, 100, 100)).Count, 2, "далёкие целы");

        Log.AppendLine();
        Log.AppendLine("Cleanup.Paint");

        // Кисть кладёт круг, а не квадрат: у квадратной правки видны углы
        var m = new byte[21 * 21];
        Cleanup.Paint(m, 21, 21, 10, 10, 5, add: true);
        Eq(m[10 * 21 + 10], (byte)255, "середина закрашена");
        Eq(m[10 * 21 + 15], (byte)255, "край радиуса закрашен");
        Eq(m[5 * 21 + 5], (byte)0, "угол квадрата не тронут");

        // И стирает тем же движением
        Cleanup.Paint(m, 21, 21, 10, 10, 5, add: false);
        var left = 0;
        foreach (var v in m) if (v != 0) left++;
        Eq(left, 0, "ластик убрал ровно то же");

        // У края кадра кисть не должна выходить за пределы массива
        Cleanup.Paint(m, 21, 21, 0, 0, 8, add: true);
        Eq(m[0], (byte)255, "угол закрашен");
        Cleanup.Paint(m, 21, 21, 20, 20, 8, add: true);
        Eq(m[20 * 21 + 20], (byte)255, "противоположный угол тоже");
    }

    static void CleanupMath()
    {
        Log.AppendLine();
        Log.AppendLine("TextMask.Fit");

        // Вырезка кладётся в квадрат с сохранением пропорций и полями поровну
        Eq(TextMask.Fit(384, 384), (384, 384, 0, 0), "квадрат ложится без полей");
        Eq(TextMask.Fit(768, 384), (384, 192, 0, 96), "широкая — поля сверху и снизу");
        Eq(TextMask.Fit(192, 384), (192, 384, 96, 0), "высокая — поля по бокам");
        Eq(TextMask.Fit(100, 50), (384, 192, 0, 96), "мелкая растягивается");
        Eq(TextMask.Fit(0, 10), (0, 0, 0, 0), "вырожденная");

        Log.AppendLine();
        Log.AppendLine("Inpaint.Region");

        // Стороны ОБЯЗАНЫ быть кратны 8 — иначе модель падает
        var (x, y, w, h) = Inpaint.Region(1000, 800, 100, 100, 199, 199, context: 48);
        Eq(w % 8, 0, "ширина кратна восьми");
        Eq(h % 8, 0, "высота кратна восьми");
        True(x <= 52 && y <= 52, "запас взят слева и сверху");
        True(x + w <= 1000 && y + h <= 800, "не вылезли за страницу");
        True(x <= 100 && y <= 100 && x + w >= 200 && y + h >= 200, "маска внутри области");

        // Маска у самого края: область обязана остаться внутри страницы
        var (x2, y2, w2, h2) = Inpaint.Region(100, 100, 0, 0, 5, 5, context: 48);
        True(x2 >= 0 && y2 >= 0 && x2 + w2 <= 100 && y2 + h2 <= 100, "угол не вывалился");
        Eq((w2 % 8, h2 % 8), (0, 0), "и там кратность цела");

        // Страница мельче восьми — резать нечего, а не падать
        Eq(Inpaint.Region(5, 5, 0, 0, 1, 1), (0, 0, 0, 0), "крошечная страница");

        Log.AppendLine();
        Log.AppendLine("TextMask.Blobs");

        // Два пятна врозь считаются двумя, соприкасающиеся по стороне — одним
        var on = new bool[6 * 3];
        on[0] = on[1] = true;                 // слева
        on[4] = on[5] = true;                 // справа, через просвет
        var blobs = TextMask.Blobs(on, 6, 3);
        Eq(blobs.Count, 2, "просвет разделяет пятна");
        Eq(blobs[0].Area + blobs[1].Area, 4, "площади сошлись");

        var solid = new bool[4 * 4];
        for (var i = 0; i < solid.Length; i++) solid[i] = true;
        var one = TextMask.Blobs(solid, 4, 4);
        Eq(one.Count, 1, "сплошное — одно пятно");
        Eq((one[0].X0, one[0].Y0, one[0].X1, one[0].Y1), (0, 0, 3, 3), "рамка по краям");

        Eq(TextMask.Blobs(new bool[9], 3, 3).Count, 0, "пусто — ни одного");

        Log.AppendLine();
        Log.AppendLine("TextMask.Grow");

        static int Lit(byte[] m) { var n = 0; foreach (var v in m) if (v != 0) n++; return n; }

        // Запас растёт КРУГОМ, а не квадратом: у квадрата диагональ длиннее
        // стороны в полтора раза, и у буквы вырастают углы вместо каймы
        var dot = new byte[11 * 11];
        dot[5 * 11 + 5] = 255;
        TextMask.Grow(dot, 11, 11, 2);
        Eq(Lit(dot), 13, "точка выросла в круг радиуса 2");

        var wide = new byte[11 * 11];
        wide[5 * 11 + 5] = 255;
        TextMask.Grow(wide, 11, 11, 3);
        Eq(Lit(wide), 29, "круг радиуса 3");
        Eq(wide[5 * 11 + 8], (byte)255, "край радиуса закрашен");
        Eq(wide[8 * 11 + 8], (byte)0, "угол квадрата — нет");

        // Ноль — не трогаем вовсе: это «как отдала модель»
        var same = new byte[9];
        same[4] = 255;
        TextMask.Grow(same, 3, 3, 0);
        Eq(Lit(same), 1, "нулевой запас ничего не меняет");

        // У края буфера рост обязан упереться, а не уйти за массив
        var corner = new byte[5 * 5];
        corner[0] = 255;
        TextMask.Grow(corner, 5, 5, 2);
        Eq(Lit(corner), 6, "в углу вырастает только внутрь");

        Log.AppendLine();
        Log.AppendLine("TextMask.Swell");

        static SkiaSharp.SKRectI Box(int x, int y, int w, int h) => new(x, y, x + w, y + h);

        // Раздувание — то же расширение, только по готовой разметке
        var blow = new byte[11 * 11];
        blow[5 * 11 + 5] = 255;
        TextMask.Swell(blow, 11, 11, Box(0, 0, 11, 11), 2, grow: true);
        Eq(Lit(blow), 13, "раздулось в круг радиуса 2");

        // Поджатие снимает кайму: сплошной квадрат 5x5 теряет внешнее кольцо
        var tight = new byte[11 * 11];
        for (var ty = 3; ty <= 7; ty++)
            for (var tx = 3; tx <= 7; tx++) tight[ty * 11 + tx] = 255;
        TextMask.Swell(tight, 11, 11, Box(0, 0, 11, 11), 1, grow: false);
        Eq(Lit(tight), 9, "поджалось до сердцевины 3x3");

        // За рамку рост не выходит — иначе запас лез бы в соседнюю надпись
        var caged = new byte[11 * 11];
        caged[5 * 11 + 5] = 255;
        TextMask.Swell(caged, 11, 11, Box(4, 4, 3, 3), 3, grow: true);
        var out0 = 0;
        for (var cy = 0; cy < 11; cy++)
            for (var cx = 0; cx < 11; cx++)
                if ((cx < 4 || cx > 6 || cy < 4 || cy > 6) && caged[cy * 11 + cx] != 0) out0++;
        Eq(out0, 0, "за рамку не вылезло");
        Eq(Lit(caged), 9, "внутри рамки залилось всё");

        // Туда-обратно НЕ теряет размеченного: лишнее остаться может, буквы — нет
        var trip = new byte[11 * 11];
        trip[5 * 11 + 5] = 255;
        trip[2 * 11 + 2] = 255;
        TextMask.Swell(trip, 11, 11, Box(0, 0, 11, 11), 2, grow: true);
        TextMask.Swell(trip, 11, 11, Box(0, 0, 11, 11), 2, grow: false);
        True(trip[5 * 11 + 5] != 0 && trip[2 * 11 + 2] != 0, "туда-обратно не съело разметку");

    }

    static void Duplicates()
    {
        Log.AppendLine();
        Log.AppendLine("Slots.FindSame");

        var index = new Dictionary<long, List<string>>
        {
            [100] = new() { @"D:\keep\ch1\a.jpg", @"D:\keep\ch2\b.jpg" },
            [200] = new() { @"D:\keep\c.jpg" },
        };

        // Размера нет в индексе — ответ без единого чтения диска
        var reads = 0;
        Eq(Slots.FindSame(index, @"D:\src\x.jpg", 999, _ => { reads++; return true; }),
           null, "нет такого размера — не дубликат");
        Eq(reads, 0, "и файлы при этом не читались");

        // Размер совпал, содержимое тоже
        Eq(Slots.FindSame(index, @"D:\src\x.jpg", 100, c => c.EndsWith("b.jpg")),
           @"D:\keep\ch2\b.jpg", "найден в подпапке");

        // Размер совпал, а содержимое нет — не дубликат
        Eq(Slots.FindSame(index, @"D:\src\x.jpg", 100, _ => false),
           null, "совпадение размера — ещё не совпадение");

        // Слот смотрит на ту же папку: картинка не должна найти сама себя
        Eq(Slots.FindSame(index, @"D:\keep\ch1\a.jpg", 100, c => c.EndsWith("a.jpg")),
           null, "сам себе не дубликат");

        // Потолок проб: тысяча файлов одного размера не должна вешать нажатие
        var many = new Dictionary<long, List<string>> { [1] = new() };
        for (var i = 0; i < 100; i++) many[1].Add($"f{i}");
        var probes = 0;
        Slots.FindSame(many, "src", 1, _ => { probes++; return false; }, maxProbe: 20);
        Eq(probes, 20, "проб не больше потолка");

        Log.AppendLine();
        Log.AppendLine("MainWindow.Relative");
        Eq(MainWindow.Relative(@"D:\keep", @"D:\keep\ch2\b.jpg"), @"ch2\b.jpg", "путь от корня слота");
        Eq(MainWindow.Relative(@"D:\keep\", @"D:\keep\a.jpg"), "a.jpg", "разделитель на конце корня");
        Eq(MainWindow.Relative(@"D:\other", @"D:\keep\a.jpg"), "a.jpg", "вне корня — только имя");
        Eq(MainWindow.Relative(null, @"D:\keep\a.jpg"), "a.jpg", "корня нет");
        Eq(MainWindow.Relative(@"D:\keep", null), "", "файла нет");
    }

    static void ShortPaths()
    {
        Log.AppendLine();
        Log.AppendLine("MainWindow.ShortPath");

        // Собираем пути без литералов с обратным слэшем: их слишком легко
        // испортить при генерации кода — на этом уже наступали (см. ResolveTarget)
        var sep = System.IO.Path.DirectorySeparatorChar;
        string P(params string[] parts) => "D:" + sep + string.Join(sep, parts);

        Eq(MainWindow.ShortPath(null), "", "null");
        Eq(MainWindow.ShortPath(""), "", "пустая строка");

        var near = P("foto");
        Eq(MainWindow.ShortPath(near), near, "короткий путь не трогаем");

        var deep = P("Project", "Personal", "gallery", "testdata", "2026", "сентябрь");
        var cut = MainWindow.ShortPath(deep, 30);
        True(cut.Length <= 30, $"уложились в лимит: {cut}");
        True(cut.EndsWith("сентябрь", StringComparison.Ordinal), "имя папки уцелело");
        True(cut.StartsWith("D:", StringComparison.Ordinal), "корень уцелел");
        True(cut.Contains('…'), "середина отмечена многоточием");
        True(!cut.Contains("Project", StringComparison.Ordinal), "лишнее начало выброшено");

        // Режем только по разделителям: обрывка сегмента быть не должно
        True(!cut.Contains("onal", StringComparison.Ordinal), "нет обрывка сегмента");

        // Одно имя длиннее лимита — не падаем и лимит не превышаем
        var huge = P(new string('я', 80));
        True(MainWindow.ShortPath(huge, 20).Length <= 20, "длинное имя обрезано по лимиту");

        // Ровно на границе — без изменений
        var exact = P("abc");
        Eq(MainWindow.ShortPath(exact, exact.Length), exact, "длина ровно по лимиту");
    }

    /// <summary>
    /// Память по папкам (§4.6). Свежесть тут задана порядком вставки словаря, и
    /// на ней держатся две вещи сразу: к какой папке вернуться при запуске и
    /// какую выбросить при переполнении. Ошибка тихая — человек просто попадает
    /// не туда, где остановился.
    /// </summary>
    static void HistoryList()
    {
        Log.AppendLine();
        Log.AppendLine("SettingsStore: список папок");

        var s = new Settings();
        SettingsStore.Remember(s, @"D:\a", @"D:\a\1.jpg");
        SettingsStore.Remember(s, @"D:\b", @"D:\b\2.jpg");
        SettingsStore.Remember(s, @"D:\c", @"D:\c\3.jpg");

        // Удаление строки забывает и кадр, и место в порядке — иначе папка
        // всплыла бы снова при следующем открытии
        SettingsStore.Forget(s, @"D:\b");
        Eq(string.Join(" ", SettingsStore.RecentFolders(s)), @"D:\c D:\a", "строка убрана");
        Eq(s.FolderLast.ContainsKey(@"D:\b"), false, "кадр забыт вместе со строкой");
        Eq(s.FolderOrder.Contains(@"D:\b"), false, "и место в порядке тоже");

        // Исчезнувшие папки выбрасываются при открытии списка
        var gone = SettingsStore.PruneMissing(s, f => f != @"D:\a");
        Eq(string.Join(" ", gone), @"D:\a", "исчезнувшая названа");
        Eq(string.Join(" ", SettingsStore.RecentFolders(s)), @"D:\c", "и убрана из списка");

        // Ничего не пропало — ничего и не трогаем
        Eq(SettingsStore.PruneMissing(s, _ => true).Count, 0, "все на месте");
        Eq(string.Join(" ", SettingsStore.RecentFolders(s)), @"D:\c", "список цел");

        // Удаление того, чего нет, не роняет
        SettingsStore.Forget(s, @"D:\нет-такой");
        Eq(string.Join(" ", SettingsStore.RecentFolders(s)), @"D:\c", "лишнее удаление безвредно");
    }

    static void FolderMemory()
    {
        Log.AppendLine();
        Log.AppendLine("SettingsStore.Remember / RecentFolders / Trim");

        var s = new Settings();
        SettingsStore.Remember(s, "A", "a1");
        SettingsStore.Remember(s, "B", "b1");
        SettingsStore.Remember(s, "C", "c1");

        Eq(string.Join(",", SettingsStore.RecentFolders(s)), "C,B,A", "свежая первой");
        Eq(s.FolderLast["A"], "a1", "значение на месте");

        // Повторное обращение к давней папке делает её самой свежей.
        // Именно здесь ломалась версия на голом Dictionary: удаление и вставка
        // возвращали ключ на прежнее место, и порядок не менялся.
        SettingsStore.Remember(s, "A", "a2");
        Eq(string.Join(",", SettingsStore.RecentFolders(s)), "A,C,B", "повтор поднимает наверх");
        Eq(s.FolderLast["A"], "a2", "значение обновилось");
        Eq(s.FolderLast.Count, 3, "дубликата не появилось");
        Eq(s.FolderOrder.Count, 3, "в порядке тоже без дубликата");

        // Вытеснение снимает самые давние, а не самые частые
        SettingsStore.Trim(s, 2);
        Eq(string.Join(",", SettingsStore.RecentFolders(s)), "A,C", "выброшена самая давняя");
        Eq(s.FolderLast.Count, 2, "словарь ужался вместе с порядком");

        SettingsStore.Trim(s, 5);
        Eq(s.FolderLast.Count, 2, "запас больше размера — ничего не трогаем");

        // Настройки прошлых версий: порядка нет, а папки есть
        var old = new Settings();
        old.FolderLast["Z"] = "z1";
        old.FolderLast["Y"] = "y1";
        Eq(string.Join(",", SettingsStore.RecentFolders(old).OrderBy(x => x)), "Y,Z",
           "старые настройки не теряются");
        SettingsStore.Trim(old, 1);
        Eq(old.FolderLast.Count, 1, "старые тоже вытесняются");

        var one = new Settings();
        SettingsStore.Remember(one, "X", "x1");
        Eq(string.Join(",", SettingsStore.RecentFolders(one)), "X", "единственная папка");
        SettingsStore.Trim(one, 0);
        Eq(one.FolderLast.Count, 0, "нулевой запас опустошает");
    }

    /// <summary>
    /// Причёсывание распознанного (§4.9). Измерено на шести трудных случаях:
    /// «I» превращается в «1» в КАЖДОМ из них, поэтому починка обязана быть
    /// надёжной — и обязана не трогать настоящие числа.
    /// </summary>
    static void PlateLayout()
    {
        Log.AppendLine();
        Log.AppendLine("Plates");

        static Plates.Rect R(double x, double y, double w, double h) => new(x, y, w, h);

        // Две реплики одна под другой: верхней некуда расти ниже соседа
        var stack = new[] { R(10, 10, 100, 20), R(10, 60, 100, 20) };
        Eq(Plates.Band(stack, 0, 2, 500), (0.0, 58.0), "сверху край слоя, снизу сосед");
        Eq(Plates.Band(stack, 1, 2, 500), (32.0, 500.0), "сверху сосед, снизу край");

        // Реплика в другой колонке росту не мешает
        var side = new[] { R(10, 10, 100, 20), R(300, 60, 100, 20) };
        Eq(Plates.Band(side, 0, 2, 500), (0.0, 500.0), "непересекающаяся по X — не сосед");

        // Рост симметричен, пока хватает места
        Eq(Plates.Fit(100, 20, 40, 0, 500), (90.0, 40.0), "растём от середины");

        // У соседа не отнимаем: упёрлись — прижались, но не налезли
        var (y, h) = Plates.Fit(60, 20, 40, 32, 500);
        Eq((y, h), (50.0, 40.0), "рост вниз, раз сверху сосед");
        True(y >= 32, "верх не залез на соседа");

        // Места меньше базовой высоты — подложка всё равно закрывает исходник
        Eq(Plates.Fit(10, 20, 40, 0, 15), (10.0, 20.0), "базовую высоту не ужимаем");
    }

    static void LinePitchCheck()
    {
        Log.AppendLine();
        Log.AppendLine("Bubbles.LinePitch");

        static TextBox2 B(double h, int lines) =>
            new() { Source = "x", X = 0, Y = 0, W = 100, H = h, Lines = lines };

        // Реплика из трёх строк даёт шаг втрое меньше своей высоты
        Eq(Bubbles.LinePitch(new[] { B(60, 3) }), 20.0, "высота делится на число строк");

        // Нечётное число реплик — обычная середина
        Eq(Bubbles.LinePitch(new[] { B(20, 1), B(60, 2), B(90, 1) }), 30.0, "середина из трёх");

        // Чётное — НИЖНЯЯ середина: раздутая рамка не должна задирать кегль
        Eq(Bubbles.LinePitch(new[] { B(96, 4), B(77, 1) }), 24.0, "раздутая рамка не решает");

        Eq(Bubbles.LinePitch(Array.Empty<TextBox2>()), 0.0, "нет реплик — нет шага");
    }

    static void BubbleBackground()
    {
        Log.AppendLine();
        Log.AppendLine("Bubbles.Background");

        // Белый пузырь 40x40 с чёрной надписью в середине: медиана обязана
        // дать белый, иначе перевод ляжет тёмным прямоугольником на светлое
        const int w = 40, h = 40;
        var px = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            px[i * 4] = 250; px[i * 4 + 1] = 248; px[i * 4 + 2] = 246; px[i * 4 + 3] = 255;
        }
        for (var y = 16; y < 24; y++)
            for (var x = 8; x < 32; x++)
            {
                var o = (y * w + x) * 4;
                px[o] = 10; px[o + 1] = 10; px[o + 2] = 10;
            }

        var whole = new Bubbles.Box(0, 0, w, h, Bubbles.Bubble, 0.9f);
        Eq(Bubbles.Background(px, w, h, whole), ((byte)246, (byte)248, (byte)250),
           "медиана дала цвет пузыря, а не букв");

        // Рамка вне кадра не роняет и не врёт — отдаёт белое
        var outside = new Bubbles.Box(500, 500, 20, 20, Bubbles.Bubble, 0.9f);
        Eq(Bubbles.Background(px, w, h, outside), ((byte)255, (byte)255, (byte)255),
           "рамка вне кадра — белый по умолчанию");
    }

    static void FreeTextStitch()
    {
        Log.AppendLine();
        Log.AppendLine("Bubbles.Stitch");

        static TextBox2 L(double x, double y, double w, double h, string t) =>
            new() { Source = t, X = x, Y = y, W = w, H = h };

        // Ровно та страница, с которой всё вскрылось: подпись по рисунку,
        // облака нет, рамки строк ещё и налезают друг на друга
        var caption = Bubbles.Stitch(new List<TextBox2>
        {
            L(1542, 245, 330, 201, "Mind"),
            L(1437, 504, 524, 263, "me out?"),
            L(1487, 381, 434, 226, "helping"),
        });
        Eq(caption.Count, 1, "три строки подписи стали одной репликой");
        Eq(caption[0].Source, "Mind helping me out?", "склеены сверху вниз");
        Eq(caption[0].Lines, 3, "запомнили, сколько строк сшили");

        // Две подписи в разных углах кадра не смешиваются
        var corners = Bubbles.Stitch(new List<TextBox2>
        {
            L(105, 21, 381, 223, "I'm in"),
            L(17, 135, 546, 273, "heat ok..."),
            L(1542, 245, 330, 201, "Mind"),
        });
        Eq(corners.Count, 2, "левая и правая подписи порознь");

        // Реплики одной колонки с настоящим просветом НЕ склеиваются
        var column = Bubbles.Stitch(new List<TextBox2>
        {
            L(58, 20, 238, 55, "AHH GEEZE"),
            L(181, 117, 157, 56, "SORRY I'M LATE"),
        });
        Eq(column.Count, 2, "просвет между репликами уважается");

        Eq(Bubbles.Stitch(new List<TextBox2>()).Count, 0, "пустой список");
    }

    static void BubbleGrouping()
    {
        Log.AppendLine();
        Log.AppendLine("Bubbles.Group");

        static TextBox2 L(double x, double y, double w, double h, string t) =>
            new() { Source = t, X = x, Y = y, W = w, H = h };

        var cloud = new Bubbles.Box(100, 100, 300, 200, Bubbles.TextInBubble, 0.9f);

        // Три строки одного пузыря — одна реплика, порядок сверху вниз.
        // Высоты РАЗНЫЕ нарочно: на одинаковых не видно нетранзитивного
        // сравнения, а именно оно и переставляло строки на живых страницах
        var one = Bubbles.Group(new List<TextBox2>
        {
            L(120, 240, 200, 26, "ON TIME..."),
            L(120, 120, 200, 44, "WHY AM I"),
            L(120, 180, 200, 32, "ALWAYS THE ONLY ONE"),
        }, new List<Bubbles.Box> { cloud });

        Eq(one.Count, 1, "три строки пузыря стали одной репликой");
        Eq(one[0].Source, "WHY AM I ALWAYS THE ONLY ONE ON TIME...", "склеены сверху вниз");
        Eq((one[0].X, one[0].Y, one[0].W, one[0].H), (120.0, 120.0, 200.0, 146.0),
           "рамка реплики накрыла все строки");

        // Тот же набор в другом порядке обязан дать тот же текст
        var shuffled = Bubbles.Group(new List<TextBox2>
        {
            L(120, 180, 200, 32, "ALWAYS THE ONLY ONE"),
            L(120, 240, 200, 26, "ON TIME..."),
            L(120, 120, 200, 44, "WHY AM I"),
        }, new List<Bubbles.Box> { cloud });
        Eq(shuffled[0].Source, one[0].Source, "порядок на входе ничего не решает");

        // Строка вне облаков остаётся сама по себе: звук, вывеска
        var loose = Bubbles.Group(new List<TextBox2> { L(900, 900, 80, 20, "BOOM") },
                                  new List<Bubbles.Box> { cloud });
        Eq(loose.Count, 1, "надпись вне пузыря уцелела");
        Eq(loose[0].Source, "BOOM", "и не склеилась ни с чем");

        // Два разных пузыря не смешиваются
        var two = Bubbles.Group(new List<TextBox2>
        {
            L(120, 120, 100, 40, "HEY"),
            L(620, 120, 100, 40, "HI"),
        }, new List<Bubbles.Box>
        {
            cloud,
            new(600, 100, 300, 200, Bubbles.TextInBubble, 0.9f),
        });
        Eq(two.Count, 2, "разные пузыри — разные реплики");

        // Без облаков — ничего не трогаем
        Eq(Bubbles.Group(new List<TextBox2> { L(1, 1, 10, 10, "X") },
                         new List<Bubbles.Box>()).Count, 1, "нет облаков — строки как есть");
    }

    static void Overlaps()
    {
        Log.AppendLine();
        Log.AppendLine("Ocr.Dedupe");

        static TextBox2 B(double x, double y, double w, double h, string t) =>
            new() { Source = t, X = x, Y = y, W = w, H = h };

        // Две рамки на одном пузыре: рисовались одна поверх другой
        var same = Ocr.Dedupe(new List<TextBox2>
        {
            B(100, 100, 200, 60, "WHY AM I ALWAYS"),
            B(105, 100, 190, 60, "THE ONLY ONE ON TIME"),
        });
        Eq(same.Count, 1, "наложение свёрнуто в одну рамку");
        Eq(same[0].Source, "THE ONLY ONE ON TIME", "осталась та, где больше букв");

        // Соседние строки одного пузыря НЕ накладываются — обе обязаны выжить
        var lines = Ocr.Dedupe(new List<TextBox2>
        {
            B(100, 100, 200, 40, "HEY"),
            B(100, 145, 200, 40, "SHINEY"),
        });
        Eq(lines.Count, 2, "соседние строки не трогаем");

        // Касание краем — тоже не наложение
        var touch = Ocr.Dedupe(new List<TextBox2>
        {
            B(0, 0, 100, 100, "ONE"),
            B(90, 0, 100, 100, "TWO"),
        });
        Eq(touch.Count, 2, "перекрытие краем оставляет обе");

        Eq(Ocr.Dedupe(new List<TextBox2>()).Count, 0, "пустой список");
    }

    static void BubbleMath()
    {
        Log.AppendLine();
        Log.AppendLine("Bubbles");

        // Рамка прочитана в вырезке, увеличенной вдвое и снятой с (100, 50).
        // Ошибка знака здесь сдвигает реплику на соседний пузырь, а на глаз
        // это не видно — потому и проверяется числом
        var (x, y, w, h) = Bubbles.ToPage(20, 10, 80, 40, 100, 50, 2.0);
        Eq((x, y, w, h), (110.0, 55.0, 40.0, 20.0), "вырезка -> страница");

        var (x2, y2, _, _) = Bubbles.ToPage(0, 0, 10, 10, 7, 3, 1.0);
        Eq((x2, y2), (7.0, 3.0), "без увеличения — просто сдвиг");

        // Мелкое поднимаем до читаемого, крупное не трогаем и не раздуваем
        Eq(Bubbles.CropScale(140, 70), 10.0, "мелкая вырезка тянется до 1400");
        Eq(Bubbles.CropScale(1400, 700), 1.0, "нужного размера — без изменений");
        Eq(Bubbles.CropScale(2000, 900), 1.0, "крупную не уменьшаем");
        True(Bubbles.CropScale(50, 20) * 50 <= 2400, "выше потолка не растём");
    }

    static void DecodeLoop()
    {
        Log.AppendLine();
        Log.AppendLine("MarianTokenizer.Loops");

        // Ровно то, что вылезло на присланной картинке: «ЧТО ЧТО ЧТО…» без конца
        Eq(MarianTokenizer.Loops(new[] { 7, 7, 7, 7, 7 }), true, "одно слово подряд");
        Eq(MarianTokenizer.Loops(new[] { 1, 2, 3, 1, 2, 3, 1, 2, 3 }), true, "круг из трёх слов");

        Eq(MarianTokenizer.Loops(new[] { 1, 2, 3, 4 }), false, "короче окна — не петля");
        Eq(MarianTokenizer.Loops(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }), false, "все разные");

        // Слово, повторившееся на расстоянии, — обычная речь, обрывать нельзя
        Eq(MarianTokenizer.Loops(new[] { 1, 2, 3, 4, 2, 5, 6, 2 }), false,
           "повтор слова, но не четвёркой");
    }

    static void OcrCleanup()
    {
        Log.AppendLine();
        Log.AppendLine("Ocr.Clean");

        // Ровно тот случай, с которого всё началось
        Eq(Ocr.Clean("OH WELL, MORE BEACH FOR US 1 GUESS"),
           "Oh well, more beach for us I guess", "одинокая единица стала «I»");

        Eq(Ocr.Clean("1 told you so"), "I told you so", "единица в начале");
        Eq(Ocr.Clean("SO 1 SAID"), "So I said", "единица в середине");

        // Настоящее число не трогаем: в строке есть другие цифры
        Eq(Ocr.Clean("1 of 3 pages"), "1 of 3 pages", "при других цифрах не трогаем");
        Eq(Ocr.Clean("ROOM 101"), "Room 101", "число внутри слова не разбираем");

        // Капслок приводится к обычному регистру, но местоимение остаётся
        Eq(Ocr.Clean("WHY AM I ALWAYS LATE"), "Why am I always late", "капслок и «I»");
        Eq(Ocr.Clean("Already normal case"), "Already normal case", "обычный регистр не трогаем");

        // Пробелы схлопываются
        // Регистр трогаем ТОЛЬКО у капслока: обычному тексту заглавную взять неоткуда
        Eq(Ocr.Clean("  too    many   spaces "), "too many spaces", "лишние пробелы убраны");

        // Вырожденное не роняет
        Eq(Ocr.Clean(""), "", "пустая строка");
        Eq(Ocr.Clean("A"), "A", "один знак");
    }

    /// <summary>
    /// Полнота ответа API (§4.10).
    ///
    /// Самая дорогая ошибка всего тракта: пропущенный номер означает, что в
    /// пузырь попадёт чужой текст, и страница будет ВЫГЛЯДЕТЬ переведённой.
    /// Поймано на живой странице — модель слила две реплики в одну.
    /// </summary>
    static void ApiCompleteness()
    {
        Log.AppendLine();
        Log.AppendLine("ApiTranslator.Missing");

        static Dictionary<int, string> M(params (int id, string ru)[] items)
        {
            var d = new Dictionary<int, string>();
            foreach (var (id, ru) in items) d[id] = ru;
            return d;
        }

        Eq(string.Join(",", ApiTranslator.Missing(M((1, "а"), (2, "б"), (3, "в")), 3)),
           "", "полный ответ — пропусков нет");

        // Ровно тот случай со страницы: номер 5 не вернулся
        Eq(string.Join(",", ApiTranslator.Missing(
               M((1, "а"), (2, "б"), (3, "в"), (4, "г"), (6, "е")), 6)),
           "5", "склеенная реплика оставляет дыру");

        Eq(string.Join(",", ApiTranslator.Missing(M((2, "б")), 3)), "1,3", "несколько пропусков");
        Eq(string.Join(",", ApiTranslator.Missing(M(), 2)), "1,2", "пустой ответ — пропущено всё");

        // Пустая строка — это НЕ перевод: такой номер тоже считается пропущенным,
        // иначе в пузыре окажется пустая подложка поверх оригинала
        Eq(string.Join(",", ApiTranslator.Missing(M((1, "а"), (2, "  ")), 2)),
           "2", "пробелы не считаются переводом");
        Eq(string.Join(",", ApiTranslator.Missing(M((1, ""), (2, "б")), 2)),
           "1", "пустая строка не считается переводом");

        // Лишние номера сверх отправленных не мешают: их просто некуда положить
        Eq(string.Join(",", ApiTranslator.Missing(M((1, "а"), (2, "б"), (9, "лишний")), 2)),
           "", "лишний номер не ломает проверку");

        Eq(string.Join(",", ApiTranslator.Missing(M(), 0)), "", "страница без реплик");
    }

    static TextBox2 B(double x, double y, double w, double h, string name) =>
        new() { Source = name, X = x, Y = y, W = w, H = h };

    static string Order(System.Collections.Generic.IReadOnlyList<TextBox2> boxes, bool rtl = false) =>
        string.Join(",", ReadOrder.Sort(boxes, rtl).Select(b => b.Source));

    /// <summary>
    /// Порядок чтения (§4.9). Ошибка тут тихая и дорогая: перевод останется
    /// связным на вид, но реплика достанется другому персонажу, и род в ней
    /// будет согласован не с тем.
    /// </summary>
    static void Reading()
    {
        Log.AppendLine();
        Log.AppendLine("ReadOrder.Sort");

        // Две строки по два пузыря: читается рядами, внутри ряда слева направо
        var grid = new[]
        {
            B(600, 40, 200, 60, "1п"), B(60, 50, 200, 60, "1л"),
            B(60, 400, 200, 60, "2л"), B(600, 420, 200, 60, "2п"),
        };
        Eq(Order(grid), "1л,1п,2л,2п", "сетка 2x2 читается рядами");

        // Манга: тот же лист читается справа налево
        Eq(Order(grid, rtl: true), "1п,1л,2п,2л", "манга — справа налево");

        // Одна колонка: порядок сверху вниз независимо от направления
        var col = new[] { B(50, 300, 300, 60, "в"), B(50, 60, 300, 60, "а"), B(50, 180, 300, 60, "б") };
        Eq(Order(col), "а,б,в", "колонка сверху вниз");
        Eq(Order(col, rtl: true), "а,б,в", "колонка не зависит от направления");

        // Одна строка: порядок по горизонтали
        var row = new[] { B(600, 50, 200, 60, "в"), B(60, 50, 200, 60, "а"), B(330, 50, 200, 60, "б") };
        Eq(Order(row), "а,б,в", "строка слева направо");
        Eq(Order(row, rtl: true), "в,б,а", "строка справа налево");

        // Перекрывающиеся рамки: разрезать нечем, но порядок обязан быть
        // осмысленным, а не случайным
        var overlap = new[] { B(100, 110, 300, 100, "низ"), B(80, 100, 300, 100, "верх") };
        var got = Order(overlap);
        True(got is "верх,низ" or "низ,верх", $"перекрытие не роняет: {got}");

        // Вырожденные случаи
        Eq(Order(Array.Empty<TextBox2>()), "", "пустой список");
        Eq(Order(new[] { B(10, 10, 50, 20, "один") }), "один", "единственная рамка");

        // Ничего не теряется и не двоится — на длинном листе это главное
        var many = Enumerable.Range(0, 25)
            .Select(i => B(60 + i % 5 * 200, 40 + i / 5 * 150, 180, 60, i.ToString()))
            .ToArray();
        var sorted = ReadOrder.Sort(many);
        Eq(sorted.Count, 25, "все рамки на месте");
        Eq(sorted.Select(b => b.Source).Distinct().Count(), 25, "дубликатов нет");
        Eq(Order(many), string.Join(",", Enumerable.Range(0, 25)), "правильная сетка 5x5");
    }

    /// <summary>
    /// Разбор ответа API (§4.9). Ошибка тут сдвигает перевод на соседний пузырь —
    /// страница выглядит переведённой, но реплики перепутаны местами.
    /// </summary>
    static void ApiParsing()
    {
        Log.AppendLine();
        Log.AppendLine("ApiTranslator.Parse");

        var clean = "{\"lines\":[{\"id\":1,\"ru\":\"Привет\"},{\"id\":2,\"ru\":\"Пока\"}]}";
        var m = ApiTranslator.Parse(clean, 2);
        Eq(m.Count, 2, "чистый JSON");
        Eq(m[1], "Привет", "первая реплика");
        Eq(m[2], "Пока", "вторая реплика");

        // Модель любит обернуть ответ в тройные кавычки или предварить фразой
        var fenced = "Вот перевод:\n```json\n" + clean + "\n```";
        Eq(ApiTranslator.Parse(fenced, 2).Count, 2, "JSON в блоке кода");

        // Совсем не JSON — выручает нумерация
        var numbered = "1. Привет\n2) Пока\n3: Ещё";
        var nm = ApiTranslator.Parse(numbered, 3);
        Eq(nm.Count, 3, "разбор по нумерации");
        Eq(nm[2], "Пока", "номер со скобкой");
        Eq(nm[3], "Ещё", "номер с двоеточием");

        // Модель переставила номера — сопоставление идёт ПО НОМЕРУ, не по порядку
        var shuffled = "{\"lines\":[{\"id\":2,\"ru\":\"второй\"},{\"id\":1,\"ru\":\"первый\"}]}";
        var sm = ApiTranslator.Parse(shuffled, 2);
        Eq(sm[1], "первый", "перестановка не сдвигает перевод");
        Eq(sm[2], "второй", "перестановка не сдвигает перевод (2)");

        // Неполный ответ: что есть — берём, недостающее вызывающий добирает сам
        var partial = "{\"lines\":[{\"id\":1,\"ru\":\"только первая\"}]}";
        var pm = ApiTranslator.Parse(partial, 3);
        Eq(pm.Count, 1, "неполный ответ не выдумывает недостающее");
        True(!pm.ContainsKey(2), "пропущенного номера нет в результате");

        // Мусор не роняет и не выдумывает
        Eq(ApiTranslator.Parse("", 2).Count, 0, "пустой ответ");
        Eq(ApiTranslator.Parse("извините, не могу", 2).Count, 0, "отказ без нумерации");

        // Пустые переводы отбрасываются: пустая подложка хуже оригинала
        var empties = "{\"lines\":[{\"id\":1,\"ru\":\"\"},{\"id\":2,\"ru\":\"есть\"}]}";
        var em = ApiTranslator.Parse(empties, 2);
        True(!em.ContainsKey(1), "пустая строка не считается переводом");
        Eq(em[2], "есть", "непустая рядом с пустой уцелела");

        // Состояние тома достаётся из того же ответа
        var withState = "{\"lines\":[{\"id\":1,\"ru\":\"а\"}],\"state\":\"Алиса — ж\"}";
        Eq(ApiTranslator.ExtractState(withState), "Алиса — ж", "карточка персонажей прочитана");
        Eq(ApiTranslator.ExtractState("не json"), null, "нет состояния — нет и ошибки");
    }

    // WinExe не имеет консоли — цепляемся к родительской, если она есть
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    static void Write(string text)
    {
        AttachConsole(-1);
        Console.Out.Write(text);
        Console.Out.Flush();
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gallery");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "self-test.txt"), text);
        }
        catch { }
    }
}
