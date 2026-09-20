using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace Gallery;

/// <summary>
/// Веха 5: оболочка окна — хром, полноэкранный режим, drag&amp;drop,
/// настройки, шпаргалка (SPEC.md §7.2, §4.5).
/// </summary>
public sealed partial class MainWindow
{
    readonly UISettings _ui = new();
    readonly AccessibilitySettings _access = new();
    bool _animations = true;
    bool _highContrast;

    // Хром прячется через 2400 мс покоя, курсор — через 2000 мс (§7.2)
    readonly DispatcherTimer _chromeHide = new() { Interval = TimeSpan.FromMilliseconds(2400) };
    bool _chromeVisible = true;
    bool _fullScreen;
    bool _focusInChrome;
    double _textScale = 1.0;
    Windows.Foundation.Point _lastPointer;

    void InitChrome()
    {
        _animations = SafeAnimations();
        _highContrast = SafeHighContrast();

        // События UISettings приходят НЕ из UI-потока — маршалить обязательно
        _ui.AnimationsEnabledChanged += (_, __) =>
            DispatcherQueue.TryEnqueue(() => _animations = SafeAnimations());
        _ui.ColorValuesChanged += (_, __) => DispatcherQueue.TryEnqueue(ApplyTheme);
        try { _access.HighContrastChanged += (_, __) => DispatcherQueue.TryEnqueue(ApplyTheme); }
        catch (Exception) { }

        // «Размер текста» в специальных возможностях — отдельная от DPI настройка,
        // и WinUI её сам не применяет (§7.9)
        _textScale = SafeTextScale();
        try { _ui.TextScaleFactorChanged += (_, __) => DispatcherQueue.TryEnqueue(ApplyTextScale); }
        catch (Exception) { }

        // Хром не прячем, пока клавиатурный фокус внутри него. GotFocus/LostFocus
        // всплывают от вложенных кнопок, обходить дерево не нужно.
        StatusBar.GotFocus += (_, __) => _focusInChrome = true;
        StatusBar.LostFocus += (_, __) => _focusInChrome = false;

        _chromeHide.Tick += (_, __) => { _chromeHide.Stop(); SetChrome(false); };

        // Любое движение мыши возвращает хром; движение меньше 4 px игнорируем,
        // иначе панель мигает при панорамировании
        Root.PointerMoved += (_, e) =>
        {
            var p = e.GetCurrentPoint(Root).Position;
            if (Math.Abs(p.X - _lastPointer.X) < 4 && Math.Abs(p.Y - _lastPointer.Y) < 4) return;
            _lastPointer = p;
            SetChrome(true);
        };

        // Tab вызывает хром и оставляет его — интерфейс, существующий только при
        // наведении мыши, для клавиатурного пользователя не существует вовсе
        Root.GettingFocus += (_, __) => SetChrome(true);

        CanvasRoot.DragOver += OnDragOver;
        CanvasRoot.Drop += OnDrop;
        CanvasRoot.DragLeave += (_, __) => DropHint.Visibility = Visibility.Collapsed;

        // `+` `=` `-` основного ряда: числовой Key в XAML не записывается.
        // Плюс на большинстве раскладок — это Shift+`=`, поэтому два акселератора.
        AddAccel((VirtualKey)187, VirtualKeyModifiers.None, OnZoomIn);       // VK_OEM_PLUS
        AddAccel((VirtualKey)187, VirtualKeyModifiers.Shift, OnZoomIn);
        AddAccel((VirtualKey)189, VirtualKeyModifiers.None, OnZoomOut);      // VK_OEM_MINUS

        OpenSettingsFile.Click += (_, __) => FileOps.RevealInExplorer(
            Path.Combine(SettingsStore.Dir, "settings.json"));

        ChkStrip.IsChecked = SettingsStore.Current.StripVisible;
        ChkStrip.Checked += (_, __) => SetStrip(true);
        ChkStrip.Unchecked += (_, __) => SetStrip(false);

        TransBackendBox.SelectedIndex = SettingsStore.Current.TranslateBackend == "api" ? 1 : 0;
        ApiRows.Visibility = TransBackendBox.SelectedIndex == 1
            ? Visibility.Visible : Visibility.Collapsed;
        TransBackendBox.SelectionChanged += (_, __) =>
        {
            var api = TransBackendBox.SelectedIndex == 1;
            SettingsStore.Current.TranslateBackend = api ? "api" : "local";
            SettingsStore.Commit();
            ApiRows.Visibility = api ? Visibility.Visible : Visibility.Collapsed;
            ShowApiKeyState();
        };

        // TextChanged, а не только LostFocus: Esc закрывает панель, НЕ переводя
        // фокус, и набранный адрес иначе теряется молча
        ChkSendImage.IsChecked = SettingsStore.Current.ApiSendImage;
        ChkSendImage.Click += (_, __) =>
        {
            SettingsStore.Current.ApiSendImage = ChkSendImage.IsChecked == true;
            SettingsStore.Commit();
        };

        ApiUrlBox.Text = SettingsStore.Current.ApiUrl;
        ApiUrlBox.TextChanged += (_, __) =>
        {
            SettingsStore.Current.ApiUrl = ApiUrlBox.Text.Trim();
            SettingsStore.Touch();
        };
        ApiUrlBox.LostFocus += (_, __) =>
        {
            SettingsStore.Current.ApiUrl = ApiUrlBox.Text.Trim();
            SettingsStore.Commit();
        };

        ApiModelBox.Text = SettingsStore.Current.ApiModel;
        ApiModelBox.TextChanged += (_, __) =>
        {
            SettingsStore.Current.ApiModel = ApiModelBox.Text.Trim();
            SettingsStore.Touch();
        };
        ApiModelBox.LostFocus += (_, __) =>
        {
            SettingsStore.Current.ApiModel = ApiModelBox.Text.Trim();
            SettingsStore.Commit();
        };

        // Ключ не проходит ни через настройки, ни через журнал: из поля — сразу
        // в зашифрованный файл, и поле тут же очищается
        ApiKeySave.Click += (_, __) =>
        {
            ApiKeyStore.Save(ApiKeyBox.Password);
            ApiKeyBox.Password = "";
            ShowApiKeyState();
            ShowToast("Ключ сохранён", error: false);
        };
        ApiKeyClear.Click += (_, __) =>
        {
            ApiKeyStore.Clear();
            ApiKeyBox.Password = "";
            ShowApiKeyState();
            ShowToast("Ключ удалён", error: false);
        };
        ShowApiKeyState();

        StripSideBox.SelectedIndex = SettingsStore.Current.StripSide == "right" ? 1 : 0;
        StripSideBox.SelectionChanged += (_, __) =>
            SetStripSide(StripSideBox.SelectedIndex == 1 ? "right" : "bottom");

        StripAlpha.Value = Math.Clamp(SettingsStore.Current.StripOpacity, 25, 100);
        StripAlpha.ValueChanged += (_, e) =>
        {
            SettingsStore.Current.StripOpacity = (int)e.NewValue;
            SettingsStore.Touch();
            ApplyStripOpacity();
        };

        ThemeBox.SelectedIndex = SettingsStore.Current.Theme switch
        {
            "light" => 1, "system" => 2, _ => 0,
        };
        ThemeBox.SelectionChanged += (_, __) =>
        {
            SettingsStore.Current.Theme = ThemeBox.SelectedIndex switch
            {
                1 => "light", 2 => "system", _ => "dark",
            };
            SettingsStore.Touch();
            ApplyThemeChoice();
        };

        ChkAdvance.IsChecked = SettingsStore.Current.AdvanceAfterCopy;
        ChkSpaceNext.IsChecked = SettingsStore.Current.SpaceIsNext;
        ChkAdvance.Click += (_, __) =>
        {
            SettingsStore.Current.AdvanceAfterCopy = ChkAdvance.IsChecked == true;
            SettingsStore.Commit();
        };
        ChkSpaceNext.Click += (_, __) =>
        {
            SettingsStore.Current.SpaceIsNext = ChkSpaceNext.IsChecked == true;
            SettingsStore.Commit();
        };

        EmptyPick.Click += (_, __) => _ = PickSourceFolderAsync();
        EmptyReveal.Click += (_, __) =>
        {
            if (_emptyFolder is not null) FileOps.RevealInExplorer(_emptyFolder);
        };
        EmptyKeys.Click += (_, __) => ToggleCheatSheet();

        // Клик по пути открывает папку. Когда кадр показан — через него: Проводник
        // откроет ту же папку и заодно подсветит в ней текущий файл, что всегда
        // полезнее голой папки. Фокус возвращаем на холст, иначе следующий Space
        // достанется кнопке, а не отбору.
        FolderButton.Click += (_, __) =>
        {
            if (_currentPath is not null) Reveal();
            else if (_list.Folder is { } f) FileOps.RevealInExplorer(f);
            FocusCanvas();
        };

        // Кнопки карточки ошибки. После каждой возвращаем фокус на канвас — иначе
        // Space достанется кнопке, а не отбору.
        ErrCopyAnyway.Click += (_, __) => { _ = CopyToSlotAsync(0); Zoom.Focus(FocusState.Programmatic); };
        ErrReveal.Click += (_, __) =>
        {
            if (_currentPath is not null) FileOps.RevealInExplorer(_currentPath);
            Zoom.Focus(FocusState.Programmatic);
        };
        ErrSkip.Click += (_, __) => { Step(+1); Zoom.Focus(FocusState.Programmatic); };
        ErrStore.Click += (_, __) => { OpenStore(); Zoom.Focus(FocusState.Programmatic); };

        BuildCheatSheet();
        ApplyThemeChoice();
        _chromeHide.Start();
    }

    void AddAccel(VirtualKey key, VirtualKeyModifiers mod,
                  TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var a = new KeyboardAccelerator { Key = key, Modifiers = mod };
        a.Invoked += handler;
        Root.KeyboardAccelerators.Add(a);
    }

    void CloseWindow() =>
        RequestCloseAsync();     // не Close(): он не поднимает AppWindow.Closing

    bool SafeAnimations()
    {
        try { return _ui.AnimationsEnabled; }
        catch (Exception) { return true; }
    }

    bool SafeHighContrast()
    {
        try { return _access.HighContrast; }
        catch (Exception) { return false; }
    }

    double SafeTextScale()
    {
        try { return Math.Clamp(_ui.TextScaleFactor, 1.0, 2.25); }
        catch (Exception) { return 1.0; }
    }

    // Базовые размеры запоминаем при первом проходе: иначе повторные вызовы
    // возведут коэффициент в степень
    readonly Dictionary<TextBlock, double> _baseFontSize = new();

    /// <summary>
    /// Применить системный «Размер текста». Размеры бейджей и свотчей не трогаем —
    /// §7.9 прямо требует не привязывать их к масштабу текста.
    /// </summary>
    void ApplyTextScale()
    {
        _textScale = SafeTextScale();
        Walk(Root);

        void Walk(DependencyObject o)
        {
            if (o is TextBlock tb)
            {
                if (!_baseFontSize.TryGetValue(tb, out var basis))
                    _baseFontSize[tb] = basis = tb.FontSize;
                tb.FontSize = basis * _textScale;
            }
            var count = VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(o, i));
        }
    }

    /// <summary>
    /// Кисти, созданные из кода, — это конкретные экземпляры из активного словаря,
    /// а не живые ссылки. Значит при смене темы поддеревья, собранные кодом,
    /// обязаны пересобраться (§7.9).
    /// </summary>
    /// <summary>Показать, есть ключ или нет. Сам ключ не показываем никогда.</summary>
    void ShowApiKeyState() =>
        ApiKeyState.Text = ApiKeyStore.Present
            ? "Ключ сохранён и зашифрован на вашу учётную запись Windows."
            : "Ключ не задан — перевод через API работать не будет.";

    void ApplyTheme()
    {
        _highContrast = SafeHighContrast();

        // Mica подмешивает цвет обоев — в высокой контрастности это недопустимо
        SystemBackdrop = _highContrast ? null : new MicaBackdrop();

        BuildCheatSheet();
        RebuildStrip();
        RenderSlots();
        if (SettingsPanel.Visibility == Visibility.Visible) RenderSlotRows();
        UpdateFrameBadges();
        ApplyTextScale();
    }

    /// <summary>Тема из настроек. По умолчанию тёмная — §7.5.</summary>
    void ApplyThemeChoice()
    {
        Root.RequestedTheme = SettingsStore.Current.Theme switch
        {
            "light" => ElementTheme.Light,
            "system" => ElementTheme.Default,
            _ => ElementTheme.Dark,
        };
        ApplyTheme();
    }

    // ---------- хром ----------

    void SetChrome(bool visible)
    {
        if (visible)
        {
            _chromeVisible = true;
            TitleBarArea.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
            StatusBar.Visibility = Visibility.Visible;
            Zoom.SetCursor(InputSystemCursorShape.Arrow);
            ApplyStrip();
            _chromeHide.Stop(); _chromeHide.Start();
            return;
        }

        // Интерфейс, существующий только при наведении мыши, для незрячего
        // пользователя не существует вовсе — при активном UIA-клиенте не прячем (§7.9)
        if (AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged)) return;

        // Не прячем, пока открыт оверлей, панель или в хроме клавиатурный фокус.
        // Таймер перевзводим — иначе после закрытия панели хром больше не спрячется.
        if (_focusInChrome ||
            SettingsPanel.Visibility == Visibility.Visible ||
            CheatSheet.Visibility == Visibility.Visible)
        {
            _chromeHide.Start();
            return;
        }

        _chromeVisible = false;
        if (_fullScreen) TitleBarArea.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        ApplyStrip();

        // Курсор прячем отсюда, а не отдельным таймером: тот срабатывал раньше
        // (2000 мс против 2400 мс), и его условие «хром уже скрыт» в штатном
        // потоке не выполнялось никогда — курсор не прятался вообще.
        Zoom.HideCursor();
    }

    void OnFullScreen(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        _fullScreen = !_fullScreen;
        AppWindow.SetPresenter(_fullScreen
            ? AppWindowPresenterKind.FullScreen
            : AppWindowPresenterKind.Default);
        TitleBarArea.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        SetChrome(true);
    }

    /// <summary>
    /// Упор на границе папки: короткий отскок, чтобы не выглядело зависанием.
    /// Цикла нет — конец папки это сигнал «сессия разбора закончена» (§4.6).
    /// </summary>
    public void BounceEdge(bool forward)
    {
        // Не затираем сообщение, которое человек обязан прочитать: упор в край
        // папки — уведомление фоновое, а «уже есть» или ошибка — нет
        if (!_toastSticky)
            ShowToast(forward ? $"Последний файл · {_list.Count}" : "Первый файл",
                      error: false);

        if (!_animations) return;      // системное «уменьшить анимацию» уважаем

        var to = forward ? -8.0 : 8.0;
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        { KeyTime = TimeSpan.FromMilliseconds(90), Value = to });
        anim.KeyFrames.Add(new EasingDoubleKeyFrame
        { KeyTime = TimeSpan.FromMilliseconds(260), Value = 0 });

        Storyboard.SetTarget(anim, CanvasShift);
        Storyboard.SetTargetProperty(anim, "X");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
    }

    // ---------- drag & drop ----------

    void OnDragOver(object sender, DragEventArgs e)
    {
        // Без явного AcceptedOperation курсор показывает «запрещено» и Drop не приходит
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = false;
        e.Handled = true;
        DropHint.Visibility = Visibility.Visible;
    }

    async void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var def = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            // Берём первый, остальные игнорируем: предсказуемо и без эвристик (§4.5)
            var first = items.FirstOrDefault();
            if (first is StorageFolder f) OpenFolder(f.Path, null);
            else if (first is StorageFile file) OpenFolder(Path.GetDirectoryName(file.Path)!, file.Path);
        }
        catch (Exception ex) { ShowToast($"Не удалось открыть: {ex.Message}", error: true); }
        finally { def.Complete(); }
    }

    // ---------- открытие ----------

    /// <summary>Ctrl+O — файл, Ctrl+Shift+O — папка. Одна точка входа на оба.</summary>
    void OpenPicker(bool folder)
    {
        _lastCmd = folder ? "открыть папку" : "открыть файл";
        if (folder) _ = PickSourceFolderAsync();
        else _ = PickFileAsync();
    }

    async Task PickFileAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            // Тот же список, что и при перечислении папки: расхождение означало бы,
            // что часть файлов видно в галерее, но нельзя открыть через Ctrl+O
            foreach (var ext in FolderList.Exts) picker.FileTypeFilter.Add(ext);

            var file = await picker.PickSingleFileAsync();
            if (file is not null) OpenFolder(Path.GetDirectoryName(file.Path)!, file.Path);
        }
        catch (Exception ex) { ShowToast($"Не удалось открыть: {ex.Message}", error: true); }
    }

    async Task PickSourceFolderAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) OpenFolder(folder.Path, null);
        }
        catch (Exception ex) { ShowToast($"Не удалось открыть: {ex.Message}", error: true); }
    }

    /// <summary>
    /// Store может быть выключен политикой (LTSC, корпоративная машина). Тогда
    /// показываем название нужного пакета текстом, а не молчим (§8.3).
    /// </summary>
    void OpenStore()
    {
        var query = _currentPath is null ? null : StoreQuery(Path.GetFileName(_currentPath));
        if (query is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "ms-windows-store://search/?query=" + Uri.EscapeDataString(query))
            { UseShellExecute = true });
        }
        catch (Exception)
        {
            ShowToast("Магазин недоступен. Нужен пакет: " + query, error: true);
        }
    }

    // ---------- панели ----------

    /// <summary>Фокус возвращается на кадр — он таб-стоп и носитель UIA-имени.</summary>
    void FocusCanvas() => Img.Focus(FocusState.Programmatic);

    void ToggleSettings()
    {
        var show = SettingsPanel.Visibility != Visibility.Visible;
        CheatSheet.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) { RenderSlotRows(); SetChrome(true); SettingsPanel.Focus(FocusState.Programmatic); }
        else FocusCanvas();
    }

    void OnCheatSheet(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        // Пока открыта чистка, F1 принадлежит ей — там это «показать исходник»
        if (CleanPanel.Visibility == Visibility.Visible) return;
        ToggleCheatSheet();
    }

    void ToggleCheatSheet()
    {
        var show = CheatSheet.Visibility != Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Collapsed;
        CheatSheet.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) { SetChrome(true); CheatSheet.Focus(FocusState.Programmatic); }
        else FocusCanvas();
    }

    void OnSettings(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        ToggleSettings();
    }

    /// <summary>Девять строк слотов: цифра, свотч, путь, кнопки.</summary>
    void RenderSlotRows()
    {
        SlotRows.Children.Clear();
        for (int i = 0; i < Slots.Count; i++)
        {
            int slot = i;
            var bound = !string.IsNullOrEmpty(_slots.FolderOf(slot));

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                VerticalAlignment = VerticalAlignment.Center,
                Background = bound ? SlotBrush(slot) : Res("GallerySlotEmptyBrush"),
                Child = new TextBlock
                {
                    Text = (slot + 1).ToString(),
                    FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = bound ? Res("GalleryBadgeTextBrush")
                                      : Res("GalleryTextTertiaryBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(dot, 0);

            var path = new TextBlock
            {
                Text = bound ? _slots.FolderOf(slot)! : "не выбрана",
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = bound ? Res("GalleryTextSecondaryBrush")
                                  : Res("GalleryTextTertiaryBrush"),
            };
            ToolTipService.SetToolTip(path, bound ? _slots.FolderOf(slot) : null);
            Grid.SetColumn(path, 1);

            var unbind = new Button
            {
                Content = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 12, MinHeight = 32, MinWidth = 32,
                Visibility = bound ? Visibility.Visible : Visibility.Collapsed,
            };
            AutomationSetName(unbind, $"Отвязать слот {slot + 1}");
            unbind.Click += (_, __) => { _slots.Bind(slot, null); RenderSlotRows(); };
            Grid.SetColumn(unbind, 3);

            var pick = new Button { Content = bound ? "Сменить" : "Выбрать…", FontSize = 12, MinHeight = 32 };
            AutomationSetName(pick, bound
                ? $"Слот {slot + 1}, папка {_slots.NameOf(slot)}, сменить"
                : $"Слот {slot + 1}, папка не выбрана, выбрать");
            pick.Click += async (_, __) => { await RebindSlotAsync(slot); RenderSlotRows(); };
            Grid.SetColumn(pick, 2);

            row.Children.Add(dot);
            row.Children.Add(path);
            row.Children.Add(pick);
            row.Children.Add(unbind);
            SlotRows.Children.Add(row);
        }
    }

    void BuildCheatSheet()
    {
        // Пересобирается при смене темы, поэтому обязана быть идемпотентной
        CheatNav.Children.Clear();
        CheatPick.Children.Clear();
        CheatZoom.Children.Clear();
        CheatMisc.Children.Clear();

        void Add(StackPanel host, string keys, string what)
        {
            var g = new Grid { ColumnSpacing = 12 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var k = new TextBlock
            {
                Text = keys, FontFamily = new FontFamily("Consolas"), FontSize = 12,
                MinWidth = 120,
                Foreground = Res("GalleryAccentBrush"),
            };
            var v = new TextBlock
            {
                Text = what, FontSize = 13,
                Foreground = Res("GalleryTextPrimaryBrush"),
            };
            Grid.SetColumn(k, 0); Grid.SetColumn(v, 1);
            g.Children.Add(k); g.Children.Add(v);
            host.Children.Add(g);
        }

        void Head(StackPanel host, string title) => host.Children.Add(new TextBlock
        {
            Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = Res("GalleryTextTertiaryBrush"),
        });

        Head(CheatNav, "НАВИГАЦИЯ");
        Add(CheatNav, "← →  колесо", "предыдущий / следующий");
        Add(CheatNav, "Home  End", "первый / последний");
        Add(CheatNav, "F5", "пересканировать папку");
        Add(CheatNav, "Ctrl+O", "открыть файл");
        Add(CheatNav, "Ctrl+Shift+O", "открыть папку");

        Head(CheatPick, "ОТБОР");
        Add(CheatPick, "Space  Enter", "отложить в слот 1");
        Add(CheatPick, "1 … 9", "отложить в слот N");
        Add(CheatPick, "Ctrl+Shift+N", "сменить папку слота");
        Add(CheatPick, "Ctrl+Z", "отменить последнее");
        Add(CheatPick, "Ctrl+C", "файл в буфер обмена");
        Add(CheatPick, "Ctrl+Shift+E", "показать в Проводнике");

        Head(CheatZoom, "ПРОСМОТР");
        Add(CheatZoom, "Ctrl+колесо", "масштаб к курсору");
        Add(CheatZoom, "+ =   −", "масштаб от центра");
        Add(CheatZoom, "F", "вписать");
        Add(CheatZoom, "A", "1:1");
        Add(CheatZoom, "Ctrl+стрелки", "панорамирование");

        Head(CheatMisc, "ПРОЧЕЕ");
        Add(CheatNav, "F6", "лента миниатюр");
        Add(CheatNav, "F2", "перевод страницы");
        Add(CheatNav, "F4", "что распозналось");
        Add(CheatMisc, "F3", "запросы к API");
        Add(CheatMisc, "F7", "недавние папки");
        Add(CheatMisc, "F8", "очистка от текста");
        Add(CheatMisc, "F11", "полный экран");
        Add(CheatMisc, "Ctrl+,", "настройки");
        Add(CheatMisc, "F1", "эта шпаргалка");
        Add(CheatMisc, "F12", "счётчики производительности");
        Add(CheatMisc, "Ctrl+W", "закрыть окно");
        Add(CheatMisc, "Esc", "шаг назад / закрыть");
    }

    /// <summary>Esc слоями: панель → шпаргалка → зум → полный экран → закрыть (§4.1).</summary>
    bool TryEscapeLayer()
    {
        // Ошибочный тост не гаснет сам — Esc первым делом убирает его
        if (Toast.Visibility == Visibility.Visible &&
            ToastClose.Visibility == Visibility.Visible)
        { HideToast(); return true; }

        if (OcrLayer.Visibility == Visibility.Visible)
        { SetOcrLayer(false); return true; }

        if (ApiLogPanel.Visibility == Visibility.Visible)
        { ApiLogPanel.Visibility = Visibility.Collapsed; FocusCanvas(); return true; }

        if (HistoryPanel.Visibility == Visibility.Visible)
        { ShowHistory(false); return true; }

        if (CleanPanel.Visibility == Visibility.Visible)
        { ShowClean(false); return true; }

        if (ModelCard.Visibility == Visibility.Visible)
        { ModelCard.Visibility = Visibility.Collapsed; FocusCanvas(); return true; }

        if (WorkCard.Visibility == Visibility.Visible)
        { _transCts?.Cancel(); return true; }

        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
            SettingsStore.Commit();     // Esc не переводит фокус — фиксируем набранное сами
            FocusCanvas();
            return true;
        }

        if (CheatSheet.Visibility == Visibility.Visible)
        { CheatSheet.Visibility = Visibility.Collapsed; FocusCanvas(); return true; }

        if (Math.Abs(Zoom.ZoomFactor - 1f) > 0.001f) { ResetZoom(); FocusCanvas(); return true; }

        if (_fullScreen)
        {
            _fullScreen = false;
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            TitleBarArea.Visibility = Visibility.Visible;
            SetChrome(true);
            return true;
        }
        return false;
    }
}
