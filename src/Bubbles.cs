using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Gallery;

/// <summary>
/// Поиск облаков текста на странице комикса — RT-DETRv2, модель
/// ogkalu/comic-text-and-bubble-detector (Apache-2.0, 10,6 МБ).
///
/// Зачем отдельно от распознавания: движок Windows находит текст ТОЛЬКО там,
/// где сумел прочитать хотя бы букву. Пузырь с трудным леттерингом теряется
/// бесследно — не «прочитан с ошибками», а не найден вовсе, и восстановить его
/// потом нечем. Детектор ищет ФОРМУ, а не буквы: он отмечает облако независимо
/// от того, читается ли содержимое. Найденное отдаётся распознаванию вырезанным
/// и увеличенным — и это ровно те реплики, которые раньше пропадали.
///
/// Постобработка запечена в граф: на выходе сразу рамки в пикселях исходного
/// кадра, без NMS и декодирования якорей. Поэтому здесь только масштабирование
/// входа и отбор по уверенности.
/// </summary>
static class Bubbles
{
    /// <summary>Вход у модели жёсткий: 640x640, без сохранения пропорций.</summary>
    public const int Side = 640;

    public const int Bubble = 0;        // сам пузырь целиком
    public const int TextInBubble = 1;  // текст внутри пузыря
    public const int FreeText = 2;      // надпись без пузыря: звук, вывеска

    /// <summary>Ниже этого рамку не берём: измерено — настоящие идут 0.84+.</summary>
    public const float MinScore = 0.5f;

    public sealed record Box(double X, double Y, double W, double H, int Label, float Score);

    public static string ModelPath =>
        Path.Combine(AppContext.BaseDirectory, "models", "comic-bubbles.onnx");

    public static bool Present => File.Exists(ModelPath);

    static InferenceSession? _session;
    static readonly object Gate = new();

    static InferenceSession? Session
    {
        get
        {
            if (_session is not null) return _session;
            lock (Gate)
            {
                if (_session is not null) return _session;
                if (!Present) return null;
                try
                {
                    var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                    _session = new InferenceSession(ModelPath, opts);
                }
                catch (Exception ex)
                {
                    Log.Warn($"детектор облаков не загрузился: {ex.Message}");
                    return null;
                }
            }
            return _session;
        }
    }

    /// <summary>
    /// Найти облака текста. px — кадр в BGRA8, w x h. Рамки возвращаются
    /// в координатах ЭТОГО кадра: модели передан его размер, и она сама
    /// пересчитывает выход обратно.
    /// </summary>
    public static List<Box> Detect(byte[] px, int w, int h)
    {
        var found = new List<Box>();
        var s = Session;
        if (s is null || w < 2 || h < 2) return found;

        var input = new DenseTensor<float>(new[] { 1, 3, Side, Side });
        Fill(input, px, w, h);

        var sizes = new DenseTensor<long>(new[] { 1, 2 });
        sizes[0, 0] = w;                      // ширина, потом высота — порядок модели
        sizes[0, 1] = h;

        using var res = s.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("images", input),
            NamedOnnxValue.CreateFromTensor("orig_target_sizes", sizes),
        });

        Tensor<long>? labels = null;
        Tensor<float>? boxes = null, scores = null;
        foreach (var v in res)
        {
            if (v.Name == "labels") labels = v.AsTensor<long>();
            else if (v.Name == "boxes") boxes = v.AsTensor<float>();
            else if (v.Name == "scores") scores = v.AsTensor<float>();
        }
        if (labels is null || boxes is null || scores is null) return found;

        var n = scores.Dimensions[^1];
        for (var i = 0; i < n; i++)
        {
            var sc = scores[0, i];
            if (sc < MinScore) continue;

            double x0 = boxes[0, i, 0], y0 = boxes[0, i, 1];
            double x1 = boxes[0, i, 2], y1 = boxes[0, i, 3];
            if (x1 - x0 < 2 || y1 - y0 < 2) continue;

            found.Add(new Box(x0, y0, x1 - x0, y1 - y0, (int)labels[0, i], sc));
        }
        return found;
    }

    /// <summary>
    /// Кадр BGRA8 → тензор NCHW RGB, [0..1], билинейно и без нормировки по
    /// среднему: ровно так модель обучали (do_normalize=false, rescale 1/255).
    /// </summary>
    static void Fill(DenseTensor<float> dst, byte[] px, int w, int h)
    {
        // Пропорции не сохраняем нарочно: обучение шло на сплющенных до
        // квадрата страницах, а не на дополненных полями
        var sx = (double)w / Side;
        var sy = (double)h / Side;

        for (var y = 0; y < Side; y++)
        {
            var fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, h - 1);
            var y0 = (int)fy;
            var y1 = Math.Min(y0 + 1, h - 1);
            var ky = fy - y0;

            for (var x = 0; x < Side; x++)
            {
                var fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, w - 1);
                var x0 = (int)fx;
                var x1 = Math.Min(x0 + 1, w - 1);
                var kx = fx - x0;

                var o00 = (y0 * w + x0) * 4;
                var o01 = (y0 * w + x1) * 4;
                var o10 = (y1 * w + x0) * 4;
                var o11 = (y1 * w + x1) * 4;

                for (var c = 0; c < 3; c++)
                {
                    var src = 2 - c;            // BGRA → RGB
                    var top = px[o00 + src] * (1 - kx) + px[o01 + src] * kx;
                    var bot = px[o10 + src] * (1 - kx) + px[o11 + src] * kx;
                    dst[0, c, y, x] = (float)((top * (1 - ky) + bot * ky) / 255.0);
                }
            }
        }
    }

    /// <summary>
    /// Шаг строки на странице, в пикселях кадра.
    ///
    /// Считается ПО РЕПЛИКАМ: высота облака, делённая на число сшитых в него
    /// строк. Рамку строки брать нельзя — каждый распознаватель рисует её
    /// по-своему (на одной странице `Windows.Media.Ocr` даёт 19 px там, где
    /// PP-OCRv5 даёт 33), а высота реплики задана самим пузырём и от движка
    /// не зависит.
    ///
    /// Для чётного числа реплик берём НИЖНЮЮ середину: страница, где реплик
    /// всего две и одна из них — раздутая рамка на весь пузырь, иначе задрала
    /// бы кегль перевода вдвое.
    /// </summary>
    public static double LinePitch(IReadOnlyList<TextBox2> boxes)
    {
        var pitches = boxes
            .Select(b => b.H / Math.Max(1, b.Lines))
            .Where(v => v > 1)
            .OrderBy(v => v)
            .ToList();
        if (pitches.Count == 0) return 0;
        return pitches[(pitches.Count - 1) / 2];
    }

    /// <summary>
    /// Сшить строки в реплики по найденным облакам.
    ///
    /// PP-OCRv5 отдаёт СТРОКИ, а реплика — это пузырь: «WHY AM I ALWAYS» и «THE
    /// ONLY ONE ON TIME...» суть одна фраза, и переводить их порознь — значит
    /// получить два обрывка вместо предложения. Границу реплики знает детектор,
    /// а не распознаватель: он видит форму облака.
    ///
    /// Строка относится к облаку, если в нём лежит её ЦЕНТР: рамки строк часто
    /// чуть вылезают за контур, и проверка по углам теряла бы их.
    /// Из нескольких подходящих берём самое тесное — внутри «пузыря целиком»
    /// обычно лежит рамка «текста в пузыре», и нужна именно вторая.
    /// </summary>
    public static List<TextBox2> Group(List<TextBox2> lines, List<Box> clouds)
    {
        if (lines.Count == 0) return lines;
        if (clouds.Count == 0) return Stitch(lines);

        var buckets = new Dictionary<int, List<TextBox2>>();
        var loose = new List<TextBox2>();

        foreach (var ln in lines)
        {
            var cx = ln.X + ln.W / 2;
            var cy = ln.Y + ln.H / 2;

            var best = -1;
            var bestArea = double.MaxValue;
            for (var i = 0; i < clouds.Count; i++)
            {
                var c = clouds[i];
                if (cx < c.X || cx > c.X + c.W || cy < c.Y || cy > c.Y + c.H) continue;
                var area = c.W * c.H;
                if (area >= bestArea) continue;
                bestArea = area; best = i;
            }

            if (best < 0) { loose.Add(ln); continue; }
            if (!buckets.TryGetValue(best, out var list))
                buckets[best] = list = new List<TextBox2>();
            list.Add(ln);
        }

        var result = new List<TextBox2>(buckets.Count + loose.Count);
        foreach (var (_, list) in buckets) result.Add(Join(list));

        // Строки, которым облака не нашлось, — подписи прямо по рисунку: их
        // детектор видит плохо, а реплика там такая же цельная. Сшиваем по
        // геометрии
        result.AddRange(Stitch(loose));
        return result;
    }

    /// <summary>
    /// Склеить строки в одну реплику. Порядок обязан быть ПОЛНЫМ: сравнение
    /// вида «если разница больше половины высоты» нетранзитивно, и Sort на таком
    /// выдаёт произвольную перестановку — реплика склеивается задом наперёд.
    /// </summary>
    static TextBox2 Join(List<TextBox2> list)
    {
        if (list.Count == 1) return list[0];

        list.Sort((p, q) =>
        {
            var c = p.Y.CompareTo(q.Y);
            return c != 0 ? c : p.X.CompareTo(q.X);
        });

        var head = list[0];
        head.Source = string.Join(" ", list.Select(l => l.Source));
        head.Lines = list.Count;

        double x0 = list.Min(l => l.X), y0 = list.Min(l => l.Y);
        double x1 = list.Max(l => l.X + l.W), y1 = list.Max(l => l.Y + l.H);
        head.X = x0; head.Y = y0; head.W = x1 - x0; head.H = y1 - y0;
        return head;
    }

    /// <summary>
    /// Сшить строки подписи без пузыря — по одной геометрии.
    ///
    /// Надпись поверх рисунка детектор находит плохо: облака вокруг неё нет, а
    /// рамку он даёт в лучшем случае на часть. Между тем «Mind / helping / me
    /// out?» — одна фраза, и порознь эти три строки переводятся в три обрывка.
    ///
    /// Признак соседства: строки заметно перекрываются ПО ГОРИЗОНТАЛИ и стоят
    /// вплотную ПО ВЕРТИКАЛИ. Обе проверки нужны: только по вертикали склеятся
    /// подписи в разных углах кадра, только по горизонтали — соседние реплики
    /// одной колонки.
    ///
    /// Применяется ТОЛЬКО к строкам без облака: где детектор сказал своё слово,
    /// геометрия не вмешивается.
    /// </summary>
    internal static List<TextBox2> Stitch(List<TextBox2> lines)
    {
        const double sideShare = 0.3;   // доля перекрытия по X у более узкой
        const double gapShare = 0.6;    // просвет по Y в долях меньшей высоты

        var group = new int[lines.Count];
        for (var i = 0; i < group.Length; i++) group[i] = i;

        int Root(int i) { while (group[i] != i) i = group[i] = group[group[i]]; return i; }

        for (var i = 0; i < lines.Count; i++)
            for (var j = i + 1; j < lines.Count; j++)
            {
                var a = lines[i];
                var b = lines[j];

                var side = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
                if (side < Math.Min(a.W, b.W) * sideShare) continue;

                // Отрицательный просвет — рамки налезают, это тем более соседи
                var gap = Math.Max(a.Y, b.Y) - Math.Min(a.Y + a.H, b.Y + b.H);
                if (gap > Math.Min(a.H, b.H) * gapShare) continue;

                var ra = Root(i);
                var rb = Root(j);
                if (ra != rb) group[ra] = rb;
            }

        var buckets = new Dictionary<int, List<TextBox2>>();
        for (var i = 0; i < lines.Count; i++)
        {
            var r = Root(i);
            if (!buckets.TryGetValue(r, out var list)) buckets[r] = list = new List<TextBox2>();
            list.Add(lines[i]);
        }

        var result = new List<TextBox2>(buckets.Count);
        foreach (var (_, list) in buckets) result.Add(Join(list));
        return result;
    }

    /// <summary>
    /// Цвет подложки внутри облака — МЕДИАНА по сетке точек.
    ///
    /// Не кольцо вокруг текста: рамки строк жмутся к глифам вплотную, и кольцо
    /// вокруг них попадает уже на рисунок, а не на пузырь — подложка выходила
    /// небесно-голубой в белом облаке. Внутри же облака текст занимает меньшую
    /// часть площади, и его тёмные точки уходят в хвост, не сдвигая середину.
    /// </summary>
    public static (byte R, byte G, byte B) Background(byte[] px, int w, int h, Box c)
    {
        // Отступ внутрь: у самого контура лежит чёрная обводка облака
        var inset = 0.12;
        var x0 = (int)Math.Round(c.X + c.W * inset);
        var y0 = (int)Math.Round(c.Y + c.H * inset);
        var x1 = (int)Math.Round(c.X + c.W * (1 - inset));
        var y1 = (int)Math.Round(c.Y + c.H * (1 - inset));

        var rs = new List<byte>(1024);
        var gs = new List<byte>(1024);
        var bs = new List<byte>(1024);

        var stepX = Math.Max(1, (x1 - x0) / 32);
        var stepY = Math.Max(1, (y1 - y0) / 32);
        for (var y = y0; y <= y1; y += stepY)
        {
            if (y < 0 || y >= h) continue;
            for (var x = x0; x <= x1; x += stepX)
            {
                if (x < 0 || x >= w) continue;
                var o = (y * w + x) * 4;            // BGRA8
                bs.Add(px[o]); gs.Add(px[o + 1]); rs.Add(px[o + 2]);
            }
        }

        if (rs.Count < 4) return (255, 255, 255);
        rs.Sort(); gs.Sort(); bs.Sort();
        var m = rs.Count / 2;
        return (rs[m], gs[m], bs[m]);
    }

    /// <summary>
    /// Рамка, прочитанная в вырезке, — обратно в координаты страницы.
    /// Отдельной функцией, потому что перепутанный здесь знак смещает реплику
    /// на соседний пузырь, а на глаз это не видно.
    /// </summary>
    public static (double X, double Y, double W, double H) ToPage(
        double x, double y, double w, double h, double cropX, double cropY, double scale)
        => (cropX + x / scale, cropY + y / scale, w / scale, h / scale);

    /// <summary>
    /// Во сколько раз увеличить вырезку перед распознаванием: мелкий текст
    /// движок не берёт вовсе, но выше потолка растут только время и память.
    /// </summary>
    public static double CropScale(double w, double h, double want = 1400, double cap = 2400)
    {
        var side = Math.Max(w, h);
        if (side < 1) return 1;
        return Math.Clamp(want / side, 1, cap / side);
    }
}
