using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Gallery;

/// <summary>
/// Единственный вход в WIC. Путь A, выбранный замером на вехе 1 (SPEC.md §6.3.1):
/// EXIF-ориентация и ICC→sRGB достаются одним вызовом и стоят ~1 мс.
/// </summary>
static class Decoder
{
    /// <summary>Результат декодирования плюс исходные размеры для строки статуса.</summary>
    public readonly record struct Frame(SoftwareBitmap Bitmap, uint SourceWidth, uint SourceHeight);

    /// <summary>
    /// Декодируем ровно в целевой размер, никогда не увеличивая.
    ///
    /// Правило степени двойки (1/2, 1/4, 1/8) действительно быстрее — замерено, 5–21%.
    /// Но оно ломается на границе тира: исходник 4000 px при цели 2048 даёт 1/2 = 2000,
    /// что ниже цели, поэтому деление не применяется вообще и кадр декодируется в
    /// нативном разрешении. Замерено на вехе 3: **91 МБ на кадр вместо 25**.
    /// Вчетверо памяти ради 5% времени, которые всё равно скрыты префетчем, — плохой
    /// размен. Точный размер стоит одного лишнего ресэмплинга внутри WIC.
    /// </summary>
    public static (uint w, uint h) PickScale(uint sw, uint sh, uint target,
                                            bool allowUpscale = false)
    {
        if (sw == 0 || sh == 0 || target == 0) return (Math.Max(sw, 1u), Math.Max(sh, 1u));

        var longSide = Math.Max(sw, sh);

        // Для показа увеличивать нельзя — получится мыло вместо кадра. Для
        // распознавания наоборот: измерено, что на увеличенной вдвое странице
        // возвращаются слова, которых движок на натуральной не видел вовсе (§4.9).
        if (longSide <= target && !allowUpscale) return (sw, sh);

        var w = (uint)((long)sw * target / longSide);
        var h = (uint)((long)sh * target / longSide);
        return (Math.Max(1u, w), Math.Max(1u, h));
    }

    public static async Task<Frame> DecodeAsync(string path, uint targetLongSide,
                                               CancellationToken ct,
                                               bool allowUpscale = false)
    {
        // Файл читаем целиком в память: декодер, читающий мелкими порциями,
        // на SMB-шаре даёт 400 мс вместо 40 (§6.3).
        // FileShare.ReadWrite | Delete — иначе не открыть файл, который кто-то пишет,
        // и пользователь не сможет удалить показанный файл (§8.6).
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete, 1, useAsync: true))
        {
            bytes = new byte[fs.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = await fs.ReadAsync(bytes.AsMemory(read), ct);
                if (n == 0) break;
                read += n;
            }
            if (read != bytes.Length) Array.Resize(ref bytes, read);
        }
        ct.ThrowIfCancellationRequested();

        using var ms = new InMemoryRandomAccessStream();
        await ms.WriteAsync(bytes.AsBuffer());
        ms.Seek(0);

        var dec = await BitmapDecoder.CreateAsync(ms);
        ct.ThrowIfCancellationRequested();

        var (tw, th) = PickScale(dec.PixelWidth, dec.PixelHeight, targetLongSide, allowUpscale);
        var transform = new BitmapTransform
        {
            ScaledWidth = tw,
            ScaledHeight = th,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        var bmp = await dec.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);

        return new Frame(bmp, dec.PixelWidth, dec.PixelHeight);
    }

    /// <summary>
    /// Встроенная миниатюра: 1,1–2,4 мс против ~150 мс полного декода (§6.4).
    /// Есть не у всех файлов — возвращает null, и это нормальный путь, а не ошибка.
    /// </summary>
    public static async Task<SoftwareBitmap?> ThumbnailAsync(string path, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, useAsync: true);
            var bytes = new byte[Math.Min(fs.Length, 512 * 1024)];   // миниатюра живёт в начале файла
            var read = await fs.ReadAsync(bytes, ct);

            using var ms = new InMemoryRandomAccessStream();
            await ms.WriteAsync(bytes.AsBuffer(0, read));
            ms.Seek(0);

            var dec = await BitmapDecoder.CreateAsync(ms);
            using var thumb = await dec.GetThumbnailAsync();
            if (thumb is null || thumb.Size == 0) return null;

            var tdec = await BitmapDecoder.CreateAsync(thumb);
            return await tdec.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
        catch (Exception)
        {
            return null;   // нет миниатюры — не ошибка
        }
    }
}
