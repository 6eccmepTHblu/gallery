using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Gallery;

/// <summary>
/// Токенизатор Marian: SentencePiece Unigram (SPEC.md §4.9).
///
/// Своя реализация, а не библиотека: в tokenizer.json у Marian нормализатор
/// `Precompiled` с пустой картой, и готовые загрузчики на нём спотыкаются.
/// Самого алгоритма тут на полсотни строк — Витерби по словарю кусков с
/// логарифмическими весами, — и он полностью определён файлом модели.
/// </summary>
sealed class MarianTokenizer
{
    public const int Unk = 1;
    public const int Eos = 0;
    public const int Start = 62517;        // он же pad: decoder_start_token_id

    // Жадный поиск на мусорном входе зацикливается: одно и то же слово идёт
    // по кругу, пока не упрётся в предел длины. Обрываем, как только четвёрка
    // токенов встречается второй раз — в переводе одной реплики так не бывает.
    //
    // Живёт здесь, а не рядом с самим циклом: функция чистая, и самотесту не
    // приходится трогать класс с сессиями ONNX, чтобы её прогнать.
    public const int LoopWindow = 4;

    public static bool Loops(IReadOnlyList<int> ids)
    {
        var tail = ids.Count - LoopWindow;
        if (tail < 1) return false;
        for (var s = 0; s < tail; s++)
        {
            var same = true;
            for (var k = 0; k < LoopWindow; k++)
                if (ids[s + k] != ids[tail + k]) { same = false; break; }
            if (same) return true;
        }
        return false;
    }
    readonly string[] _pieces;
    readonly float[] _scores;
    readonly Dictionary<string, int> _ids;
    readonly int _maxPiece;

    MarianTokenizer(string[] pieces, float[] scores)
    {
        _pieces = pieces;
        _scores = scores;
        _ids = new Dictionary<string, int>(pieces.Length, StringComparer.Ordinal);
        for (var i = 0; i < pieces.Length; i++) _ids[pieces[i]] = i;
        _maxPiece = pieces.Max(p => p.Length);
    }

    public static MarianTokenizer Load(string tokenizerJson)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(tokenizerJson));
        var vocab = doc.RootElement.GetProperty("model").GetProperty("vocab");

        var n = vocab.GetArrayLength();
        var pieces = new string[n];
        var scores = new float[n];

        var i = 0;
        foreach (var entry in vocab.EnumerateArray())
        {
            pieces[i] = entry[0].GetString() ?? "";
            scores[i] = (float)entry[1].GetDouble();
            i++;
        }
        return new MarianTokenizer(pieces, scores);
    }

    /// <summary>
    /// Текст → идентификаторы. Порядок шагов задан самим tokenizer.json:
    /// разбить по пробелам, каждому слову приписать «▁», разложить Витерби,
    /// в конце добавить «＜/s＞».
    /// </summary>
    public List<int> Encode(string text)
    {
        var ids = new List<int>(32);
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            Viterbi('▁' + word, ids);

        ids.Add(Eos);
        return ids;
    }

    void Viterbi(string s, List<int> outIds)
    {
        var n = s.Length;
        var best = new float[n + 1];
        var from = new int[n + 1];
        var piece = new int[n + 1];

        Array.Fill(best, float.NegativeInfinity);
        Array.Fill(from, -1);
        best[0] = 0f;

        for (var i = 0; i < n; i++)
        {
            if (float.IsNegativeInfinity(best[i])) continue;

            var matched = false;
            var limit = Math.Min(_maxPiece, n - i);
            for (var len = 1; len <= limit; len++)
            {
                if (!_ids.TryGetValue(s.Substring(i, len), out var id)) continue;
                matched = true;

                var score = best[i] + _scores[id];
                if (score <= best[i + len]) continue;
                best[i + len] = score;
                from[i + len] = i;
                piece[i + len] = id;
            }

            if (matched) continue;

            // Ни один кусок не подошёл — съедаем один символ как неизвестный.
            // Суррогатную пару не разрываем: иначе получится ломаный UTF-16.
            var step = char.IsHighSurrogate(s[i]) && i + 1 < n ? 2 : 1;
            var unkScore = best[i] - 10f;
            if (unkScore <= best[i + step]) continue;
            best[i + step] = unkScore;
            from[i + step] = i;
            piece[i + step] = Unk;
        }

        var stack = new List<int>(16);
        for (var i = n; i > 0;)
        {
            var prev = from[i];
            if (prev < 0) break;
            stack.Add(piece[i]);
            i = prev;
        }
        stack.Reverse();
        outIds.AddRange(stack);
    }

    /// <summary>Идентификаторы → текст: склеить куски, «▁» обратно в пробел.</summary>
    public string Decode(IEnumerable<int> ids)
    {
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            if (id == Eos || id == Start || id == Unk) continue;
            if ((uint)id < (uint)_pieces.Length) sb.Append(_pieces[id]);
        }
        return sb.Replace('▁', ' ').ToString().Trim();
    }
}

/// <summary>
/// Локальный перевод EN→RU моделью Marian (SPEC.md §4.9).
///
/// Почему специализированная модель перевода, а не языковая общего назначения:
/// на этой задаче она и точнее, и на порядок быстрее. Замерено на этой машине —
/// 94 мс на реплику против десятков секунд у языковой модели
/// сопоставимого качества.
///
/// Почему точная модель, а не квантованная: она оказалась И точнее, И ВДВОЕ
/// БЫСТРЕЕ (94 мс против 182). Int8-ядра ONNX Runtime на этом процессоре
/// проигрывают хорошо оптимизированному fp32-GEMM. Единственная плата — размер,
/// и она вынесена из поставки: веса качаются по требованию.
/// </summary>
sealed class Translator : IDisposable
{
    const int Layers = 6, Heads = 8, HeadDim = 64;
    const int MaxOutTokens = 128;

    public static string ModelDir => Path.Combine(SettingsStore.Dir, "models", "opus-mt-en-ru");

    /// <summary>Файл → сколько байт ждём. Размеры известны заранее — есть чем показать прогресс.</summary>
    public static readonly (string Name, long Bytes)[] Parts =
    {
        ("tokenizer.json", 7_205_876),
        ("onnx/encoder_model.onnx", 204_853_248),
        ("onnx/decoder_model_merged.onnx", 230_744_064),
    };

    public static bool ModelPresent =>
        Parts.All(p => new FileInfo(Path.Combine(ModelDir, p.Name.Replace('/', Path.DirectorySeparatorChar)))
                       is { Exists: true, Length: > 0 });

    const string Repo = "https://huggingface.co/Xenova/opus-mt-en-ru/resolve/main";

    /// <summary>
    /// Скачать веса. Во временный файл и переименованием — оборванная загрузка
    /// не должна оставить огрызок, который потом молча примут за готовую модель.
    /// </summary>
    public static async Task DownloadAsync(IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(ModelDir, "onnx"));

        var total = Parts.Sum(p => p.Bytes);
        long done = 0;

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        foreach (var (name, expect) in Parts)
        {
            var dest = Path.Combine(ModelDir, name.Replace('/', Path.DirectorySeparatorChar));
            if (new FileInfo(dest) is { Exists: true, Length: > 0 })
            {
                done += expect;
                progress.Report((double)done / total);
                continue;
            }

            var tmp = dest + ".part";
            using (var rsp = await http.GetAsync($"{Repo}/{name}",
                       System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct))
            {
                rsp.EnsureSuccessStatusCode();
                using var src = await rsp.Content.ReadAsStreamAsync(ct);
                using var dst = File.Create(tmp);

                var buf = new byte[1 << 16];
                var at = done;
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    at += read;
                    progress.Report(Math.Min(1.0, (double)at / total));
                }
            }

            File.Move(tmp, dest, overwrite: true);
            done += expect;
        }

        progress.Report(1.0);
    }

    MarianTokenizer? _tok;
    InferenceSession? _enc, _dec;
    string[]? _decOutNames;

    public double LastMs { get; private set; }
    public bool Ready => _enc is not null;

    /// <summary>
    /// Загрузка моделей — секунды и сотни мегабайт, поэтому строго по требованию
    /// и строго вне UI-потока.
    /// </summary>
    public void Load()
    {
        if (_enc is not null) return;

        var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // 0 = по числу ядер. Перевод идёт в фоне, забрать все ядра он может:
            // UI-поток в это время не считает, а ждёт.
            IntraOpNumThreads = 0,
        };

        _tok = MarianTokenizer.Load(Path.Combine(ModelDir, "tokenizer.json"));
        _enc = new InferenceSession(Path.Combine(ModelDir, "onnx", "encoder_model.onnx"), opts);
        _dec = new InferenceSession(Path.Combine(ModelDir, "onnx", "decoder_model_merged.onnx"), opts);
        _decOutNames = _dec.OutputMetadata.Keys.ToArray();
    }

    // Предложение с хвостом знаков и пробелов. Точка тут не косметика — см. ниже.
    static readonly Regex Sentences = new(@"[^.!?…]+[.!?…]*\s*", RegexOptions.Compiled);

    /// <summary>
    /// Перевод реплики.
    ///
    /// ПО ПРЕДЛОЖЕНИЯМ, а не целиком: Marian обучен на парах предложений и на
    /// нескольких разом молча теряет часть. Замерено: «I can't believe it. We
    /// actually made it. Look at that view!» целиком превращается в одну
    /// последнюю фразу, по предложениям — переводится полностью.
    /// </summary>
    public string Translate(string text, CancellationToken ct)
    {
        if (_enc is null || _dec is null || _tok is null) return text;

        var t0 = Stopwatch.GetTimestamp();
        var parts = Sentences.Matches(text)
            .Select(m => m.Value.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        if (parts.Count == 0) parts.Add(text);

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(One(part, ct));
        }

        LastMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        return sb.ToString();
    }

    string One(string text, CancellationToken ct)
    {
        var ids = _tok!.Encode(text);
        var len = ids.Count;

        var inputIds = new DenseTensor<long>(new[] { 1, len });
        var mask = new DenseTensor<long>(new[] { 1, len });
        for (var i = 0; i < len; i++) { inputIds[0, i] = ids[i]; mask[0, i] = 1; }

        using var encOut = _enc!.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        });
        var hidden = encOut.First(v => v.Name == "last_hidden_state").AsTensor<float>();

        // Первый шаг идёт по ветке без кэша, и прошлые значения обязаны быть
        // тензорами нулевой длины — не отсутствовать. Граф объединённый: какую
        // ветку считать, решает use_cache_branch.
        var empty = new DenseTensor<float>(new[] { 1, Heads, 0, HeadDim });
        var pastDec = new Tensor<float>[Layers * 2];
        var pastEnc = new Tensor<float>[Layers * 2];
        Array.Fill(pastDec, empty);
        Array.Fill(pastEnc, empty);

        var outIds = new List<int>(MaxOutTokens);
        var cur = MarianTokenizer.Start;

        // Результат ПЕРВОГО шага держим до конца цикла: в нём лежит кросс-внимание,
        // которое подаётся на вход каждому следующему шагу. Освобождать по общему
        // правилу «предыдущий больше не нужен» здесь нельзя.
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? first = null;
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue>? prev = null;

        try
        {
            for (var step = 0; step < MaxOutTokens; step++)
            {
                ct.ThrowIfCancellationRequested();

                var step1 = new DenseTensor<long>(new[] { 1, 1 });
                step1[0, 0] = cur;

                var feed = new List<NamedOnnxValue>(4 + Layers * 4)
                {
                    NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask),
                    NamedOnnxValue.CreateFromTensor("input_ids", step1),
                    NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
                    NamedOnnxValue.CreateFromTensor("use_cache_branch",
                        new DenseTensor<bool>(new[] { step > 0 }, new[] { 1 })),
                };
                for (var l = 0; l < Layers; l++)
                {
                    feed.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.decoder.key", pastDec[l * 2]));
                    feed.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.decoder.value", pastDec[l * 2 + 1]));
                    feed.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.encoder.key", pastEnc[l * 2]));
                    feed.Add(NamedOnnxValue.CreateFromTensor($"past_key_values.{l}.encoder.value", pastEnc[l * 2 + 1]));
                }

                var res = _dec!.Run(feed);
                var map = res.ToDictionary(v => v.Name, v => v);

                var logits = map["logits"].AsTensor<float>();
                var vocab = logits.Dimensions[^1];
                var offset = (logits.Dimensions[1] - 1) * vocab;

                var bestId = 0;
                var bestVal = float.NegativeInfinity;
                var flat = logits.ToArray();
                for (var v = 0; v < vocab; v++)
                {
                    var x = flat[offset + v];
                    if (x <= bestVal) continue;
                    bestVal = x; bestId = v;
                }

                if (bestId == MarianTokenizer.Eos)
                {
                    if (step > 0) res.Dispose();    // на шаге 0 это ещё и кросс-внимание
                    else first = res;
                    break;
                }
                outIds.Add(bestId);
                cur = bestId;

                if (MarianTokenizer.Loops(outIds))
                {
                    // Хвост-повтор выбрасываем: это уже второй заход по кругу
                    outIds.RemoveRange(outIds.Count - MarianTokenizer.LoopWindow,
                                       MarianTokenizer.LoopWindow);
                    if (step > 0) res.Dispose();
                    else first = res;
                    break;
                }

                for (var l = 0; l < Layers; l++)
                {
                    pastDec[l * 2] = map[$"present.{l}.decoder.key"].AsTensor<float>();
                    pastDec[l * 2 + 1] = map[$"present.{l}.decoder.value"].AsTensor<float>();

                    // Кросс-внимание от шага не зависит: считаем один раз и держим.
                    if (step == 0)
                    {
                        pastEnc[l * 2] = map[$"present.{l}.encoder.key"].AsTensor<float>();
                        pastEnc[l * 2 + 1] = map[$"present.{l}.encoder.value"].AsTensor<float>();
                    }
                }

                // Освобождаем предыдущий шаг только теперь: его тензоры были
                // входами текущего Run и до его конца обязаны оставаться живыми
                if (step == 0)
                {
                    first = res;
                }
                else
                {
                    var stale = prev;
                    prev = res;
                    stale?.Dispose();
                }
            }
        }
        finally
        {
            prev?.Dispose();
            first?.Dispose();
        }

        return _tok.Decode(outIds);
    }

    public void Dispose()
    {
        _enc?.Dispose(); _enc = null;
        _dec?.Dispose(); _dec = null;
        _tok = null;
    }
}
