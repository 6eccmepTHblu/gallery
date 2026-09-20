using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Gallery;

/// <summary>
/// Разметка букв — TareHimself/comic-text-mask (MIT, 57 МБ, Unet/resnet18).
///
/// Зачем обученная модель там, где напрашивается порог: порогом не выходит.
/// Проверено тремя способами — по кайме рамки, морфологией и по цвету надписи;
/// каждый валится на тексте поверх пёстрого рисунка, и валится по-разному.
/// Локально штрих буквы и край листа — одно и то же, различить их можно только
/// ЗНАЯ, как выглядят буквы (§4.14).
///
/// Модель работает двумя проходами, и оба нужны:
///   * по всей странице — грубо, зато находит надписи, прошедшие мимо
///     распознавания и детектора облаков;
///   * по вырезке вокруг надписи — точно, потому что там буквы занимают
///     заметную долю квадрата 384, а не десяток пикселей.
/// </summary>
static class TextMask
{
    /// <summary>Вход модели жёсткий: квадрат 384 с полями, значения 0..255.</summary>
    public const int Side = 384;

    /// <summary>Нормировка запечена в сами веса — масштабировать вход не надо.</summary>
    public const float Threshold = 0.5f;

    public static string ModelPath =>
        Path.Combine(AppContext.BaseDirectory, "models", "comic-text-mask.onnx");

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
                try { _session = new InferenceSession(ModelPath); }
                catch (Exception ex)
                {
                    Log.Warn($"разметка букв не загрузилась: {ex.Message}");
                    return null;
                }
            }
            return _session;
        }
    }

    /// <summary>Построить сессию заранее. Иначе цена ляжет на первый шаг.</summary>
    public static void Warm() { _ = Session; }

    /// <summary>
    /// Куда ляжет вырезка внутри квадрата: размер с сохранением пропорций и
    /// отступы по краям. Отдельной функцией, потому что ошибка здесь смещает
    /// маску относительно картинки, а на глаз такое видно не сразу.
    /// </summary>
    public static (int W, int H, int X, int Y) Fit(int cw, int ch, int side = Side)
    {
        if (cw < 1 || ch < 1) return (0, 0, 0, 0);
        var k = Math.Min((double)side / cw, (double)side / ch);
        var w = Math.Max(1, (int)Math.Round(cw * k));
        var h = Math.Max(1, (int)Math.Round(ch * k));
        return (w, h, (side - w) / 2, (side - h) / 2);
    }

    /// <summary>
    /// Вероятность текста по пикселям вырезки, в её собственном размере.
    /// Возвращает null, если модели нет.
    /// </summary>
    public static float[]? Probe(SKBitmap crop)
    {
        var s = Session;
        if (s is null || crop.Width < 2 || crop.Height < 2) return null;

        var (nw, nh, ox, oy) = Fit(crop.Width, crop.Height);
        // Уменьшение идёт в разы (страница 2400 px ужимается в 384), и брать
        // при этом ближайший пиксель — как по умолчанию — нельзя: девять из
        // десяти точек просто выбрасываются, а уцелевшие приносят с собой
        // ступеньки. Модель принимает их за штрихи и находит «надписи» на
        // углах пузырей и на краю мазка. Со сглаживанием мусорных рамок на
        // шести пробных страницах стало 7 вместо 21 — при том же тексте
        using var small = crop.Resize(new SKImageInfo(nw, nh),
                                      new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (small is null) return null;

        var input = new DenseTensor<float>(new[] { 1, 3, Side, Side });
        var px = small.Pixels;
        for (var y = 0; y < nh; y++)
            for (var x = 0; x < nw; x++)
            {
                var c = px[y * nw + x];
                input[0, 0, oy + y, ox + x] = c.Red;
                input[0, 1, oy + y, ox + x] = c.Green;
                input[0, 2, oy + y, ox + x] = c.Blue;
            }

        using var res = s.Run(new[] { NamedOnnxValue.CreateFromTensor("image", input) });
        var t = res.First().AsTensor<float>();

        // Обратно в размер вырезки: поля отбрасываем, середину растягиваем
        var outp = new float[crop.Width * crop.Height];
        for (var y = 0; y < crop.Height; y++)
        {
            var sy = oy + Math.Clamp((int)((y + 0.5) * nh / crop.Height), 0, nh - 1);
            for (var x = 0; x < crop.Width; x++)
            {
                var sx = ox + Math.Clamp((int)((x + 0.5) * nw / crop.Width), 0, nw - 1);
                outp[y * crop.Width + x] = t[0, 0, sy, sx];
            }
        }
        return outp;
    }

    /// <summary>
    /// Грубый проход по всей странице: прямоугольники найденных пятен.
    ///
    /// Это КУСКИ, а не надписи: слово, строка, иногда отдельная буква. Сводит
    /// их в надписи <see cref="Cleanup.Group"/> — по высоте самих кусков.
    /// Расширять маску здесь пробовали, и это была ошибка: запас брался в
    /// долях страницы (min/32 — это 75 px на кадре 2400), и соседние реплики
    /// слипались в одну рамку на пол-листа ещё до всякой группировки.
    /// </summary>
    public static List<SKRectI> Discover(SKBitmap page)
    {
        var found = new List<SKRectI>();
        var p = Probe(page);
        if (p is null) return found;

        int w = page.Width, h = page.Height;
        var on = new bool[w * h];
        var any = false;
        for (var i = 0; i < p.Length; i++)
            if (p[i] > Threshold) { on[i] = true; any = true; }
        if (!any) return found;

        foreach (var (x0, y0, x1, y1, area) in Blobs(on, w, h))
        {
            if (area < 40) continue;
            found.Add(new SKRectI(x0, y0, x1 + 1, y1 + 1));
        }
        return found;
    }

    /// <summary>
    /// Точная маска по списку рамок, во весь размер страницы. 255 — буква.
    ///
    /// <paramref name="grow"/> — запас вокруг найденного, в пикселях кадра.
    /// Сегментатор обводит глиф вплотную, а у сглаженного края остаётся
    /// полутёмный ободок: он идёт около 0,2…0,4 и порога не берёт, зато
    /// прекрасно виден после дорисовки. Опускать порог ради него нельзя — на
    /// 0,25 вместе с ободком полезут контур пузыря и тонкие линии рисунка.
    /// Запас растит разметку по контуру самой буквы: где буквы нет, расти
    /// нечему.
    /// </summary>
    public static byte[] Refine(SKBitmap page, IEnumerable<SKRectI> boxes, int grow = 0)
    {
        int w = page.Width, h = page.Height;
        var mask = new byte[w * h];

        foreach (var b in boxes)
        {
            // Поля вокруг рамки: выносные элементы и хвосты часто торчат наружу
            var pad = Math.Max(8, Math.Min(b.Width, b.Height) / 5);
            var x0 = Math.Max(0, b.Left - pad);
            var y0 = Math.Max(0, b.Top - pad);
            var x1 = Math.Min(w, b.Right + pad);
            var y1 = Math.Min(h, b.Bottom + pad);
            if (x1 - x0 < 6 || y1 - y0 < 6) continue;

            using var crop = new SKBitmap(x1 - x0, y1 - y0);
            if (!page.ExtractSubset(crop, new SKRectI(x0, y0, x1, y1))) continue;

            var p = Probe(crop);
            if (p is null) continue;

            // Поля ушли в МОДЕЛЬ, но не в разметку. Рамку человек провёл сам,
            // и краска за её краем читается как промах: обвёл одну надпись —
            // разметилась она и кусок соседней.
            //
            // Собираем по рамке отдельным куском, а не сразу в страницу: запас
            // обязан расти ВНУТРИ своей рамки. Расти по всей странице — значит
            // склеить запасом две соседние надписи
            int bx = Math.Max(b.Left, 0), by = Math.Max(b.Top, 0);
            int bw = Math.Min(b.Right, w) - bx, bh = Math.Min(b.Bottom, h) - by;
            if (bw < 1 || bh < 1) continue;

            var part = new byte[bw * bh];
            for (var y = 0; y < bh; y++)
            {
                var cy = by + y - y0;
                for (var x = 0; x < bw; x++)
                    if (p[cy * crop.Width + bx + x - x0] > Threshold) part[y * bw + x] = 255;
            }

            Grow(part, bw, bh, grow);

            for (var y = 0; y < bh; y++)
                for (var x = 0; x < bw; x++)
                    if (part[y * bw + x] != 0) mask[(by + y) * w + bx + x] = 255;
        }
        return mask;
    }

    /// <summary>
    /// Раздуть или поджать готовую разметку внутри рамки — на r пикселей по
    /// контуру. Работает по тому, ЧТО В МАСКЕ СЕЙЧАС, включая правки кистью:
    /// помнить, откуда что взялось, не нужно вовсе.
    ///
    /// Поджатие — то же расширение, только у изнанки: фон растёт в буквы.
    /// За рамку рост не выходит ни в ту, ни в другую сторону: снаружи для
    /// куска пусто, и от края он не растёт и не отъедается.
    /// </summary>
    internal static void Swell(byte[] mask, int w, int h, SKRectI box, int r, bool grow)
    {
        if (r < 1) return;
        int bx = Math.Clamp(box.Left, 0, w), by = Math.Clamp(box.Top, 0, h);
        int bw = Math.Clamp(box.Right, 0, w) - bx, bh = Math.Clamp(box.Bottom, 0, h) - by;
        if (bw < 1 || bh < 1) return;

        var a = new byte[bw * bh];
        for (var y = 0; y < bh; y++)
        {
            var row = (by + y) * w + bx;
            for (var x = 0; x < bw; x++)
            {
                var on = mask[row + x] != 0;
                a[y * bw + x] = (byte)(on == grow ? 255 : 0);   // поджимаем — по изнанке
            }
        }

        Grow(a, bw, bh, r);

        for (var y = 0; y < bh; y++)
        {
            var row = (by + y) * w + bx;
            for (var x = 0; x < bw; x++)
            {
                var on = a[y * bw + x] != 0;
                mask[row + x] = (byte)(on == grow ? 255 : 0);
            }
        }
    }

    /// <summary>
    /// Расширить разметку на r пикселей по контуру.
    ///
    /// Считается по РАССТОЯНИЮ до ближайшей размеченной точки (чамфер 3-4, два
    /// прохода), а не квадратным окном: квадрат растёт по диагонали в полтора
    /// раза сильнее, и у буквы вместо ровной каймы вырастают углы. Два прохода
    /// дают линейное время от площади и не зависят от r вовсе.
    /// </summary>
    internal static void Grow(byte[] m, int w, int h, int r)
    {
        if (r < 1 || w < 1 || h < 1) return;

        const int Far = 1 << 20;
        var d = new int[w * h];
        for (var i = 0; i < d.Length; i++) d[i] = m[i] != 0 ? 0 : Far;

        // Вперёд: смотрим на уже посчитанных соседей сверху и слева
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var v = d[i];
                if (v == 0) continue;
                if (y > 0)
                {
                    if (x > 0) v = Math.Min(v, d[i - w - 1] + 4);
                    v = Math.Min(v, d[i - w] + 3);
                    if (x < w - 1) v = Math.Min(v, d[i - w + 1] + 4);
                }
                if (x > 0) v = Math.Min(v, d[i - 1] + 3);
                d[i] = v;
            }

        // Назад: снизу и справа. Расстояние здесь уже окончательное, поэтому
        // тем же проходом и красим
        var lim = 3 * r;
        for (var y = h - 1; y >= 0; y--)
            for (var x = w - 1; x >= 0; x--)
            {
                var i = y * w + x;
                var v = d[i];
                if (v != 0)
                {
                    if (y < h - 1)
                    {
                        if (x > 0) v = Math.Min(v, d[i + w - 1] + 4);
                        v = Math.Min(v, d[i + w] + 3);
                        if (x < w - 1) v = Math.Min(v, d[i + w + 1] + 4);
                    }
                    if (x < w - 1) v = Math.Min(v, d[i + 1] + 3);
                    d[i] = v;
                }
                if (v <= lim) m[i] = 255;
            }
    }

    /// <summary>
    /// Связные пятна: рамка и площадь каждого. Обход стеком, а не рекурсией —
    /// на пятне во всю страницу рекурсия переполнит стек.
    /// </summary>
    internal static List<(int X0, int Y0, int X1, int Y1, int Area)> Blobs(bool[] on, int w, int h)
    {
        var seen = new bool[w * h];
        var res = new List<(int, int, int, int, int)>();
        var stack = new Stack<int>();

        for (var start = 0; start < on.Length; start++)
        {
            if (!on[start] || seen[start]) continue;

            int x0 = start % w, x1 = x0, y0 = start / w, y1 = y0, area = 0;
            seen[start] = true;
            stack.Push(start);

            while (stack.Count > 0)
            {
                var i = stack.Pop();
                int x = i % w, y = i / w;
                area++;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;

                if (x > 0 && on[i - 1] && !seen[i - 1]) { seen[i - 1] = true; stack.Push(i - 1); }
                if (x < w - 1 && on[i + 1] && !seen[i + 1]) { seen[i + 1] = true; stack.Push(i + 1); }
                if (y > 0 && on[i - w] && !seen[i - w]) { seen[i - w] = true; stack.Push(i - w); }
                if (y < h - 1 && on[i + w] && !seen[i + w]) { seen[i + w] = true; stack.Push(i + w); }
            }
            res.Add((x0, y0, x1, y1, area));
        }
        return res;
    }
}
