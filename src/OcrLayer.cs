using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Core;

namespace Gallery;

/// <summary>
/// Показ распознанного текста поверх кадра (SPEC.md §4.12).
///
/// Это не перевод и не отладка, а поверка: распознавание ошибается тихо, и
/// единственный способ увидеть ЧТО прочиталось и не пропущено ли что-то — это
/// положить прочитанное на те же места и сравнить глазами с оригиналом.
///
/// Отсюда три свойства, которых нет у слоя перевода:
///   — наведение делает ячейку почти прозрачной, чтобы под ней был виден
///     исходный текст: сравнивать надо с оригиналом, а не по памяти;
///   — правая кнопка копирует текст, и работает она даже по прозрачной ячейке,
///     потому что прозрачность здесь означает «не загораживай», а не «выключись»;
///   — шрифт моноширинный: в нём видно, что «1» это единица, а не «I».
/// </summary>
public sealed partial class MainWindow
{
    bool _ocrOn;
    string? _ocrShown;

    /// <summary>
    /// Гашение ячейки под курсором считает САМ СЛОЙ, а не каждая ячейка своей
    /// парой Entered/Exited.
    ///
    /// Причина измерена: Exited приходит не всегда — при скачке указателя
    /// ячейка получает Entered и остаётся прозрачной навсегда, выглядя пропавшей.
    /// Здесь состояние выводится из одной координаты, поэтому пропустить нечего:
    /// ровно одна ячейка погашена, остальные целы, всегда.
    /// </summary>
    void InitOcrHover()
    {
        OcrLayer.PointerMoved += (_, e) =>
        {
            var p = e.GetCurrentPoint(OcrLayer).Position;
            foreach (var child in OcrLayer.Children)
            {
                if (child is not Border c) continue;
                var l = Canvas.GetLeft(c);
                var t = Canvas.GetTop(c);
                var under = p.X >= l && p.X <= l + c.Width &&
                            p.Y >= t && p.Y <= t + c.Height;

                var want = under ? 0.12 : 1.0;
                if (Math.Abs(c.Opacity - want) > 0.01) c.Opacity = want;
            }
        };

        OcrLayer.PointerExited += (_, __) => RestoreOcrCells();
        OcrLayer.PointerCanceled += (_, __) => RestoreOcrCells();
        OcrLayer.PointerCaptureLost += (_, __) => RestoreOcrCells();
    }

    void RestoreOcrCells()
    {
        foreach (var child in OcrLayer.Children)
            if (child is Border c) c.Opacity = 1.0;
    }

    void OnToggleOcr(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        _lastCmd = "распознанное";
        UpdateOverlay();

        if (_ocrOn)
        {
            SetOcrLayer(false);
            Announce("Распознанный текст скрыт", important: false);
            return;
        }

        if (_currentPath is null) return;
        _ocrOn = true;
        _ = ShowOcrAsync();
    }

    void SetOcrLayer(bool on)
    {
        _ocrOn = on;
        if (on) return;

        OcrLayer.Visibility = Visibility.Collapsed;
        OcrLayer.Children.Clear();
        _ocrShown = null;
    }

    /// <summary>
    /// Распознать, если ещё не распознавали. Перевод для этого не нужен и не
    /// запускается: поверка должна работать и без сети, и без ключа.
    /// </summary>
    async Task ShowOcrAsync()
    {
        var path = _currentPath;
        if (path is null) return;

        if (!_pages.ContainsKey(path))
        {
            // Перевод этой же страницы уже идёт и распознаёт её сам. Отменить
            // его ради поверки — молча угробить минуту работы и запрос к API;
            // ждём, слой покажет сам перевод, когда закончит
            if (WorkCard.Visibility == Visibility.Visible)
            {
                ShowToast("Идёт перевод — слой появится, когда закончится", error: false);
                return;
            }

            _transCts?.Cancel();
            _transCts = new CancellationTokenSource();
            var ct = _transCts.Token;
            try
            {
                ShowWork("Распознаю текст…", progress: null);
                _pages[path] = await Ocr.ReadAsync(path, ct);
                HideWork();
            }
            catch (OperationCanceledException) { HideWork(); return; }
            catch (Exception ex)
            {
                HideWork();
                Log.Error($"распознавание: {ex}");
                ShowToast("Не удалось распознать текст", error: true);
                SetOcrLayer(false);
                return;
            }
        }

        if (_currentPath == path && _ocrOn) RenderOcrLayer();
    }

    void RenderOcrLayer()
    {
        var path = _currentPath;
        if (path is null || !_pages.TryGetValue(path, out var page)) return;
        if (_fitW < 1) return;

        OcrLayer.Children.Clear();
        OcrLayer.Width = _fitW;
        OcrLayer.Height = _fitH;

        var scale = _fitW / Math.Max(1, page.ImgW);

        if (page.Boxes.Count == 0)
        {
            OcrLayer.Visibility = Visibility.Visible;
            _ocrShown = path;
            ShowToast("Текст на странице не найден", error: false);
            return;
        }

        for (var i = 0; i < page.Boxes.Count; i++)
        {
            var b = page.Boxes[i];

            var pad = 3.0;
            var x = b.X * scale - pad;
            var y = b.Y * scale - pad;
            var w = b.W * scale + pad * 2;
            var h = b.H * scale + pad * 2;

            var text = new TextBlock
            {
                // Моноширинный: только в нём с одного взгляда видно единицу
                // вместо «I» и прочие подмены похожих знаков
                FontFamily = new FontFamily("Consolas"),
                Text = b.Source,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 180, 255, 190)),
            };
            FitText(text, w - 6, h * 2.4 - 4, Math.Max(9, b.H * scale * 0.8));

            var order = new TextBlock
            {
                // Номер по порядку чтения: по нему видно, в какой
                // последовательности реплики уйдут переводчику (§4.9)
                Text = (i + 1).ToString(),
                FontFamily = new FontFamily("Consolas"),
                FontSize = Math.Max(8, Math.Min(14, h * 0.3)),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 120, 200, 255)),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(2, 0, 0, 0),
            };

            var grid = new Grid();
            grid.Children.Add(text);
            grid.Children.Add(order);

            // Ячейка растёт СИММЕТРИЧНО от середины строки, а не вниз: иначе
            // длинная реплика свешивается из пузыря и накрывает соседнюю
            var cellH = Math.Max(h, text.DesiredSize.Height + 6);
            y -= (cellH - h) / 2;

            var cell = new Border
            {
                Width = w,
                Height = cellH,
                Background = new SolidColorBrush(Color.FromArgb(232, 16, 20, 16)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 90, 150, 100)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(2),
                Child = grid,
                Tag = b,
            };

            cell.RightTapped += OnOcrCellRightTapped;

            Canvas.SetLeft(cell, x);
            Canvas.SetTop(cell, y);
            OcrLayer.Children.Add(cell);
        }

        OcrLayer.Visibility = Visibility.Visible;
        _ocrShown = path;
    }

    /// <summary>
    /// Правая кнопка по ячейке — её текст в буфер. С Shift — вся страница
    /// разом, в порядке чтения: так проверяют, не пропущено ли что-то.
    /// </summary>
    void OnOcrCellRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;

        var shift = InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        string? text = null;
        string said = "";

        if (shift)
        {
            var path = _currentPath;
            if (path is not null && _pages.TryGetValue(path, out var page))
            {
                var sb = new StringBuilder();
                for (var i = 0; i < page.Boxes.Count; i++)
                    sb.AppendLine($"{i + 1}. {page.Boxes[i].Source}");
                text = sb.ToString();
                said = $"Вся страница скопирована: {page.Boxes.Count} реплик";
            }
        }
        else if (sender is Border { Tag: TextBox2 box })
        {
            text = box.Source;
            said = $"Скопировано: {Short(box.Source)}";
        }

        if (string.IsNullOrEmpty(text)) return;

        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            ShowToast(said, error: false);
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось скопировать: {ex.Message}", error: true);
        }
    }

    static string Short(string s) => s.Length <= 40 ? s : s[..40] + "…";

    /// <summary>Смена кадра: слой поверки следует за ней, как и слой перевода.</summary>
    void SyncOcrLayer()
    {
        if (!_ocrOn) return;

        if (_currentPath is null) { OcrLayer.Visibility = Visibility.Collapsed; return; }
        if (_ocrShown == _currentPath) return;

        OcrLayer.Children.Clear();
        OcrLayer.Visibility = Visibility.Collapsed;
        _ocrShown = null;
        _ = ShowOcrAsync();
    }
}
