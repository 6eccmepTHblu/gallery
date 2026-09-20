using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RapidOcrNet;
using SkiaSharp;

namespace Gallery;

/// <summary>
/// Второй распознаватель — PP-OCRv5 на ONNX Runtime (RapidOcrNet, Apache-2.0).
///
/// Заведён ДЛЯ ЗАМЕРА, а не вместо `Windows.Media.Ocr`: тот находит все пузыри,
/// но на комиксном леттеринге ошибается в половине знаков (§4.9), и вопрос
/// ровно один — читает ли этот лучше НА ТОМ ЖЕ корпусе. Пока ответа в цифрах
/// нет, движок доступен только ключом `--page --rapid` и в настройки не вынесен.
/// </summary>
static class RapidOcrEngine
{
    static RapidOcr? _ocr;
    static readonly object Gate = new();

    static string Dir => System.IO.Path.Combine(AppContext.BaseDirectory, "models", "v5");

    static readonly string[] Files =
    {
        "ch_PP-OCRv5_mobile_det.onnx",
        "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx",
        "latin_PP-OCRv5_rec_mobile_infer.onnx",
        "ppocrv5_latin_dict.txt",
    };

    /// <summary>Все ли файлы на месте. Нет — страница читается запасным движком.</summary>
    public static bool Present =>
        Files.All(f => System.IO.File.Exists(System.IO.Path.Combine(Dir, f)));

    public static double LastMs { get; private set; }

    static RapidOcr Instance
    {
        get
        {
            if (_ocr is not null) return _ocr;
            lock (Gate)
            {
                if (_ocr is null)
                {
                    // Пути обязаны быть абсолютными: библиотека ищет модели от
                    // ТЕКУЩЕЙ папки, а её у приложения, запущенного из другого
                    // места, никто не гарантирует
                    string P(string f) => System.IO.Path.Combine(Dir, f);

                    var o = new RapidOcr();
                    o.InitModels(P(Files[0]), P(Files[1]), P(Files[2]), P(Files[3]));
                    _ocr = o;
                }
            }
            return _ocr;
        }
    }

    /// <summary>
    /// Распознать страницу. Возвращает те же TextBox2, что и основной путь, —
    /// иначе сравнивать было бы нечего.
    /// </summary>
    public static PageText Read(string path)
    {
        var t0 = Stopwatch.GetTimestamp();

        using var bmp = SKBitmap.Decode(path);
        if (bmp is null) return new PageText { Boxes = new List<TextBox2>() };

        var res = Instance.Detect(bmp, RapidOcrOptions.Default);
        var boxes = new List<TextBox2>();
        var lines = new List<SKRectI>();
        var heights = new List<double>();

        foreach (var b in res.TextBlocks)
        {
            // Детектор отдаёт четырёхугольник — берём его габарит: остальной
            // конвейер (порядок чтения, подложка, вёрстка перевода) работает
            // с прямоугольниками
            double x0 = double.MaxValue, y0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue;
            foreach (var p in b.BoxPoints)
            {
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
            }
            if (x1 <= x0 || y1 <= y0) continue;

            // Строку запоминаем ДО отбора по прочитанному: очистке важно, что
            // детектор увидел здесь надпись, а не смог ли её прочесть — именно
            // нечитаемый леттеринг и надо стереть
            lines.Add(new SKRectI((int)x0, (int)y0, (int)Math.Ceiling(x1), (int)Math.Ceiling(y1)));

            var text = Ocr.Clean(b.Text ?? string.Empty);
            if (string.IsNullOrWhiteSpace(text)) continue;

            heights.Add(y1 - y0);
            boxes.Add(new TextBox2
            {
                Source = text,
                X = x0,
                Y = y0,
                W = x1 - x0,
                H = y1 - y0,
            });
        }

        // Кадр в BGRA нужен дважды — детектору облаков и выборке фона,
        // поэтому копия одна на обоих
        byte[]? pixels = null;
        try
        {
            using var bgra = bmp.Copy(SKColorType.Bgra8888);
            pixels = bgra.GetPixelSpan().ToArray();
        }
        catch (Exception ex)
        {
            Log.Warn($"пиксели кадра: {ex.Message}");
        }

        // Реплики, которым облака не нашлось: звук, вывеска, надпись на фоне.
        // Список ведём ЯВНО, а не по признаку «фон остался белым»: белый пузырь
        // от неподобранного фона так не отличить, и подложка честно белой
        // реплики пересчитывалась по кольцу — в небо вокруг облака
        var noHost = new List<TextBox2>();
        var viaClouds = false;

        // Строки — в реплики: границу даёт детектор облаков, а не распознаватель
        if (pixels is not null && Bubbles.Present)
        {
            try
            {
                var all = Bubbles.Detect(pixels, bmp.Width, bmp.Height);
                boxes = Bubbles.Group(boxes, all.Where(c => c.Label != Bubbles.Bubble).ToList());

                // Цвет подложки — из облака, в котором лежит реплика. Берём
                // самое ПРОСТОРНОЕ из подходящих: это пузырь целиком, а не
                // тесная рамка текста внутри него
                foreach (var b in boxes)
                {
                    var cx = b.X + b.W / 2;
                    var cy = b.Y + b.H / 2;
                    Bubbles.Box? host = null;
                    foreach (var c in all)
                    {
                        if (cx < c.X || cx > c.X + c.W || cy < c.Y || cy > c.Y + c.H) continue;
                        if (host is null || c.W * c.H > host.W * host.H) host = c;
                    }
                    if (host is null) { noHost.Add(b); continue; }
                    var (r, g, bl) = Bubbles.Background(pixels, bmp.Width, bmp.Height, host);
                    b.BgR = r; b.BgG = g; b.BgB = bl;
                }
                viaClouds = true;
            }
            catch (Exception ex)
            {
                Log.Warn($"сшивка строк по облакам: {ex.Message}");
            }
        }

        // Надписи вне облаков (звук, вывеска) фона от детектора не получили —
        // им остаётся прежний способ, кольцом вокруг рамки
        if (pixels is not null)
        {
            // Детектор отработал — досчитываем только бесхозные; не отработал —
            // всю страницу прежним способом
            var need = viaClouds ? noHost : boxes;
            if (need.Count > 0)
                Ocr.SampleBackgrounds(pixels, bmp.Width, bmp.Height, need, ring: 0.10);
        }

        // Кегль перевода — от шага строки в реплике, а не от рамки строки:
        // рамки у распознавателей разные, шаг один. Прописная занимает около
        // 0,72 кегля, отсюда и множитель
        var median = Bubbles.LinePitch(boxes) * 0.72;
        if (median < 1)
        {
            heights.Sort();
            median = heights.Count > 0 ? heights[heights.Count / 2] : 0;
        }

        LastMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return new PageText
        {
            Boxes = ReadOrder.Sort(Ocr.Dedupe(boxes)),
            Lines = lines,
            ImgW = bmp.Width,
            ImgH = bmp.Height,
            MedianLineH = median,
        };
    }
}
