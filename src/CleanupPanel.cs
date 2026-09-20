using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using Windows.Foundation;

namespace Gallery;

/// <summary>
/// Панель очистки от текста — F8 (SPEC.md §4.14).
///
/// Работа идёт ПО БЛОКАМ, а не только по странице целиком. Причина простая:
/// на странице из десяти надписей одна выходит плохо, и переделывать из-за неё
/// всё — значит терять девять удавшихся. Поэтому блок выбирается, размечается,
/// дорисовывается и откатывается сам по себе.
///
/// Всё, что делается, ложится в ОДИН стек отмены: и мазок кистью, и правка
/// рамок, и дорисовка блока. Два стека («отменить мазок» и «отменить блок»)
/// человеку пришлось бы держать в голове, а порядок действий он помнит и так.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Что можно откатить. Каждое поле — «если не null, восстановить».</summary>
    sealed class CleanStep
    {
        public string What = "";
        public byte[]? Mask;
        public List<SKRectI>? Boxes;
        public SKRectI Region;
        public SKColor[]? Pixels;       // кусок рабочего кадра до дорисовки
        public SKRectI? Clip;           // откатывать только здесь (выбранный блок)
    }

    SKBitmap? _clnPage;             // исходник, не меняется никогда
    SKBitmap? _clnWork;             // то, что видно и во что дорисовывается
    byte[]? _clnMask;               // 255 — стереть
    List<SKRectI> _clnBoxes = new();
    int _clnSel = -1;
    string? _clnPath;

    WriteableBitmap? _clnBase;
    WriteableBitmap? _clnOver;
    byte[]? _clnOverPx;

    CancellationTokenSource? _clnCts;
    readonly List<CleanStep> _clnUndo = new();
    readonly List<CleanStep> _clnRedo = new();

    bool _clnDragBox, _clnPainting, _clnPreview, _clnPanning, _clnDirty;

    /// <summary>Q держат ради колеса: что было в руке и крутили ли вообще.</summary>
    ToggleButton? _clnToolBeforeQ;
    bool _clnPadTurned;

    /// <summary>
    /// Разметка КАК ЕЁ ОТДАЛА МОДЕЛЬ, до запаса, и рамки, по которым её
    /// считали. Нужна, чтобы колесо меняло запас сразу: запас — это рост от
    /// найденных букв, а сами буквы от него не зависят, и гонять ради них
    /// модель заново незачем.
    ///
    /// Живёт ровно до следующей правки: любой мазок, откат, дорисовка или
    /// заливка её обнуляют. Пересчитывать поверх чужих правок нельзя — они бы
    /// молча пропали.
    /// </summary>
    byte[]? _clnRaw;
    List<SKRectI>? _clnRawBoxes;
    bool _clnRawBlock;

    /// <summary>
    /// Идёт фоновый шаг. Отдельным полем, а не только состоянием кнопок:
    /// UpdateBlockInfo зовётся из Push, то есть из КАЖДОГО мазка кистью, и
    /// заново включал кнопки прямо посреди счёта — два прохода по одному кадру
    /// заканчивались тем, что первый освобождал битмап под вторым.
    /// </summary>
    bool _clnBusy;

    /// <summary>Рамка, которую тянут за край, и схваченные стороны. −1 — не тянут.</summary>
    int _clnResize = -1;
    Cleanup.Side _clnGrip;
    List<SKRectI>? _clnResBefore;

    /// <summary>
    /// Рамка, какой мы её оставили последним движением. Сверяемся с ней перед
    /// каждой правкой: список рамок могли подменить из-под протяжки — шаг
    /// «найти текст», начатый ДО неё, доезжает и кладёт новый список.
    /// </summary>
    SKRectI _clnResLast;

    /// <summary>Меньше этого рамку не ужать: за край надо ещё суметь схватиться.</summary>
    const int MinBox = 8;
    double _clnCurX = -1, _clnCurY = -1;

    /// <summary>Фигура, которую ведут прямо сейчас, и её начало.</summary>
    bool _clnLineOn;
    int _clnLx0, _clnLy0, _clnLx1, _clnLy1;

    /// <summary>Цвет пера. Берётся пипеткой ИЗ КАДРА — подобранный на глаз
    /// всегда мимо: на сжатой картинке даже ровная заливка гуляет.</summary>
    SKColor _clnInk = SKColors.Black;

    /// <summary>Кадр до штриха карандашом: штрих копится, а в откат идёт один.</summary>
    SKColor[]? _clnPenPx;
    SKRectI _clnPenBox;

    /// <summary>Предыдущий щелчок левой — для распознавания двойного.</summary>
    ulong _clnClickAt;
    double _clnClickX, _clnClickY;

    /// <summary>
    /// Окно двойного щелчка берём у системы, а не зашиваем: человек мог
    /// растянуть его в настройках мыши, и своя константа отказывалась бы
    /// понимать его привычный темп.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetDoubleClickTime();

    static ulong DoubleClickMs()
    {
        try { var t = GetDoubleClickTime(); return t == 0 ? 500UL : t; }
        catch (Exception) { return 500UL; }
    }
    double _clnPanX, _clnPanY, _clnPanH, _clnPanV;
    string? _clnSaidBefore;
    int _clnAx, _clnAy, _clnBx, _clnBy;     // рамка, которую тянут
    int _clnLastX, _clnLastY;

    /// <summary>Глубина отмены. Снимок маски на кадр 2000x2000 — это 4 МБ.</summary>
    const int CleanUndoDepth = 12;

    void InitClean()
    {
        CleanClose.Click += (_, __) => ShowClean(false);
        CleanAll.Click += (_, __) => _ = RunCleanStepsAsync(3);
        CleanStep1.Click += (_, __) => _ = RunCleanStepsAsync(1);
        CleanStep2.Click += (_, __) => _ = RunCleanStepsAsync(2, only: true);
        CleanStep3.Click += (_, __) => _ = RunCleanStepsAsync(3, only: true);
        CleanReset.Click += (_, __) => ResetClean();
        CleanUndo.Click += (_, __) => UndoClean();
        CleanSave.Click += (_, __) => SaveClean();

        CleanBlockMark.Click += (_, __) => _ = MarkBlockAsync();
        CleanBlockErase.Click += (_, __) => _ = EraseBlockAsync();
        CleanSmooth.Click += (_, __) => _ = SmoothAsync();
        CleanBlockDrop.Click += (_, __) => DropBlock();
        CleanDropAll.Click += (_, __) => DropAllBlocks();
        CleanWipe.Click += (_, __) => WipeMask();

        CleanToolBox.Click += (_, __) => Tool(CleanToolBox);
        CleanBrush.Click += (_, __) => Tool(CleanBrush);
        CleanEraser.Click += (_, __) => Tool(CleanEraser);
        CleanPick.Click += (_, __) => Tool(CleanPick);
        CleanPencil.Click += (_, __) => Tool(CleanPencil);
        CleanLine.Click += (_, __) => Tool(CleanLine);
        CleanRect.Click += (_, __) => Tool(CleanRect);
        CleanOval.Click += (_, __) => Tool(CleanOval);

        _clnInk = new SKColor(SettingsStore.Current.PenColor);
        ShowInk();

        CleanFit.Click += (_, __) => ZoomFit();
        CleanActual.Click += (_, __) => CleanScroll.ChangeView(null, null, 1.0f);

        CleanSize.Value = ToolSize;
        CleanSize.ValueChanged += (_, __) =>
        {
            UpdateBrushLabel();
            UpdateCursor();
            ToolSize = (int)CleanSize.Value;
            SettingsStore.Touch();      // с дебаунсом: Ctrl+колесо шлёт события пачкой
        };

        CleanPad.Value = Math.Clamp(SettingsStore.Current.MaskPad,
                                    (int)CleanPad.Minimum, (int)CleanPad.Maximum);
        UpdatePadLabel();
        CleanPad.ValueChanged += (_, __) =>
        {
            SettingsStore.Current.MaskPad = (int)CleanPad.Value;
            UpdatePadLabel();
            SettingsStore.Touch();
            ApplyPad(live: true);
        };
        CleanScroll.ViewChanged += (_, __) => UpdateCursor();
        CleanStack.PointerExited += (_, __) => { _clnCurX = -1; UpdateCursor(); };

        // Не только на движение: клавишу инструмента жмут, когда мышь уже
        // стоит над кадром и никуда не едет — без этого круг не появлялся,
        // пока указатель не шевельнут
        CleanStack.PointerEntered += (_, e) =>
        {
            var p = e.GetCurrentPoint(CleanScroll).Position;
            UpdateCursor(p.X, p.Y);
        };
        UpdateBrushLabel();
        UpdateBlockInfo();

        // Клавиши ловим на КОРНЕ окна, а не на панели, и не акселераторами.
        //
        // Не акселераторами — потому что нужен не только момент нажатия, но и
        // ОТПУСКАНИЕ: предпросмотр живёт ровно пока держат клавишу.
        // Не на панели — потому что фокус при щелчке по кадру остаётся на
        // холсте ЗА ней, и до панели событие не доходит вовсе. Поймано тем,
        // что Delete не срабатывал ни разу.
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnCleanKeyDown), true);
        Root.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(OnCleanKeyUp), true);

        CleanStack.PointerWheelChanged += OnCleanWheel;
        CleanStack.PointerPressed += OnCleanPointerDown;
        CleanStack.PointerMoved += OnCleanPointerMove;
        CleanStack.PointerReleased += OnCleanPointerUp;
        CleanStack.PointerCaptureLost += (_, __) =>
        {
            _clnPainting = false; _clnDragBox = false; _clnPanning = false;
            _clnResize = -1; _clnResBefore = null; _clnLineOn = false; _clnPenPx = null;
        };
    }

    ToggleButton[] Tools => new[] { CleanToolBox, CleanBrush, CleanEraser,
                                    CleanPick, CleanPencil, CleanLine, CleanRect, CleanOval };

    /// <summary>Инструменты — переключатели, и включён ровно один.</summary>
    void Tool(ToggleButton on)
    {
        foreach (var t in Tools) t.IsChecked = ReferenceEquals(t, on);

        // Ползунок принадлежит тому, что сейчас в руке: у кисти, ластика и пера
        // размеры свои. Сначала флажки, потом значение — иначе запись ушла бы
        // в настройку прежнего инструмента
        CleanSize.Value = ToolSize;
        UpdateBrushLabel();             // значение могло не измениться, а подпись должна
        UpdateCursor();
    }

    bool PickTool => CleanPick.IsChecked == true;
    bool PencilTool => CleanPencil.IsChecked == true;
    bool RectTool => CleanRect.IsChecked == true;
    bool OvalTool => CleanOval.IsChecked == true;

    /// <summary>Инструменты, которые правят САМ КАДР, а не разметку.</summary>
    bool DrawTool => PencilTool || LineTool || RectTool || OvalTool;

    void ShowInk()
    {
        CleanInk.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(255, _clnInk.Red, _clnInk.Green, _clnInk.Blue));
        // Подпись поверх плашки — светлая на тёмном и наоборот, иначе на
        // чёрном пере надпись пропадает
        var lum = 0.299 * _clnInk.Red + 0.587 * _clnInk.Green + 0.114 * _clnInk.Blue;
        CleanInkText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            lum > 140 ? Windows.UI.Color.FromArgb(255, 20, 20, 20)
                      : Windows.UI.Color.FromArgb(255, 235, 235, 235));
        CleanInkText.Text = $"{_clnInk.Red} {_clnInk.Green} {_clnInk.Blue}";
    }

    /// <summary>
    /// Размер того инструмента, что сейчас в руке. «Рамка» не рисует, и ползунок
    /// при ней показывает кисть: следующий мазок будет ею.
    /// </summary>
    int ToolSize
    {
        get => Math.Clamp(DrawTool ? SettingsStore.Current.PenSize
                        : CleanEraser.IsChecked == true ? SettingsStore.Current.EraserSize
                        : SettingsStore.Current.BrushSize,
                          (int)CleanSize.Minimum, (int)CleanSize.Maximum);
        set
        {
            if (DrawTool) SettingsStore.Current.PenSize = value;
            else if (CleanEraser.IsChecked == true) SettingsStore.Current.EraserSize = value;
            else SettingsStore.Current.BrushSize = value;
        }
    }

    /// <summary>
    /// Круг под курсором — это будущий след кисти. Радиус задан в пикселях
    /// КАДРА, а на экране он зависит от увеличения: без круга размер мазка
    /// приходится угадывать и проверять на самой странице.
    /// </summary>
    void UpdateCursor(double? x = null, double? y = null)
    {
        if (x is not null) { _clnCurX = x.Value; _clnCurY = y!.Value; }

        var on = (CleanBrush.IsChecked == true || CleanEraser.IsChecked == true || DrawTool)
                 && _clnCurX >= 0 && CleanPanel.Visibility == Visibility.Visible;
        CleanCursor.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;

        // У кисти ползунок задаёт РАДИУС, у пера — ТОЛЩИНУ: круг должен
        // показывать след, а не число
        var d = CleanSize.Value * (DrawTool ? 1 : 2) * CleanScroll.ZoomFactor;
        CleanCursor.Width = d;
        CleanCursor.Height = d;
        CleanCursor.Margin = new Thickness(_clnCurX - d / 2, _clnCurY - d / 2, 0, 0);
    }

    bool BoxTool => CleanToolBox.IsChecked == true;
    bool LineTool => CleanLine.IsChecked == true;

    /// <summary>
    /// Полоса захвата края — в пикселях КАДРА, но постоянная на экране: на
    /// приближённой странице край иначе пришлось бы ловить по одному пикселю,
    /// а на отдалённой полоса накрыла бы всю рамку.
    /// </summary>
    int GripMargin() =>
        Math.Max(3, (int)Math.Round(9 / Math.Max(0.1, CleanScroll.ZoomFactor)));

    /// <summary>
    /// Запас звучит по-разному в нуле и не в нуле: «0 px» читается как
    /// «настройка выключена», и объяснять это надо там же, где она стоит.
    /// </summary>
    void UpdatePadLabel() =>
        CleanPadLabel.Text = CleanPad.Value < 1
            ? "Запас вокруг букв: нет (Q + колесо)"
            : $"Запас вокруг букв: {(int)CleanPad.Value} px (Q + колесо)";

    /// <summary>
    /// Разложить запомненную разметку с ТЕКУЩИМ запасом.
    ///
    /// live — пришло от колеса. Тогда пересчитываем, только если выбрано ровно
    /// то, что размечали последним: колесо правит эту разметку, а не какую
    /// придётся. Сразу после самой разметки выбор проверять нечего — там
    /// force.
    /// </summary>
    void ApplyPad(bool live = false)
    {
        var pad = (int)CleanPad.Value;
        var has = _clnSel >= 0 && _clnSel < _clnBoxes.Count;

        if (_clnRaw is null || _clnRawBoxes is null || _clnMask is null || _clnWork is null
            || (live && (_clnRawBlock != has
                         || (has && _clnBoxes[_clnSel] != _clnRawBoxes[0]))))
        {
            // Молча ничего не делать нельзя: человек крутит колесо и ждёт,
            // что разметка поедет. Говорим, почему не поехала
            if (live && _clnMask is not null)
                CleanStatus.Text = $"Запас {pad} px — ляжет при следующей разметке: "
                                 + "поверх уже правленой его не пересчитать.";
            return;
        }
        var w = _clnWork.Width;

        foreach (var b in _clnRawBoxes)
        {
            var bw = b.Width;
            var bh = b.Height;
            if (bw < 1 || bh < 1) continue;

            // Кусок растёт ВНУТРИ своей рамки — как и при самой разметке,
            // иначе запас склеил бы соседние надписи
            var part = new byte[bw * bh];
            for (var y = 0; y < bh; y++)
                for (var x = 0; x < bw; x++)
                    part[y * bw + x] = _clnRaw[(b.Top + y) * w + b.Left + x];

            TextMask.Grow(part, bw, bh, pad);

            for (var y = 0; y < bh; y++)
            {
                var row = (b.Top + y) * w + b.Left;
                for (var x = 0; x < bw; x++)
                    _clnMask[row + x] = part[y * bw + x] != 0 ? (byte)255 : (byte)0;
            }
        }

        RedrawOverlay();
        UpdateBlockInfo();
        if (live)
            CleanStatus.Text = pad < 1
                ? "Запас убран — разметка как у модели."
                : $"Запас {pad} px, разметка пересчитана. {Lit()} px под стирание.";
    }

    void UpdateBrushLabel() =>
        CleanSizeLabel.Text = (DrawTool ? "Толщина пера: "
                             : CleanEraser.IsChecked == true ? "Размер ластика: "
                             : "Размер кисти: ") + $"{(int)CleanSize.Value} px";

    void UpdateBlockInfo()
    {
        var has = _clnSel >= 0 && _clnSel < _clnBoxes.Count;
        CleanBlockMark.IsEnabled = has && !_clnBusy;
        CleanBlockDrop.IsEnabled = has && !_clnBusy;
        CleanDropAll.IsEnabled = _clnBoxes.Count > 0 && !_clnBusy;

        // Дорисовка держится на РАЗМЕТКЕ, а не на выбранном блоке: обвёл кистью
        // без рамки — всё равно есть что стереть
        CleanBlockErase.IsEnabled = _clnMask is not null && !_clnBusy;
        CleanBlockErase.Content = has ? "Дорисовать блок (S)" : "Дорисовать размеченное (S)";
        CleanSmooth.IsEnabled = _clnMask is not null && !_clnBusy;

        // Подпись меняется до нажатия, а не после: «убрать разметку» без
        // уточнения «чью» — это ловушка, за которой пропадает работа
        CleanWipe.IsEnabled = _clnMask is not null && !_clnBusy;
        CleanWipe.Content = has ? "Убрать разметку блока (F)" : "Убрать всю разметку (F)";
        CleanBlockInfo.Text = has
            ? $"Блок {_clnSel + 1} из {_clnBoxes.Count}: {_clnBoxes[_clnSel].Width}×{_clnBoxes[_clnSel].Height} px"
            : "Не выбран. Инструмент «Рамка»: щелчок выбирает, перетаскивание создаёт новую.";
        CleanUndo.IsEnabled = _clnUndo.Count > 0 && !_clnBusy;
        CleanUndo.Content = _clnUndo.Count > 0
            ? $"Отменить (Y): {_clnUndo[^1].What}"
            : "Отменять нечего";
    }

    void OnToggleClean(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        _lastCmd = "очистка от текста";
        UpdateOverlay();
        ShowClean(CleanPanel.Visibility != Visibility.Visible);
    }

    void ShowClean(bool on)
    {
        if (on && _currentPath is null) return;

        CleanPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on)
        {
            _clnCts?.Cancel();
            FocusCanvas();
            return;
        }

        SetChrome(true);

        // Модели строятся заранее и в фоне: граф дорисовки поднимается около
        // шести секунд, и платить это ожиданием ПОСЛЕ нажатия «Дорисовать»
        // значит выдавать загрузку за работу
        _ = Task.Run(() => { TextMask.Warm(); Inpaint.Warm(); });

        if (!Cleanup.Ready)
        {
            CleanStatus.Text = "Моделей нет рядом с приложением — очистка недоступна.";
            return;
        }
        if (!string.Equals(_clnPath, _currentPath, StringComparison.OrdinalIgnoreCase)) LoadClean();
        else FitWhenReady();    // тот же кадр — но открывают панель заново, и ждут «вписать»
    }

    void LoadClean()
    {
        _clnCts?.Cancel();

        // Фоновый шаг читает кадр в своём потоке и держит его до последней
        // строки: и Smooth.Run, и Inpaint.Run зовут Copy() уже ПОСЛЕ счёта.
        // Освободить кадр здесь — обращение к мёртвому объекту из чужого
        // потока, а это падение, которое нечем поймать. Пока идёт счёт просто
        // отпускаем ссылку: нативную память заберёт финализатор SkiaSharp
        if (!_clnBusy) { _clnWork?.Dispose(); _clnPage?.Dispose(); }
        _clnWork = null;
        _clnPage = null;
        _clnMask = null;
        _clnRaw = null;
        _clnBoxes = new List<SKRectI>();
        _clnSel = -1;
        _clnUndo.Clear();
        _clnRedo.Clear();
        _clnDirty = false;

        _clnPath = _currentPath;
        try { _clnPage = SKBitmap.Decode(_clnPath); }
        catch (Exception ex) { Log.Error($"очистка, открытие кадра: {ex}"); _clnPage = null; }

        if (_clnPage is null)
        {
            CleanStatus.Text = "Кадр не открылся.";
            return;
        }

        _clnWork = _clnPage.Copy();
        _clnBase = new WriteableBitmap(_clnPage.Width, _clnPage.Height);
        _clnOver = new WriteableBitmap(_clnPage.Width, _clnPage.Height);
        _clnOverPx = new byte[_clnPage.Width * _clnPage.Height * 4];
        CleanBase.Source = _clnBase;
        CleanOver.Source = _clnOver;

        DrawBase();
        RedrawOverlay();
        UpdateBlockInfo();
        FitWhenReady();
        CleanStatus.Text = $"Кадр {_clnPage.Width}×{_clnPage.Height}. «Сделать всё» — за один раз, "
                         + "или по шагам и по блокам.";
    }

    void ResetClean()
    {
        if (_clnPage is null) return;
        _clnWork?.Dispose();
        _clnWork = _clnPage.Copy();
        _clnMask = null;
        _clnRaw = null;
        _clnBoxes = new List<SKRectI>();
        _clnSel = -1;
        _clnUndo.Clear();
        _clnRedo.Clear();
        _clnDirty = false;
        DrawBase();
        RedrawOverlay();
        UpdateBlockInfo();
        CleanStatus.Text = "Сброшено. Исходник на месте.";
    }

    /// <summary>
    /// Вписать кадр, дождавшись раскладки. Панель только что показали — размеры
    /// прокрутки ещё нулевые, и ZoomFit делить не на что: страница открывалась
    /// в 1:1 и первым делом её приходилось вписывать руками.
    /// </summary>
    void FitWhenReady()
    {
        if (CleanScroll.ViewportWidth >= 1) { ZoomFit(); return; }

        void Once(object s, SizeChangedEventArgs e)
        {
            CleanScroll.SizeChanged -= Once;
            ZoomFit();
        }
        CleanScroll.SizeChanged += Once;
    }

    void ZoomFit()
    {
        if (_clnPage is null || CleanScroll.ViewportWidth < 1) return;
        var k = (float)Math.Min(CleanScroll.ViewportWidth / _clnPage.Width,
                                CleanScroll.ViewportHeight / _clnPage.Height);
        CleanScroll.ChangeView(0, 0, Math.Clamp(k, CleanScroll.MinZoomFactor, CleanScroll.MaxZoomFactor));
    }

    // ---------- отмена ----------

    static void Stack(List<CleanStep> st, CleanStep step)
    {
        st.Add(step);
        while (st.Count > CleanUndoDepth) st.RemoveAt(0);
    }

    void Push(CleanStep step)
    {
        // Любое действие делает запомненную «разметку до запаса» устаревшей.
        // Сама разметка ставит её ПОСЛЕ своего Push — и потому переживает
        _clnRaw = null;
        _clnRawBoxes = null;

        _clnDirty = true;
        Stack(_clnUndo, step);
        _clnRedo.Clear();       // новое действие обрывает ветку возврата
        UpdateBlockInfo();
    }

    /// <summary>
    /// Применить запись и вернуть ОБРАТНУЮ — то, что было до применения. Одна
    /// операция и на «отменить», и на «вернуть»: разница только в том, из
    /// какого стека взяли и в какой положили.
    /// </summary>
    CleanStep Apply(CleanStep s) => Apply(s, out _);

    /// <param name="changed">
    /// Изменилось ли хоть что-нибудь. Нужно для отката по блоку: там запись
    /// остаётся в стопке, и повторное применение к тому же блоку уже ничего
    /// не меняет — вот по этому признаку отмена и переходит на шаг целиком,
    /// вместо того чтобы молча стоять на месте.
    /// </param>
    CleanStep Apply(CleanStep s, out bool changed)
    {
        // Откат и возврат меняют разметку мимо Push — запомненную тоже гасим
        _clnRaw = null;
        _clnRawBoxes = null;

        var back = new CleanStep { What = s.What, Clip = s.Clip };
        changed = false;

        if (s.Boxes is not null)
        {
            back.Boxes = new List<SKRectI>(_clnBoxes);
            changed = true;     // запись о рамках кладут только когда список менялся
            _clnBoxes = s.Boxes;
            _clnSel = -1;
        }

        if (s.Mask is not null && _clnPage is not null)
        {
            back.Mask = _clnMask is null ? new byte[s.Mask.Length] : (byte[])_clnMask.Clone();
            if (s.Clip is { } c && _clnMask is not null)
            {
                var w = _clnPage.Width;
                for (var y = c.Top; y < c.Bottom; y++)
                    for (var x = c.Left; x < c.Right; x++)
                    {
                        var i = y * w + x;
                        if (_clnMask[i] != s.Mask[i]) changed = true;
                        _clnMask[i] = s.Mask[i];
                    }
            }
            else
            {
                var cur = _clnMask;
                if (cur is null || !cur.AsSpan().SequenceEqual(s.Mask.AsSpan())) changed = true;
                _clnMask = (byte[])s.Mask.Clone();
            }
        }

        if (s.Pixels is not null && _clnWork is not null)
        {
            var r = s.Clip is { } cc ? SKRectI.Intersect(s.Region, cc) : s.Region;
            if (r.Width > 0 && r.Height > 0)
            {
                back.Region = r;
                back.Pixels = SnapshotRegion(r, s.What).Pixels;
                var px = _clnWork.Pixels;
                var w = _clnWork.Width;
                for (var y = r.Top; y < r.Bottom; y++)
                    for (var x = r.Left; x < r.Right; x++)
                    {
                        var was = px[y * w + x];
                        var now = s.Pixels[(y - s.Region.Top) * s.Region.Width
                                           + (x - s.Region.Left)];
                        if (was != now) changed = true;
                        px[y * w + x] = now;
                    }
                _clnWork.Pixels = px;
                DrawBase();
            }
        }
        return back;
    }

    /// <summary>
    /// Отмена. С ВЫБРАННЫМ БЛОКОМ откатывается только он: из трёх дорисованных
    /// блоков два выходят хорошо, и терять их из-за третьего незачем. Запись
    /// при этом остаётся в стеке — соседние блоки ещё можно откатить поодиночке
    /// или все разом, сняв выбор.
    /// </summary>
    void UndoClean()
    {
        // Посреди счёта отменять нечего: фоновый шаг всё равно положит поверх
        // свой результат, а запись отмены уже уедет в стек возврата
        if (_clnBusy || _clnUndo.Count == 0 || _clnWork is null) return;
        var s = _clnUndo[^1];

        var part = _clnSel >= 0 && _clnSel < _clnBoxes.Count && s.Boxes is null;
        if (part)
        {
            var back = Apply(new CleanStep
            {
                What = s.What, Mask = s.Mask, Pixels = s.Pixels,
                Region = s.Region, Clip = _clnBoxes[_clnSel],
            }, out var changed);

            // Запись остаётся в стопке нарочно: соседние блоки ещё можно
            // откатить поодиночке. Но если в ЭТОМ блоке откатывать уже нечего,
            // стоять на месте нельзя — иначе отмена упирается в невидимую
            // стену, и человек жмёт её впустую. Тогда отменяем шаг целиком
            if (changed)
            {
                Stack(_clnRedo, back);
                CleanStatus.Text = $"Отменено в выбранном блоке: {s.What}. Остальное на месте.";
                part = true;
            }
            else part = false;
        }

        if (!part)
        {
            _clnUndo.RemoveAt(_clnUndo.Count - 1);
            Stack(_clnRedo, Apply(s));
            CleanStatus.Text = $"Отменено: {s.What}.";
        }

        RedrawOverlay();
        UpdateBlockInfo();
    }

    void RedoClean()
    {
        if (_clnBusy || _clnRedo.Count == 0 || _clnWork is null) return;
        var s = _clnRedo[^1];
        _clnRedo.RemoveAt(_clnRedo.Count - 1);
        Stack(_clnUndo, Apply(s));
        RedrawOverlay();
        UpdateBlockInfo();
        CleanStatus.Text = $"Возвращено: {s.What}.";
    }

    /// <summary>Снимок куска рабочего кадра — чтобы дорисовку блока можно было откатить.</summary>
    CleanStep SnapshotRegion(SKRectI r, string what)
    {
        var px = _clnWork!.Pixels;
        var w = _clnWork.Width;
        var buf = new SKColor[r.Width * r.Height];
        var i = 0;
        for (var y = r.Top; y < r.Bottom; y++)
            for (var x = r.Left; x < r.Right; x++)
                buf[i++] = px[y * w + x];
        return new CleanStep { What = what, Region = r, Pixels = buf };
    }

    // ---------- шаги по всей странице ----------

    async Task RunCleanStepsAsync(int upto, bool only = false)
    {
        if (_clnPage is null || _clnWork is null || _clnPath is null || !Cleanup.Ready) return;

        _clnCts?.Cancel();
        _clnCts = new CancellationTokenSource();
        var ct = _clnCts.Token;

        SetCleanBusy(true);
        try
        {
            var from = only ? upto : 1;

            if (from <= 1 && upto >= 1)
            {
                CleanStatus.Text = "Ищу надписи…";
                var t = DateTime.UtcNow;
                var found = await Cleanup.FindAsync(_clnWork, _clnPath, ct);
                Push(new CleanStep { What = "поиск надписей", Boxes = _clnBoxes });
                _clnBoxes = found;
                _clnSel = -1;
                _clnMask = null;
                _clnRaw = null;
                RedrawOverlay();
                UpdateBlockInfo();
                CleanStatus.Text = $"Найдено надписей: {_clnBoxes.Count} "
                                 + $"({(DateTime.UtcNow - t).TotalMilliseconds:F0} мс). "
                                 + "Лишние уберите, недостающие обведите.";
            }

            if (from <= 2 && upto >= 2)
            {
                if (_clnBoxes.Count == 0) { CleanStatus.Text = "Сначала найдите или обведите текст."; return; }
                CleanStatus.Text = "Размечаю буквы…";
                var t = DateTime.UtcNow;
                // Модель зовём БЕЗ запаса и результат запоминаем: запас потом
                // накладывается поверх и меняется колесом без нового прогона
                var raw = await Cleanup.MarkAsync(_clnWork, _clnBoxes, 0, ct);
                Push(new CleanStep { What = "разметка страницы", Mask = _clnMask });
                _clnMask = new byte[raw.Length];
                _clnRaw = raw;
                _clnRawBoxes = new List<SKRectI>(_clnBoxes);
                _clnRawBlock = false;
                ApplyPad();
                CleanStatus.Text = $"Размечено {Lit()} px ({(DateTime.UtcNow - t).TotalMilliseconds:F0} мс). "
                                 + "Поправьте кистью, если буква пропущена.";
            }

            if (upto >= 3)
            {
                if (_clnMask is null) { CleanStatus.Text = "Сначала разметьте буквы."; return; }
                await EraseAsync(_clnMask, "дорисовка страницы", ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"очистка: {ex}");
            CleanStatus.Text = $"Не получилось: {ex.Message}";
        }
        finally { SetCleanBusy(false); }
    }

    /// <summary>Общая дорисовка: и для страницы, и для одного блока.</summary>
    async Task EraseAsync(byte[] mask, string what, CancellationToken ct)
    {
        if (_clnWork is null) return;
        mask = (byte[])mask.Clone();        // та же причина, что и в BlockMask

        CleanStatus.Text = "Дорисовываю…";
        var t = DateTime.UtcNow;
        var work = _clnWork;
        var res = await Cleanup.EraseAsync(work, mask, ct);
        if (res is null) { CleanStatus.Text = "Стирать нечего."; return; }

        // Кадр могли сменить, пока считалось: стрелки листают папку и при
        // открытой панели. Результат тогда про ЧУЖУЮ страницу, и снимок под
        // откат брался бы по её размерам
        if (!ReferenceEquals(work, _clnWork)) { res.Dispose(); return; }

        // Снимок ДО подмены — только области дорисовки, а не кадра целиком.
        // Маску кладём в ТУ ЖЕ запись: откат обязан вернуть и пиксели, и разметку,
        // иначе после отмены исходный текст вернулся бы без разметки под ним
        var (rx, ry, rw, rh) = Inpaint.Region(_clnWork.Width, _clnWork.Height,
                                              Inpaint.LastX, Inpaint.LastY,
                                              Inpaint.LastX + Inpaint.LastW - 1,
                                              Inpaint.LastY + Inpaint.LastH - 1, context: 0);
        var step = SnapshotRegion(new SKRectI(rx, ry, rx + rw, ry + rh), what);
        step.Mask = _clnMask is null ? null : (byte[])_clnMask.Clone();
        Push(step);

        // Стёртое больше не размечено: краска поверх уже чистого места читается
        // как «текст остался», а повторная дорисовка прошлась бы по нему заново
        if (_clnMask is not null)
            for (var i = 0; i < mask.Length; i++)
                if (mask[i] >= 128) _clnMask[i] = 0;

        _clnWork.Dispose();
        _clnWork = res;
        DrawBase();
        RedrawOverlay();
        UpdateBlockInfo();
        CleanStatus.Text = $"Готово ({(DateTime.UtcNow - t).TotalMilliseconds:F0} мс). "
                         + "Не понравилось — «Отменить».";
    }

    /// <summary>
    /// Стереть разметку — в выбранном блоке или всю. Кадр не трогается: это
    /// отказ от намерения стирать, а не отмена уже стёртого.
    /// </summary>
    void WipeMask()
    {
        if (_clnMask is null || _clnPage is null) return;

        var has = _clnSel >= 0 && _clnSel < _clnBoxes.Count;
        Push(new CleanStep { What = has ? "разметка блока" : "вся разметка",
                             Mask = (byte[])_clnMask.Clone() });

        if (has)
        {
            var b = _clnBoxes[_clnSel];
            var w = _clnPage.Width;
            for (var y = b.Top; y < b.Bottom; y++)
                for (var x = b.Left; x < b.Right; x++)
                    _clnMask[y * w + x] = 0;
            CleanStatus.Text = "Разметка блока убрана.";
        }
        else
        {
            Array.Clear(_clnMask);
            CleanStatus.Text = "Разметка убрана вся.";
        }

        RedrawOverlay();
        UpdateBlockInfo();
    }

    // ---------- клавиши и предпросмотр ----------

    /// <summary>
    /// Клавиши блока идут подряд слева направо по верхнему ряду: Q W E R T Y —
    /// в том же порядке, в каком человек работает с блоком (посмотрел →
    /// разметил → дорисовал → убрал разметку → убрал рамку → откатил). Рука
    /// стоит на месте, а порядок ряда сам напоминает порядок шагов.
    ///
    /// Каждая клавиша спрашивает у СВОЕЙ кнопки, можно ли сейчас: занятость и
    /// «блок не выбран» уже посчитаны там, второй копии этих условий не надо.
    /// </summary>
    void OnCleanKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (CleanPanel.Visibility != Visibility.Visible) return;

        // Автоповтор не нужен: удержание W — это «задумался», а не «размечай
        // пять раз подряд». Для Q первое нажатие уже включило предпросмотр
        if (e.KeyStatus.WasKeyDown) return;

        // Пока тянут мышью — клавиши молчат. Они меняют тот самый список рамок
        // и ту самую разметку, которые держит протяжка: Delete посреди протяжки
        // убирал рамку, а протяжка дотягивала уже несуществующую
        if (_clnResize >= 0 || _clnDragBox || _clnPainting || _clnPanning) return;

        // Ctrl+S и Ctrl+Z здесь значат ровно то же, что и везде, только
        // применительно к чистке. Снаружи Ctrl+Z отменяет файловые операции —
        // при открытой панели он туда уже не доходит
        if (Down(Windows.System.VirtualKey.Control))
        {
            var shift = Down(Windows.System.VirtualKey.Shift);
            switch (e.Key)
            {
                case Windows.System.VirtualKey.S: SaveClean(overwrite: shift); break;
                case Windows.System.VirtualKey.Z when shift: RedoClean(); break;
                case Windows.System.VirtualKey.Z: UndoClean(); break;
                default: return;
            }
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            // Держать, чтобы сравнить с исходником. Именно ДЕРЖАТЬ, а не
            // переключать: сравнение «до и после» делается взглядом туда-обратно,
            // и лишнее нажатие на возврат сбивает его. F1, а не буква: рука на
            // буквенных рядах занята работой, а эта клавиша стоит особняком
            case Windows.System.VirtualKey.F1:
                Preview(true);
                break;

            // Верхний ряд — ИНСТРУМЕНТ, нижний — действия над блоком: сперва
            // берут инструмент, потом им работают, и ряды идут в том же порядке
            // Q держат и ради колеса — чтобы подобрать запас вокруг букв.
            // Инструмент на это время меняется, но к отпусканию вернётся тот,
            // что был: иначе подбор запаса каждый раз отбирал бы кисть
            case Windows.System.VirtualKey.Q:
                _clnToolBeforeQ = Array.Find(Tools, t => t.IsChecked == true);
                _clnPadTurned = false;
                Tool(CleanToolBox);
                break;
            case Windows.System.VirtualKey.W: Tool(CleanBrush); break;
            case Windows.System.VirtualKey.E: Tool(CleanEraser); break;

            // Нижний ряд — рисование по кадру, слева направо в порядке
            // «сначала возьми цвет, потом рисуй»
            case Windows.System.VirtualKey.Z: Tool(CleanPick); break;
            case Windows.System.VirtualKey.X: Tool(CleanPencil); break;
            case Windows.System.VirtualKey.C: Tool(CleanLine); break;
            case Windows.System.VirtualKey.V: Tool(CleanRect); break;
            case Windows.System.VirtualKey.B: Tool(CleanOval); break;

            case Windows.System.VirtualKey.D:
            case Windows.System.VirtualKey.Delete:
                if (CleanBlockDrop.IsEnabled) DropBlock();
                break;

            case Windows.System.VirtualKey.Y:
                UndoClean();
                break;

            case Windows.System.VirtualKey.A:
                if (CleanBlockMark.IsEnabled) _ = MarkBlockAsync();
                break;

            case Windows.System.VirtualKey.S:
                if (CleanBlockErase.IsEnabled) _ = EraseBlockAsync();
                break;

            case Windows.System.VirtualKey.F:
                if (CleanWipe.IsEnabled) WipeMask();
                break;

            case Windows.System.VirtualKey.G:
                if (CleanSmooth.IsEnabled) _ = SmoothAsync();
                break;

            // Шаги по всей странице — своими же номерами, как на кнопках
            case Windows.System.VirtualKey.Number1:
            case Windows.System.VirtualKey.NumberPad1:
                if (CleanStep1.IsEnabled) _ = RunCleanStepsAsync(1);
                break;

            case Windows.System.VirtualKey.Number2:
            case Windows.System.VirtualKey.NumberPad2:
                if (CleanStep2.IsEnabled) _ = RunCleanStepsAsync(2, only: true);
                break;

            case Windows.System.VirtualKey.Number3:
            case Windows.System.VirtualKey.NumberPad3:
                if (CleanStep3.IsEnabled) _ = RunCleanStepsAsync(3, only: true);
                break;

            case Windows.System.VirtualKey.Number4:
            case Windows.System.VirtualKey.NumberPad4:
                DropAllBlocks();
                break;

            default:
                return;
        }
        e.Handled = true;
    }

    void OnCleanKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Q)
        {
            // Колесо крутили — значит Q была модификатором, а не выбором
            // инструмента, и рука ждёт прежний
            if (_clnPadTurned && _clnToolBeforeQ is not null) Tool(_clnToolBeforeQ);
            _clnPadTurned = false;
            _clnToolBeforeQ = null;
            e.Handled = true;
            return;
        }
        if (e.Key != Windows.System.VirtualKey.F1) return;
        Preview(false);
        e.Handled = true;
    }

    /// <summary>
    /// Показать исходник вместо рабочего кадра. Масштаб и положение прокрутки
    /// не трогаются вовсе — меняется только содержимое слоёв, поэтому взгляд
    /// остаётся на том же месте страницы.
    /// </summary>
    void Preview(bool on)
    {
        if (_clnPage is null || _clnWork is null || on == _clnPreview) return;
        _clnPreview = on;
        DrawBase(on ? _clnPage : _clnWork);
        CleanOver.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        // Прежнюю подпись возвращаем дословно: человек читал её до сравнения и
        // ждёт на том же месте — подменять её на «готово» значит терять ответ
        if (on) { _clnSaidBefore = CleanStatus.Text; CleanStatus.Text = "Исходник — пока держите F1."; }
        else if (_clnSaidBefore is not null) { CleanStatus.Text = _clnSaidBefore; _clnSaidBefore = null; }
    }

    void SetCleanBusy(bool busy)
    {
        _clnBusy = busy;
        CleanBusyRing.IsActive = busy;
        CleanBusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CleanAll.IsEnabled = !busy;
        CleanStep1.IsEnabled = !busy;
        CleanStep2.IsEnabled = !busy;
        CleanStep3.IsEnabled = !busy;
        CleanSave.IsEnabled = !busy;
        CleanReset.IsEnabled = !busy;

        // Остальные кнопки зависят не только от занятости, но и от выбранного
        // блока, разметки и стопки отмен — их состояние считается в одном
        // месте. Считать его здесь ещё раз значит забыть половину: так и вышло
        // с «Убрать рамку», которая после дорисовки блока оставалась серой,
        // пока блок не выберешь заново
        UpdateBlockInfo();
    }

    int Lit()
    {
        if (_clnMask is null) return 0;
        var n = 0;
        foreach (var v in _clnMask) if (v >= 128) n++;
        return n;
    }

    // ---------- работа по одному блоку ----------

    async Task MarkBlockAsync()
    {
        if (_clnWork is null || _clnSel < 0 || _clnSel >= _clnBoxes.Count) return;
        var box = _clnBoxes[_clnSel];

        _clnCts?.Cancel();
        _clnCts = new CancellationTokenSource();
        SetCleanBusy(true);
        try
        {
            var t = DateTime.UtcNow;

            // Модель зовём БЕЗ запаса и результат запоминаем: запас потом
            // накладывается поверх и меняется колесом без нового прогона
            var raw = await Cleanup.MarkAsync(_clnWork, new[] { box }, 0, _clnCts.Token);

            Push(new CleanStep { What = "разметка блока", Mask = _clnMask });

            // Разметка блока ЗАМЕНЯЕТ прежнюю внутри его рамки, а снаружи не
            // трогает: иначе повторный проход по блоку накапливал бы мусор.
            // Чистит и раскладывает ApplyPad — ровно то же делает и колесо
            _clnMask = _clnMask is null ? new byte[raw.Length] : (byte[])_clnMask.Clone();
            _clnRaw = raw;
            _clnRawBoxes = new List<SKRectI> { box };
            _clnRawBlock = true;
            ApplyPad();

            CleanStatus.Text = $"Блок размечен ({(DateTime.UtcNow - t).TotalMilliseconds:F0} мс). "
                             + "Запас вокруг букв — Q и колесо, ложится сразу.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error($"разметка блока: {ex}"); CleanStatus.Text = ex.Message; }
        finally { SetCleanBusy(false); }
    }

    async Task EraseBlockAsync()
    {
        if (_clnWork is null || _clnMask is null)
        {
            CleanStatus.Text = "Сначала разметьте — сегментатором или кистью.";
            return;
        }

        // Блок выбран — стираем только его: остальная страница не должна
        // дорисовываться заодно. Блок НЕ выбран — стираем всё размеченное:
        // обвести кистью и нажать «дорисовать» человек вправе и без рамки
        var part = BlockMask(out var has);
        if (part is null) return;

        _clnCts?.Cancel();
        _clnCts = new CancellationTokenSource();
        SetCleanBusy(true);
        try { await EraseAsync(part, has ? "дорисовка блока" : "дорисовка размеченного", _clnCts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error($"дорисовка блока: {ex}"); CleanStatus.Text = ex.Message; }
        finally { SetCleanBusy(false); }
    }

    /// <summary>
    /// Убрать ВСЕ рамки разом. Поиск на пёстрой странице находит десяток лишних,
    /// и выбрасывать их по одной — работа ради работы. Разметка остаётся: рамки
    /// и разметка — разные вещи, и человек мог уже поправить её кистью.
    /// </summary>
    void DropAllBlocks()
    {
        if (_clnBoxes.Count == 0) return;
        Push(new CleanStep { What = "убранные рамки", Boxes = _clnBoxes });
        _clnBoxes = new List<SKRectI>();
        _clnSel = -1;
        RedrawOverlay();
        UpdateBlockInfo();
        CleanStatus.Text = "Рамки убраны все. Разметка под ними осталась.";
    }

    /// <summary>
    /// Залить размеченное градиентом вместо сети. Порядок тот же, что у
    /// дорисовки: блок выбран — только он, не выбран — всё размеченное.
    /// </summary>
    async Task SmoothAsync()
    {
        if (_clnWork is null || _clnMask is null)
        {
            CleanStatus.Text = "Сначала разметьте — сегментатором или кистью.";
            return;
        }

        var part = BlockMask(out var has);
        if (part is null) return;

        _clnCts?.Cancel();
        _clnCts = new CancellationTokenSource();
        var ct = _clnCts.Token;
        SetCleanBusy(true);
        try
        {
            CleanStatus.Text = "Заливаю градиентом…";
            var t = DateTime.UtcNow;
            var work = _clnWork;
            var res = await Task.Run(() => Smooth.Run(work, part), ct);
            if (res is null) { CleanStatus.Text = "Заливать нечего."; return; }

            // Кадр могли сменить, пока считалось (стрелки листают папку и при
            // открытой панели) — результат тогда про чужую страницу
            if (!ReferenceEquals(work, _clnWork)) { res.Dispose(); return; }

            var step = SnapshotRegion(
                new SKRectI(Smooth.LastX, Smooth.LastY,
                            Smooth.LastX + Smooth.LastW, Smooth.LastY + Smooth.LastH),
                has ? "заливка блока" : "заливка градиентом");
            step.Mask = (byte[])_clnMask.Clone();
            Push(step);

            // Залитое больше не размечено — по той же причине, что и у дорисовки
            for (var i = 0; i < part.Length; i++)
                if (part[i] >= 128) _clnMask[i] = 0;

            _clnWork.Dispose();
            _clnWork = res;
            DrawBase();
            RedrawOverlay();
            UpdateBlockInfo();
            CleanStatus.Text = $"Залито ({(DateTime.UtcNow - t).TotalMilliseconds:F0} мс). "
                             + "Это для гладкого фона; на узоре берите дорисовку.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error($"заливка градиентом: {ex}"); CleanStatus.Text = ex.Message; }
        finally { SetCleanBusy(false); }
    }

    /// <summary>
    /// Разметка, ограниченная выбранным блоком, или вся — если блок не выбран.
    /// null, если стирать нечего (о причине уже сказано в строке состояния).
    /// </summary>
    byte[]? BlockMask(out bool has)
    {
        has = _clnSel >= 0 && _clnSel < _clnBoxes.Count;
        // Копия, а не сама разметка: пока считается фоновый шаг, кисть рисует
        // дальше, и очистка по итогам стёрла бы новые мазки, ничего под ними
        // не залив
        var part = (byte[])_clnMask!.Clone();
        if (has)
        {
            var box = _clnBoxes[_clnSel];
            var w = _clnWork!.Width;
            part = new byte[_clnMask!.Length];
            for (var y = box.Top; y < box.Bottom; y++)
                for (var x = box.Left; x < box.Right; x++)
                {
                    var i = y * w + x;
                    if (_clnMask[i] >= 128) part[i] = 255;
                }
        }

        foreach (var v in part) if (v >= 128) return part;
        CleanStatus.Text = has ? "В блоке нечего стирать." : "Разметка пустая — стирать нечего.";
        return null;
    }

    /// <summary>
    /// Положить отрезок в кадр. Цвет не выбирается вручную: замеряем, чем шов
    /// отличается от фона в НАЧАЛЕ отрезка, и ведём эту разницу дальше. Значит
    /// вести надо от живой линии в ту сторону, где её не хватает.
    /// </summary>
    /// <summary>
    /// Карандаш: кладём отрезок в буфер штриха и показываем его в слое поверх.
    /// В кадр штрих попадёт целиком на отпускании — одной записью отмены.
    /// </summary>
    void Pencil(int x0, int y0, int x1, int y1)
    {
        if (_clnPenPx is null || _clnWork is null || _clnOverPx is null) return;
        int w = _clnWork.Width, h = _clnWork.Height;

        var box = Paint.Line(_clnPenPx, w, h, x0, y0, x1, y1, (int)CleanSize.Value, _clnInk);
        _clnPenBox = Paint.Union(_clnPenBox, box);
        if (box.IsEmpty) return;

        // Предпросмотр — в слой разметки: перекладывать весь кадр на каждое
        // движение мыши слишком дорого, а слой обновляется полосой
        for (var y = box.Top; y < box.Bottom; y++)
            for (var x = box.Left; x < box.Right; x++)
            {
                var i = y * w + x;
                var c = _clnPenPx[i];
                _clnOverPx[i * 4] = c.Blue;
                _clnOverPx[i * 4 + 1] = c.Green;
                _clnOverPx[i * 4 + 2] = c.Red;
                _clnOverPx[i * 4 + 3] = 255;
            }
        PushOverlay();
    }

    /// <summary>
    /// Закрепить нарисованное. Снимок под откат берётся ДО подмены пикселей:
    /// SKBitmap.Pixels отдаёт копию, поэтому правки живут в буфере, пока их
    /// не вернут обратно.
    /// </summary>
    void Commit()
    {
        if (_clnWork is null) { _clnPenPx = null; return; }
        int w = _clnWork.Width, h = _clnWork.Height;
        var width = (int)CleanSize.Value;

        SKRectI box;
        string what;
        SKColor[] px;

        if (PencilTool)
        {
            if (_clnPenPx is null || _clnPenBox.IsEmpty) { _clnPenPx = null; RedrawOverlay(); return; }
            px = _clnPenPx;
            box = _clnPenBox;
            what = "карандаш";
        }
        else
        {
            var dx = _clnLx1 - _clnLx0;
            var dy = _clnLy1 - _clnLy0;
            if (dx * dx + dy * dy < 4) { RedrawOverlay(); return; }

            px = _clnWork.Pixels;
            var r = Cleanup.Clamp(Cleanup.Norm(_clnLx0, _clnLy0, _clnLx1, _clnLy1), w, h);
            if (RectTool) { box = Paint.Rect(px, w, h, r, width, _clnInk); what = "квадрат"; }
            else if (OvalTool) { box = Paint.Ellipse(px, w, h, r, width, _clnInk); what = "круг"; }
            else { box = Paint.Line(px, w, h, _clnLx0, _clnLy0, _clnLx1, _clnLy1, width, _clnInk); what = "линия"; }
        }

        _clnPenPx = null;
        if (box.IsEmpty) { RedrawOverlay(); return; }

        Push(SnapshotRegion(box, what));
        _clnWork.Pixels = px;
        DrawBase();
        RedrawOverlay();
        UpdateBlockInfo();
        CleanStatus.Text = $"{char.ToUpper(what[0])}{what[1..]}: {width} px, цвет "
                         + $"{_clnInk.Red} {_clnInk.Green} {_clnInk.Blue}. Не понравилось — «Отменить».";
    }

    void DropBlock()
    {
        if (_clnSel < 0 || _clnSel >= _clnBoxes.Count) return;
        Push(new CleanStep { What = "убранная рамка", Boxes = new List<SKRectI>(_clnBoxes) });
        _clnBoxes.RemoveAt(_clnSel);
        _clnSel = -1;
        RedrawOverlay();
        UpdateBlockInfo();
        CleanStatus.Text = "Рамка убрана. Разметка под ней осталась — уберите ластиком, если мешает.";
    }

    // ---------- отрисовка ----------

    void DrawBase() => DrawBase(_clnPreview ? _clnPage : _clnWork);

    void DrawBase(SKBitmap? src)
    {
        if (_clnBase is null || src is null) return;
        var px = src.Pixels;
        var buf = new byte[px.Length * 4];
        for (var i = 0; i < px.Length; i++)
        {
            var c = px[i];
            buf[i * 4] = c.Blue;
            buf[i * 4 + 1] = c.Green;
            buf[i * 4 + 2] = c.Red;
            buf[i * 4 + 3] = 255;
        }
        using (var st = _clnBase.PixelBuffer.AsStream()) st.Write(buf, 0, buf.Length);
        _clnBase.Invalidate();
    }

    /// <summary>Слой поверх кадра: разметка заливкой, рамки контуром.</summary>
    void RedrawOverlay()
    {
        if (_clnOverPx is null || _clnPage is null) return;
        Array.Clear(_clnOverPx);

        int w = _clnPage.Width, h = _clnPage.Height;

        if (_clnMask is not null)
            for (var i = 0; i < _clnMask.Length; i++)
            {
                if (_clnMask[i] < 128) continue;
                _clnOverPx[i * 4] = 130;
                _clnOverPx[i * 4 + 1] = 20;
                _clnOverPx[i * 4 + 2] = 160;
                _clnOverPx[i * 4 + 3] = 170;
            }

        DrawFrames();
        PushOverlay();
    }

    /// <summary>
    /// Рамки поверх разметки. Вынесены отдельно и рисуются ПОСЛЕ каждой правки
    /// слоя: мазок перекрашивает свою полосу целиком по маске и стирал зелёную
    /// линию — со стороны выглядело так, будто ластик убирает границу блока.
    /// Рисуется только периметр, так что звать это на каждый мазок не жалко.
    /// </summary>
    void DrawFrames()
    {
        if (_clnOverPx is null || _clnPage is null) return;
        int w = _clnPage.Width, h = _clnPage.Height;

        void Dot(int x, int y, byte b, byte g, byte r)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            var o = (y * w + x) * 4;
            _clnOverPx[o] = b; _clnOverPx[o + 1] = g; _clnOverPx[o + 2] = r; _clnOverPx[o + 3] = 255;
        }

        var thin = Math.Max(1, Math.Min(w, h) / 500);
        for (var i = 0; i < _clnBoxes.Count; i++)
        {
            var b = _clnBoxes[i];
            var sel = i == _clnSel;
            var t = sel ? thin * 3 : thin;
            for (var k = 0; k < t; k++)
            {
                for (var x = b.Left; x < b.Right; x++)
                {
                    Dot(x, b.Top + k, sel ? (byte)60 : (byte)200, sel ? (byte)220 : (byte)40, sel ? (byte)60 : (byte)255);
                    Dot(x, b.Bottom - 1 - k, sel ? (byte)60 : (byte)200, sel ? (byte)220 : (byte)40, sel ? (byte)60 : (byte)255);
                }
                for (var y = b.Top; y < b.Bottom; y++)
                {
                    Dot(b.Left + k, y, sel ? (byte)60 : (byte)200, sel ? (byte)220 : (byte)40, sel ? (byte)60 : (byte)255);
                    Dot(b.Right - 1 - k, y, sel ? (byte)60 : (byte)200, sel ? (byte)220 : (byte)40, sel ? (byte)60 : (byte)255);
                }
            }
        }

        // Ручки на выбранной рамке: за край можно тянуть, и это должно быть
        // видно, а не угадываться. Размер постоянный на экране, как и полоса
        // захвата — иначе на отдалённой странице ручки исчезали бы
        if (_clnSel >= 0 && _clnSel < _clnBoxes.Count)
        {
            var s = _clnBoxes[_clnSel];

            // Ручка не шире полосы захвата: нарисованное должно ХВАТАТЬСЯ,
            // иначе нажатие по её краю рисует новую рамку вместо правки старой
            var hs = Math.Min(Math.Max(2, (int)Math.Round(6 / Math.Max(0.1, CleanScroll.ZoomFactor))),
                              Cleanup.Band(s, GripMargin()));
            var xs = new[] { s.Left, s.MidX, s.Right - 1 };
            var ys = new[] { s.Top, s.MidY, s.Bottom - 1 };
            for (var i = 0; i < 3; i++)
                for (var j = 0; j < 3; j++)
                {
                    if (i == 1 && j == 1) continue;             // середина — не ручка

                    // Контур, а не заливка: сплошной квадрат закрывал бы
                    // разметку под собой, а ради неё вся панель и сделана
                    for (var dy = -hs; dy <= hs; dy++)
                        for (var dx = -hs; dx <= hs; dx++)
                            if (Math.Abs(dx) == hs || Math.Abs(dy) == hs)
                                Dot(xs[i] + dx, ys[j] + dy, 60, 220, 60);
                }
        }

        // Фигура, которую ведут прямо сейчас — показываем ЦВЕТОМ ПЕРА, а не
        // зелёным: человек выбирает место по тому, как это будет выглядеть
        if (_clnLineOn && !PencilTool)
        {
            // Толщиной пера, а не в пиксель: иначе на пере в 8 px человек
            // видит волосок, а получает полосу
            var pr = Math.Max(0, (int)CleanSize.Value / 2);
            void Ink(int x, int y)
            {
                for (var dy = -pr; dy <= pr; dy++)
                    for (var dx = -pr; dx <= pr; dx++)
                        if (dx * dx + dy * dy <= pr * pr)
                            Dot(x + dx, y + dy, _clnInk.Blue, _clnInk.Green, _clnInk.Red);
            }

            if (RectTool || OvalTool)
            {
                var r = Cleanup.Clamp(Cleanup.Norm(_clnLx0, _clnLy0, _clnLx1, _clnLy1), w, h);
                if (RectTool)
                {
                    for (var x = r.Left; x < r.Right; x++) { Ink(x, r.Top); Ink(x, r.Bottom - 1); }
                    for (var y = r.Top; y < r.Bottom; y++) { Ink(r.Left, y); Ink(r.Right - 1, y); }
                }
                else
                {
                    float cx = (r.Left + r.Right - 1) / 2f, cy = (r.Top + r.Bottom - 1) / 2f;
                    float ra = Math.Max(1, r.Width - 1) / 2f, rb = Math.Max(1, r.Height - 1) / 2f;
                    var steps = (int)(4 * (ra + rb)) + 8;
                    for (var i = 0; i < steps; i++)
                    {
                        var a = i * 2 * MathF.PI / steps;
                        Ink((int)MathF.Round(cx + ra * MathF.Cos(a)),
                            (int)MathF.Round(cy + rb * MathF.Sin(a)));
                    }
                }
            }
            else
            {
                var steps = Math.Max(Math.Abs(_clnLx1 - _clnLx0), Math.Abs(_clnLy1 - _clnLy0));
                for (var i = 0; i <= steps; i++)
                {
                    var t = steps == 0 ? 0f : (float)i / steps;
                    Ink((int)(_clnLx0 + (_clnLx1 - _clnLx0) * t),
                        (int)(_clnLy0 + (_clnLy1 - _clnLy0) * t));
                }
            }
        }

        // Рамка, которую тянут прямо сейчас
        if (_clnDragBox)
        {
            var r = Norm(_clnAx, _clnAy, _clnBx, _clnBy);
            for (var x = r.Left; x < r.Right; x++) { Dot(x, r.Top, 60, 220, 60); Dot(x, r.Bottom - 1, 60, 220, 60); }
            for (var y = r.Top; y < r.Bottom; y++) { Dot(r.Left, y, 60, 220, 60); Dot(r.Right - 1, y, 60, 220, 60); }
        }
    }

    void PushOverlay()
    {
        if (_clnOver is null || _clnOverPx is null) return;
        using (var st = _clnOver.PixelBuffer.AsStream()) st.Write(_clnOverPx, 0, _clnOverPx.Length);
        _clnOver.Invalidate();
    }

    static SKRectI Norm(int x0, int y0, int x1, int y1) => Cleanup.Norm(x0, y0, x1, y1);

    // ---------- указатель ----------

    /// <summary>
    /// Колесо — увеличение, Ctrl+колесо — размер кисти. Наоборот, как принято
    /// в редакторах, здесь неудобно: по странице ездят постоянно, а размер
    /// кисти меняют изредка.
    /// </summary>
    void OnCleanWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_clnPage is null) return;
        var pt = e.GetCurrentPoint(CleanScroll);
        var d = pt.Properties.MouseWheelDelta;
        if (d == 0) return;
        e.Handled = true;

        // Запас вокруг букв — на Q: это параметр разметки, а Q ею и заведует
        if (Down(Windows.System.VirtualKey.Q))
        {
            _clnPadTurned = true;
            CleanPad.Value = Math.Clamp(CleanPad.Value + (d > 0 ? 1 : -1),
                                        CleanPad.Minimum, CleanPad.Maximum);
            return;
        }

        if (Down(Windows.System.VirtualKey.Control))
        {
            CleanSize.Value = Math.Clamp(CleanSize.Value + (d > 0 ? 2 : -2),
                                         CleanSize.Minimum, CleanSize.Maximum);
            return;
        }

        var z = Math.Clamp(CleanScroll.ZoomFactor * (d > 0 ? 1.25f : 1f / 1.25f),
                           CleanScroll.MinZoomFactor, CleanScroll.MaxZoomFactor);
        if (Math.Abs(z - CleanScroll.ZoomFactor) < 1e-4) return;

        // Точка под курсором обязана остаться под курсором: иначе каждое
        // приближение уводит кадр и его приходится ловить прокруткой
        var k = z / CleanScroll.ZoomFactor;
        CleanScroll.ChangeView((CleanScroll.HorizontalOffset + pt.Position.X) * k - pt.Position.X,
                               (CleanScroll.VerticalOffset + pt.Position.Y) * k - pt.Position.Y,
                               z, true);
    }

    void OnCleanPointerDown(object sender, PointerRoutedEventArgs e)
    {
        if (_clnPage is null) return;

        // Левая кнопка тянет кадр — как и в самом просмотрщике за панелью,
        // рука не переучивается. Рисует правая
        var props = e.GetCurrentPoint(CleanScroll).Properties;
        if (props.IsLeftButtonPressed)
        {
            // Правая уже рисует — левая не лезет. Иначе щелчок левой посреди
            // протяжки рамки вписывал кадр, а рамка дотягивалась до нового
            // места курсора одним прыжком
            if (_clnDragBox || _clnPainting || _clnResize >= 0) return;

            var pt = e.GetCurrentPoint(CleanScroll);
            var p = pt.Position;

            // Двойной щелчок вписывает кадр — как и в самом просмотрщике.
            // Считаем вручную: PointerPressed мы помечаем обработанным, и жест
            // DoubleTapped до нас не доходит вовсе
            if (pt.Timestamp - _clnClickAt < DoubleClickMs() * 1000UL
                && Math.Abs(p.X - _clnClickX) < 8 && Math.Abs(p.Y - _clnClickY) < 8)
            {
                _clnClickAt = 0;
                ZoomFit();
                e.Handled = true;
                return;
            }
            _clnClickAt = pt.Timestamp; _clnClickX = p.X; _clnClickY = p.Y;

            _clnPanX = p.X; _clnPanY = p.Y;
            _clnPanH = CleanScroll.HorizontalOffset;
            _clnPanV = CleanScroll.VerticalOffset;
            _clnPanning = true;
            CleanStack.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (!props.IsRightButtonPressed) return;

        var (x, y) = ToImage(e.GetCurrentPoint(CleanStack).Position);
        CleanStack.CapturePointer(e.Pointer);
        e.Handled = true;

        if (PickTool)
        {
            _clnInk = Paint.Pick(_clnWork!.Pixels, _clnWork.Width, _clnWork.Height, x, y);
            SettingsStore.Current.PenColor = (uint)_clnInk;
            SettingsStore.Commit();
            ShowInk();
            CleanStatus.Text = $"Цвет взят: {_clnInk.Red} {_clnInk.Green} {_clnInk.Blue}. "
                             + "Теперь карандаш, линия, квадрат или круг.";
            return;
        }

        if (DrawTool)
        {
            _clnLx0 = _clnLx1 = x; _clnLy0 = _clnLy1 = y;
            _clnLineOn = true;
            if (PencilTool && _clnWork is not null)
            {
                // Карандаш кладётся в отдельный буфер и попадает в кадр один раз,
                // на отпускании: иначе каждый рывок мыши был бы своей записью
                // отмены, и стек вымывался бы за один штрих
                _clnPenPx = _clnWork.Pixels;
                _clnPenBox = SKRectI.Empty;
                Pencil(x, y, x, y);
            }
            return;
        }

        if (BoxTool)
        {
            // Схватились за край ВЫБРАННОЙ рамки — меняем её, а не рисуем новую.
            // Только выбранной: рамки лежат внахлёст, и «та, что под курсором»
            // слишком часто оказывалась бы не той
            if (_clnSel >= 0 && _clnSel < _clnBoxes.Count)
            {
                var g = Cleanup.Grip(_clnBoxes[_clnSel], x, y, GripMargin());
                if (g != Cleanup.Side.None)
                {
                    _clnResize = _clnSel;
                    _clnGrip = g;
                    _clnResLast = _clnBoxes[_clnSel];
                    _clnResBefore = new List<SKRectI>(_clnBoxes);
                    return;
                }
            }

            _clnAx = _clnBx = x; _clnAy = _clnBy = y;
            _clnDragBox = true;
            return;
        }

        // Пустая разметка заводится прямо здесь: «обвёл сам кистью и стёр» —
        // это законный путь, а не ошибка. Раньше кисть до первой разметки
        // молча отказывала, и путь упирался в тупик
        _clnMask ??= new byte[_clnPage.Width * _clnPage.Height];

        Push(new CleanStep { What = "мазок", Mask = (byte[])_clnMask.Clone() });
        _clnPainting = true;
        _clnLastX = x; _clnLastY = y;
        Stroke(x, y, x, y);
    }

    void OnCleanPointerMove(object sender, PointerRoutedEventArgs e)
    {
        if (_clnPage is null) return;

        var scr = e.GetCurrentPoint(CleanScroll).Position;
        UpdateCursor(scr.X, scr.Y);

        if (_clnPanning)
        {
            var p = e.GetCurrentPoint(CleanScroll).Position;

            // Кадр поехал — значит это перетаскивание, а не половина двойного
            // щелчка. Без этого две быстрые подтяжки туда-обратно вписывали
            // кадр и теряли место, на котором человек работал
            if (Math.Abs(p.X - _clnPanX) >= 8 || Math.Abs(p.Y - _clnPanY) >= 8) _clnClickAt = 0;

            CleanScroll.ChangeView(_clnPanH - (p.X - _clnPanX),
                                   _clnPanV - (p.Y - _clnPanY), null, true);
            e.Handled = true;
            return;
        }

        var (x, y) = ToImage(e.GetCurrentPoint(CleanStack).Position);

        if (_clnResize >= 0)
        {
            // Рамку из-под протяжки могли убрать или подменить весь список.
            // Тянуть дальше нечего — молча отпускаем, а не падаем по индексу
            if (_clnResize >= _clnBoxes.Count || !_clnBoxes[_clnResize].Equals(_clnResLast))
            {
                _clnResize = -1;
                _clnResBefore = null;
                return;
            }

            _clnResLast = Cleanup.Clamp(
                Cleanup.Resize(_clnBoxes[_clnResize], _clnGrip, x, y, MinBox),
                _clnPage.Width, _clnPage.Height);
            _clnBoxes[_clnResize] = _clnResLast;
            RedrawOverlay();
            UpdateBlockInfo();      // размер в подписи меняется на глазах
            e.Handled = true;
            return;
        }

        if (_clnLineOn)
        {
            if (PencilTool) Pencil(_clnLx1, _clnLy1, x, y);
            _clnLx1 = x; _clnLy1 = y;

            // Карандаш свой предпросмотр уже положил в слой полосой, а
            // RedrawOverlay начинается с очистки слоя — она стирала штрих
            // прямо в этом же обработчике, и до отпускания не было видно ничего
            if (!PencilTool) RedrawOverlay();
            e.Handled = true;
            return;
        }
        if (_clnDragBox) { _clnBx = x; _clnBy = y; RedrawOverlay(); e.Handled = true; return; }
        if (!_clnPainting || _clnMask is null) return;

        Stroke(_clnLastX, _clnLastY, x, y);
        _clnLastX = x; _clnLastY = y;
        e.Handled = true;
    }

    void OnCleanPointerUp(object sender, PointerRoutedEventArgs e)
    {
        if (_clnPanning) { _clnPanning = false; e.Handled = true; return; }

        if (_clnResize >= 0)
        {
            // В стек отмены кладём ТО, ЧТО БЫЛО ДО протяжки, и один раз на всю
            // протяжку: запись на каждое движение мыши вымыла бы стек за секунду.
            //
            // Индекс берём СВОЙ, пойманный при нажатии, а не _clnSel: выбор мог
            // съехать за время протяжки, и запись легла бы не про ту рамку
            var i = _clnResize;
            var before = _clnResBefore;
            _clnResize = -1;
            _clnResBefore = null;
            if (i >= _clnBoxes.Count || before is null || i >= before.Count)
            {
                UpdateBlockInfo();
                e.Handled = true;
                return;
            }

            var box = _clnBoxes[i];
            if (!before[i].Equals(box))
            {
                Push(new CleanStep { What = "размер рамки", Boxes = before });
                CleanStatus.Text = $"Рамка: {box.Width}×{box.Height} px. "
                                 + "Разметка внутри осталась прежней — «Разметить блок» пересчитает её.";
            }
            UpdateBlockInfo();
            e.Handled = true;
            return;
        }

        if (_clnLineOn) { _clnLineOn = false; Commit(); e.Handled = true; return; }

        _clnPainting = false;
        if (!_clnDragBox || _clnPage is null) return;
        _clnDragBox = false;

        // Прижимаем сразу при рождении: рамка снаружи кадра дальше по тракту
        // адресует маску мимо массива
        var r = Cleanup.Clamp(Norm(_clnAx, _clnAy, _clnBx, _clnBy),
                              _clnPage.Width, _clnPage.Height);

        // Короткое нажатие — это выбор блока, а не рамка в пару пикселей
        if (r.Width < 8 || r.Height < 8)
        {
            SelectAt(_clnAx, _clnAy);
            RedrawOverlay();
            UpdateBlockInfo();
            return;
        }

        Push(new CleanStep { What = "новая рамка", Boxes = new List<SKRectI>(_clnBoxes) });
        _clnBoxes.Add(r);
        _clnSel = _clnBoxes.Count - 1;
        RedrawOverlay();
        UpdateBlockInfo();
        CleanStatus.Text = "Рамка добавлена. «Разметить блок» — найти в ней буквы.";
    }

    void SelectAt(int x, int y) => _clnSel = Cleanup.Pick(_clnBoxes, x, y);

    (int X, int Y) ToImage(Point p) => ((int)Math.Round(p.X), (int)Math.Round(p.Y));

    /// <summary>
    /// Мазок отрезком, а не точкой: указатель приходит рывками, и по точкам
    /// быстрый мах оставлял бы пунктир.
    /// </summary>
    void Stroke(int x0, int y0, int x1, int y1)
    {
        if (_clnMask is null || _clnPage is null || _clnOverPx is null) return;

        int w = _clnPage.Width, h = _clnPage.Height;
        var add = CleanEraser.IsChecked != true;
        var r = Math.Max(1, (int)CleanSize.Value);

        var steps = Math.Max(1, (int)Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)));
        for (var i = 0; i <= steps; i++)
            Cleanup.Paint(_clnMask, w, h,
                          x0 + (x1 - x0) * i / steps, y0 + (y1 - y0) * i / steps, r, add);

        // Перерисовываем только задетую полосу: полный слой на 2000x2000 — это
        // 16 МБ за мазок, и рука начинает ощущать задержку
        var pad = r + 2;
        var ay = Math.Max(0, Math.Min(y0, y1) - pad);
        var by = Math.Min(h - 1, Math.Max(y0, y1) + pad);
        var ax = Math.Max(0, Math.Min(x0, x1) - pad);
        var bx = Math.Min(w - 1, Math.Max(x0, x1) + pad);

        for (var y = ay; y <= by; y++)
            for (var x = ax; x <= bx; x++)
            {
                var i = y * w + x;
                var on = _clnMask[i] >= 128;
                _clnOverPx[i * 4] = (byte)(on ? 130 : 0);
                _clnOverPx[i * 4 + 1] = (byte)(on ? 20 : 0);
                _clnOverPx[i * 4 + 2] = (byte)(on ? 160 : 0);
                _clnOverPx[i * 4 + 3] = (byte)(on ? 170 : 0);
            }

        // Рамки поверх — иначе мазок вдоль границы блока стирал бы её линию
        DrawFrames();
        PushOverlay();
    }

    // ---------- сохранение ----------

    /// <summary>
    /// Сохранить результат. Рядом — обычный путь, поверх оригинала — по
    /// Ctrl+Shift+S и НЕОБРАТИМО: исходника после этого не останется, поэтому
    /// у него отдельное сочетание, а не «перезаписать, если файл есть».
    /// </summary>
    void SaveClean(bool overwrite = false)
    {
        if (_clnWork is null || _clnPath is null) { CleanStatus.Text = "Сохранять нечего."; return; }
        try
        {
            var dst = overwrite
                ? _clnPath
                : Path.Combine(Path.GetDirectoryName(_clnPath)!,
                               Path.GetFileNameWithoutExtension(_clnPath) + "-clean.png");

            // Поверх оригинала пишем в ЕГО формате: png-байты в файле .jpg
            // открылись бы не везде
            var ext = Path.GetExtension(dst).ToLowerInvariant();
            var fmt = ext is ".jpg" or ".jpeg" ? SKEncodedImageFormat.Jpeg
                    : ext == ".webp" ? SKEncodedImageFormat.Webp
                    : SKEncodedImageFormat.Png;

            using (var img = SKImage.FromBitmap(_clnWork))
            using (var data = img.Encode(fmt, 95))
            using (var fs = File.Create(dst))
                data.SaveTo(fs);

            var said = overwrite ? $"Оригинал перезаписан: {Path.GetFileName(dst)}"
                                 : $"Сохранено: {Path.GetFileName(dst)}";
            CleanStatus.Text = said;
            ShowToast(said, error: false);
            _clnDirty = false;

            // Папка перечитывается сразу: поверх оригинала легли другие пиксели,
            // а рядом мог появиться новый файл — и то и другое приложение обязано
            // показывать, а не держать прежнее в кэше
            RescanFolder();
        }
        catch (Exception ex)
        {
            Log.Error($"очистка, сохранение: {ex}");
            CleanStatus.Text = $"Не сохранилось: {ex.Message}";
        }
    }
}
