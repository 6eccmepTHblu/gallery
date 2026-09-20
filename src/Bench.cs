using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Gallery;

/// <summary>
/// Веха 1: замер трёх путей декодирования (SPEC.md §6.3).
/// Запуск: gallery.exe --bench-decode &lt;папка&gt;
/// Отчёт: %LOCALAPPDATA%\Gallery\bench-decode.md
///
/// BitmapImage требует XAML-потока, поэтому бенч живёт внутри приложения,
/// а не в отдельной консольной утилите.
/// </summary>
static class Bench
{
    const int Runs = 5;
    const uint Target = 2560;   // типичная ширина вьюпорта × DPI

    public static async Task<string> RunDecodeAsync(string folder)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Замер декодирования — веха 1");
        sb.AppendLine();
        sb.AppendLine($"Цель: длинная сторона ≥ {Target} px. Лучшее из {Runs} прогонов, мс.");
        sb.AppendLine();
        sb.AppendLine("| Файл | МП | A: SoftwareBitmap<br>+EXIF +ICC | A2: то же<br>без ICC | B: BitmapImage<br>DecodePixelWidth | Миниатюра | Превью |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");

        var files = Directory.EnumerateFiles(folder)
            .Where(f => new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => new FileInfo(f).Length > 500_000)   // мелочь неинтересна
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        foreach (var path in files)
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(path); }
            catch (Exception ex) { sb.AppendLine($"| {Path.GetFileName(path)} | — | ошибка чтения: {ex.GetType().Name} ||||| "); continue; }

            uint w = 0, h = 0;
            try
            {
                using var s0 = await ToStreamAsync(bytes);
                var d0 = await BitmapDecoder.CreateAsync(s0);
                w = d0.PixelWidth; h = d0.PixelHeight;
            }
            catch (Exception ex)
            {
                sb.AppendLine($"| {Path.GetFileName(path)} | — | не декодируется: `0x{ex.HResult:X8}` ||||| ");
                continue;
            }

            var mp = w * h / 1_000_000.0;
            var a = await BestAsync(() => PathA(bytes, true));
            var a2 = await BestAsync(() => PathA(bytes, false));
            var b = await BestAsync(() => PathB(bytes));
            var th = await BestAsync(() => ThumbOrPreview(bytes, preview: false));
            var pv = await BestAsync(() => ThumbOrPreview(bytes, preview: true));

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "| `{0}` | {1:F1} | {2} | {3} | {4} | {5} | {6} |",
                Path.GetFileName(path), mp, Fmt(a), Fmt(a2), Fmt(b), Fmt(th), Fmt(pv)));
        }

        sb.AppendLine();
        sb.AppendLine("Правило масштаба: Decoder.PickScale — ровно в целевой размер, без апскейла.");
        return sb.ToString();
    }

    static string Fmt(double ms) => ms < 0 ? "—" : ms.ToString("F1", CultureInfo.InvariantCulture);

    static async Task<double> BestAsync(Func<Task<bool>> action)
    {
        var best = double.MaxValue;
        for (int i = 0; i < Runs; i++)
        {
            var sw = Stopwatch.StartNew();
            bool ok;
            try { ok = await action(); }
            catch { return -1; }
            sw.Stop();
            if (!ok) return -1;
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    static async Task<IRandomAccessStream> ToStreamAsync(byte[] bytes)
    {
        var ms = new InMemoryRandomAccessStream();
        await ms.WriteAsync(bytes.AsBuffer());
        ms.Seek(0);
        return ms;
    }

    // A — единственный путь, где EXIF-ориентация и ICC даются одним вызовом
    static async Task<bool> PathA(byte[] bytes, bool colorManage)
    {
        using var s = await ToStreamAsync(bytes);
        var dec = await BitmapDecoder.CreateAsync(s);
        var (tw, th) = Decoder.PickScale(dec.PixelWidth, dec.PixelHeight, Target);
        var tr = new BitmapTransform { ScaledWidth = tw, ScaledHeight = th, InterpolationMode = BitmapInterpolationMode.Fant };
        using var bmp = await dec.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tr,
            ExifOrientationMode.RespectExifOrientation,
            colorManage ? ColorManagementMode.ColorManageToSRgb : ColorManagementMode.DoNotColorManage);
        return bmp.PixelWidth > 0;
    }

    // B — быстрый путь через IWICBitmapSourceTransform, но без EXIF и ICC
    static async Task<bool> PathB(byte[] bytes)
    {
        using var s = await ToStreamAsync(bytes);
        var dec = await BitmapDecoder.CreateAsync(s);
        var (tw, _) = Decoder.PickScale(dec.PixelWidth, dec.PixelHeight, Target);
        s.Seek(0);

        var bi = new BitmapImage { DecodePixelType = DecodePixelType.Physical, DecodePixelWidth = (int)tw };
        var tcs = new TaskCompletionSource<bool>();
        void Ok(object? _, RoutedEventArgs __) => tcs.TrySetResult(true);
        void Fail(object? _, ExceptionRoutedEventArgs __) => tcs.TrySetResult(false);
        bi.ImageOpened += Ok; bi.ImageFailed += Fail;
        try
        {
            await bi.SetSourceAsync(s);
            return await tcs.Task;
        }
        finally { bi.ImageOpened -= Ok; bi.ImageFailed -= Fail; }
    }

    // Встроенная миниатюра / превью — кандидат в мгновенный плейсхолдер (§6.4)
    static async Task<bool> ThumbOrPreview(byte[] bytes, bool preview)
    {
        using var s = await ToStreamAsync(bytes);
        var dec = await BitmapDecoder.CreateAsync(s);
        // возвращают ImageStream (сжатые байты), а не декодированный кадр
        using var f = preview ? await dec.GetPreviewAsync() : await dec.GetThumbnailAsync();
        return f is not null && f.Size > 0;
    }
}
