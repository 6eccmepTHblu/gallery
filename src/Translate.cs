using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace Gallery;

/// <summary>Разобранная страница: блоки текста и размер кадра, в котором их нашли.</summary>
sealed class PageText
{
    public required List<TextBox2> Boxes;
    public int ImgW, ImgH;

    /// <summary>
    /// Рамки СТРОК, как их отдал детектор, — до сшивки в реплики и без отбора
    /// по прочитанному. Нужны очистке от текста: там единица работы — строка,
    /// а не фраза. Сшивка по облакам объединяет «How risky are you feeling~?»
    /// со звуком в полукадре ниже — для перевода это одна реплика и так надо,
    /// для стирания это рамка на пол-листа. null — распознавал запасной движок,
    /// у него строк не спросить.
    /// </summary>
    public List<SkiaSharp.SKRectI>? Lines;

    /// <summary>
    /// Чем переведена страница. Не отладка, а честность: перевод приходит
    /// извне, и человек должен видеть источник, а не догадываться о нём (§4.11).
    /// </summary>
    public string Source = "";

    /// <summary>
    /// Медианная высота СТРОКИ на странице, в пикселях кадра распознавания.
    ///
    /// Кегль перевода берётся отсюда, а не из высоты каждой рамки: у однострочной
    /// реплики рамка вдвое ниже, чем у двухстрочной, и если считать по ней, набор
    /// на одной странице поедет в разные размеры. У комикса кегль по странице
    /// почти постоянный — медиана его и ловит, игнорируя выбросы вроде звуков.
    /// </summary>
    public double MedianLineH = 24;
}

/// <summary>
/// Перевод страницы поверх кадра (SPEC.md §4.9).
///
/// Пиксели не трогаем вовсе: перевод — это слой XAML внутри зумируемого
/// содержимого. Отсюда три свойства даром — он масштабируется вместе с
/// картинкой, гасится мгновенно и никогда не портит исходный файл.
/// </summary>
public sealed partial class MainWindow
{
    readonly Dictionary<string, PageText> _pages = new(StringComparer.OrdinalIgnoreCase);

    Translator? _mt;
    ApiTranslator? _api;
    string _pageSource = "";

    // Карточка персонажей копится на ПАПКУ: том читают подряд, и знание о том,
    // кто какого рода и кто к кому на «ты», должно переживать смену страницы
    readonly Dictionary<string, string> _volumeState = new(StringComparer.OrdinalIgnoreCase);

    CancellationTokenSource? _transCts;
    bool _transOn;
    string? _transShown;        // какая страница сейчас нарисована на слое

    void InitTranslate()
    {
        WorkCancel.Click += (_, __) => _transCts?.Cancel();

        ModelGet.Click += (_, __) =>
        {
            ModelCard.Visibility = Visibility.Collapsed;
            _ = DownloadModelAsync();
        };
    }

    /// <summary>
    /// F2, а не буква: буквенный акселератор на русской раскладке попадает на
    /// клавишу с другой надписью, и подпись в шпаргалке врала бы (§4.4).
    /// </summary>
    void OnToggleTranslate(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        _lastCmd = "перевод";
        UpdateOverlay();

        if (_transOn)
        {
            SetTranslate(false);
            Announce("Перевод скрыт", important: false);
            return;
        }

        if (_currentPath is null) return;

        if (!Translator.ModelPresent)
        {
            ModelCard.Visibility = Visibility.Visible;
            SetChrome(true);
            return;
        }

        SetTranslate(true);
        _ = TranslatePageAsync();
    }

    void SetTranslate(bool on)
    {
        _transOn = on;
        if (on) return;

        _transCts?.Cancel();
        HideWork();
        TransLayer.Visibility = Visibility.Collapsed;
        TransLayer.Children.Clear();
        ShowTransSource(null);
        _transShown = null;
    }

    /// <summary>
    /// Смена кадра при включённом переводе. Готовую страницу показываем
    /// мгновенно, новую — переводим сами: иначе читать комикс значит жать F2
    /// на каждой странице.
    /// </summary>
    void SyncTranslate()
    {
        if (!_transOn) return;

        if (_currentPath is null)
        {
            TransLayer.Visibility = Visibility.Collapsed;
            ShowTransSource(null);
            return;
        }
        if (_transShown == _currentPath) return;

        TransLayer.Children.Clear();
        TransLayer.Visibility = Visibility.Collapsed;
        ShowTransSource(null);
        _transShown = null;

        if (_pages.ContainsKey(_currentPath)) RenderTranslate();
        else _ = TranslatePageAsync();
    }

    async Task TranslatePageAsync()
    {
        var path = _currentPath;
        if (path is null) return;

        // Проверяем НАЛИЧИЕ ПЕРЕВОДА, а не страницы: F4 кладёт сюда страницу
        // только с распознанным текстом, и по одному лишь присутствию ключа
        // перевод был бы сочтён готовым, а слой вышел бы пустым
        if (_pages.TryGetValue(path, out var done) &&
            done.Boxes.Any(b => !string.IsNullOrWhiteSpace(b.Translated)))
        {
            RenderTranslate();
            return;
        }

        _transCts?.Cancel();
        _transCts = new CancellationTokenSource();
        var ct = _transCts.Token;

        try
        {
            // Если F4 уже распознал эту страницу — второй раз не читаем
            PageText page;
            if (_pages.TryGetValue(path, out var known) && known.Boxes.Count > 0)
            {
                page = known;
            }
            else
            {
                ShowWork("Распознаю текст…", progress: null);
                page = await Ocr.ReadAsync(path, ct);
            }
            var boxes = page.Boxes;

            if (boxes.Count == 0)
            {
                HideWork();
                ShowToast("Текст на странице не найден", error: false);
                SetTranslate(false);
                return;
            }

            var viaApi = SettingsStore.Current.TranslateBackend == "api" && ApiKeyStore.Present;
            if (viaApi)
            {
                try
                {
                    await TranslateViaApiAsync(page, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Сеть отвалилась, ключ протух, поставщик отказал — человек
                    // не должен остаться ни с чем: локальная модель уже на диске
                    Log.Warn($"API: {ex.Message}");
                    ShowToast($"API не ответил ({ex.Message}) — перевожу локально", error: true);
                    await TranslateLocallyAsync(boxes, ct);
                }
            }
            else
            {
                await TranslateLocallyAsync(boxes, ct);
            }

            page.Source = _pageSource;
            _pages[path] = page;

            HideWork();
            if (_currentPath == path && _transOn) RenderTranslate();
            Announce($"Переведено блоков: {boxes.Count}", important: false);
        }
        catch (OperationCanceledException)
        {
            HideWork();
        }
        catch (Exception ex)
        {
            HideWork();
            Log.Error($"перевод: {ex}");
            ShowToast("Не удалось перевести страницу", error: true);
            SetTranslate(false);
        }
    }

    /// <summary>
    /// Локальный перевод: по реплике за раз. Контекста у Marian нет и быть не
    /// может — он обучен на отдельных предложениях.
    /// </summary>
    async Task TranslateLocallyAsync(List<TextBox2> boxes, CancellationToken ct)
    {
        _pageSource = "перевод: на этом компьютере";
        _mt ??= new Translator();
        if (!_mt.Ready)
        {
            ShowWork("Загружаю модель перевода…", progress: null);
            await Task.Run(_mt.Load, ct);
        }

        for (var i = 0; i < boxes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            ShowWork($"Перевожу {i + 1} из {boxes.Count}", (i + 1.0) / boxes.Count);

            var src = boxes[i].Source;
            boxes[i].Translated = await Task.Run(() => _mt!.Translate(src, ct), ct);
        }
    }

    /// <summary>
    /// Перевод страницы ОДНИМ запросом: реплики уходят пронумерованными и в
    /// порядке чтения, вместе с карточкой персонажей тома.
    ///
    /// Ради этого всё и делалось. Согласование рода и обращения «ты/вы» —
    /// не свойство модели, а следствие того, что она видит диалог целиком.
    /// </summary>
    async Task TranslateViaApiAsync(PageText page, CancellationToken ct)
    {
        var boxes = page.Boxes;
        var path = _currentPath;
        if (path is null) return;

        _api ??= new ApiTranslator();
        var folder = _list.Folder ?? "";
        _api.State = _volumeState.TryGetValue(folder, out var st) ? st : "";

        // Картинка кодируется здесь, а не внутри переводчика: так видно, что
        // именно и когда покидает компьютер, и это же место решает — отправлять ли
        string? image = null;
        if (SettingsStore.Current.ApiSendImage)
        {
            ShowWork("Готовлю страницу для модели…", progress: null);
            image = await PageImage.EncodeAsync(path, ct);
        }

        ShowWork($"Перевожу страницу целиком ({boxes.Count} реплик)…", progress: null);
        var lines = boxes.Select(b => b.Source).ToList();
        var map = await _api.TranslatePageAsync(lines, ct, image);

        // Сверяем по номерам, а не по порядку: модель могла переставить или
        // потерять строку, и молча сдвинуть перевод на соседний пузырь — худшее,
        // что здесь может случиться
        var missing = 0;
        for (var i = 0; i < boxes.Count; i++)
        {
            // Исправленный моделью оригинал кладём на место сырого: слой F4
            // обязан показывать то, что реально ушло в перевод, а не мусор
            // движка — иначе поверка врёт про качество страницы
            if (ApiTranslator.Fixed.TryGetValue(i + 1, out var en)
                && !string.IsNullOrWhiteSpace(en))
                boxes[i].Source = en.Trim();

            boxes[i].Fallback = false;
            if (map.TryGetValue(i + 1, out var ru) && !string.IsNullOrWhiteSpace(ru))
            {
                boxes[i].Translated = ru;
            }
            else
            {
                boxes[i].Fallback = true;
                missing++;
            }
        }

        // Слой поверки мог быть открыт во время перевода — перерисуем,
        // иначе на экране останется старый, неисправленный текст
        if (_ocrOn) RenderOcrLayer();

        if (!string.IsNullOrWhiteSpace(_api.State)) _volumeState[folder] = _api.State;

        // Найденное моделью добавляем как обычные реплики — с пометкой, что
        // геометрия у них приблизительная: её дала модель, а не распознавание
        foreach (var (x0, y0, x1, y1, en, ru) in ApiTranslator.Extra)
        {
            boxes.Add(new TextBox2
            {
                Source = en,
                Translated = ru,
                X = x0 * page.ImgW,
                Y = y0 * page.ImgH,
                W = Math.Max(8, (x1 - x0) * page.ImgW),
                H = Math.Max(8, (y1 - y0) * page.ImgH),
                FromImage = true,
            });
        }

        // Наложения сворачиваем ВСЕГДА, а не только когда модель что-то
        // дописала: две рамки на одном тексте — это две плашки друг поверх
        // друга, и ростом их не развести, они налезают уже в исходном размере
        {
            var before = boxes.Count;
            var clean = Ocr.Dedupe(boxes);
            if (clean.Count < before)
            {
                boxes.Clear();
                boxes.AddRange(ReadOrder.Sort(clean));
            }
        }

        var model = SettingsStore.Current.ApiModel;
        var found = ApiTranslator.Extra.Count;
        _pageSource = missing == 0
            ? $"перевод: {model}" + (found > 0 ? $", найдено по картинке: {found}" : "")
            : $"перевод: {model}, {missing} локально"
              + (found > 0 ? $", найдено по картинке: {found}" : "");

        if (missing > 0)
        {
            // Недостающее добираем локально: лучше смесь, чем дыры на странице
            ShowWork($"Добираю {missing} реплик локально…", progress: null);
            _mt ??= new Translator();
            if (!_mt.Ready) await Task.Run(_mt.Load, ct);

            foreach (var b in boxes.Where(b => string.IsNullOrWhiteSpace(b.Translated)))
            {
                ct.ThrowIfCancellationRequested();
                var src = b.Source;
                b.Translated = await Task.Run(() => _mt!.Translate(src, ct), ct);
            }
            Log.Warn($"API вернул не все реплики: не хватило {missing} из {boxes.Count}");
        }
    }

    // ---------- отрисовка ----------

    /// <summary>
    /// Слой строится в координатах «вписанного» кадра: ImgHost имеет размер
    /// _fitW×_fitH, и Canvas внутри него живёт в той же системе. Зум и
    /// панорамирование пересчитывать не нужно — их делает ScrollView.
    /// </summary>
    void RenderTranslate()
    {
        var path = _currentPath;
        if (path is null || !_pages.TryGetValue(path, out var page)) return;
        if (_fitW < 1 || _srcW < 1) return;

        TransLayer.Children.Clear();
        TransLayer.Width = _fitW;
        TransLayer.Height = _fitH;

        // Распознавали на отдельном декоде: его размер не равен ни оригиналу,
        // ни тому, что лежит в кэше. Масштаб считаем именно от него.
        var scale = _fitW / Math.Max(1, page.ImgW);

        // Сначала считаем рамки ВСЕХ плашек и только потом растим: рост
        // вслепую наводит плашку на соседнюю, и обе становятся нечитаемы
        var shown = new List<TextBox2>(page.Boxes.Count);
        var bases = new List<Plates.Rect>(page.Boxes.Count);
        foreach (var b in page.Boxes)
        {
            if (string.IsNullOrWhiteSpace(b.Translated)) continue;

            // Рамка обрезает выносные элементы букв, а русский текст длиннее
            // английского — запас нужен, иначе исходник выглядывает из-под
            // подложки. По вертикали он ВПЯТЕРО меньше горизонтального:
            // сверху и снизу почти всегда сосед, и лишние поля съедают именно
            // его место, а выносные элементы закрываются и малым отступом
            var px = b.W * 0.06 + 3;
            var py = b.H * 0.01 + 0.4;

            shown.Add(b);
            bases.Add(new Plates.Rect((b.X - px) * scale, (b.Y - py) * scale,
                                      (b.W + px * 2) * scale, (b.H + py * 2) * scale));
        }

        for (var i = 0; i < shown.Count; i++)
        {
            var b = shown[i];
            var text = b.Translated!;
            var (x, y, w, h) = (bases[i].X, bases[i].Y, bases[i].W, bases[i].H);

            // Русский текст длиннее английского, и в исходную рамку он часто не
            // влезает. Пузырь вокруг текста почти всегда имеет запас, поэтому
            // сначала пробуем нарастить рамку, и только потом мельчить набор.
            // Рост ограничен полуторным: замечено на живой странице, что при
            // 1.9 длинная реплика свешивается за край пузыря. Лучше мельче набор,
            // чем текст поверх рисунка.
            var (top, bottom) = Plates.Band(bases, i, gap: 2, height: _fitH);
            var want = Math.Min(h * 1.5, h + page.MedianLineH * scale * 1.4);
            var grown = Math.Clamp(want, h, Math.Max(h, bottom - top));

            var tb = new TextBlock
            {
                Text = text.ToUpperInvariant(),     // капслок — набор комиксов
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(b.DarkBg ? Colors.White : Color.FromArgb(255, 20, 20, 22)),
            };

            var used = FitText(tb, w - 6, grown - 4, page.MedianLineH * scale * 1.34);

            // Рамка ровно под набор, но не уже исходной: пустые поля по бокам
            // выглядят заплаткой, а слишком тесная рамка режет выносные элементы
            (y, var boxH) = Plates.Fit(y, h, Math.Min(grown, used + 6), top, bottom);

            var host = new Border
            {
                Width = w,
                Height = boxH,
                // Реплику, которую модель не вернула, показываем с рамкой:
                // это не её перевод, и выдавать его за таковой нельзя
                BorderThickness = new Thickness(b.Fallback ? 2 : 0),
                BorderBrush = b.Fallback ? Res("GalleryWarningBrush") : null,
                CornerRadius = new CornerRadius(Math.Min(10, boxH * 0.25)),
                Background = new SolidColorBrush(Color.FromArgb(255, b.BgR, b.BgG, b.BgB)),
                Padding = new Thickness(3, 2, 3, 2),
                Child = tb,
                Opacity = 0,
            };

            Canvas.SetLeft(host, x);
            Canvas.SetTop(host, y);
            TransLayer.Children.Add(host);

            FadeIn(host);
        }

        TransLayer.Visibility = Visibility.Visible;
        ShowTransSource(page.Source);
        _transShown = path;
    }

    /// <summary>
    /// Подбор кегля. Начинаем с размера, взятого от страницы, и мельчим только
    /// если не влезает: перебором, а не формулой — перенос слов делает высоту
    /// ступенчатой функцией от кегля, и решения в замкнутом виде у неё нет.
    /// Возвращает занятую высоту, чтобы вызывающий подогнал рамку.
    /// </summary>
    static double FitText(TextBlock tb, double w, double h, double startSize)
    {
        if (w < 4 || h < 4) { tb.FontSize = 6; return h; }

        var from = Math.Clamp(startSize, 7, 44);
        for (var size = from; size >= 6.0; size -= 0.5)
        {
            tb.FontSize = size;
            tb.LineHeight = size * 1.12;
            tb.Measure(new Windows.Foundation.Size(w, double.PositiveInfinity));
            if (tb.DesiredSize.Height <= h && tb.DesiredSize.Width <= w + 0.5)
                return tb.DesiredSize.Height;
        }
        tb.FontSize = 6;
        return h;
    }

    void FadeIn(UIElement el)
    {
        if (!_animations) { el.Opacity = 1; return; }

        var a = new DoubleAnimation
        {
            From = 0, To = 1,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(a, el);
        Storyboard.SetTargetProperty(a, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(a);
        sb.Begin();
    }

    // ---------- карточка работы ----------

    void ShowWork(string text, double? progress)
    {
        WorkText.Text = text;
        WorkRing.IsActive = true;
        WorkBar.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        if (progress is { } p) WorkBar.Value = Math.Clamp(p, 0, 1);
        WorkCard.Visibility = Visibility.Visible;
        SetChrome(true);
    }

    void HideWork()
    {
        WorkRing.IsActive = false;          // иначе анимация крутится в скрытой карточке
        WorkCard.Visibility = Visibility.Collapsed;
    }

    // ---------- модель ----------

    async Task DownloadModelAsync()
    {
        _transCts?.Cancel();
        _transCts = new CancellationTokenSource();
        var ct = _transCts.Token;

        try
        {
            ShowWork("Скачиваю модель перевода…", 0);
            var progress = new Progress<double>(p =>
                ShowWork($"Скачиваю модель перевода… {p * 100:F0}%", p));

            await Translator.DownloadAsync(progress, ct);

            HideWork();
            ShowToast("Модель готова — перевод работает без сети", error: false);

            SetTranslate(true);
            await TranslatePageAsync();
        }
        catch (OperationCanceledException)
        {
            HideWork();
        }
        catch (Exception ex)
        {
            HideWork();
            Log.Error($"загрузка модели: {ex}");
            ShowToast($"Не удалось скачать модель: {ex.Message}", error: true);
        }
    }
}
