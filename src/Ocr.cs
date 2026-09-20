using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Gallery;

/// <summary>
/// Блок текста на странице: несколько строк, найденных рядом, и прямоугольник
/// в пикселях ИСХОДНОГО кадра. Координаты в исходных пикселях, а не в экранных,
/// потому что наложение живёт внутри зумируемого содержимого и пересчитывается
/// из них при любом масштабе (§4.9).
/// </summary>
sealed class TextBox2
{
    public required string Source;              // как распознали
    public string? Translated;                  // как перевели, null пока не переведено

    /// <summary>
    /// Сколько строк распознавателя сшито в эту реплику. Нужно, чтобы померить
    /// высоту ОДНОЙ строки: чернила соседних строк в тесном пузыре сливаются,
    /// и разделить их по пустым промежуткам не выходит.
    /// </summary>
    public int Lines = 1;
    public double X, Y, W, H;

    /// <summary>
    /// Цвет фона вокруг текста, взятый из самого кадра. Белая подложка по
    /// умолчанию выдала бы себя прямоугольником в любом небелом пузыре.
    /// </summary>
    public byte BgR = 255, BgG = 255, BgB = 255;

    /// <summary>
    /// Перевод получен НЕ тем способом, который заявлен для страницы: модель
    /// эту реплику не вернула, и её добрали локально. Такие обязаны отличаться
    /// на вид — иначе чужой по качеству текст не отличить от остального (§4.10).
    /// </summary>
    public bool Fallback;

    /// <summary>
    /// Реплику нашла модель по картинке, а не распознавание. Рамка у неё
    /// ПРИБЛИЗИТЕЛЬНАЯ: языковые модели плохо попадают в точные координаты,
    /// и обещать тут пиксельную точность нельзя (§4.13).
    /// </summary>
    public bool FromImage;

    /// <summary>Тёмный фон требует светлого текста — иначе перевод не прочесть.</summary>
    public bool DarkBg => (BgR * 299 + BgG * 587 + BgB * 114) / 1000 < 128;

    public double Right => X + W;
    public double Bottom => Y + H;
}

/// <summary>
/// Распознавание текста на странице (SPEC.md §4.9).
///
/// Движок — встроенный в Windows <see cref="OcrEngine"/>: офлайн, без единой
/// зависимости, языки уже стоят в системе. Он рассчитан на документы, а не на
/// комиксы, поэтому аккуратную печатную надпись в пузыре читает уверенно, а
/// рукописные звуки и текст по дуге — нет. Это потолок подхода, а не настройка.
/// </summary>
static class Ocr
{
    /// <summary>
    /// Распознавание требует разрешения: мелкий шрифт в пузыре на кадре,
    /// ужатом до 1600 px, разваливается. Берём отдельный декод под OCR.
    /// </summary>
    public const uint InputWidth = 2400;

    public static double LastMs { get; private set; }

    static OcrEngine? _engine;

    /// <summary>
    /// Движок создаётся один раз: инициализация стоит десятки миллисекунд,
    /// а сам он потокобезопасен для последовательных вызовов.
    /// </summary>
    public static OcrEngine? Engine =>
        _engine ??= OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                 ?? OcrEngine.TryCreateFromUserProfileLanguages();

    /// <summary>
    /// Возвращает блоки И размеры кадра, на котором распознавали: без них
    /// координаты не во что пересчитывать — декод под OCR укладывает в 2400 px
    /// длинную сторону, и для портретной страницы ширина будет другой.
    /// </summary>
    /// <summary>
    /// Читать запасным движком, даже если основной на месте. Только для замера:
    /// сравнение имеет смысл на одном и том же корпусе, одной командой.
    /// </summary>
    public static bool ForceLegacy { get; set; }

    public static async Task<PageText> ReadAsync(string path, CancellationToken ct)
    {
        // PP-OCRv5 читает комиксный леттеринг вдевятеро точнее при том же
        // времени: ошибок в знаках 3,3 % против 28,8 на корпусе из 96 реплик
        // (§4.9). Windows.Media.Ocr остаётся запасным — на случай, если моделей
        // рядом с исполняемым файлом не окажется
        if (!ForceLegacy && RapidOcrEngine.Present)
            return await Task.Run(() => RapidOcrEngine.Read(path), ct);

        var engine = Engine;
        if (engine is null) return new PageText { Boxes = new List<TextBox2>() };

        var t0 = Stopwatch.GetTimestamp();

        // ПРОХОД 1 — натуральный масштаб, не крупнее InputWidth.
        var (boxesA, wA, hA, medianA, bmpA) =
            await PassAsync(engine, path, InputWidth, upscale: false, ct);

        using (bmpA)
        {
            // ПРОХОД 2 — вдвое крупнее.
            //
            // Измерено на странице 1060 px: на увеличенной вдвое движок находит
            // слова, которых на натуральной не видел ВОВСЕ («ONLY» в «WHY AM I
            // ALWAYS THE ONLY ONE ON TIME»). Ошибки в знаках у проходов при этом
            // разные, и слияние берёт лучшее от каждого.
            var boxes = boxesA;
            var target = (uint)Math.Min(UpscaleCap, Math.Max(wA, hA) * 2);
            try
            {
                var (boxesB, wB, _, _, bmpB) =
                    await PassAsync(engine, path, target, upscale: true, ct);
                using (bmpB)
                {
                    // ПРОХОД 3 — по облакам, найденным детектором формы.
                    //
                    // Первые два прохода ищут БУКВЫ и потому слепы к пузырю, где
                    // не прочиталось ни одной: реплика пропадает бесследно, и
                    // восстановить её потом нечем. Детектор ищет ФОРМУ и находит
                    // облако независимо от содержимого.
                    //
                    // Режем именно увеличенный кадр, пока он ещё жив: вырезка из
                    // натурального — это апскейл апскейла, мыло, на котором
                    // движок читает ХУЖЕ, чем на целой странице (проверено).
                    if (Bubbles.Present)
                    {
                        try { boxesB = await BubblePassAsync(engine, bmpB, boxesB, ct); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { Log.Warn($"проход по облакам: {ex.Message}"); }
                    }

                    // Координаты второго прохода приводим к системе первого
                    var k = wB > 0 ? (double)wA / wB : 1.0;
                    foreach (var b in boxesB) { b.X *= k; b.Y *= k; b.W *= k; b.H *= k; }
                    boxes = Merge(boxesA, boxesB);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Второй проход — улучшение, а не обязанность: не вышло — работаем на первом
                Log.Warn($"второй проход распознавания: {ex.Message}");
            }

            SampleBackgrounds(bmpA, boxes);

            LastMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            // Порядок чтения задаём здесь, один раз: от него зависит и показ,
            // и — главное — контекст, который уйдёт переводчику (§4.9)
            return new PageText
            {
                Boxes = ReadOrder.Sort(Dedupe(boxes)),
                ImgW = wA,
                ImgH = hA,
                MedianLineH = medianA,
            };
        }
    }

    /// <summary>Потолок второго прохода: выше него память и время растут зря.</summary>
    public const uint UpscaleCap = 4000;

    static async Task<(List<TextBox2> boxes, int w, int h, double median, SoftwareBitmap bmp)>
        PassAsync(OcrEngine engine, string path, uint target, bool upscale, CancellationToken ct)
    {
        var frame = await Task.Run(
            () => Decoder.DecodeAsync(path, target, ct, upscale), ct);

        SoftwareBitmap input;
        using (frame.Bitmap)
        {
            // OcrEngine принимает только Bgra8; премультипликация ему безразлична,
            // но формат обязан совпасть — иначе RecognizeAsync бросает
            input = frame.Bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? SoftwareBitmap.Copy(frame.Bitmap)
                : SoftwareBitmap.Convert(frame.Bitmap, BitmapPixelFormat.Bgra8,
                                         BitmapAlphaMode.Ignore);
        }

        ct.ThrowIfCancellationRequested();
        var result = await engine.RecognizeAsync(input);
        var boxes = Group(result, input.PixelWidth, input.PixelHeight, out var median);
        return (boxes, input.PixelWidth, input.PixelHeight, median, input);
    }

    /// <summary>
    /// Убрать наложения.
    ///
    /// Слияние сопоставляет одной рамке РОВНО ОДНУ встречную, и когда проход по
    /// облакам возвращает на один пузырь две — вторая добавляется как есть. На
    /// экране это два перевода друг поверх друга, а для переводчика — две
    /// реплики вместо одной. Оставляем ту, где больше букв.
    ///
    /// Порог считается по МЕНЬШЕЙ рамке: соседний пузырь, задевающий эту краем,
    /// наложением не является и обязан выжить.
    /// </summary>
    internal static List<TextBox2> Dedupe(List<TextBox2> boxes, double part = 0.6)
    {
        static int Letters(string s) => s.Count(char.IsLetter);

        var kept = new List<TextBox2>(boxes.Count);
        foreach (var x in boxes.OrderByDescending(b => Letters(b.Source)))
        {
            var mine = Math.Max(1, x.W * x.H);
            var covered = false;
            foreach (var k in kept)
            {
                var his = Math.Max(1, k.W * k.H);
                if (Overlap(x, k) > Math.Min(mine, his) * part) { covered = true; break; }
            }
            if (!covered) kept.Add(x);
        }
        return kept;
    }

    /// <summary>
    /// Слияние двух проходов.
    ///
    /// Правило выбора: побеждает текст, в котором БОЛЬШЕ БУКВ. Оно кажется грубым,
    /// но отражает измеренное — проходы теряют РАЗНОЕ, и потерянное слово вредит
    /// сильнее перепутанного знака: слово уже не восстановить ничем, а знак
    /// восстанавливается по смыслу соседей (§4.10). Найденное только вторым
    /// проходом добавляется целиком — это и есть главный выигрыш.
    /// </summary>
    internal static List<TextBox2> Merge(List<TextBox2> a, List<TextBox2> b)
    {
        static int Letters(string s) => s.Count(char.IsLetter);

        var used = new bool[b.Count];
        var result = new List<TextBox2>(a.Count + b.Count);

        foreach (var x in a)
        {
            var best = -1;
            var bestArea = 0.0;
            for (var i = 0; i < b.Count; i++)
            {
                if (used[i]) continue;
                var area = Overlap(x, b[i]);
                if (area <= bestArea) continue;
                bestArea = area; best = i;
            }

            // Порог по площади меньшей из рамок: соседний пузырь, лишь краем
            // задевающий эту рамку, — не тот же самый текст
            var mine = Math.Max(1, x.W * x.H);
            if (best >= 0 && bestArea > Math.Min(mine, Math.Max(1, b[best].W * b[best].H)) * 0.35)
            {
                used[best] = true;
                result.Add(Letters(b[best].Source) > Letters(x.Source) ? b[best] : x);
            }
            else result.Add(x);
        }

        for (var i = 0; i < b.Count; i++)
            if (!used[i]) result.Add(b[i]);

        return result;
    }

    static double Overlap(TextBox2 p, TextBox2 q)
    {
        var w = Math.Min(p.Right, q.Right) - Math.Max(p.X, q.X);
        var h = Math.Min(p.Bottom, q.Bottom) - Math.Max(p.Y, q.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    /// <summary>
    /// Строки → блоки. OCR отдаёт строки поодиночке, а переводить их порознь
    /// нельзя: фраза в пузыре разбита переносами, и по одной строке смысл
    /// теряется вместе с падежами. Склеиваем то, что стоит рядом по вертикали
    /// и перекрывается по горизонтали, — это и есть пузырь.
    /// </summary>
    static List<TextBox2> Group(OcrResult result, int imgW, int imgH, out double medianLineH)
    {
        medianLineH = 24;
        var lines = new List<(Rect box, string text)>();
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0) continue;

            var x0 = line.Words.Min(w => w.BoundingRect.Left);
            var y0 = line.Words.Min(w => w.BoundingRect.Top);
            var x1 = line.Words.Max(w => w.BoundingRect.Right);
            var y1 = line.Words.Max(w => w.BoundingRect.Bottom);

            var text = line.Text?.Trim();
            if (string.IsNullOrEmpty(text)) continue;

            lines.Add((new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0)), text));
        }

        if (lines.Count > 0)
        {
            var hs = lines.Select(l => l.box.Height).OrderBy(x => x).ToList();
            medianLineH = hs[hs.Count / 2];
        }

        lines.Sort((a, b) => a.box.Top.CompareTo(b.box.Top));

        var blocks = new List<(Rect box, List<string> lines)>();
        foreach (var (box, text) in lines)
        {
            var merged = false;
            for (var i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];

                // По вертикали — не дальше строки с небольшим запасом: так
                // соседние пузыри не слипаются в один
                var gap = box.Top - (b.box.Top + b.box.Height);
                if (gap > box.Height * 1.1 || gap < -box.Height) continue;

                // По горизонтали — заметное перекрытие: колонки текста рядом
                // друг с другом остаются разными блоками
                var overlap = Math.Min(box.Right, b.box.Right) - Math.Max(box.Left, b.box.Left);
                if (overlap < Math.Min(box.Width, b.box.Width) * 0.35) continue;

                var nx = Math.Min(box.Left, b.box.Left);
                var ny = Math.Min(box.Top, b.box.Top);
                blocks[i] = (new Rect(nx, ny,
                                Math.Max(box.Right, b.box.Right) - nx,
                                Math.Max(box.Bottom, b.box.Bottom) - ny),
                             b.lines);
                b.lines.Add(text);
                merged = true;
                break;
            }

            if (!merged) blocks.Add((box, new List<string> { text }));
        }

        return blocks
            // Одиночный символ в углу — это шум, а не реплика
            .Where(b => b.lines.Sum(l => l.Length) >= 2)
            .Select(b => new TextBox2
            {
                Source = Clean(string.Join(" ", b.lines)),
                X = b.box.Left,
                Y = b.box.Top,
                W = b.box.Width,
                H = b.box.Height,
            })
            .Where(b => b.Source.Length >= 2)
            .ToList();
    }

    /// <summary>
    /// Цвет подложки — медиана по кольцу вокруг текста. Медиана, а не среднее:
    /// в кольцо неизбежно попадают куски контура пузыря и хвостики букв, и
    /// среднее от них уезжает в серый, а медиана их просто игнорирует.
    /// </summary>
    /// <summary>
    /// Пиксели кадра как BGRA8. Через DataReader, а не указатели: копия разовая,
    /// зато не приходится включать unsafe во всём проекте ради тридцати строк.
    /// </summary>
    static byte[]? ReadPixels(SoftwareBitmap bmp)
    {
        try
        {
            var buf = new Windows.Storage.Streams.Buffer(
                (uint)(bmp.PixelWidth * bmp.PixelHeight * 4));
            bmp.CopyToBuffer(buf);
            var px = new byte[buf.Length];
            Windows.Storage.Streams.DataReader.FromBuffer(buf).ReadBytes(px);
            return px;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Распознать каждое найденное облако отдельно и слить с уже найденным.
    /// Слияние обычное — побеждает текст, в котором больше букв, — поэтому
    /// проход не может ухудшить страницу: он только добавляет и уточняет.
    /// </summary>
    static async Task<List<TextBox2>> BubblePassAsync(
        OcrEngine engine, SoftwareBitmap bmp, List<TextBox2> boxes, CancellationToken ct)
    {
        var px = ReadPixels(bmp);
        if (px is null) return boxes;

        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var found = await Task.Run(() => Bubbles.Detect(px, w, h), ct);
        if (found.Count == 0) return boxes;

        var extra = new List<TextBox2>();
        foreach (var f in found)
        {
            // Сам пузырь пропускаем: нас интересуют рамки ТЕКСТА, иначе в
            // вырезку попадёт контур облака и движок примет его за буквы
            if (f.Label == Bubbles.Bubble) continue;
            ct.ThrowIfCancellationRequested();

            var pad = Math.Max(3, f.H * 0.12);
            var x0 = (int)Math.Round(Math.Max(0, f.X - pad));
            var y0 = (int)Math.Round(Math.Max(0, f.Y - pad));
            var x1 = (int)Math.Round(Math.Min(w, f.X + f.W + pad));
            var y1 = (int)Math.Round(Math.Min(h, f.Y + f.H + pad));
            if (x1 - x0 < 8 || y1 - y0 < 8) continue;

            var scale = Bubbles.CropScale(x1 - x0, y1 - y0);
            using var crop = CropScaled(px, w, h, x0, y0, x1 - x0, y1 - y0, scale);
            var res = await engine.RecognizeAsync(crop);
            foreach (var b in Group(res, crop.PixelWidth, crop.PixelHeight, out _))
            {
                var (nx, ny, nw, nh) = Bubbles.ToPage(b.X, b.Y, b.W, b.H, x0, y0, scale);
                b.X = nx; b.Y = ny; b.W = nw; b.H = nh;
                extra.Add(b);
            }
        }

        return extra.Count == 0 ? boxes : Merge(boxes, extra);
    }

    /// <summary>Вырезка из кадра с увеличением, билинейно. BGRA8 на входе и выходе.</summary>
    static SoftwareBitmap CropScaled(byte[] px, int w, int h,
                                     int x0, int y0, int cw, int chh, double scale)
    {
        var dw = Math.Max(1, (int)Math.Round(cw * scale));
        var dh = Math.Max(1, (int)Math.Round(chh * scale));
        var dst = new byte[dw * dh * 4];

        for (var y = 0; y < dh; y++)
        {
            var sy = Math.Clamp(y0 + (y + 0.5) / scale - 0.5, 0, h - 1);
            int sy0 = (int)sy, sy1 = Math.Min((int)sy + 1, h - 1);
            var ky = sy - sy0;

            for (var x = 0; x < dw; x++)
            {
                var sx = Math.Clamp(x0 + (x + 0.5) / scale - 0.5, 0, w - 1);
                int sx0 = (int)sx, sx1 = Math.Min((int)sx + 1, w - 1);
                var kx = sx - sx0;

                var o00 = (sy0 * w + sx0) * 4;
                var o01 = (sy0 * w + sx1) * 4;
                var o10 = (sy1 * w + sx0) * 4;
                var o11 = (sy1 * w + sx1) * 4;
                var d = (y * dw + x) * 4;

                for (var c = 0; c < 4; c++)
                {
                    var top = px[o00 + c] * (1 - kx) + px[o01 + c] * kx;
                    var bot = px[o10 + c] * (1 - kx) + px[o11 + c] * kx;
                    dst[d + c] = (byte)Math.Clamp(top * (1 - ky) + bot * ky, 0, 255);
                }
            }
        }

        return SoftwareBitmap.CreateCopyFromBuffer(
            dst.AsBuffer(), BitmapPixelFormat.Bgra8, dw, dh);
    }

    static void SampleBackgrounds(SoftwareBitmap bmp, List<TextBox2> boxes)
    {
        if (boxes.Count == 0) return;
        var px = ReadPixels(bmp);
        if (px is null) return;     // не прочитали пиксели — остаётся белая подложка
        SampleBackgrounds(px, bmp.PixelWidth, bmp.PixelHeight, boxes);
    }

    /// <summary>
    /// Цвет подложки под каждой рамкой. Отдельно от SoftwareBitmap: второй
    /// распознаватель работает со своим кадром, а белый прямоугольник в цветном
    /// пузыре выдаёт перевод с головой.
    /// </summary>
    /// <param name="ring">
    /// Насколько отступить от рамки, в долях её высоты. Ровно та величина, где
    /// уже не буквы, но ещё пузырь: возьмёшь больше — заберёшь небо снаружи,
    /// меньше — сам текст. Разным распознавателям нужна разная: PP-OCRv5 жмёт
    /// рамку вплотную к глифам, Windows оставляет запас.
    /// </param>
    internal static void SampleBackgrounds(byte[] px, int w, int h, List<TextBox2> boxes,
                                           double ring = 0.35)
    {
        if (boxes.Count == 0) return;

        var rs = new List<byte>(128);
        var gs = new List<byte>(128);
        var bs = new List<byte>(128);

        foreach (var b in boxes)
        {
            rs.Clear(); gs.Clear(); bs.Clear();

            var pad = Math.Max(4, b.H * ring);
            var x0 = (int)Math.Round(b.X - pad);
            var y0 = (int)Math.Round(b.Y - pad);
            var x1 = (int)Math.Round(b.Right + pad);
            var y1 = (int)Math.Round(b.Bottom + pad);

            void Take(int x, int y)
            {
                if (x < 0 || y < 0 || x >= w || y >= h) return;
                var o = (y * w + x) * 4;        // BGRA8
                bs.Add(px[o]); gs.Add(px[o + 1]); rs.Add(px[o + 2]);
            }

            var stepX = Math.Max(1, (x1 - x0) / 24);
            var stepY = Math.Max(1, (y1 - y0) / 24);
            for (var x = x0; x <= x1; x += stepX) { Take(x, y0); Take(x, y1); }
            for (var y = y0; y <= y1; y += stepY) { Take(x0, y); Take(x1, y); }

            if (rs.Count < 4) continue;
            rs.Sort(); gs.Sort(); bs.Sort();
            var m = rs.Count / 2;
            b.BgR = rs[m]; b.BgG = gs[m]; b.BgB = bs[m];
        }
    }

    /// <summary>Отдельно стоящая «i»: именно она и есть местоимение.</summary>
    static readonly System.Text.RegularExpressions.Regex LoneI =
        new(@"\bi\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Отдельно стоящая единица — почти всегда неузнанное «I».</summary>
    static readonly System.Text.RegularExpressions.Regex LoneOne =
        new(@"\b1\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Любая другая цифра: признак того, что число тут настоящее.</summary>
    static readonly System.Text.RegularExpressions.Regex OtherDigit =
        new(@"[02-9]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Комиксы набирают капслоком, и распознанное «WHY AM I ALWAYS» переводчику
    /// лучше отдавать обычным регистром: модель обучена на нормальном тексте и
    /// на сплошных прописных заметно теряет. Обратно в капс текст вернётся уже
    /// при отрисовке.
    /// </summary>
    internal static string Clean(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ");
        if (s.Length < 2) return s;

        // «I» → «1» — самая частая ошибка распознавания комиксного набора:
        // измерено, она встретилась во ВСЕХ шести трудных случаях. В репликах
        // одинокая единица почти не бывает числом, но если в строке есть другие
        // цифры, число скорее настоящее — тогда не трогаем.
        if (!OtherDigit.IsMatch(s)) s = LoneOne.Replace(s, "I");

        var letters = s.Count(char.IsLetter);
        var upper = s.Count(char.IsUpper);
        if (letters > 0 && upper >= letters * 0.8)
        {
            s = char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

            // «I» — единственное английское слово, которое обязано остаться
            // прописным. После общего приведения оно становится «i», и модель
            // начинает путать его с артиклем
            s = LoneI.Replace(s, "I");
        }

        return s;
    }
}
