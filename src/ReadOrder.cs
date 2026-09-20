using System;
using System.Collections.Generic;
using System.Linq;

namespace Gallery;

/// <summary>
/// Порядок чтения реплик на странице (SPEC.md §4.9).
///
/// Рекурсивный XY-разрез по рамкам САМИХ РЕПЛИК, без выделения панелей.
/// Межпанельные промежутки и так пусты, поэтому разрез находит их сам, а
/// детектор панелей был бы отдельной моделью с отдельными способами ошибаться.
///
/// Зачем вообще: без правильного порядка контекст страницы бесполезен — модель
/// получит диалог вперемешку и согласует род с не тем говорящим. Ошибка здесь
/// тихая: перевод выглядит связным, но принадлежит другому персонажу.
/// </summary>
static class ReadOrder
{
    /// <summary>
    /// Упорядочить рамки. Западный комикс читается слева направо; для манги
    /// достаточно поменять знак сравнения по горизонтали, поэтому направление —
    /// параметр, а не жёстко зашитая константа.
    /// </summary>
    public static List<TextBox2> Sort(IReadOnlyList<TextBox2> boxes, bool rightToLeft = false)
    {
        var result = new List<TextBox2>(boxes.Count);
        Cut(boxes.ToList(), rightToLeft, result, depth: 0);
        return result;
    }

    static void Cut(List<TextBox2> items, bool rtl, List<TextBox2> outp, int depth)
    {
        if (items.Count == 0) return;
        if (items.Count == 1) { outp.Add(items[0]); return; }

        // Глубину ограничиваем: на вырожденной странице (все рамки наложены друг
        // на друга) рекурсия иначе не сойдётся
        if (depth < 12)
        {
            // Сначала горизонтальные полосы: строки панелей важнее колонок,
            // потому что комикс читается сверху вниз рядами
            var rows = Split(items, horizontal: true);
            if (rows.Count > 1)
            {
                foreach (var row in rows) Cut(row, rtl, outp, depth + 1);
                return;
            }

            var cols = Split(items, horizontal: false);
            if (cols.Count > 1)
            {
                if (rtl) cols.Reverse();
                foreach (var col in cols) Cut(col, rtl, outp, depth + 1);
                return;
            }
        }

        // Разрезать нечем — рамки перекрываются по обеим осям. Читаем так, как
        // читает человек: сверху вниз, при равной высоте — вдоль строки
        items.Sort((a, b) =>
        {
            var sameRow = Math.Abs(a.Y - b.Y) < Math.Max(a.H, b.H) * 0.6;
            if (sameRow)
                return rtl ? b.X.CompareTo(a.X) : a.X.CompareTo(b.X);
            return a.Y.CompareTo(b.Y);
        });
        outp.AddRange(items);
    }

    /// <summary>
    /// Разбить набор пустой полосой. Возвращает группы в порядке возрастания
    /// координаты; одна группа означает, что разреза по этой оси нет.
    /// </summary>
    static List<List<TextBox2>> Split(List<TextBox2> items, bool horizontal)
    {
        var sorted = items
            .OrderBy(b => horizontal ? b.Y : b.X)
            .ToList();

        var groups = new List<List<TextBox2>>();
        var current = new List<TextBox2> { sorted[0] };
        var edge = horizontal ? sorted[0].Bottom : sorted[0].Right;

        for (var i = 1; i < sorted.Count; i++)
        {
            var b = sorted[i];
            var start = horizontal ? b.Y : b.X;

            if (start > edge)
            {
                // Есть просвет — начинается новая полоса
                groups.Add(current);
                current = new List<TextBox2> { b };
                edge = horizontal ? b.Bottom : b.Right;
            }
            else
            {
                current.Add(b);
                edge = Math.Max(edge, horizontal ? b.Bottom : b.Right);
            }
        }

        groups.Add(current);
        return groups;
    }
}
