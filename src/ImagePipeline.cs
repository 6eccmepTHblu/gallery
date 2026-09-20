using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace Gallery;

/// <summary>
/// Готовый к показу кадр. Кэшируется именно <see cref="SoftwareBitmapSource"/>, а не
/// <see cref="SoftwareBitmap"/>: SetBitmapAsync копирует 17–20 МБ в композиционную
/// поверхность, и если платить эту цену на каждом переключении, попадание в кэш стоит
/// 15–30 мс вместо нуля. Отклонение от §10.1 осознанное — там речь шла о варианте
/// вообще без кэша.
/// </summary>
sealed class Ready
{
    public required SoftwareBitmapSource Source;

    /// <summary>Размеры оригинала после EXIF-поворота — по ним считается «вписать» и 1:1.</summary>
    public uint SrcW, SrcH;

    /// <summary>Реальные пиксели декодированного кадра: глубже них зум мылит.</summary>
    public uint BmpW, BmpH;
    public bool Rotated;
    public long Bytes;
    public int Index;

    /// <summary>
    /// Освобождаем ТОЛЬКО источник. SetBitmapAsync забирает владение SoftwareBitmap:
    /// явный Dispose битмапа после него — двойное освобождение, и XAML позже падает
    /// с RO_E_CLOSED (поймано на вехе 3, крэш 0xC000027B в Microsoft.UI.Xaml.dll).
    /// </summary>
    public void Dispose()
    {
        try { Source.Dispose(); } catch { }
    }
}

/// <summary>
/// Горячий путь (SPEC.md §6.5, §6.6). Три сущности, не пять:
/// одна SemaphoreSlim, один CancellationTokenSource на навигацию, дебаунс в UI.
/// Epoch, резервация слота P0 и приоритетный канал выкинуты как дублирующие.
/// </summary>
sealed class ImagePipeline
{
    readonly record struct Key(string Path, DateTime MTime, uint Tier);

    const long Budget = 384L * 1024 * 1024;   // §6.5: константой, не 20% RAM
    const int Pinned = 3;                     // окно ±3 не вытесняется никогда
    const int Ahead = 3, Behind = 2;          // асимметричный префетч по направлению

    // Квантованные тиры: без этого любой пиксель ресайза выбрасывает весь кэш (§6.4)
    static readonly uint[] Tiers = { 1024, 1600, 2048, 2560, 3200, 4096 };

    readonly Dictionary<Key, Ready> _cache = new();
    readonly Dictionary<Key, Task<Ready?>> _inflight = new();
    readonly SemaphoreSlim _gate = new(4);
    readonly DispatcherQueue _ui;

    FolderList _list = new();
    long _bytes;

    public int Current { get; private set; }
    public int Direction { get; private set; } = 1;
    public uint Tier { get; private set; } = 2560;

    // Счётчики для оверлея F12 (§6.9) — пять, не двадцать
    public long HitCount, MissCount, DecodeStarted, DecodeCancelled;
    public double LastHitMs, LastMissMs;
    public string LastSource = "—";

    public ImagePipeline(DispatcherQueue ui) => _ui = ui;

    public void SetList(FolderList list)
    {
        _list = list;
        Clear();
    }

    public static uint QuantizeTier(double viewportLongSide)
    {
        foreach (var t in Tiers)
            if (t >= viewportLongSide) return t;
        return Tiers[^1];
    }

    /// <summary>Смена тира выбрасывает кэш: старые кадры отрисованы под другой размер.</summary>
    public bool SetTier(uint tier)
    {
        if (tier == Tier) return false;
        Tier = tier;
        Clear();
        return true;
    }

    public void Clear()
    {
        foreach (var r in _cache.Values) Release(r);
        _cache.Clear();
        _bytes = 0;
    }

    Key KeyOf(int i)
    {
        var e = _list[i];
        return new Key(e.Path, e.MTime, Tier);
    }

    /// <summary>Синхронная проверка кэша — горячий путь, никакого await.</summary>
    public Ready? Peek(int index)
    {
        if (index < 0 || index >= _list.Count) return null;
        return _cache.TryGetValue(KeyOf(index), out var r) ? r : null;
    }

    public void Track(int index)
    {
        if (index != Current) Direction = index > Current ? 1 : -1;
        Current = index;
    }

    /// <summary>
    /// Декод с дедупликацией: два запроса на один файл делят одну задачу.
    ///
    /// Токен заказчика гасит ТОЛЬКО его ожидание, но не сам декод. Иначе второй
    /// заказчик получает задачу, привязанную к токену первого, и отмена навигации
    /// выбрасывает уже готовый кадр — работа сделана, а в кэш ничего не попало.
    /// </summary>
    public Task<Ready?> GetAsync(int index, CancellationToken ct)
    {
        if (index < 0 || index >= _list.Count) return Task.FromResult<Ready?>(null);

        var key = KeyOf(index);
        if (_cache.TryGetValue(key, out var hit)) return Task.FromResult<Ready?>(hit);

        if (!_inflight.TryGetValue(key, out var running))
        {
            running = DecodeAsync(index, key);
            _inflight[key] = running;
        }
        return Await(running, ct);
    }

    async Task<Ready?> Await(Task<Ready?> task, CancellationToken ct)
    {
        try
        {
            return await task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Заказчик ушёл, декод продолжается и попадёт в кэш. Счётчик считает
            // именно брошенные ожидания — иначе оверлей F12 врал бы нулём.
            Interlocked.Increment(ref DecodeCancelled);
            throw;
        }
    }

    async Task<Ready?> DecodeAsync(int index, Key key)
    {
        // Без токена: конкуренцию ограничивает семафор, а начатый декод доводим
        // до конца и кладём в кэш — соседний кадр почти наверняка пригодится.
        await _gate.WaitAsync();

        Interlocked.Increment(ref DecodeStarted);
        try
        {
            var frame = await Task.Run(
                () => Decoder.DecodeAsync(key.Path, Tier, CancellationToken.None));

            var landscapeSrc = frame.SourceWidth > frame.SourceHeight;
            var rotated = frame.Bitmap.PixelWidth > frame.Bitmap.PixelHeight != landscapeSrc;
            var bytes = (long)frame.Bitmap.PixelWidth * frame.Bitmap.PixelHeight * 4;

            // SetBitmapAsync обязан идти на UI-потоке. До успешного вызова
            // владение битмапом наше, после — источника (см. Ready.Dispose).
            SoftwareBitmapSource src;
            try
            {
                src = await OnUiAsync(async () =>
                {
                    var s = new SoftwareBitmapSource();
                    await s.SetBitmapAsync(frame.Bitmap);
                    return s;
                });
            }
            catch
            {
                frame.Bitmap.Dispose();
                throw;
            }

            var ready = new Ready
            {
                Source = src,
                SrcW = rotated ? frame.SourceHeight : frame.SourceWidth,
                SrcH = rotated ? frame.SourceWidth : frame.SourceHeight,
                BmpW = (uint)frame.Bitmap.PixelWidth,
                BmpH = (uint)frame.Bitmap.PixelHeight,
                Rotated = rotated,
                // Замерено: кадр стоит вдвое больше декодированных байт — сам битмап
                // (во владении источника) плюс композиционная поверхность. Считать
                // только пиксели значит промахнуться по памяти вдвое.
                Bytes = bytes * 2,
                Index = index,
            };

            LastFrameBytes = ready.Bytes;
            _cache[key] = ready;
            _bytes += ready.Bytes;
            Evict();
            return ready;
        }
        finally
        {
            _inflight.Remove(key);
            _gate.Release();
        }
    }

    /// <summary>
    /// Вытеснение по расстоянию от текущего индекса, а не LRU: при развороте
    /// направления LRU выбрасывает ровно те кадры, к которым пользователь сейчас
    /// пойдёт назад (§6.5).
    /// </summary>
    void Evict()
    {
        while (_bytes > Budget && _cache.Count > 1)
        {
            Key? worstKey = null;
            var worst = -1;
            foreach (var (k, v) in _cache)
            {
                var d = Math.Abs(v.Index - Current);
                if (d <= Pinned) continue;          // окно ±3 закреплено
                if (d <= worst) continue;
                worst = d; worstKey = k;
            }
            if (worstKey is null)
            {
                // Всё в закреплённом окне. Если мы при этом вдвое за бюджетом —
                // кадры оказались крупнее, чем предполагалось, и держать ±3 нельзя:
                // ужимаем закрепление до ±1, иначе память уедет за потолок.
                if (_bytes <= Budget * 2) break;
                foreach (var (k, v) in _cache)
                    if (Math.Abs(v.Index - Current) > 1) { worstKey = k; break; }
                if (worstKey is null) break;
            }

            var victim = _cache[worstKey.Value];
            _bytes -= victim.Bytes;
            _cache.Remove(worstKey.Value);
            Release(victim);
        }
    }

    /// <summary>Освобождение WinRT-объектов только на UI-потоке.</summary>
    void Release(Ready r)
    {
        if (_ui.HasThreadAccess) r.Dispose();
        else _ui.TryEnqueue(r.Dispose);
    }

    /// <summary>Асимметричное окно, зеркалится при развороте направления.</summary>
    public IEnumerable<int> PrefetchOrder(int center)
    {
        var fwd = Direction >= 0;
        var ahead = fwd ? Ahead : Behind;
        var behind = fwd ? Behind : Ahead;

        for (int d = 1; d <= Math.Max(ahead, behind); d++)
        {
            if (d <= ahead) yield return fwd ? center + d : center - d;
            if (d <= behind) yield return fwd ? center - d : center + d;
        }
    }

    public async Task PrefetchAsync(int center, CancellationToken ct)
    {
        foreach (var i in PrefetchOrder(center))
        {
            if (ct.IsCancellationRequested) return;
            if (i < 0 || i >= _list.Count) continue;
            if (_cache.ContainsKey(KeyOf(i))) continue;
            try { await GetAsync(i, ct); }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* битый сосед не должен ломать префетч */ }
        }
    }

    public long CacheBytes => _bytes;
    public int CacheCount => _cache.Count;
    public long LastFrameBytes { get; private set; }

    Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        if (_ui.HasThreadAccess) return work();
        var tcs = new TaskCompletionSource<T>();
        _ui.TryEnqueue(async () =>
        {
            try { tcs.SetResult(await work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
