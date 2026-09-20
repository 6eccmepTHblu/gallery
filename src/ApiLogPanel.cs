using System;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Gallery;

/// <summary>
/// Окно обращений к API и подпись источника перевода (SPEC.md §4.11).
///
/// Обе вещи об одном: перевод приходит извне, и это должно быть видно, а не
/// выводиться из качества текста. Подозрение «а точно ли работает API» возникает
/// первым же и не проверяется никак, кроме как показом самого обмена.
/// </summary>
public sealed partial class MainWindow
{
    void InitApiLog()
    {
        ApiLogClear.Click += (_, __) => { ApiLog.Clear(); BuildApiLog(); };
        ApiLogCopy.Click += (_, __) => CopyApiLog();

        // Журнал пополняется из фонового потока перевода
        ApiLog.Changed += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (ApiLogPanel.Visibility == Visibility.Visible) BuildApiLog();
        });
    }

    void OnToggleApiLog(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        _lastCmd = "журнал API";
        UpdateOverlay();

        var show = ApiLogPanel.Visibility != Visibility.Visible;
        ApiLogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) { BuildApiLog(); SetChrome(true); }
        else FocusCanvas();
    }

    void BuildApiLog()
    {
        ApiLogRows.Children.Clear();
        var items = ApiLog.Recent;

        if (items.Count == 0)
        {
            ApiLogRows.Children.Add(new TextBlock
            {
                Text = SettingsStore.Current.TranslateBackend == "api"
                    ? "Обращений ещё не было. Нажмите F2 на странице с текстом."
                    : "Перевод настроен на локальный — к API обращений не будет.\n" +
                      "Переключить: Ctrl+, → Перевод → Чем переводить.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                Foreground = Res("GalleryTextSecondaryBrush"),
            });
            return;
        }

        foreach (var x in items)
        {
            var head = new TextBlock
            {
                Text = $"{x.At:HH:mm:ss}   {x.Model}   {x.Status}   {x.Ms:F0} мс",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Res(x.Failed ? "GalleryWarningBrush" : "GalleryAccentBrush"),
            };

            var card = new StackPanel { Spacing = 6 };
            card.Children.Add(head);
            card.Children.Add(Body("ушло", x.Request));
            card.Children.Add(Body("пришло", x.Response));

            ApiLogRows.Children.Add(new Border
            {
                Background = Res("GallerySurfaceBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                Child = card,
            });
        }
    }

    TextBlock Body(string label, string text) => new()
    {
        // Выделяемый текст: журнал существует ради того, чтобы его прочитали
        // и при нужде скопировали в переписку с поставщиком
        Text = $"{label}:\n{text}",
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        Foreground = Res("GalleryTextSecondaryBrush"),
    };

    void CopyApiLog()
    {
        var sb = new StringBuilder();
        foreach (var x in ApiLog.Recent)
        {
            sb.AppendLine($"=== {x.At:yyyy-MM-dd HH:mm:ss}  {x.Model}  {x.Status}  {x.Ms:F0} мс");
            sb.AppendLine($"--- {x.Url}");
            sb.AppendLine("--- ушло:");
            sb.AppendLine(x.Request);
            sb.AppendLine("--- пришло:");
            sb.AppendLine(x.Response);
            sb.AppendLine();
        }

        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(sb.Length == 0 ? "(журнал пуст)" : sb.ToString());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
            ShowToast("Журнал скопирован", error: false);
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось скопировать: {ex.Message}", error: true);
        }
    }

    /// <summary>
    /// Подпись источника. Показывается ровно тогда, когда на экране есть перевод:
    /// в остальное время это был бы шум.
    /// </summary>
    void ShowTransSource(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            TransSource.Visibility = Visibility.Collapsed;
            return;
        }

        TransSource.Text = text;
        // Оверлей F12 занимает тот же угол — уступаем ему место, когда он включён
        TransSource.Margin = _overlayOn ? new Thickness(12, 210, 0, 0) : new Thickness(12, 12, 0, 0);
        TransSource.Visibility = Visibility.Visible;
    }
}
