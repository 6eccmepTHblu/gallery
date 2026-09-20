using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Gallery;

/// <summary>
/// Кодирование страницы для отправки модели (SPEC.md §4.13).
///
/// Размер здесь — не косметика, а деньги и время: поставщик считает картинку
/// плитками, и вдвое большая сторона стоит вчетверо. Полторы тысячи пикселей по
/// длинной стороне — потолок, за которым для чтения леттеринга уже ничего не
/// прибавляется, а счёт растёт.
/// </summary>
static class PageImage
{
    public const uint MaxSide = 1536;
    const uint JpegQuality = 78;

    public static long LastBytes { get; private set; }

    /// <summary>
    /// Страница в base64 JPEG. JPEG, а не PNG: страница комикса — это плоские
    /// заливки и контур, PNG на ней вдвое тяжелее без выигрыша для распознавания.
    /// </summary>
    public static async Task<string> EncodeAsync(string path, CancellationToken ct)
    {
        var frame = await Task.Run(() => Decoder.DecodeAsync(path, MaxSide, ct), ct);
        using var src = frame.Bitmap;

        // Кодировщику нужен Bgra8 без премультипликации — иначе бросает
        using var flat = src.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
                         src.BitmapAlphaMode == BitmapAlphaMode.Ignore
            ? SoftwareBitmap.Copy(src)
            : SoftwareBitmap.Convert(src, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);

        using var ms = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms,
            new[] { new KeyValuePair<string, BitmapTypedValue>(
                "ImageQuality", new BitmapTypedValue(JpegQuality / 100.0, Windows.Foundation.PropertyType.Single)) });

        enc.SetSoftwareBitmap(flat);
        await enc.FlushAsync();

        ct.ThrowIfCancellationRequested();

        var bytes = new byte[ms.Size];
        ms.Seek(0);
        await ms.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);

        LastBytes = bytes.Length;
        return Convert.ToBase64String(bytes);
    }
}
