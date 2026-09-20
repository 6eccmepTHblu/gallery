using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Gallery;

/// <summary>
/// Недавние папки (SPEC.md §4.6.2) — F7.
///
/// Позиция в каждой папке хранилась и раньше, но добраться до неё можно было
/// только заново открыв папку через проводник. Список делает эту память видимой:
/// строка — папка и кадр, на котором её оставили, двойной щелчок — вернуться
/// туда же.
///
/// Исчезнувшие папки выбрасываются ПРИ ОТКРЫТИИ списка, а не в фоне: внешний
/// диск или сетевая шара отключаются и подключаются, и вычищать их по таймеру
/// значило бы терять позиции у тех, кто просто отсоединил флешку.
/// </summary>
public sealed partial class MainWindow
{
    void InitHistory()
    {
        HistoryClose.Click += (_, __) => ShowHistory(false);
    }

    void OnToggleHistory(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        _lastCmd = "недавние папки";
        UpdateOverlay();
        ShowHistory(HistoryPanel.Visibility != Visibility.Visible);
    }

    void ShowHistory(bool on)
    {
        HistoryPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) { BuildHistory(); SetChrome(true); }
        else FocusCanvas();
    }

    void BuildHistory()
    {
        // Сначала чистка: строка, ведущая в никуда, хуже отсутствия строки
        var gone = SettingsStore.PruneMissing(Directory.Exists);

        HistoryRows.Children.Clear();
        var folders = new List<string>(SettingsStore.RecentFolders());

        HistoryNote.Text = gone.Count > 0
            ? $"Убрано исчезнувших папок: {gone.Count}"
            : "";
        HistoryNote.Visibility = gone.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (folders.Count == 0)
        {
            HistoryRows.Children.Add(new TextBlock
            {
                Text = "Пока пусто. Откройте папку — она появится здесь вместе с кадром, "
                     + "на котором вы её оставили.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                Foreground = Res("GalleryTextSecondaryBrush"),
            });
            return;
        }

        foreach (var folder in folders)
        {
            SettingsStore.Current.FolderLast.TryGetValue(folder, out var last);
            HistoryRows.Children.Add(Row(folder, last));
        }
    }

    Border Row(string folder, string? last)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = folder,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Res("GalleryTextPrimaryBrush"),
        });
        text.Children.Add(new TextBlock
        {
            // Имя кадра, а не полный путь: папка уже написана строкой выше
            Text = last is null ? "кадр не запомнен" : Path.GetFileName(last),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Res("GalleryTextSecondaryBrush"),
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var del = new Button { Content = "Удалить", MinHeight = 32 };
        // Забываем только запись. Сама папка и кадры на диске не трогаются —
        // здесь нет ни одной операции, которую нельзя было бы повторить
        del.Click += (_, __) =>
        {
            SettingsStore.Forget(folder);
            BuildHistory();
        };
        ToolTipService.SetToolTip(del, "Убрать из списка. Файлы на диске не трогаются");
        Grid.SetColumn(del, 1);
        grid.Children.Add(del);

        var row = new Border
        {
            Padding = new Thickness(12, 8, 8, 8),
            CornerRadius = new CornerRadius(8),
            Background = Res("GallerySurfaceBrush"),
            Child = grid,
            IsTabStop = true,
        };

        row.DoubleTapped += (_, e) => { e.Handled = true; OpenFromHistory(folder, last); };

        // Клавиатурный путь к тому же действию: двойной щелчок мышью — не
        // единственный способ работать со списком
        row.KeyDown += (_, e) =>
        {
            if (e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)) return;
            e.Handled = true;
            OpenFromHistory(folder, last);
        };

        return row;
    }

    void OpenFromHistory(string folder, string? last)
    {
        ShowHistory(false);

        if (!Directory.Exists(folder))
        {
            // Пропала между открытием списка и щелчком — бывает с сетевой шарой
            SettingsStore.Forget(folder);
            ShowToast("Папка больше не существует", error: true);
            return;
        }

        // Кадр мог исчезнуть, а папка остаться: открываем папку, но без наводки
        // на несуществующий файл — иначе получим тост «формат не поддерживается»
        OpenFolder(folder, last is not null && File.Exists(last) ? last : null);
    }
}
