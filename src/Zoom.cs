using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Gallery;

/// <summary>
/// Веха 2: зум и панорамирование (SPEC.md §4.7).
///
/// Содержимое ScrollView всегда имеет размер «вписать», поэтому ZoomFactor == 1
/// это ровно «вписать», а 1:1 — это SrcW/fitW. Такая привязка избавляет от
/// отдельного состояния режима: режим выводится из одного числа.
/// </summary>
public sealed partial class MainWindow
{
    const float ZoomStep = 1.25f;          // геометрический шаг на щелчок колеса
    const double NativeMax = 32.0;         // 3200% от оригинала
    const double NativeMin = 0.05;         // 5% от оригинала

    double _fitW, _fitH;                   // размер содержимого при ZoomFactor == 1
    uint _srcW, _srcH, _bmpW;              // оригинал (после поворота) и текущий декод

    // Панорамирование левой кнопкой
    bool _panning;
    Point _panStart;
    double _panOffsetX, _panOffsetY;

    // Полноразмерный кадр по требованию: живёт в одном экземпляре и умирает
    // при смене кадра (§6.4)
    SoftwareBitmapSource? _full;
    CancellationTokenSource? _fullCts;
    readonly DispatcherTimer _fullTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    void InitZoom()
    {
        // ScrollView потребляет колесо на уровне InteractionTracker — ДО
        // маршрутизации XAML, поэтому handledEventsToo тут бессилен. Единственная
        // рабочая ручка — сказать ему не трогать колесо вовсе. Цена: Ctrl+колесо
        // тоже перестаёт работать само, и зум к курсору мы делаем вручную (5 строк).
        Zoom.IgnoredInputKinds = ScrollingInputKinds.MouseWheel;

        Zoom.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnWheel), handledEventsToo: true);
        Zoom.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnPointerPressed), handledEventsToo: true);
        Zoom.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
        Zoom.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnPointerReleased), handledEventsToo: true);
        Zoom.PointerCaptureLost += (_, __) => EndPan();
        Zoom.DoubleTapped += OnDoubleTapped;
        Zoom.ViewChanged += (_, __) => { ShowZoomPercent(); ScheduleFullDecode(); UpdateOverlay(); };
        _fullTimer.Tick += OnFullTimer;
    }

    /// <summary>
    /// Убрать кадр совсем. Одного Img.Source = null мало: ImgHost сохраняет
    /// размеры и заливку, и за текстом ошибки остаётся серый прямоугольник.
    /// </summary>
    void ClearFrame()
    {
        Img.Source = null;
        Img.Opacity = 1.0;
        ImgHost.Visibility = Visibility.Collapsed;
        ZoomPct.Text = "";
    }

    /// <summary>Пересчёт «вписать» под текущий кадр и вьюпорт. Не увеличивает мелкое.</summary>
    void LayoutFrame(uint srcW, uint srcH, uint bmpW, bool allowUpscale = false)
    {
        ImgHost.Visibility = Visibility.Visible;
        _srcW = Math.Max(1, srcW);
        _srcH = Math.Max(1, srcH);
        _bmpW = Math.Max(1, bmpW);

        var vw = Zoom.ActualWidth;
        var vh = Zoom.ActualHeight;
        if (vw < 1 || vh < 1) { vw = Math.Max(1, Root.ActualWidth); vh = Math.Max(1, Root.ActualHeight); }

        (_fitW, _fitH) = Fit(_srcW, _srcH, vw, vh, allowUpscale);

        ImgHost.Width = _fitW;
        ImgHost.Height = _fitH;

        // Слой перевода живёт в координатах «вписать» — при их пересчёте его
        // надо разложить заново, иначе он останется от прошлого размера окна
        if (_transOn && _transShown == _currentPath) RenderTranslate();
        if (_ocrOn && _ocrShown == _currentPath) RenderOcrLayer();

        // Диапазон считаем от оригинала: 5%…3200% независимо от того, как сильно вписали
        var nativeToFit = _srcW / _fitW;            // ZoomFactor, дающий 100%
        Zoom.MinZoomFactor = (float)Math.Min(1.0, NativeMin * nativeToFit);
        Zoom.MaxZoomFactor = (float)Math.Max(1.0, NativeMax * nativeToFit);

        ResetZoom();
    }

    /// <summary>
    /// «Вписать»: пропорционально, по более тесной стороне, без апскейла.
    /// Исключение — миниатюра-заглушка: она по определению меньше кадра, и
    /// показывать её в 320 px посреди экрана глупо.
    /// </summary>
    public static (double w, double h) Fit(
        uint srcW, uint srcH, double vw, double vh, bool allowUpscale = false)
    {
        if (srcW == 0 || srcH == 0 || vw < 1 || vh < 1) return (1, 1);

        var k = Math.Min(vw / srcW, vh / srcH);
        if (!allowUpscale) k = Math.Min(k, 1.0);

        return (Math.Max(1, Math.Round(srcW * k)), Math.Max(1, Math.Round(srcH * k)));
    }

    /// <summary>Смена кадра сбрасывает зум — иначе при сортировке пользователь теряется.</summary>
    void ResetZoom()
    {
        DropFull();
        Zoom.ZoomTo(1f, null, new ScrollingZoomOptions(
            ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
        ShowZoomPercent();
    }

    double NativePercent => _fitW < 1 ? 100 : Zoom.ZoomFactor * _fitW / _srcW * 100.0;

    void ShowZoomPercent()
    {
        var pct = NativePercent;
        // При «вписать» и 100% индикатор не нужен — он и так ни о чём не сообщает
        ZoomPct.Text = Math.Abs(Zoom.ZoomFactor - 1f) < 0.001f && pct < 99.5
            ? ""
            : $"{pct:F0}%";
    }

    void ZoomBy(float factor, Point? center)
    {
        var target = Math.Clamp(Zoom.ZoomFactor * factor, Zoom.MinZoomFactor, Zoom.MaxZoomFactor);
        Zoom.ZoomTo((float)target,
            center is null ? null : new Vector2((float)center.Value.X, (float)center.Value.Y),
            new ScrollingZoomOptions(ScrollingAnimationMode.Auto, ScrollingSnapPointsMode.Ignore));
    }

    // ---------- мышь ----------

    // Колесо — всегда навигация, никогда зум: контекстно-зависимое колесо ломает
    // предсказуемость (§4.6). Зум — Ctrl+колесо, и pinch тачпада приходит так же,
    // поэтому кода жестов писать не надо.
    void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Zoom);
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                   .HasFlag(CoreVirtualKeyStates.Down);
        e.Handled = true;

        if (ctrl)
        {
            // Зум к курсору: точка под указателем остаётся на месте.
            // Pinch тачпада приходит сюда же как Ctrl+колесо.
            var steps = p.Properties.MouseWheelDelta / 120.0;
            ZoomBy((float)Math.Pow(ZoomStep, steps), p.Position);
            return;
        }

        WheelStep(p.Properties.MouseWheelDelta);
    }

    /// <summary>
    /// Один шаг колеса. Вынесено из OnWheel, потому что лента миниатюр (§4.8)
    /// перехватывает указатель раньше холста и обязана листать так же.
    /// </summary>
    void WheelStep(int delta)
    {
        // Смена направления обнуляет остаток: иначе накопленное «вниз» съедает
        // первое движение «вверх». Math.Sign(0) == 0, поэтому первый шаг корректен.
        if (Math.Sign(delta) != Math.Sign(_wheelAccum)) _wheelAccum = 0;
        _wheelAccum += delta;
        // Дельты тачпада накапливаем до 120, иначе двухпальцевый скролл пролистывает пачку
        while (Math.Abs(_wheelAccum) >= 120)
        {
            var dir = Math.Sign(_wheelAccum);
            _wheelAccum -= dir * 120;
            Step(-dir);
        }
    }

    int _wheelAccum;

    // ВНИМАНИЕ: ExtentWidth/Height в ScrollView — это размер содержимого ДО зума.
    // Прокручиваемая область равна экстенту, умноженному на ZoomFactor. Без
    // умножения «панорамировать нечего» было истинно почти всегда, и перетаскивание
    // не начиналось вовсе.
    double ScrollableW => Math.Max(0, Zoom.ExtentWidth * Zoom.ZoomFactor - Zoom.ViewportWidth);
    double ScrollableH => Math.Max(0, Zoom.ExtentHeight * Zoom.ZoomFactor - Zoom.ViewportHeight);

    bool CanPan => ScrollableW > 1 || ScrollableH > 1;

    void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Zoom);
        if (!p.Properties.IsLeftButtonPressed) return;
        if (!CanPan) return;                    // изображение целиком в окне

        _panning = true;
        _panStart = p.Position;
        _panOffsetX = Zoom.HorizontalOffset;
        _panOffsetY = Zoom.VerticalOffset;
        Zoom.CapturePointer(e.Pointer);
        Zoom.SetCursor(InputSystemCursorShape.SizeAll);
    }

    void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning)
        {
            // Affordance: курсор показывает, что кадр можно тащить
            if (_chromeVisible)
                Zoom.SetCursor(CanPan ? InputSystemCursorShape.SizeAll
                                      : InputSystemCursorShape.Arrow);
            return;
        }
        var p = e.GetCurrentPoint(Zoom).Position;
        var x = Math.Clamp(_panOffsetX - (p.X - _panStart.X), 0, ScrollableW);
        var y = Math.Clamp(_panOffsetY - (p.Y - _panStart.Y), 0, ScrollableH);
        Zoom.ScrollTo(x, y,
            new ScrollingScrollOptions(ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
        e.Handled = true;
    }

    void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        Zoom.ReleasePointerCapture(e.Pointer);
        EndPan();
    }

    void EndPan()
    {
        if (!_panning) return;
        _panning = false;
        Zoom.SetCursor(InputSystemCursorShape.Arrow);
    }

    /// <summary>
    /// Двойной клик возвращает кадр целиком в окно — по той стороне, которая
    /// упирается первой. Это выход из любого зума одним движением; попасть в
    /// пиксель-в-пиксель можно клавишей A.
    /// </summary>
    void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ResetZoom();
        e.Handled = true;
    }

    void ZoomToNative(Point? at)
    {
        var target = (float)Math.Clamp(_srcW / _fitW, Zoom.MinZoomFactor, Zoom.MaxZoomFactor);
        _lastCmd = $"actual -> ZoomTo({target:F2})";
        Zoom.ZoomTo(target,
            at is null ? null : new Vector2((float)at.Value.X, (float)at.Value.Y),
            new ScrollingZoomOptions(ScrollingAnimationMode.Auto, ScrollingSnapPointsMode.Ignore));
    }

    // ---------- полноразмерный кадр по требованию ----------

    /// <summary>
    /// Кадр в кэше декодирован под тир. Когда зум уходит глубже его пикселей,
    /// показывать нечего кроме мыла — доводим до оригинала. Разово, с задержкой
    /// 150 мс после остановки жеста, чтобы не декодировать на каждом щелчке колеса.
    /// </summary>
    void ScheduleFullDecode()
    {
        if (_currentPath is null) return;
        var displayed = _fitW * Zoom.ZoomFactor;
        if (displayed <= _bmpW * 1.2 || _bmpW >= _srcW) return;   // хватает того, что есть
        _fullTimer.Stop();
        _fullTimer.Start();
    }

    async void OnFullTimer(object? sender, object e)
    {
        _fullTimer.Stop();
        var path = _currentPath;
        if (path is null || _full is not null) return;

        _fullCts?.Cancel();
        _fullCts = new CancellationTokenSource();
        var ct = _fullCts.Token;
        try
        {
            var frame = await Task.Run(() => Decoder.DecodeAsync(path, uint.MaxValue, ct), ct);
            if (ct.IsCancellationRequested || _currentPath != path)
            {
                frame.Bitmap.Dispose();     // владение ещё наше — иначе течём
                return;
            }

            var src = new SoftwareBitmapSource();
            await src.SetBitmapAsync(frame.Bitmap);
            if (ct.IsCancellationRequested || _currentPath != path) { src.Dispose(); return; }

            _full = src;
            Img.Source = src;            // размеры не меняем — визуального скачка нет
            _pipe.LastSource = "full";
            UpdateOverlay();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* не смогли — остаёмся на кадре из кэша */ }
    }

    void DropFull()
    {
        _fullTimer.Stop();
        _fullCts?.Cancel();
        if (_full is null) return;
        if (ReferenceEquals(Img.Source, _full)) return;   // ещё на экране
        try { _full.Dispose(); } catch { }
        _full = null;
    }

    // ---------- клавиатура ----------

    // Пока открыта чистка, F и A вписывают и приближают ЕЁ кадр, а не тот, что
    // за панелью: молча менять масштаб невидимого — худший вид сюрприза
    void OnFit(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (CleanPanel.Visibility == Visibility.Visible) return;
        _lastCmd = "fit"; ResetZoom(); UpdateOverlay();
    }

    void OnActual(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        if (CleanPanel.Visibility == Visibility.Visible) return;
        _lastCmd = "actual"; ZoomToNative(null); UpdateOverlay();
    }

    void OnZoomIn(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; _lastCmd = "zoom+"; ZoomBy(ZoomStep, null); UpdateOverlay(); }

    void OnZoomOut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    { e.Handled = true; _lastCmd = "zoom-"; ZoomBy(1f / ZoomStep, null); UpdateOverlay(); }

    // Esc слоями: сначала сброс зума, потом выход из полноэкранного, потом закрыть (§4.1)
    void OnEscape(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        _lastCmd = "esc";
        if (TryEscapeLayer()) { UpdateOverlay(); return; }
        RequestCloseAsync();     // тот же путь, что и Ctrl+W
    }

    void PanBy(double dx, double dy) =>
        Zoom.ScrollBy(dx * Zoom.ViewportWidth * 0.1, dy * Zoom.ViewportHeight * 0.1,
            new ScrollingScrollOptions(ScrollingAnimationMode.Auto, ScrollingSnapPointsMode.Ignore));

}
