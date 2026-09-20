using System;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Gallery;

/// <summary>
/// Дорисовка под стёртым — lama-manga (206 МБ), ogkalu/lama-manga-onnx-dynamic.
///
/// Контракт выяснен запуском, а не чтением карточки (§4.14):
///   вход  image float32 [1,3,H,W] в 0..1, RGB;
///   вход  mask  float32 [1,1,H,W], 1 — стирать;
///   выход inpainted float32 [1,3,H,W] в 0..1.
///
/// Две особенности, которых в описании не было:
///   * модель САМА склеивает результат с исходником — вне маски отличие ровно
///     ноль, смешивать ничего не надо;
///   * стороны обязаны быть КРАТНЫ 8, иначе падает в Mul с несовпадением осей.
///
/// Цена растёт со стороной вырезки: 256 — около 0,8 с, 512 — до 5 с, 1024 —
/// дюжина секунд. Поэтому режется область маски с запасом, а не вся страница.
/// </summary>
static class Inpaint
{
    /// <summary>Запас рисунка вокруг дырки: модели нужно на что опереться.</summary>
    public const int Context = 48;

    public static string ModelPath =>
        Path.Combine(AppContext.BaseDirectory, "models", "lama-manga.onnx");

    public static bool Present => File.Exists(ModelPath);

    /// <summary>
    /// Где и какого размера была последняя вырезка. Нужно не для красоты:
    /// панель снимает по ней кусок кадра ДО дорисовки, чтобы можно было
    /// откатить одну дорисовку, не храня весь кадр целиком.
    /// </summary>
    public static int LastX { get; private set; }
    public static int LastY { get; private set; }
    public static int LastW { get; private set; }
    public static int LastH { get; private set; }
    public static int LastThreads { get; private set; }

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
                    Log.Warn($"дорисовка не загрузилась: {ex.Message}");
                    return null;
                }
            }
            return _session;
        }
    }

    /// <summary>Построить сессию заранее: граф на 206 МБ строится секунд шесть.</summary>
    public static void Warm() { _ = Session; }

    /// <summary>
    /// Какую область резать под дорисовку: рамка маски плюс запас, стороны
    /// кратны восьми, и всё это внутри страницы.
    ///
    /// Чистая арифметика — единственное место, где ошибка даёт не кривой
    /// результат, а падение модели, и потому проверяется числом.
    /// </summary>
    public static (int X, int Y, int W, int H) Region(
        int pageW, int pageH, int mx0, int my0, int mx1, int my1, int context = Context)
    {
        var x0 = Math.Max(0, mx0 - context);
        var y0 = Math.Max(0, my0 - context);
        var x1 = Math.Min(pageW, mx1 + 1 + context);
        var y1 = Math.Min(pageH, my1 + 1 + context);

        var w = Math.Min((x1 - x0 + 7) / 8 * 8, pageW / 8 * 8);
        var h = Math.Min((y1 - y0 + 7) / 8 * 8, pageH / 8 * 8);
        if (w < 8 || h < 8) return (0, 0, 0, 0);

        return (Math.Max(0, Math.Min(x0, pageW - w)),
                Math.Max(0, Math.Min(y0, pageH - h)), w, h);
    }

    /// <summary>
    /// Стереть по маске. Возвращает НОВЫЙ кадр; исходный не трогается —
    /// пользователь должен иметь возможность сравнить и передумать.
    /// Маска — 255 там, где стирать, размером со страницу.
    /// </summary>
    public static SKBitmap? Run(SKBitmap page, byte[] mask)
    {
        var s = Session;
        if (s is null) return null;

        int w = page.Width, h = page.Height;
        if (mask.Length != w * h) return null;

        int mx0 = int.MaxValue, my0 = int.MaxValue, mx1 = -1, my1 = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (mask[y * w + x] < 128) continue;
                if (x < mx0) mx0 = x;
                if (x > mx1) mx1 = x;
                if (y < my0) my0 = y;
                if (y > my1) my1 = y;
            }
        if (mx1 < 0) return null;       // стирать нечего

        var (rx, ry, rw, rh) = Region(w, h, mx0, my0, mx1, my1);
        if (rw == 0) return null;
        LastX = rx; LastY = ry; LastW = rw; LastH = rh;
        LastThreads = Environment.ProcessorCount;

        var img = new DenseTensor<float>(new[] { 1, 3, rh, rw });
        var msk = new DenseTensor<float>(new[] { 1, 1, rh, rw });

        var px = page.Pixels;
        for (var y = 0; y < rh; y++)
            for (var x = 0; x < rw; x++)
            {
                var c = px[(ry + y) * w + rx + x];
                img[0, 0, y, x] = c.Red / 255f;
                img[0, 1, y, x] = c.Green / 255f;
                img[0, 2, y, x] = c.Blue / 255f;
                msk[0, 0, y, x] = mask[(ry + y) * w + rx + x] >= 128 ? 1f : 0f;
            }

        using var res = s.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("image", img),
            NamedOnnxValue.CreateFromTensor("mask", msk),
        });
        var t = res.First().AsTensor<float>();

        var outp = page.Copy();
        var dst = outp.Pixels;
        for (var y = 0; y < rh; y++)
            for (var x = 0; x < rw; x++)
            {
                byte R(int c) => (byte)Math.Clamp(t[0, c, y, x] * 255f + 0.5f, 0, 255);
                dst[(ry + y) * w + rx + x] = new SKColor(R(0), R(1), R(2), 255);
            }
        outp.Pixels = dst;
        return outp;
    }
}
