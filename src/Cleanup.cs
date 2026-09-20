using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace Gallery;

/// <summary>
/// Очистка страницы от текста, три шага (SPEC.md §4.14).
///
/// Шаги разделены нарочно, а не ради красоты: стирание НЕОБРАТИМО по смыслу —
/// увидеть, что буква пропущена, можно только до дорисовки, а после уже нечего
/// сравнивать. Поэтому человек сперва смотрит, что нашлось, потом что размечено,
/// и только потом разрешает стереть.
/// </summary>
static class Cleanup
{
    /// <summary>Готовы ли обе модели. Нет — панель открывать незачем.</summary>
    public static bool Ready => TextMask.Present && Inpaint.Present;

    /// <summary>
    /// Шаг 1. Где на странице надписи.
    ///
    /// Источников два, и они дополняют друг друга: на восьми пробных кадрах
    /// распознавание молчало на трёх, а сегментатор находил там текст сам —
    /// и наоборот, на пёстром фоне точнее оказывались рамки распознавания.
    /// </summary>
    public static async Task<List<SKRectI>> FindAsync(SKBitmap page, string path, CancellationToken ct)
    {
        // Сегментатор отдаёт КУСКИ букв — их надо свести в надписи и отсеять
        // крошки ЗДЕСЬ, до рамок распознавания: тем крошкам верить нечего,
        // а рамке распознавания можно, и общий отсев задел бы и её
        var boxes = Group(await Task.Run(() => TextMask.Discover(page), ct));
        var crumb = Math.Max(2, Math.Min(page.Width, page.Height) / 48);
        boxes.RemoveAll(b => b.Width < crumb || b.Height < crumb);

        var lines = new List<SKRectI>();
        try
        {
            // Рамки распознавания — в координатах СВОЕГО декода, приводим к кадру
            var text = await Ocr.ReadAsync(path, ct);
            if (text.ImgW > 0 && text.ImgH > 0)
            {
                var kx = (double)page.Width / text.ImgW;
                var ky = (double)page.Height / text.ImgH;

                // Берём СТРОКИ, а не реплики: сшивка по облакам сделана для
                // перевода, где фраза обязана быть целой, и она честно сводит
                // надпись со звуком в полукадре ниже в одну рамку. Для стирания
                // это рамка на пол-листа
                var src = text.Lines is { Count: > 0 }
                    ? text.Lines.Select(r => (X: (double)r.Left, Y: (double)r.Top,
                                              W: (double)r.Width, H: (double)r.Height))
                    : text.Boxes.Select(b => (b.X, b.Y, b.W, b.H));

                foreach (var b in src)
                {
                    // Рамка распознавания обтягивает строку вплотную, а разметка
                    // теперь живёт строго внутри рамки — без поля отрезало бы
                    // хвосты букв. Поле видно на экране: что обведено, то и красится
                    var pad = Math.Max(2, b.H * ky / 8);
                    lines.Add(new SKRectI(
                        (int)Math.Max(0, b.X * kx - pad), (int)Math.Max(0, b.Y * ky - pad),
                        (int)Math.Min(page.Width, (b.X + b.W) * kx + pad),
                        (int)Math.Min(page.Height, (b.Y + b.H) * ky + pad)));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Warn($"рамки распознавания для чистки: {ex.Message}"); }

        return Separate(Absorb(boxes, lines));
    }

    /// <summary>Шаг 2. Какие пиксели внутри рамок — буквы.</summary>
    public static Task<byte[]> MarkAsync(SKBitmap page, IReadOnlyList<SKRectI> boxes, CancellationToken ct)
        => Task.Run(() => TextMask.Refine(page, boxes), ct);

    /// <summary>Шаг 3. Стереть и дорисовать. Исходник не меняется.</summary>
    public static Task<SKBitmap?> EraseAsync(SKBitmap page, byte[] mask, CancellationToken ct)
        => Task.Run(() => Inpaint.Run(page, mask), ct);

    /// <summary>
    /// Свести рамки в надписи — по одной рамке на надпись.
    ///
    /// Складывать всё, что пересеклось (так было раньше), нельзя: объединённая
    /// рамка растёт и дотягивается до следующей надписи, та до следующей — и
    /// на странице 2400 px четыре реплики сходились в одну рамку на пол-листа.
    /// Поэтому соседство проверяется ТОЛЬКО у исходных рамок, а объединение
    /// идёт одним разом в конце.
    ///
    /// Масштаб задаёт высота самих рамок, а не размер страницы: буквы в слове
    /// стоят на четверть высоты друг от друга, строки в реплике — на треть, и
    /// это верно и на вырезке 300 px, и на развороте 4000 px. Правила три:
    ///   * одна рамка внутри другой — это одна надпись, найденная дважды;
    ///   * соседние слова строки: перекрываются по вертикали, просвет по
    ///     горизонтали меньше высоты буквы;
    ///   * соседние строки надписи: перекрываются по горизонтали, просвет по
    ///     вертикали меньше высоты строки — то же правило, что у Bubbles.Stitch.
    /// </summary>
    internal static List<SKRectI> Group(List<SKRectI> src)
    {
        const double sideShare = 0.3;   // доля перекрытия по X у более узкой
        const double vGap = 0.6;        // просвет по Y в долях меньшей высоты
        const double vOver = 0.4;       // доля перекрытия по Y у более низкой
        const double hGap = 0.6;        // просвет по X в долях меньшей высоты

        // Вырожденные выбрасываем сразу: вырезка в два пикселя не надпись,
        // а модели на ней делать нечего
        var b = src.Where(r => r.Width >= 4 && r.Height >= 4).ToList();

        var group = new int[b.Count];
        for (var i = 0; i < group.Length; i++) group[i] = i;

        int Root(int i) { while (group[i] != i) i = group[i] = group[group[i]]; return i; }

        for (var i = 0; i < b.Count; i++)
            for (var j = i + 1; j < b.Count; j++)
            {
                var p = b[i];
                var q = b[j];
                double mh = Math.Min(p.Height, q.Height);

                // Отрицательное перекрытие — это просвет между рамками
                double ix = Math.Min(p.Right, q.Right) - Math.Max(p.Left, q.Left);
                double iy = Math.Min(p.Bottom, q.Bottom) - Math.Max(p.Top, q.Top);

                var same =
                    Same(p, q)
                    || (iy >= vOver * mh && -ix <= hGap * mh)
                    || (ix >= sideShare * Math.Min(p.Width, q.Width) && -iy <= vGap * mh);
                if (!same) continue;

                var ra = Root(i);
                var rb = Root(j);
                if (ra != rb) group[ra] = rb;
            }

        // Порядок ответа — по первому появлению, а не по перебору словаря:
        // блоки в панели нумеруются подряд, и номер обязан быть один и тот же
        // при одном и том же входе
        var slot = new Dictionary<int, int>();
        var res = new List<SKRectI>();
        for (var i = 0; i < b.Count; i++)
        {
            var r = Root(i);
            if (slot.TryGetValue(r, out var k)) res[k] = SKRectI.Union(res[k], b[i]);
            else { slot[r] = res.Count; res.Add(b[i]); }
        }
        return res;
    }

    /// <summary>
    /// Одна и та же надпись, найденная дважды: пересечение занимает бо́льшую
    /// часть меньшей из рамок.
    /// </summary>
    static bool Same(SKRectI p, SKRectI q)
    {
        const double cover = 0.6;
        double ix = Math.Min(p.Right, q.Right) - Math.Max(p.Left, q.Left);
        double iy = Math.Min(p.Bottom, q.Bottom) - Math.Max(p.Top, q.Top);
        return ix > 0 && iy > 0
            && ix * iy >= cover * Math.Min((double)p.Width * p.Height, (double)q.Width * q.Height);
    }

    /// <summary>
    /// Подшить рамки распознавания к уже собранным надписям.
    ///
    /// Соседство здесь НЕ считается, только совпадение: распознавание отдаёт
    /// четырёхугольник, а весь остальной тракт живёт на прямоугольниках, и у
    /// надписи по дуге габарит выходит втрое выше строки. По такому габариту
    /// «сосед в полустроке» — это сосед в пол-листа, и рамки сцеплялись через
    /// всю страницу. Своё соседство куски сегментатора уже посчитали по
    /// собственной высоте, и она честная.
    ///
    /// Хозяин выбирается по ИСХОДНОМУ списку: если искать его по ходу
    /// объединения, подросшая рамка начнёт подбирать следующие — тот самый
    /// снежный ком, ради которого всё и переписано.
    /// </summary>
    internal static List<SKRectI> Absorb(List<SKRectI> found, IReadOnlyList<SKRectI> extra)
    {
        var res = new List<SKRectI>(found);
        var add = extra.Where(r => r.Width >= 4 && r.Height >= 4).ToList();
        var host = new int[add.Count];

        for (var i = 0; i < add.Count; i++)
        {
            host[i] = -1;
            long best = 0;
            for (var j = 0; j < found.Count; j++)
            {
                if (!Same(found[j], add[i])) continue;
                long ix = Math.Min(found[j].Right, add[i].Right) - Math.Max(found[j].Left, add[i].Left);
                long iy = Math.Min(found[j].Bottom, add[i].Bottom) - Math.Max(found[j].Top, add[i].Top);
                if (ix * iy <= best) continue;
                best = ix * iy; host[i] = j;
            }
        }

        for (var i = 0; i < add.Count; i++)
        {
            if (host[i] >= 0) res[host[i]] = SKRectI.Union(res[host[i]], add[i]);
            else res.Add(add[i]);
        }
        return res;
    }

    /// <summary>
    /// Развести рамки, чтобы они не лезли одна в другую.
    ///
    /// Рамки приходят из двух источников и накладываются: одна оказывается
    /// внутри другой, или они заходят друг на друга краем. Для человека это
    /// две рамки на один текст, а для действий над блоком — двойная работа:
    /// «дорисовать блок» пройдёт по общим пикселям дважды.
    ///
    /// Вложенную выбрасываем, налезающую подрезаем. И то и другое НИЧЕГО не
    /// теряет: выброшенная и отрезанная части остаются накрыты соседкой, и
    /// общая закрашенная площадь та же — меняется только чья она.
    ///
    /// Подрезаем ТОЛЬКО начисто — когда соседка накрывает рамку насквозь по
    /// одной из осей, и отрезанная полоса целиком лежит в пересечении. Если
    /// рамки сошлись углами, чистого реза нет: любой отрежет и то, что не
    /// пересекается, а это уже потерянные буквы. Такие оставляем внахлёст.
    /// </summary>
    internal static List<SKRectI> Separate(List<SKRectI> src)
    {
        // Крупные держат форму, уступают мелкие: у крупной рамки текст занимает
        // её целиком, а мелкая чаще всего и есть кусок соседки
        var res = src.OrderByDescending(r => (long)r.Width * r.Height).ToList();

        for (var i = 0; i < res.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                var big = res[j];
                var r = res[i];
                if (r.Right <= big.Left || r.Left >= big.Right ||
                    r.Bottom <= big.Top || r.Top >= big.Bottom) continue;

                if (r.Left >= big.Left && r.Top >= big.Top &&
                    r.Right <= big.Right && r.Bottom <= big.Bottom)
                {
                    res[i] = SKRectI.Empty;
                    break;
                }
                res[i] = Cut(r, big);
                if (res[i].Width < 4 || res[i].Height < 4) break;
            }

            if (res[i].Width >= 4 && res[i].Height >= 4) continue;
            res.RemoveAt(i);
            i--;
        }
        return res;
    }

    /// <summary>
    /// Отрезать от рамки полосу, занятую соседкой, — если это можно сделать,
    /// не задев ничего сверх пересечения.
    /// </summary>
    static SKRectI Cut(SKRectI r, SKRectI big)
    {
        // Соседка накрывает рамку насквозь по высоте — режем сбоку
        if (big.Top <= r.Top && big.Bottom >= r.Bottom)
        {
            if (big.Left <= r.Left) return new SKRectI(big.Right, r.Top, r.Right, r.Bottom);
            if (big.Right >= r.Right) return new SKRectI(r.Left, r.Top, big.Left, r.Bottom);
        }

        // Насквозь по ширине — режем сверху или снизу
        if (big.Left <= r.Left && big.Right >= r.Right)
        {
            if (big.Top <= r.Top) return new SKRectI(r.Left, big.Bottom, r.Right, r.Bottom);
            if (big.Bottom >= r.Bottom) return new SKRectI(r.Left, r.Top, r.Right, big.Top);
        }

        return r;
    }

    /// <summary>
    /// Рамка из двух углов, в каком бы порядке их ни тянули. Правый и нижний
    /// край исключающие — как во всех рамках этого приложения.
    /// </summary>
    internal static SKRectI Norm(int x0, int y0, int x1, int y1) =>
        new(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1) + 1, Math.Max(y0, y1) + 1);

    /// <summary>Стороны рамки. Угол — это две стороны сразу.</summary>
    [Flags]
    internal enum Side { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }

    /// <summary>
    /// Какие стороны рамки схвачены в точке. Полоса захвата идёт по ОБЕ стороны
    /// от края: снаружи хватать край так же естественно, как изнутри, а на
    /// границе кадра снаружи места может и не быть.
    ///
    /// На мелкой рамке полоса ужимается до трети размера — иначе рамка целиком
    /// стала бы «краем» и её нельзя было бы ни выбрать щелчком, ни подвинуть.
    /// </summary>
    internal static Side Grip(SKRectI b, int x, int y, int m)
    {
        m = Band(b, m);

        // Ворота считаются по ПОСЛЕДНЕМУ пикселю рамки, а не по исключающему
        // краю: иначе справа и снизу оставался лишний пиксель слабины
        if (x < b.Left - m || x > b.Right - 1 + m || y < b.Top - m || y > b.Bottom - 1 + m)
            return Side.None;

        var s = Side.None;

        // Правый и нижний края ИСКЛЮЧАЮЩИЕ — последний пиксель рамки на единицу
        // левее и выше, и хватать надо именно за него
        if (Math.Abs(x - b.Left) <= m) s |= Side.Left;
        else if (Math.Abs(x - (b.Right - 1)) <= m) s |= Side.Right;

        if (Math.Abs(y - b.Top) <= m) s |= Side.Top;
        else if (Math.Abs(y - (b.Bottom - 1)) <= m) s |= Side.Bottom;

        return s;
    }

    /// <summary>
    /// Ширина полосы захвата для ЭТОЙ рамки. Ею же рисуются ручки: нарисованная
    /// ручка шире полосы — это ловушка, нажатие по её краю рисовало бы новую
    /// рамку вместо изменения старой.
    ///
    /// На мелкой рамке полоса ужимается до трети размера — иначе рамка целиком
    /// стала бы «краем» и её нельзя было бы ни выбрать щелчком, ни подвинуть.
    /// </summary>
    internal static int Band(SKRectI b, int m) =>
        Math.Max(1, Math.Min(m, Math.Min(b.Width, b.Height) / 3));

    /// <summary>
    /// Подвинуть схваченные стороны в точку. Сторона упирается в
    /// противоположную, а не проскакивает её: рамка, вывернутая наизнанку, —
    /// это не то, что человек имел в виду, потянув край.
    /// </summary>
    internal static SKRectI Resize(SKRectI b, Side grip, int x, int y, int min)
    {
        int l = b.Left, t = b.Top, r = b.Right, bo = b.Bottom;

        if (grip.HasFlag(Side.Left)) l = Math.Min(x, r - min);
        if (grip.HasFlag(Side.Right)) r = Math.Max(x + 1, l + min);
        if (grip.HasFlag(Side.Top)) t = Math.Min(y, bo - min);
        if (grip.HasFlag(Side.Bottom)) bo = Math.Max(y + 1, t + min);

        return new SKRectI(l, t, r, bo);
    }

    /// <summary>
    /// Прижать рамку к кадру. Рамку тянут мышью с захватом указателя, и он
    /// свободно уходит за край кадра — а дальше рамка адресует маску страницы
    /// напрямую, по одному индексу на пиксель. Рамка снаружи = выход за массив:
    /// именно так падала «Разметить блок» (журнал за 18.09).
    /// </summary>
    internal static SKRectI Clamp(SKRectI r, int w, int h) =>
        new(Math.Clamp(r.Left, 0, w), Math.Clamp(r.Top, 0, h),
            Math.Clamp(r.Right, 0, w), Math.Clamp(r.Bottom, 0, h));

    /// <summary>
    /// Какой блок под точкой. Из вложенных — САМЫЙ ТЕСНЫЙ: крупная рамка часто
    /// накрывает мелкую, и щелчок по мелкой обязан попасть в мелкую.
    /// Возвращает −1, если под точкой пусто.
    /// </summary>
    internal static int Pick(IReadOnlyList<SKRectI> boxes, int x, int y)
    {
        var best = -1;
        var area = long.MaxValue;
        for (var i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            if (x < b.Left || x >= b.Right || y < b.Top || y >= b.Bottom) continue;
            long a = (long)b.Width * b.Height;
            if (a >= area) continue;
            area = a; best = i;
        }
        return best;
    }

    /// <summary>
    /// Кисть: залить круг в маске. Радиус в пикселях КАДРА, а не экрана —
    /// иначе правка зависела бы от того, насколько человек приблизил картинку.
    /// </summary>
    internal static void Paint(byte[] mask, int w, int h, int cx, int cy, int r, bool add)
    {
        if (r < 1) r = 1;
        var v = (byte)(add ? 255 : 0);
        var r2 = r * r;
        var y0 = Math.Max(0, cy - r);
        var y1 = Math.Min(h - 1, cy + r);
        for (var y = y0; y <= y1; y++)
        {
            var dy = y - cy;
            var dx = (int)Math.Sqrt(Math.Max(0, r2 - dy * dy));
            var x0 = Math.Max(0, cx - dx);
            var x1 = Math.Min(w - 1, cx + dx);
            for (var x = x0; x <= x1; x++) mask[y * w + x] = v;
        }
    }
}
