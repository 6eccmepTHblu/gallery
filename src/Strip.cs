using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;
using Windows.UI.Core;

namespace Gallery;

/// <summary>
/// Лента миниатюр (SPEC.md §4.8).
///
/// Ключ к простоте: текущий кадр ВСЕГДА в центре, поэтому у ленты нет
/// собственного состояния прокрутки — она чистая функция от CurrentIndex.
/// Отсюда не нужны ни ScrollView, ни ItemsRepeater, ни виртуализация: хватает
/// ряда из 2K+1 ячеек, которым на каждом шаге переназначают Source. Заодно
/// обходим грабли ScrollView с колесом (§4.7) — он потребляет его в
/// InteractionTracker до маршрутизации XAML.
///
/// Лента — накладка над холстом, а не отдельная строка сетки. Строка меняла бы
/// высоту вьюпорта, а на ней держится «вписать»: каждый показ и скрытие ленты
/// пересчитывал бы LayoutFrame и сбрасывал зум пользователя (Zoom.cs). Накладка
/// не вызывает переразметки вообще, и прозрачность у неё осмысленна: кадр виден
/// насквозь. Цена решения принимается вслух: нижние ~78 px кадра закрыты и
/// недоступны для панорамирования, пока лента показана. Выход — F4.
///
/// Миниатюры берём у оболочки, а не из встроенной EXIF-миниатюры файла:
/// Decoder.ThumbnailAsync не применяет поворот. На холсте это незаметно (заглушка
/// живёт ~120 мс), но в ленте миниатюра постоянна, и каждый вертикальный кадр
/// лежал бы набок всё время работы. Оболочка поворот применяет сама, умеет HEIC,
/// AVIF и RAW и держит свой дисковый кэш — то есть решает три задачи разом.
/// </summary>
public sealed partial class MainWindow
{
    const double SlotW = 80, SlotH = 60, SlotGap = 6;
    const double PitchH = SlotW + SlotGap;     // шаг вдоль ряда снизу
    const double PitchV = SlotH + SlotGap;     // и вдоль колонки справа
    const double StripH = SlotH + 18;          // толщина накладки вместе с отступами

    /// <summary>
    /// Снизу или справа. Снизу — по умолчанию: ось ленты совпадает с осью
    /// «влево-вправо», которой человек листает. Справа выигрывает на широких
    /// экранах с вертикальными кадрами — там дефицитна высота, а не ширина.
    /// </summary>
    bool StripVertical => SettingsStore.Current.StripSide == "right";

    double Pitch => StripVertical ? PitchV : PitchH;
    const uint ThumbPx = 128;                  // с запасом на 150% DPI

    // 128×96×4 байта ≈ 48 КБ на миниатюру; 160 штук — около 8 МБ. На фоне
    // основного кэша в 384 МБ это шум.
    const int ThumbCap = 160;

    readonly List<Border> _cells = new();

    // BitmapImage, а не SoftwareBitmapSource: он не владеет чужим битмапом и не
    // требует Dispose, поэтому вытеснение не может освободить источник, который
    // всё ещё висит в Image.Source живой ячейки (§6.4, крэш RO_E_CLOSED).
    readonly Dictionary<string, (BitmapImage img, int idx)> _thumbs =
        new(StringComparer.OrdinalIgnoreCase);

    // Отрицательный кэш: без него файл без миниатюры переспрашивается на каждом
    // оседании, то есть при каждом шаге по папке.
    readonly HashSet<string> _thumbBad = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _thumbLoading = new(StringComparer.OrdinalIgnoreCase);

    // Отдельный семафор, а не общий _gate(4): лента не должна драться за диск
    // с горячим путём. Кадр под курсором важнее любой миниатюры.
    readonly SemaphoreSlim _thumbGate = new(2);

    readonly TranslateTransform _stripShift = new();
    bool _builtVertical;
    Storyboard? _slide;
    long _slideAt;

    bool _stripOn = true;
    int _stripCenter = -1;

    double StripOpacity => Math.Clamp(SettingsStore.Current.StripOpacity, 25, 100) / 100.0;

    // Кисть рамки берётся из ресурсов один раз на пересборку: поиск по цепочке
    // словарей тем на горячем пути платить незачем. Обновляется в BuildCells.
    Brush? _accent;
    Brush Accent => _accent ??= Res("GalleryAccentBrush");

    void InitStrip()
    {
        _stripOn = SettingsStore.Current.StripVisible;
        StripRow.RenderTransform = _stripShift;
        ApplyStripOpacity();
        PlaceStrip();

        // Колесо над лентой — то же листание, что и везде (§4.6). Свой обработчик
        // нужен потому, что накладка перехватывает указатель раньше PanSurface.
        StripBox.PointerWheelChanged += (_, e) =>
        {
            e.Handled = true;

            // Ctrl+колесо — это зум кадра. Над лентой зумить нечего, но и листать
            // по нему нельзя: один и тот же жест не должен означать разное в
            // соседних сантиметрах экрана (§4.6).
            if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                    .HasFlag(CoreVirtualKeyStates.Down)) return;

            WheelStep(e.GetCurrentPoint(StripBox).Properties.MouseWheelDelta);
        };

        // Число ячеек зависит от ширины окна. После расширения новые ячейки пусты,
        // поэтому заказ миниатюр обязателен — иначе они останутся пустыми навсегда.
        Root.SizeChanged += (_, __) =>
        {
            if (!BuildCells()) return;
            UpdateStrip();
            LoadStripThumbs();
        };

        ApplyStrip();
    }

    /// <summary>
    /// Прозрачность — на фоне накладки, а не на всей ленте: гасить сами миниатюры
    /// значит ухудшать то, ради чего лента и нужна. В высокой контрастности
    /// прозрачности нет вовсе (§7.9).
    /// </summary>
    void ApplyStripOpacity() =>
        StripBox.Opacity = _highContrast ? 1.0 : StripOpacity;

    /// <summary>
    /// Показ ленты подчиняется тем же правилам, что статус-бар (§7.4).
    /// Идемпотентно: зовётся из SetChrome, то есть на каждом движении мыши.
    /// </summary>
    void ApplyStrip()
    {
        var show = _stripOn && _list.Count > 0 && (!_fullScreen || _chromeVisible);
        if (show == (StripBox.Visibility == Visibility.Visible)) return;

        StripBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        // Тост прижат к низу — без сдвига он лёг бы прямо на ленту.
        // Ленте справа он не мешает: она не занимает низ.
        Toast.Margin = new Thickness(0, 0, 0, show && !StripVertical ? StripH + 16 : 24);

        if (!show) return;
        PlaceStrip();
        BuildCells();
        UpdateStrip();
        LoadStripThumbs();
    }

    /// <summary>Прижать накладку к нужной стороне и развернуть ряд вдоль неё.</summary>
    void PlaceStrip()
    {
        var v = StripVertical;

        StripBox.HorizontalAlignment = v ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        StripBox.VerticalAlignment = v ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
        StripBox.Padding = v ? new Thickness(9, 0, 9, 0) : new Thickness(0, 9, 0, 9);
        // Разделительная линия всегда со стороны кадра
        StripBox.BorderThickness = v ? new Thickness(1, 0, 0, 0) : new Thickness(0, 1, 0, 0);

        StripRow.Orientation = v ? Orientation.Vertical : Orientation.Horizontal;
        StripRow.HorizontalAlignment = v ? HorizontalAlignment.Center : HorizontalAlignment.Center;
        StripRow.VerticalAlignment = v ? VerticalAlignment.Center : VerticalAlignment.Center;
    }

    /// <summary>
    /// Длина ряда: чуть больше окна, крайние ячейки обрезает его рамка. Всегда
    /// нечётная — середина обязана существовать, на ней держится вся модель.
    /// </summary>
    public static int CellCount(double extent, double pitch) =>
        (int)Math.Ceiling(Math.Max(1, extent) / 2 / Math.Max(1, pitch)) * 2 + 1;

    /// <summary>
    /// Кадр под n-й ячейкой. Обратная сторона правила «текущий всегда в центре»:
    /// зная позицию ячейки, индекс не нужно нигде хранить.
    /// </summary>
    public static int CellIndex(int current, int cellCount, int n) =>
        current - cellCount / 2 + n;

    bool BuildCells()
    {
        var v = StripVertical;
        var want = CellCount(v ? Root.ActualHeight : Root.ActualWidth, Pitch);
        if (_cells.Count == want && _builtVertical == v) return false;
        _builtVertical = v;

        StripRow.Children.Clear();
        _cells.Clear();
        _accent = null;             // тема могла смениться — кисть перечитываем
        for (var n = 0; n < want; n++)
        {
            var cell = new Border
            {
                Width = SlotW,
                Height = SlotH,
                Margin = v ? new Thickness(0, SlotGap / 2, 0, SlotGap / 2)
                           : new Thickness(SlotGap / 2, 0, SlotGap / 2, 0),
                Background = Res("GalleryTransparencyBrush"),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(2),
                // Uniform, а не UniformToFill: ячейка фиксированная, а кадры и
                // вертикальные, и горизонтальные — обрезка врала бы о содержимом
                Child = new Image { Stretch = Stretch.Uniform },
            };
            // PointerPressed, а не Tapped: переход по нажатию отзывчивее, и не
            // приходится гадать, уложился ли жест в порог «тапа»
            cell.PointerPressed += OnCellPressed;
            StripRow.Children.Add(cell);
            _cells.Add(cell);
        }
        _stripCenter = -1;      // ряд другой длины — ехать некуда, показываем сразу
        return true;
    }

    /// <summary>Кисти ячеек берутся из ресурсов однажды — смена темы требует пересборки.</summary>
    void RebuildStrip()
    {
        if (_cells.Count == 0) return;
        _cells.Clear();
        StripRow.Children.Clear();
        _builtVertical = !StripVertical;      // заставляем пересобрать под текущую сторону
        BuildCells();
        UpdateStrip();
        ApplyStripOpacity();
    }

    /// <summary>
    /// Перерисовка ряда. Зовётся из Render(), то есть до 30 раз в секунду при
    /// автоповторе: только присваивания, ни файлового ввода-вывода, ни декода.
    /// </summary>
    void UpdateStrip()
    {
        if (StripBox.Visibility != Visibility.Visible || _cells.Count == 0) return;

        var cur = CurrentIndex;
        var k = _cells.Count / 2;

        for (var n = 0; n < _cells.Count; n++)
        {
            var i = cur < 0 ? -1 : CellIndex(cur, _cells.Count, n);
            var cell = _cells[n];
            var img = (Image)cell.Child;
            var inside = i >= 0 && i < _list.Count;
            var isCurrent = n == k && inside;

            // Текущий кадр выделен ДВУМЯ признаками: рамкой и яркостью. Только
            // цветом нельзя — §7.9, тот же довод, что у бейджей слотов.
            //
            // Сравнение перед записью не косметика: при шаге на кадр меняются
            // ровно две ячейки из пятнадцати, а запись в свойство зависимости
            // стоит инвалидации, даже когда значение то же самое.
            var opacity = !inside ? 0 : isCurrent ? 1.0 : 0.55;
            if (cell.Opacity != opacity) cell.Opacity = opacity;

            var border = isCurrent ? Accent : null;
            if (!ReferenceEquals(cell.BorderBrush, border)) cell.BorderBrush = border;

            // За краем папки ячейка держит место, но пуста: ряд не должен ползать
            // вбок на первых и последних кадрах — текущий обязан стоять в центре
            if (!inside) { img.Source = null; continue; }
            img.Source = _thumbs.TryGetValue(_list[i].Path, out var t) ? t.img : null;
        }

        // Лента едет, а не перескакивает: сдвигаем ряд на шаг назад и отпускаем.
        // Прыжок через полпапки (Home/End, переход по номеру) анимировать нечем —
        // содержимое всё равно меняется целиком.
        if (_stripCenter >= 0 && _stripCenter != cur && _animations)
        {
            var dx = (cur - _stripCenter) * Pitch;
            if (Math.Abs(dx) <= Pitch * 3) Slide(dx);
        }
        _stripCenter = cur;
    }

    void Slide(double dx)
    {
        // При автоповторе 30 Гц шаги приходят чаще, чем длится анимация. Перезапуск
        // с From=dx превратил бы движение в дрожание на месте, поэтому в плотном
        // потоке анимацию глушим и ряд просто встаёт на место (§7.6).
        var now = Stopwatch.GetTimestamp();
        var dense = _slideAt != 0 && now - _slideAt < Stopwatch.Frequency * 0.12;
        _slideAt = now;

        _slide?.Stop();
        if (dense) { _stripShift.X = _stripShift.Y = 0; return; }

        // Ряд едет вдоль своей оси, поэтому вторую координату обнуляем: после
        // смены стороны на ней мог остаться сдвиг от прошлой анимации
        if (StripVertical) _stripShift.X = 0; else _stripShift.Y = 0;

        var a = new DoubleAnimation
        {
            From = dx,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(120),   // тот же шаг, что у зума (§7.5)
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(a, _stripShift);
        Storyboard.SetTargetProperty(a, StripVertical ? "Y" : "X");

        _slide = new Storyboard();
        _slide.Children.Add(a);
        _slide.Begin();
    }

    /// <summary>
    /// Индекс считаем от позиции ячейки в ряду, а не храним в Tag: через ABI
    /// C#/WinRT упакованный int возвращается уже не тем, чем клали, и распаковка
    /// молча не срабатывает. Позиция и так однозначна — центр всегда текущий кадр.
    /// </summary>
    void OnCellPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed) return;
        e.Handled = true;

        var n = _cells.IndexOf((Border)sender);
        if (n < 0) return;

        var i = CellIndex(CurrentIndex, _cells.Count, n);
        if (i >= 0 && i < _list.Count && i != CurrentIndex) Show(i);

        // Фокус возвращаем на холст: иначе следующий Space нажмёт ячейку,
        // а не отложит кадр — главная клавиша приложения перестала бы работать
        FocusCanvas();
    }

    // ---------- миниатюры ----------

    bool InStrip(int i)
    {
        var cur = CurrentIndex;
        return cur >= 0 && Math.Abs(i - cur) <= _cells.Count / 2;
    }

    /// <summary>
    /// Заказ миниатюр на видимое окно. Зовётся только с осевшего индекса: при
    /// автоповторе заказывать по кадру на каждый щелчок значит утопить очередь
    /// в работе, которая устареет раньше, чем закончится.
    /// </summary>
    void LoadStripThumbs()
    {
        if (StripBox.Visibility != Visibility.Visible) return;
        var cur = CurrentIndex;
        if (cur < 0) return;

        var k = _cells.Count / 2;
        for (var i = cur - k; i <= cur + k; i++)
        {
            if (i < 0 || i >= _list.Count) continue;
            var e = _list[i];
            if (e.Size == 0) continue;              // пустой файл: диск спрашивать незачем
            if (_thumbs.ContainsKey(e.Path) || _thumbBad.Contains(e.Path)) continue;
            if (!_thumbLoading.Add(e.Path)) continue;
            _ = LoadThumbAsync(e.Path, i);
        }
        EvictThumbs(cur);
    }

    async Task LoadThumbAsync(string path, int idx)
    {
        await _thumbGate.WaitAsync();
        try
        {
            // Заказ мог устареть, пока стоял в очереди: человек за это время мог
            // уйти на другой конец папки, и эта миниатюра больше не нужна
            if (!InStrip(idx)) return;

            var file = await StorageFile.GetFileFromPathAsync(path);

            // SingleItem, а не PicturesView: последний обрезает кадр в квадрат.
            // Оболочка применяет EXIF-поворот сама и держит собственный дисковый кэш.
            using var t = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem, ThumbPx, ThumbnailOptions.ResizeThumbnail);

            if (t is null || t.Size == 0) { _thumbBad.Add(path); return; }

            var img = new BitmapImage();
            await img.SetSourceAsync(t);
            _thumbs[path] = (img, idx);
            UpdateStrip();
        }
        catch (Exception)
        {
            _thumbBad.Add(path);    // битый или незнакомый файл — ячейка останется пустой
        }
        finally
        {
            _thumbGate.Release();
            _thumbLoading.Remove(path);
        }
    }

    /// <summary>
    /// Вытеснение по расстоянию, как в основном кэше (§6.5): LRU при развороте
    /// направления выбрасывает ровно то, куда человек сейчас пойдёт назад.
    /// Индекс сохранён вместе с миниатюрой — искать его по списку значит платить
    /// линейным поиском по каждому ключу на каждом оседании.
    /// </summary>
    void EvictThumbs(int center)
    {
        if (_thumbs.Count <= ThumbCap) return;

        var doomed = _thumbs
            .OrderByDescending(kv => Math.Abs(kv.Value.idx - center))
            .Take(_thumbs.Count - ThumbCap)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var p in doomed) _thumbs.Remove(p);
    }

    /// <summary>Смена папки: прежние пути уже не встретятся, держать их незачем.</summary>
    void ClearThumbs()
    {
        _thumbs.Clear();
        _thumbBad.Clear();
        _stripCenter = -1;
    }

    // ---------- клавиатура ----------

    /// <summary>
    /// Две клавиши на одно действие. `T` — потому что §4.1 зарезервировала её
    /// под ленту ещё до того, как лента появилась. `F6` — потому что буквенный
    /// акселератор на русской раскладке попадает на клавишу с другой надписью
    /// (`T` там подписана «е»), и человек, читающий шпаргалку, её не найдёт.
    /// Было `F4`, освобождена под показ распознанного (§4.12).
    /// </summary>
    void OnToggleStrip(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        // Пока открыта чистка, T принадлежит ей — там это «убрать рамку блока»
        if (CleanPanel.Visibility == Visibility.Visible) return;
        _lastCmd = "лента";
        SetStrip(!_stripOn);
        UpdateOverlay();
        Announce(_stripOn ? "Лента миниатюр показана" : "Лента миниатюр скрыта", important: false);
    }

    /// <summary>Смена стороны: ряд надо переразвернуть и пересобрать под другой шаг.</summary>
    void SetStripSide(string side)
    {
        if (SettingsStore.Current.StripSide == side) return;
        SettingsStore.Current.StripSide = side;
        SettingsStore.Commit();

        _stripShift.X = _stripShift.Y = 0;
        PlaceStrip();
        RebuildStrip();
        LoadStripThumbs();

        // Видимость не менялась, поэтому ApplyStrip рано выйдет — отступ тоста
        // правим здесь: снизу лента его подпирает, справа не мешает вовсе
        Toast.Margin = new Thickness(0, 0, 0,
            StripBox.Visibility == Visibility.Visible && !StripVertical ? StripH + 16 : 24);
    }

    /// <summary>Общая точка для клавиш и галки в настройках — состояние одно.</summary>
    void SetStrip(bool on)
    {
        if (_stripOn == on) return;
        _stripOn = on;
        if (ChkStrip.IsChecked != on) ChkStrip.IsChecked = on;
        SettingsStore.Current.StripVisible = on;
        SettingsStore.Commit();
        ApplyStrip();
    }
}
