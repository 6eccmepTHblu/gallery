using System;
using System.Collections.Generic;
using System.Linq;

namespace Gallery;

/// <summary>Одна запись обмена с API: что ушло, что вернулось (SPEC.md §4.11).</summary>
sealed class ApiExchange
{
    public required DateTime At;
    public required string Url;
    public required string Model;
    public required string Status;      // «200 OK» или текст отказа
    public required double Ms;
    public required string Request;
    public required string Response;
    public bool Failed;
}

/// <summary>
/// Журнал обращений к API (SPEC.md §4.11).
///
/// Нужен не для отладки, а для доверия: перевод приходит откуда-то извне, и
/// человек имеет право видеть, что именно ушло с его машины и что вернулось,
/// а не верить на слово. Заодно это единственный способ отличить «API ответил
/// плохо» от «API не звался вовсе» — а вопрос возникает первым же.
///
/// КЛЮЧ СЮДА НЕ ПОПАДАЕТ. Он уходит заголовком Authorization, а пишется только
/// тело запроса. Это не случайность, а условие: журнал можно показать на экране
/// и скопировать в чат поддержки, не раздев себя.
/// </summary>
static class ApiLog
{
    const int Keep = 20;                 // хватает, чтобы понять картину, и не течёт
    const int MaxText = 8 * 1024;        // страница целиком — единицы килобайт

    static readonly List<ApiExchange> Items = new();

    public static IReadOnlyList<ApiExchange> Recent
    {
        get { lock (Items) return Items.ToList(); }
    }

    public static event Action? Changed;

    public static void Add(ApiExchange e)
    {
        e.Request = Clip(e.Request);
        e.Response = Clip(e.Response);

        lock (Items)
        {
            Items.Insert(0, e);                       // свежее сверху
            while (Items.Count > Keep) Items.RemoveAt(Items.Count - 1);
        }
        Changed?.Invoke();
    }

    public static void Clear()
    {
        lock (Items) Items.Clear();
        Changed?.Invoke();
    }

    static string Clip(string? s)
    {
        s ??= "";
        return s.Length <= MaxText ? s : s[..MaxText] + $"\n… обрезано, всего {s.Length} знаков";
    }
}
