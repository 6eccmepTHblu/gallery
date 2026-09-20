using System;
using SkiaSharp;

namespace Gallery;

/// <summary>
/// Ручное рисование по кадру — пипетка, карандаш, линия, прямоугольник, эллипс
/// (SPEC.md §4.14).
///
/// Зачем это при живом дорисовщике. Сеть восстанавливает то, что следует из
/// окружения; линию, которой больше нигде нет, она не восстановит — проверено
/// на четырёх дорисовщиках. На ШТРИХОВОМ рисунке — рамка кадра, контур облачка,
/// чёрная линия по белому — рука побеждает вчистую: там нет ни зерна, ни
/// градиента, которые надо подделывать, есть ровный цвет и ровная толщина.
///
/// Два свойства делают ручную правку незаметной, и оба обязательны:
/// цвет берётся ИЗ КАДРА пипеткой, а край мазка смягчён на пиксель — резкая
/// граница поверх сжатой картинки читается как наклейка.
/// </summary>
static class Paint
{
    /// <summary>
    /// Цвет под пипеткой — медиана 3x3, а не один пиксель. На сжатой картинке
    /// соседние пиксели гуляют на несколько единиц, и одиночный отсчёт может
    /// оказаться артефактом JPEG, а не цветом.
    /// </summary>
    internal static SKColor Pick(SKColor[] px, int w, int h, int x, int y)
    {
        Span<byte> r = stackalloc byte[9];
        Span<byte> g = stackalloc byte[9];
        Span<byte> b = stackalloc byte[9];
        var n = 0;
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                int sx = x + dx, sy = y + dy;
                if (sx < 0 || sy < 0 || sx >= w || sy >= h) continue;
                var c = px[sy * w + sx];
                r[n] = c.Red; g[n] = c.Green; b[n] = c.Blue; n++;
            }
        if (n == 0) return SKColors.Black;
        return new SKColor(Median(r, n), Median(g, n), Median(b, n));
    }

    static byte Median(Span<byte> v, int n)
    {
        var s = v[..n];
        s.Sort();
        return s[n / 2];
    }

    /// <summary>
    /// Отрезок круглым пером. Им же рисуется и свободный штрих: указатель
    /// приходит рывками, и по одиночным точкам быстрый мах оставлял бы пунктир.
    /// Возвращает задетую область — по ней берётся снимок под откат.
    /// </summary>
    internal static SKRectI Line(SKColor[] px, int w, int h,
                                 int x0, int y0, int x1, int y1, int width, SKColor color)
    {
        var half = Math.Max(1, width) / 2f;
        var pad = (int)MathF.Ceiling(half) + 1;

        var rx0 = Math.Max(0, Math.Min(x0, x1) - pad);
        var ry0 = Math.Max(0, Math.Min(y0, y1) - pad);
        var rx1 = Math.Min(w - 1, Math.Max(x0, x1) + pad);
        var ry1 = Math.Min(h - 1, Math.Max(y0, y1) + pad);
        if (rx1 < rx0 || ry1 < ry0) return SKRectI.Empty;

        float ax = x0, ay = y0, vx = x1 - x0, vy = y1 - y0;
        var vv = vx * vx + vy * vy;

        for (var y = ry0; y <= ry1; y++)
            for (var x = rx0; x <= rx1; x++)
            {
                var t = vv < 0.001f ? 0 : Math.Clamp(((x - ax) * vx + (y - ay) * vy) / vv, 0, 1);
                var dx = x - (ax + vx * t);
                var dy = y - (ay + vy * t);
                var d = MathF.Sqrt(dx * dx + dy * dy);

                // Полпикселя мягкости: без неё край ступенчатый, с большей —
                // линия расплывается и перестаёт совпадать с соседней настоящей
                var a = Math.Clamp(half + 0.5f - d, 0f, 1f);
                if (a <= 0.004f) continue;
                Blend(px, y * w + x, color, a);
            }

        return new SKRectI(rx0, ry0, rx1 + 1, ry1 + 1);
    }

    /// <summary>Прямоугольник — четыре отрезка по углам, контуром.</summary>
    internal static SKRectI Rect(SKColor[] px, int w, int h, SKRectI r, int width, SKColor color)
    {
        int x0 = r.Left, y0 = r.Top, x1 = r.Right - 1, y1 = r.Bottom - 1;
        var box = Line(px, w, h, x0, y0, x1, y0, width, color);
        box = Union(box, Line(px, w, h, x1, y0, x1, y1, width, color));
        box = Union(box, Line(px, w, h, x1, y1, x0, y1, width, color));
        box = Union(box, Line(px, w, h, x0, y1, x0, y0, width, color));
        return box;
    }

    /// <summary>
    /// Эллипс, вписанный в рамку. Считается по расстоянию до контура, а не
    /// отрезками: у ломаной из хорд толщина гуляет на пологих участках.
    /// </summary>
    internal static SKRectI Ellipse(SKColor[] px, int w, int h, SKRectI r, int width, SKColor color)
    {
        float cx = (r.Left + r.Right - 1) / 2f, cy = (r.Top + r.Bottom - 1) / 2f;
        float ra = Math.Max(1, r.Width - 1) / 2f, rb = Math.Max(1, r.Height - 1) / 2f;

        var half = Math.Max(1, width) / 2f;
        var pad = (int)MathF.Ceiling(half) + 1;
        var rx0 = Math.Max(0, r.Left - pad);
        var ry0 = Math.Max(0, r.Top - pad);
        var rx1 = Math.Min(w - 1, r.Right - 1 + pad);
        var ry1 = Math.Min(h - 1, r.Bottom - 1 + pad);
        if (rx1 < rx0 || ry1 < ry0) return SKRectI.Empty;

        for (var y = ry0; y <= ry1; y++)
            for (var x = rx0; x <= rx1; x++)
            {
                // Приближение расстояния до эллипса: нормированное уклонение,
                // умноженное на местный масштаб. Точного расстояния тут не
                // нужно — ошибка меньше полпикселя на разумных пропорциях
                var nx = (x - cx) / ra;
                var ny = (y - cy) / rb;
                var q = MathF.Sqrt(nx * nx + ny * ny);
                if (q < 0.001f) continue;
                var gx = nx / (ra * q);
                var gy = ny / (rb * q);
                var grad = MathF.Sqrt(gx * gx + gy * gy);
                if (grad < 1e-6f) continue;
                var d = MathF.Abs(q - 1) / grad;

                var a = Math.Clamp(half + 0.5f - d, 0f, 1f);
                if (a <= 0.004f) continue;
                Blend(px, y * w + x, color, a);
            }

        return new SKRectI(rx0, ry0, rx1 + 1, ry1 + 1);
    }

    static void Blend(SKColor[] px, int i, SKColor c, float a)
    {
        var was = px[i];
        px[i] = new SKColor(Mix(was.Red, c.Red, a), Mix(was.Green, c.Green, a),
                            Mix(was.Blue, c.Blue, a), was.Alpha);
    }

    static byte Mix(byte was, byte now, float a) =>
        (byte)Math.Clamp((int)MathF.Round(was * (1 - a) + now * a), 0, 255);

    internal static SKRectI Union(SKRectI a, SKRectI b) =>
        a.IsEmpty ? b : b.IsEmpty ? a : SKRectI.Union(a, b);
}
