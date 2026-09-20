using System;
using SkiaSharp;

namespace Gallery;

/// <summary>
/// Заливка градиентом — решение уравнения Лапласа внутри маски (SPEC.md §4.14).
///
/// Зачем рядом с дорисовкой. Сеть восстанавливает ТЕКСТУРУ, и на текстуре она
/// хороша. На гладком фоне — кожа, небо, стена, любой плавный переход — текстуры
/// нет, есть градиент, и сеть кладёт туда ровный тон, усреднённый по округе. На
/// глаз это «еле заметный шлейф» ровно по форме бывших букв: тон сам по себе
/// правдоподобный, но он не течёт вместе с фоном.
///
/// Гармоническая функция — самая гладкая поверхность, которая сходится к
/// заданным краям. Это ровно «переход от стороны к стороне»: слева край тянет
/// свой цвет, справа свой, внутри они перетекают друг в друга. Считается точно,
/// без сети и за миллисекунды.
/// </summary>
static class Smooth
{
    /// <summary>Проходов Гаусса-Зейделя на каждом уровне пирамиды.</summary>
    const int Sweeps = 24;

    /// <summary>
    /// Залить размеченное градиентом. Возвращает новый кадр или null, если
    /// заливать нечего. Исходник не меняется.
    /// </summary>
    public static SKBitmap? Run(SKBitmap page, byte[] mask)
    {
        int w = page.Width, h = page.Height;
        if (mask.Length != w * h) return null;

        // Работаем по рамке разметки с полем в два пикселя: края дыры держат
        // соседи, и без поля их пришлось бы брать за границей массива
        int x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (mask[y * w + x] >= 128)
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
        if (x1 < 0) return null;

        x0 = Math.Max(0, x0 - 2); y0 = Math.Max(0, y0 - 2);
        x1 = Math.Min(w - 1, x1 + 2); y1 = Math.Min(h - 1, y1 + 2);
        int rw = x1 - x0 + 1, rh = y1 - y0 + 1;

        var px = page.Pixels;
        var hole = new bool[rw * rh];
        var ch = new float[3][];
        for (var c = 0; c < 3; c++) ch[c] = new float[rw * rh];

        for (var y = 0; y < rh; y++)
            for (var x = 0; x < rw; x++)
            {
                var src = (y0 + y) * w + x0 + x;
                var dst = y * rw + x;
                var col = px[src];
                ch[0][dst] = col.Red;
                ch[1][dst] = col.Green;
                ch[2][dst] = col.Blue;
                hole[dst] = mask[src] >= 128;
            }

        // Всё в дыре — краевого цвета нет, тянуть градиент неоткуда. Молча
        // залить чёрным было бы худшим из возможных ответов
        if (Array.IndexOf(hole, false) < 0) return null;

        for (var c = 0; c < 3; c++) Harmonic(ch[c], hole, rw, rh);

        var res = page.Copy();
        var rp = res.Pixels;
        for (var y = 0; y < rh; y++)
            for (var x = 0; x < rw; x++)
            {
                var i = y * rw + x;
                if (!hole[i]) continue;
                rp[(y0 + y) * w + x0 + x] = new SKColor(Bit(ch[0][i]), Bit(ch[1][i]), Bit(ch[2][i]));
            }
        res.Pixels = rp;

        LastX = x0; LastY = y0; LastW = rw; LastH = rh;
        return res;
    }

    /// <summary>Область последней заливки — по ней берётся снимок под откат.</summary>
    public static int LastX { get; private set; }
    public static int LastY { get; private set; }
    public static int LastW { get; private set; }
    public static int LastH { get; private set; }

    static byte Bit(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);

    /// <summary>
    /// Один канал: значение в дыре = среднее соседей, края держат известные
    /// пиксели. Решается пирамидой, а не в лоб: на дыре в пол-экрана простые
    /// проходы «протаскивают» краевой цвет к середине по пикселю за проход, и
    /// их нужны тысячи. На уменьшенной копии тот же путь — десяток пикселей,
    /// поэтому грубое решение считается сразу, а точное только уточняет его.
    /// </summary>
    internal static void Harmonic(float[] v, bool[] hole, int w, int h)
    {
        var any = false;
        foreach (var t in hole) if (t) { any = true; break; }
        if (!any) return;

        if (w > 4 && h > 4)
        {
            int cw = (w + 1) / 2, chh = (h + 1) / 2;
            var cv = new float[cw * chh];
            var cm = new bool[cw * chh];

            // Вниз: известным считается тот, у кого известен хоть один ребёнок
            for (var y = 0; y < chh; y++)
                for (var x = 0; x < cw; x++)
                {
                    float sum = 0;
                    var cnt = 0;
                    for (var dy = 0; dy < 2; dy++)
                        for (var dx = 0; dx < 2; dx++)
                        {
                            int sy = y * 2 + dy, sx = x * 2 + dx;
                            if (sy >= h || sx >= w) continue;
                            var i = sy * w + sx;
                            if (hole[i]) continue;
                            sum += v[i];
                            cnt++;
                        }
                    var ci = y * cw + x;
                    cm[ci] = cnt == 0;
                    cv[ci] = cnt == 0 ? 0 : sum / cnt;
                }

            Harmonic(cv, cm, cw, chh);

            // Вверх: грубое решение — начальное приближение для дыры
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    if (hole[i]) v[i] = cv[(y / 2) * cw + x / 2];
                }
        }
        else
        {
            // Дно пирамиды: подпереть дыру средним известным
            float sum = 0;
            var cnt = 0;
            for (var i = 0; i < v.Length; i++)
                if (!hole[i]) { sum += v[i]; cnt++; }
            var mean = cnt == 0 ? 0 : sum / cnt;
            for (var i = 0; i < v.Length; i++) if (hole[i]) v[i] = mean;
        }

        // Гаусс-Зейдель по месту: свежие значения идут в дело сразу, и волна
        // от краёв проходит за один проход, а не за два, как у Якоби
        for (var s = 0; s < Sweeps; s++)
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    if (!hole[i]) continue;

                    float sum = 0;
                    var cnt = 0;
                    if (x > 0) { sum += v[i - 1]; cnt++; }
                    if (x < w - 1) { sum += v[i + 1]; cnt++; }
                    if (y > 0) { sum += v[i - w]; cnt++; }
                    if (y < h - 1) { sum += v[i + w]; cnt++; }
                    if (cnt > 0) v[i] = sum / cnt;
                }
    }
}
