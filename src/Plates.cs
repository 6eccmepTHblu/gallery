using System;
using System.Collections.Generic;

namespace Gallery;

/// <summary>
/// Раскладка подложек перевода (§4.10).
///
/// Плашка почти всегда шире и выше исходной рамки: русский текст длиннее
/// английского, и место под него приходится добирать ростом. Расти вслепую
/// нельзя — в тесной колонке реплик плашка налезает на соседнюю, и обе
/// становятся нечитаемы. Поэтому рост ограничен полосой, свободной от соседей.
///
/// Чистая геометрия и никакого UI: это единственная часть вёрстки, которую
/// можно проверить числом, и ошибка здесь видна только глазами.
/// </summary>
static class Plates
{
    public readonly record struct Rect(double X, double Y, double W, double H)
    {
        public double Right => X + W;
        public double Bottom => Y + H;
    }

    /// <summary>
    /// Полоса по вертикали, в которой плашка <paramref name="i"/> может расти,
    /// не задевая соседей. Соседями считаются только те, кто пересекается с ней
    /// ПО ГОРИЗОНТАЛИ: реплика в другой колонке роста не мешает.
    /// </summary>
    /// <param name="gap">Просвет между плашками, чтобы они не слипались.</param>
    /// <param name="height">Высота слоя: ниже неё плашке расти некуда.</param>
    public static (double Top, double Bottom) Band(
        IReadOnlyList<Rect> bases, int i, double gap, double height)
    {
        var me = bases[i];
        var top = 0.0;
        var bottom = height;

        for (var j = 0; j < bases.Count; j++)
        {
            if (j == i) continue;
            var o = bases[j];

            // Не пересекаются по горизонтали — не соседи
            if (o.Right <= me.X || o.X >= me.Right) continue;

            if (o.Bottom <= me.Y) top = Math.Max(top, o.Bottom + gap);
            else if (o.Y >= me.Bottom) bottom = Math.Min(bottom, o.Y - gap);
            // Иначе рамки уже налезают друг на друга — тут ограничивать нечего,
            // наложения снимаются раньше, на распознавании
        }

        if (bottom < top) bottom = top;
        return (top, bottom);
    }

    /// <summary>
    /// Итоговая высота и верх плашки: растём симметрично от середины исходной
    /// рамки, но не выходя за полосу. Базовую высоту не ужимаем — под ней лежит
    /// исходный текст, и подложка обязана его закрывать.
    /// </summary>
    public static (double Y, double H) Fit(
        double baseY, double baseH, double want, double top, double bottom)
    {
        var room = Math.Max(baseH, bottom - top);
        var h = Math.Clamp(want, baseH, room);

        var y = baseY - (h - baseH) / 2;

        // Двигаем в полосу только если плашка в неё вообще влезает. Не влезла —
        // оставляем на месте: подложка обязана закрывать исходный текст, и
        // сползшая вверх пустая плашка хуже, чем задетый край соседа
        if (h <= bottom - top)
        {
            if (y < top) y = top;
            if (y + h > bottom) y = bottom - h;
        }
        else y = baseY;

        return (y, h);
    }
}
