using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Gallery;

/// <summary>
/// Веха 4: слоты и копирование — ядро задачи (SPEC.md §5).
/// </summary>
public sealed partial class MainWindow
{
    readonly Slots _slots = new();

    // Цвета слотов фиксированы по индексу и не настраиваются. Цвет — вторичный
    // признак, первичный — цифра на бейдже (§7.5). В высококонтрастной теме
    // палитра подменяется системной кистью: там любой свой цвет — это ошибка.
    static readonly Color[] SlotColors =
    {
        Color.FromArgb(255, 0x4C, 0xC2, 0xFF), Color.FromArgb(255, 0x3D, 0xDC, 0x97),
        Color.FromArgb(255, 0xFF, 0xC5, 0x3D), Color.FromArgb(255, 0xFF, 0x8A, 0x65),
        Color.FromArgb(255, 0xB9, 0x8E, 0xFF), Color.FromArgb(255, 0x4D, 0xD0, 0xE1),
        Color.FromArgb(255, 0xF0, 0x62, 0x92), Color.FromArgb(255, 0xAE, 0xD5, 0x81),
        Color.FromArgb(255, 0xA1, 0x88, 0x7F),
    };

    readonly DispatcherTimer _toastTimer = new();

    /// <summary>Кисть из активного словаря темы, а не снимок цвета.</summary>
    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    Brush SlotBrush(int i) =>
        _highContrast ? Res("GalleryAccentBrush") : new SolidColorBrush(SlotColors[i]);

    // Причина отказа по слоту — состояние UI, в Slots ему делать нечего
    readonly string?[] _slotError = new string?[Slots.Count];
    int _burst;
    bool _lastToastWasCopy;
    bool _closing;
    bool _closePrompted;

    void InitCopying()
    {
        _toastTimer.Tick += (_, __) => { _toastTimer.Stop(); HideToast(); };
        _slots.Changed += () => DispatcherQueue.TryEnqueue(() =>
        {
            RenderSlots();
            UpdateFrameBadges();     // скан асинхронный, бейджи иначе устаревают
        });
        _slots.Failed += (slot, msg) => DispatcherQueue.TryEnqueue(() =>
        {
            // Привязку НЕ стираем: диск мог отключиться временно (§5.9)
            _slotError[slot] = msg;
            RenderSlots();
            ShowToast(_slots.Broken[slot]
                ? $"Слот {slot + 1} недоступен · Ctrl+Shift+{slot + 1} — выбрать заново"
                : $"Слот {slot + 1}: {msg}", error: true);
        });
        ToastClose.Click += (_, __) => HideToast();

        // Клавиши, меняющие файлы, обрабатываем в KeyDown, а не акселератором:
        // только здесь доступен KeyStatus.WasKeyDown, а без него придержанная
        // на полсекунды цифра создаёт пятнадцать копий (§4.3).
        Root.KeyDown += OnKeyDown;

        AppWindow.Closing += OnClosing;
        RenderSlots();
    }

    static bool Down(VirtualKey k) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _rawKey = $"VK={(int)e.Key} ctrl={Down(VirtualKey.Control)} " +
                  $"shift={Down(VirtualKey.Shift)} rep={e.KeyStatus.WasKeyDown}";
        UpdateOverlay();

        // ГЛАВНАЯ ЗАЩИТА: автоповтор не должен множить файловые операции
        if (e.KeyStatus.WasKeyDown) return;

        // Ctrl-сочетания разбираем здесь, а не акселератором. Ctrl+Z и цифры и так
        // жили тут ради KeyStatus.WasKeyDown, а держать одну группу клавиш в двух
        // механизмах — лишний повод для расхождений (§4.4).
        //
        // Поле ввода номера — исключение: там Ctrl+C копирует текст, а не файл.
        if (Down(VirtualKey.Control) && GotoBox.Visibility != Visibility.Visible &&
            HandleCtrl(e.Key, Down(VirtualKey.Shift)))
        {
            e.Handled = true;
            UpdateOverlay();        // одно место на все ветки, а не по вызову в каждой
            return;
        }

        // Пока открыта панель или шпаргалка, цифры и пробел принадлежат им,
        // а не отбору. Перенести на акселератор со ScopeOwner нельзя: там нет
        // KeyStatus, а на нём держится защита выше.
        if (SettingsPanel.Visibility == Visibility.Visible ||
            CheatSheet.Visibility == Visibility.Visible ||
            CleanPanel.Visibility == Visibility.Visible ||
            HistoryPanel.Visibility == Visibility.Visible ||
            ApiLogPanel.Visibility == Visibility.Visible ||
            GotoBox.Visibility == Visibility.Visible) return;

        var ctrl = Down(VirtualKey.Control);
        var shift = Down(VirtualKey.Shift);
        int slot = e.Key switch
        {
            >= VirtualKey.Number1 and <= VirtualKey.Number9 => e.Key - VirtualKey.Number1,
            >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad9 => e.Key - VirtualKey.NumberPad1,
            _ => -1,
        };

        if (slot >= 0)
        {
            // Кадр ещё не показан — не даём скопировать не то, что человек видит (§6.1).
            // Ctrl+Z намеренно не гейтим: он работает со стеком операций, а не с кадром.
            if (_shown != _currentPath) return;
            e.Handled = true;
            if (ctrl && shift) _ = RebindSlotAsync(slot);
            else if (!ctrl && !shift) _ = CopyToSlotAsync(slot);
            return;
        }

        if (e.Key is VirtualKey.Space or VirtualKey.Enter && !ctrl && !shift)
        {
            // Переключатель «пробел листает» относится только к пробелу: Enter
            // остаётся отбором в любом случае.
            var listen = SettingsStore.Current.SpaceIsNext && e.Key == VirtualKey.Space;
            if (!listen && _shown != _currentPath) return;
            e.Handled = true;

            // Space — это «место сохранения» из постановки задачи, то есть слот 1 (§4.2)
            if (listen) Step(+1);
            else _ = CopyToSlotAsync(0);
            return;
        }

    }

    /// <summary>
    /// Ctrl-сочетания. Запятая распознаётся в двух написаниях: `VK_OEM_COMMA` даёт
    /// её на латинских раскладках, а на русской запятая набирается как Shift и
    /// клавиша `/?` (`VK_OEM_2`) — измерено через `VkKeyScanEx`. Без второго
    /// написания подпись «Ctrl+,» в шпаргалке описывала бы клавишу, которой на
    /// русской клавиатуре нет: `VK_OEM_COMMA` там печатает «б».
    /// </summary>
    bool HandleCtrl(VirtualKey key, bool shift)
    {
        switch (key)
        {
            case VirtualKey.C when !shift: CopyToClipboard(); return true;
            // Ctrl+E и Ctrl+Shift+E — одно действие. Второе написание не роскошь:
            // измерено, что на этой машине голый Ctrl+E доходит до окна уже как
            // Ctrl+C (код 67 вместо 69), а с Shift проходит целым (§4.4).
            case VirtualKey.E: Reveal(); return true;
            case VirtualKey.Z when !shift: _lastCmd = "отмена"; DoUndo(); return true;
            case VirtualKey.W when !shift: CloseWindow(); return true;
            case VirtualKey.O: OpenPicker(folder: shift); return true;

            case (VirtualKey)188 when !shift:        // VK_OEM_COMMA — латинские раскладки
            case (VirtualKey)191 when shift:         // Shift+VK_OEM_2 — русская раскладка
                _lastCmd = "настройки";
                ToggleSettings();
                return true;

            case VirtualKey.Left when !shift: PanBy(-1, 0); return true;
            case VirtualKey.Right when !shift: PanBy(1, 0); return true;
            case VirtualKey.Up when !shift: PanBy(0, -1); return true;
            case VirtualKey.Down when !shift: PanBy(0, 1); return true;
        }
        return false;
    }

    // ---------- копирование ----------

    async Task CopyToSlotAsync(int slot)
    {
        if (_currentPath is null || CurrentIndex < 0) return;
        var entry = _list[CurrentIndex];

        if (string.IsNullOrEmpty(_slots.FolderOf(slot)))
        {
            // Привязка первым нажатием: никакого «сначала зайдите в настройки».
            // Единственный модальный диалог в приложении, и он на холодном пути.
            if (!await PickFolderForAsync(slot)) return;
            // Нажатие не теряется: после выбора папки действие выполняется
        }

        var verdict = _slots.Enqueue(slot, entry);
        if (verdict != CopyVerdict.NoTarget) _slotError[slot] = null;
        switch (verdict)
        {
            case CopyVerdict.NoTarget:
                ShowToast($"Слот {slot + 1}: папка не выбрана", error: true);
                return;
            case CopyVerdict.AlreadyThere:
                // Показываем ГДЕ именно: в дереве из сотни глав «уже есть» без
                // адреса — это не ответ, а повод искать руками
                ShowToast(DuplicateMessage(slot), error: false, copyEvent: false, warn: true);
                break;
            case CopyVerdict.Queued:
                ToastCopied(slot);
                break;
        }

        RenderSlots();
        UpdateFrameBadges();

        if (SettingsStore.Current.AdvanceAfterCopy) Step(+1);
    }

    async Task<bool> PickFolderForAsync(int slot)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return false;

            // Целевая папка не должна совпадать с исходной, иначе F5 начнёт
            // показывать собственные копии (§5.9)
            if (_list.Folder is not null &&
                string.Equals(Path.TrimEndingDirectorySeparator(folder.Path),
                              Path.TrimEndingDirectorySeparator(_list.Folder),
                              StringComparison.OrdinalIgnoreCase))
            {
                ShowToast("Это и есть просматриваемая папка", error: true);
                return false;
            }

            _slots.Bind(slot, folder.Path);
            return true;
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось выбрать папку: {ex.Message}", error: true);
            return false;
        }
    }

    async Task RebindSlotAsync(int slot)
    {
        if (await PickFolderForAsync(slot))
        {
            _slotError[slot] = null;
            ShowToast($"Слот {slot + 1} → «{_slots.NameOf(slot)}»", error: false, copyEvent: false);
        }
    }

    void DoUndo()
    {
        var (ok, message, source) = _slots.Undo();
        ShowToast(message, error: !ok, copyEvent: false);
        _burst = 0;

        // Отмена возвращает вид на тот кадр, к которому относилась операция —
        // иначе непонятно, что именно отменилось (§5.6)
        if (source is not null)
        {
            var i = _list.IndexOf(source);
            if (i >= 0 && i != CurrentIndex) Show(i);
        }
        RenderSlots();
        UpdateFrameBadges();
    }

    // ---------- обратная связь ----------

    void ToastCopied(int slot)
    {
        var quiet = SettingsStore.Current.ToastQuietCount >= 25;
        SettingsStore.Current.ToastQuietCount++;
        SettingsStore.Touch();

        if (_lastToastWasCopy && Toast.Visibility == Visibility.Visible)
        {
            // Тост не стекается: слот один, текст меняется на месте, анимации входа нет
            _burst++;
            ToastText.Text = SafeName(quiet
                ? $"Скопировано: {_burst}"
                : $"Скопировано: {_burst} файлов · Отменить  Ctrl+Z");
        }
        else
        {
            _burst = 1;
            ToastText.Text = SafeName(quiet
                ? "Скопировано: 1"
                : $"Скопировано в «{_slots.NameOf(slot)}» · Отменить  Ctrl+Z");
        }

        Toast.Opacity = quiet && !_highContrast ? 0.7 : 1.0;
        Toast.BorderBrush = Res("GalleryStrokeBrush");
        ToastClose.Visibility = Visibility.Collapsed;
        Toast.Visibility = Visibility.Visible;
        _lastToastWasCopy = true;

        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(quiet ? 1200 : 4000);
        _toastTimer.Start();
    }

    /// <param name="warn">
    /// Не ошибка, но и не рядовое событие: сообщение, которое человек обязан
    /// заметить, иначе он повторит действие. Такое не гаснет само.
    /// </param>
    string DuplicateMessage(int slot)
    {
        var where = _slots.LastDuplicate;
        var root = _slots.FolderOf(slot);
        return $"Уже есть в «{_slots.NameOf(slot)}»: {MainWindow.Relative(root, where)}";
    }

    void ShowToast(string text, bool error, bool copyEvent = false, bool warn = false)
    {
        // Все сообщения приложения идут через эту воронку, поэтому чистим здесь,
        // а не в восьми местах вызова (§8.7)
        ToastText.Text = SafeName(text);
        Toast.Opacity = 1.0;
        Toast.BorderBrush = error ? Res("GalleryDangerBrush")
                          : warn ? Res("GalleryWarningBrush")
                          : Res("GalleryStrokeBrush");
        Toast.Visibility = Visibility.Visible;
        ToastClose.Visibility = error || warn ? Visibility.Visible : Visibility.Collapsed;
        _lastToastWasCopy = copyEvent;

        // Сообщение, которое человек обязан прочитать. Отскок на границе папки
        // и прочие фоновые уведомления его не затирают: «уже есть» тонуло под
        // «последний файл», если дубликат попадался на последнем кадре
        _toastSticky = error || warn;
        if (!copyEvent) _burst = 0;

        _toastTimer.Stop();
        // Ошибочный тост НЕ гаснет сам (§7.3): человек должен успеть прочитать
        // причину. Закрывается кнопкой или Esc. Предупреждение — так же: оно
        // отвечает на «почему не скопировалось», и пропустить его нельзя.
        if (!error && !warn)
        {
            _toastTimer.Interval = TimeSpan.FromMilliseconds(3000);
            _toastTimer.Start();
        }
        Announce(text, important: error);
    }

    /// <summary>Текущий тост важен и не должен затираться фоновым сообщением.</summary>
    bool _toastSticky;

    void HideToast()
    {
        Toast.Visibility = Visibility.Collapsed;
        _lastToastWasCopy = false;
        _toastSticky = false;
        _burst = 0;
    }

    /// <summary>Полоса слотов: кликабельна, чтобы у пользователя с мышью был путь.</summary>
    void RenderSlots()
    {
        SlotBar.Children.Clear();
        for (int i = 0; i < Slots.Count; i++)
        {
            if (string.IsNullOrEmpty(_slots.FolderOf(i))) continue;
            int slot = i;

            var dot = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
                Background = SlotBrush(i),
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = Res("GalleryBadgeTextBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var failed = _slotError[slot] is not null;
            if (failed) dot.Background = Res("GalleryDangerBrush");

            var marksOff = _slots.MarksOff[slot];
            if (_slots.Broken[slot]) dot.Background = Res("GalleryDangerBrush");

            var label = new TextBlock
            {
                Text = marksOff
                    ? SafeName(_slots.NameOf(slot))
                    : $"{SafeName(_slots.NameOf(slot))} {_slots.CopiedCount(slot)}",
                FontSize = 12,
                Foreground = failed ? Res("GalleryDangerBrush") : Res("GalleryTextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(dot);
            panel.Children.Add(label);

            var btn = new Button
            {
                Content = panel,
                Padding = new Thickness(6, 2, 8, 2),
                MinHeight = 32,                     // зона нажатия ≥32 (закон Фиттса)
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
            };
            var tip = SafeName(_slots.FolderOf(slot) ?? "");
            if (_slotError[slot] is { } err) tip += "\n" + err;
            else tip += $"\nКлавиша {slot + 1} — копировать, ПКМ — сменить папку";
            if (marksOff) tip += "\nБольше 50 000 файлов — отметки отключены";
            ToolTipService.SetToolTip(btn, tip);
            btn.Click += (_, __) => _ = CopyToSlotAsync(slot);
            btn.RightTapped += (_, e) => { e.Handled = true; _ = RebindSlotAsync(slot); };
            AutomationSetName(btn, marksOff
                ? $"Слот {slot + 1}, {_slots.NameOf(slot)}, отметки отключены"
                : $"Слот {slot + 1}, {_slots.NameOf(slot)}, скопировано {_slots.CopiedCount(slot)}");

            SlotBar.Children.Add(btn);
        }

        if (_slots.Pending > 20)
            SlotBar.Children.Add(new TextBlock
            {
                Text = $"очередь {_slots.Pending}",
                FontSize = 12,
                Foreground = Res("GalleryWarningBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
    }

    static void AutomationSetName(DependencyObject o, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(o, name);

    /// <summary>Бейджи на кадре: состояние «этот файл уже отложен», а не событие.</summary>
    void UpdateFrameBadges()
    {
        FrameBadges.Children.Clear();
        if (_currentPath is null)
        {
            _a11yText = _list.Count == 0 ? "Нет изображений" : "";
            AutomationSetName(Img, _a11yText);
            return;
        }

        var name = Path.GetFileName(_currentPath);
        var inSlots = _slots.SlotsContaining(name).ToList();

        // Ровно та строка, что описана в §7.9: имя, позиция, куда уже отложено
        _a11yText = $"{SafeName(name)}, изображение {CurrentIndex + 1} из {_list.Count}";
        if (inSlots.Count > 0)
            _a11yText += ", сохранено в " + string.Join(", ", inSlots.Select(_slots.NameOf));
        AutomationSetName(Img, _a11yText);

        foreach (var slot in inSlots)
        {
            var digit = new TextBlock
            {
                Text = (slot + 1).ToString(),
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = Res("GalleryBadgeTextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // Имя канваса уже перечисляет слоты — голая «1» в дереве доступности
            // была бы дублем без контекста
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(
                digit, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

            FrameBadges.Children.Add(new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
                Background = SlotBrush(slot),
                BorderThickness = new Thickness(1),
                BorderBrush = Res("GalleryStrokeBrush"),
                Opacity = 0.6,          // состояние, а не событие: не спорит с кадром
                Child = digit,
            });
        }
    }

    // ---------- прочие команды ----------

    async void CopyToClipboard()
    {
        _lastCmd = "буфер";
        UpdateOverlay();
        if (_currentPath is null) return;
        ShowToast(await FileOps.CopyFileToClipboardAsync(_currentPath), error: false);
    }

    void Reveal()
    {
        _lastCmd = "проводник";
        UpdateOverlay();
        if (_currentPath is not null) FileOps.RevealInExplorer(_currentPath);
    }

    // ---------- закрытие ----------

    /// <summary>
    /// Оптимистичный UI сказал «сохранено», очередь фоновая. Закрыть окно, не
    /// дождавшись её, значит соврать пользователю (§5.5).
    /// </summary>
    /// <summary>
    /// Закрытие по крестику или системному меню. Программный Window.Close()
    /// сюда НЕ приходит — для него есть RequestCloseAsync (см. ниже).
    /// </summary>
    async void OnClosing(Microsoft.UI.Windowing.AppWindow sender,
                         Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_closing) return;

        args.Cancel = true;
        if (await PrepareCloseAsync()) { _closing = true; Close(); }
    }

    /// <summary>
    /// Закрытие из кода: Ctrl+W и последний Esc.
    ///
    /// ПОЧЕМУ ОТДЕЛЬНО: Window.Close() не поднимает AppWindow.Closing. Если бы
    /// эти пути просто звали Close(), защита из §5.5 работала бы только при клике
    /// по крестику, а Ctrl+W молча терял бы незаписанные копии. Поймано замером:
    /// геометрия окна не сохранялась именно потому, что обработчик не вызывался.
    /// </summary>
    public async void RequestCloseAsync()
    {
        if (_closing) return;
        if (await PrepareCloseAsync()) { _closing = true; Close(); }
    }

    /// <summary>
    /// Сохранить состояние и дождаться очереди копий. false — закрываться нельзя.
    /// </summary>
    async Task<bool> PrepareCloseAsync()
    {
        // Сессия пишется ПЕРВОЙ и своим Flush. Раньше перед ней стоял захват
        // геометрии окна, и любое его исключение уносило в общий catch всю запись
        // целиком: приложение спокойно закрывалось, а человек возвращался к папке
        // и кадру прошлого сеанса. Геометрия — приятная мелочь, позиция в папке —
        // то, ради чего вообще возвращаются.
        try
        {
            SaveSession();      // пишет на диск сам
        }
        catch (Exception ex)
        {
            Log.Error($"сохранение сессии: {ex}");
        }

        try
        {
            SettingsStore.Current.WindowPlacement =
                WindowPlacement.Capture(WinRT.Interop.WindowNative.GetWindowHandle(this))
                ?? SettingsStore.Current.WindowPlacement;
            SettingsStore.Touch();
            SettingsStore.Flush();
        }
        catch (Exception ex)
        {
            Log.Warn($"геометрия окна: {ex.Message}");
        }

        try
        {
            if (_slots.Pending == 0) return true;

            // Второй Alt+F4 поверх открытого диалога вызвал бы второй ShowAsync
            // и уронил процесс — вместе с ещё не записанными копиями
            if (_closePrompted) return false;
            _closePrompted = true;

            // Оптимистичный UI сказал «сохранено», очередь фоновая. Закрыть окно,
            // не дождавшись её, значит соврать пользователю (§5.5).
            ShowToast($"Завершаю {_slots.Pending} операций…", error: false);
            await _slots.DrainAsync(TimeSpan.FromSeconds(5));

            if (_slots.Pending > 0)
            {
                // Единственный разрешённый в приложении модальный вопрос
                var dlg = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot,
                    Title = "Операции не завершены",
                    Content = $"{_slots.Pending} копий ещё не записаны. Закрыть всё равно?",
                    PrimaryButtonText = "Закрыть",
                    CloseButtonText = "Подождать",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dlg.ShowAsync() != ContentDialogResult.Primary)
                {
                    _closePrompted = false;      // пользователь выбрал «Подождать»
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            // async void на событии владельца исключений не имеет — своё не отдаём
            Log.Error($"PrepareClose: {ex}");
            return true;
        }
    }
}
