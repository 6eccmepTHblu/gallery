# Подтверждённые находки аудита вехи 6 (всего 71)

## БЛОКЕР — 15

### 1. На холсте нет AutomationProperties.Name — скринридер не сообщает ни имени файла, ни позиции, ни слотов
**Файл:** \MainWindow.xaml.cs (Render) | **Объём:** ~10 строк

**Правка:** Ставить имя не в трёх местах, а в одном: UpdateFrameBadges() в Copying.cs — он уже вызывается из Render() (смена кадра, открытие папки), из CopyToSlotAsync() и из DoUndo(), то есть ровно во всех точках, где меняются имя, позиция или набор слотов. В конце метода собрать строку: пусто → "Нет изображений"; иначе $"{SafeName(Path.GetFileName(_currentPath))}, изображение {CurrentIndex+1} из {_list.Count}" + при непустом SlotsContaining(name) ", сохранено в " + string.Join(", ", слоты.Select(_slots.NameOf)). Сохранить в поле _a11yText (его же переиспользует находка 2) и вызвать AutomationSetName(Img, _a11yText). Отдельные вызовы в Show()/OpenFolder() не нужны, Fail() перебивает имя своим текстом (находка 4).

### 2. RaiseNotificationEvent не вызывается нигде, троттлинга 350 мс нет — смена кадра для скринридера беззвучна
**Файл:** \MainWindow.xaml.cs (Show, OnSettle) | **Объём:** ~15 строк

**Правка:** Верно по сути, но добавить дешёвый ранний выход, иначе на горячем пути (30 Гц автоповтор) каждый раз создаётся AutomationPeer впустую. Завести в MainWindow.xaml.cs: `void Announce(string text, bool important) { if (!AutomationPeer.ListenerExists(AutomationEvents.Notification)) return; var peer = FrameworkElementAutomationPeer.FromElement(Img) ?? FrameworkElementAutomationPeer.CreatePeerForElement(Img); peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, important ? AutomationNotificationProcessing.ImportantAll : AutomationNotificationProcessing.MostRecent, text, "gallery"); }` (using Microsoft.UI.Xaml.Automation.Peers). В конце Show(): если Stopwatch.GetTimestamp() - _lastAnnounce > 350 мс — Announce(_a11yText, false) и обновить _lastAnnounce, иначе _pendingAnnounce = true. В начале OnSettle (он и так срабатывает по оседанию 120 мс) — если _pendingAnnounce, добить объявление и сбросить флаг. Один Announce обслуживает и находки 3 и 4.

### 3. Тост — единственный канал ошибок — не объявляется скринридеру и не помечен как live-region
**Файл:** \MainWindow.xaml (Toast) | **Объём:** ~6 строк

**Правка:** Правка верна. Конкретно: в MainWindow.xaml на Border x:Name="Toast" добавить AutomationProperties.LiveSetting="Polite"; в Copying.cs в конце ShowToast() — Announce(text, important: error); в конце ToastCopied() — Announce(ToastText.Text, important: false). Использовать хелпер Announce из находки 2, отдельного кода поднятия пира не писать.

### 4. App.xaml <Application.Resources> без ThemeDictionaries — тематизировать нечем
**Файл:** \App.xaml | **Объём:** ~45 строк

**Правка:** В App.xaml к существующему ResourceDictionary добавить <ResourceDictionary.ThemeDictionaries> с ключами Default/Light/HighContrast (обе секции сосуществуют, MergedDictionaries оставить). Токенов ровно столько, сколько реально используется кодом: GalleryCanvasBrush(#141414), GallerySurfaceBrush(#E61B1B1D), GallerySurfaceElevatedBrush(#F2232326), GalleryStrokeBrush(#1AFFFFFF), GalleryTextPrimaryBrush(#F2F2F3), GalleryTextSecondaryBrush(#A9A9AE), GalleryTextTertiaryBrush(#6E6E73), GalleryAccentBrush, GalleryDangerBrush(#FF7B72), GalleryWarningBrush(#FFC53D). GallerySuccessBrush не заводить: #3DDC97 живёт только в отладочном оверлее F12. В HighContrast — SystemColorWindowColorBrush / SystemColorWindowTextColorBrush / SystemColorGrayTextColorBrush / SystemColorHighlightColorBrush, все без альфы. Значения Light взять из §7.5 (canvas #ECECEC, surface-elevated #FBFBFB, text-primary #1A1A1C, text-secondary #5B5B60).

### 5. MainWindow.xaml:6 — Root Grid с жёстким Background="#141414" и RequestedTheme="Dark"
**Файл:** \MainWindow.xaml | **Объём:** ~2 строки

**Правка:** Background="{ThemeResource GalleryCanvasBrush}" на строке 6. RequestedTheme="Dark" из XAML не убирать ради HC (бесполезно) — убрать его только вместе с находкой 15, когда тема станет настраиваемой. Если после этого глифы кнопок окна всё же не совпадут по контрасту, задать AppWindow.TitleBar.ButtonForegroundColor/ButtonBackgroundColor в ApplyTheme (см. находку 5), а не подкрашивать титлбар вручную.

### 6. SettingsPanel/CheatSheet/Toast с захардкоженным тёмным фоном и системно окрашиваемым текстом контролов
**Файл:** \MainWindow.xaml | **Объём:** ~3 строки

**Правка:** Строки 56/75/94: Background="{ThemeResource GallerySurfaceBrush}" для тоста и GallerySurfaceElevatedBrush для шпаргалки и панели; в HighContrast-словаре обе — SystemColorWindowColorBrush без альфы. Foreground у CheckBox и Button не задавать: после смены фона на токен системная окраска станет корректной сама.

### 7. Кисти в коде строятся как new SolidColorBrush(Color.FromArgb(...)) — снимок, не ссылка на тему
**Файл:** \Chrome.cs | **Объём:** ~25 строк

**Правка:** Ввести одну строчку-хелпер `static Brush Res(string key) => (Brush)Application.Current.Resources[key];` и заменить литералы на Res("GalleryTextSecondaryBrush") и т.п. Важно: возвращается конкретный экземпляр кисти, поэтому поддеревья ОБЯЗАНЫ пересобираться по смене темы — BuildCheatSheet() сделать идемпотентным (CheatNav/CheatPick/CheatZoom/CheatMisc .Children.Clear() в начале), и звать BuildCheatSheet/RenderSlots/RenderSlotRows/UpdateFrameBadges из ApplyTheme (находка 5).

### 8. Нет подписки на UISettings.ColorValuesChanged — смена темы/HC на лету не подхватывается
**Файл:** \Chrome.cs | **Объём:** ~12 строк

**Правка:** В InitChrome(), рядом со строкой 41: `_ui.ColorValuesChanged += (_, __) => DispatcherQueue.TryEnqueue(ApplyTheme);` TryEnqueue обязателен — событие приходит не из UI-потока. ApplyTheme(): перечитать флаг HC (находка 6), затем BuildCheatSheet(); RenderSlots(); RenderSlotRows(); UpdateFrameBadges(). Root.RequestedTheme при HC не трогать — словарь HighContrast и так побеждает ElementTheme. Подписку обернуть в try/catch по образцу SafeAnimations() (Chrome.cs:94-98): UISettings в некоторых сессиях бросает.

### 9. Нет состояния «первый запуск» из §7.8: один TextBlock Placeholder вместо колонки с кнопкой «Выбрать папку» и «Горячие клавиши»
**Файл:** \Chrome.cs:209-223,235-242 | **Объём:** ~35 строк XAML + ~15 строк C#

**Правка:** Заменить Placeholder на StackPanel x:Name="EmptyState" (то же место в CanvasRoot, Spacing=16, по центру): FontIcon (Segoe Fluent Icons) 32/48 #6E6E73; TextBlock 28/36 «Откройте папку с изображениями»; TextBlock #A9A9AE «Перетащите папку сюда или нажмите Ctrl+O»; горизонтальный StackPanel с Button AccentButtonStyle MinHeight=32 «Выбрать папку» и HyperlinkButton «Горячие клавиши»; TextBlock 12 px #6E6E73 «Пробел откладывает текущий кадр в выбранную папку». В Chrome.cs вынести тела в async Task PickFolderAsync() и void ToggleCheatSheet(), акселераторы OnOpenFolder/OnCheatSheet сводятся к их вызову — тогда Click-обработчики не дублируют код. ВАЖНО: EmptyState обязан гаситься там же, где сейчас гасится Placeholder (MainWindow.xaml.cs:127,181,208) — переиспользовать имя переменной или завести один метод SetEmptyState(bool), иначе точки скрытия разъедутся. Делать вместе с находкой 11 — контейнер общий.

### 10. Нет карточки битого файла из §7.8: Fail() пишет две строки в Placeholder, кнопок «Скопировать всё равно» / «Показать в Проводнике» / «Пропустить» нет
**Файл:** \MainWindow.xaml (CanvasRoot) | **Объём:** ~40 строк XAML + ~30 строк C#

**Правка:** Border x:Name="ErrorCard" в CanvasRoot (Width=320, CornerRadius=12, Background #232326, по центру): FontIcon warning #FFC53D → TextBlock SemiBold x:Name="ErrCaption" → TextBlock #A9A9AE x:Name="ErrWhy" → кнопки «Скопировать всё равно» (_ = CopyToSlotAsync(0)), «Показать в Проводнике» (FileOps.RevealInExplorer(_currentPath!)), «Пропустить» (Step(+1)). Fail() заполняет и показывает карточку вместо Placeholder, Show()/OnSettle её гасят (те же точки, что и Placeholder). ПОПРАВКА к предложенному исправлению: акселераторы Left/Right висят на Root и срабатывают независимо от фокуса — стрелки не отберутся; а вот Space отберётся, потому что кнопка помечает KeyRoutedEventArgs.Handled и Root.KeyDown (Copying.cs:51) до неё не дойдёт. Поэтому после Click каждой кнопки вернуть фокус на канвас: Zoom.Focus(FocusState.Programmatic). Кнопку «Установить из Store» встроить сюда же (находка 10), а не отдельным механизмом. RaiseNotificationEvent(ImportantAll) — это ось доступности (в проекте нет ни одного вызова), в этой находке не смешивать.

### 11. Ошибочный тост гаснет сам через 6 с (§7.3 требует «не исчезает сам»), красного состояния чипа слота нет
**Файл:** \Copying.cs:237-240,45-46,251-300 | **Объём:** ~25 строк

**Правка:** (1) В ShowToast при error таймер не запускать; в XAML добавить в Toast кнопку «✕» (x:Name="ToastClose", Click → HideToast, IsTabStop) и показывать её только при error; заодно добавить слой в TryEscapeLayer (Chrome.cs:368) — Esc гасит висящий тост первым. (2) ПОПРАВКА: не заводить Broken[] в Slots — индекс слота уже приходит в событии Failed. В MainWindow: readonly string?[] _slotError = new string?[Slots.Count]; в обработчике _slots.Failed (Copying.cs:45) писать _slotError[slot] = msg, в CopyToSlotAsync при Queued/AlreadyThere и в Bind-пути — сбрасывать в null; в RenderSlots при _slotError[i] != null красить dot и label в #FF7B72 и ставить ToolTipService с причиной. UI-состояние остаётся в UI, Slots не трогаем.

### 12. §6.1: ввод не отбрасывается, пока показанный кадр ≠ _currentPath — файловые клавиши работают по ещё не показанному пути
**Файл:** \Copying.cs:60-91 | **Объём:** ~12 строк

**Правка:** Поле `string? _shown` в MainWindow.xaml.cs. Присваивать `_shown = entry.Path` в ветке кэш-хита Show (после стр.125), `_shown = entry.Path` в ShowThumbAsync (после стр.176) и `_shown = _list[index].Path` в OnSettle (после стр.206); сбрасывать `_shown = null` в начале Show (перед стр.111) и в OpenFolder при пустой папке. ВАЖНО, чего нет в исходной правке: (а) присваивать `_shown = _currentPath` и в Fail() (стр.244) — иначе на нечитаемом файле карточка ошибки показана, а копирование навсегда заблокировано; (б) НЕ гейтить Ctrl+Z — undo работает со стеком операций, а не с показанным кадром, и его блокировка на зависшем декоде только вредит. То есть в Copying.OnKeyDown гейт ставится ровно перед ветками слотов (стр.75) и Space/Enter (стр.83): `if (_shown != _currentPath) return;` (без e.Handled — пусть событие уйдёт дальше, оно всё равно ничьё). Zoom.cs:239 (OnFullTimer) трогать не нужно: путь там тот же самый.

### 13. OnResizeSettled безусловно обнуляет Img.Source, а восстанавливает только в ветке смены тира
**Файл:** \MainWindow.xaml.cs:262-273 | **Объём:** ~8 строк, 15 мин

**Правка:** Не гасить источник, когда кэш не выбрасывается. Заменить тело OnResizeSettled на: `_resize.Stop(); var scale = Root.XamlRoot?.RasterizationScale ?? 1.0; var longSide = Math.Max(Root.ActualWidth, Root.ActualHeight) * scale; if (longSide < 1) return; var tier = ImagePipeline.QuantizeTier(longSide); if (tier != _pipe.Tier) { Img.Source = null; /* Clear() освободит источники */ _pipe.SetTier(tier); if (CurrentIndex >= 0) Show(CurrentIndex); } else if (CurrentIndex >= 0 && _pipe.Peek(CurrentIndex) is { } cur) LayoutFrame(cur.SrcW, cur.SrcH, cur.BmpW); UpdateOverlay();`. Вариант из находки («вернуть Img.Source = cur.Source») слабее: он не спасает случай, когда на экране была миниатюра или _full, а Peek пуст. ApplyTier() остаётся как есть для OpenFolder.

### 14. GetAsync отдаёт второму заказчику задачу, привязанную к токену первого; отменённый декод выбрасывает готовый кадр и возвращает null
**Файл:** \ImagePipeline.cs:128-209 | **Объём:** ~12 строк, 30 мин

**Правка:** Развязать декод и заказчика. В DecodeAsync подставить CancellationToken.None в _gate.WaitAsync и в Task.Run/Decoder.DecodeAsync (конкуренцию ограничивает _gate(4)), удалить блок 186-191 — кадр кладётся в _cache всегда; в GetAsync вернуть `task.WaitAsync(ct)` (и для только что созданной задачи, и для найденной в _inflight), чтобы отмена гасила только ожидание заказчика. Отмена станет приходить как OperationCanceledException — оба вызывающих (OnSettle, PrefetchAsync) её уже ловят. Счётчик DecodeCancelled после этого перестанет расти: перевесить его инкремент на брошенное ожидание в GetAsync (catch вокруг WaitAsync) — иначе оверлей F12 по §6.9 врёт нулём.

### 15. OnClosing реентерабелен: второй Alt+F4 поверх открытого ContentDialog вызывает второй ShowAsync и роняет процесс с незаписанными копиями
**Файл:** \Copying.cs:361-391 | **Объём:** ~6 строк, 15 мин

**Правка:** Флаг занятости сразу после args.Cancel: `if (_closePrompted) return; _closePrompted = true;` перед ShowToast; в ветке «Подождать» — `_closePrompted = false; return;`. Тело метода целиком обернуть в try/catch с App-логом (async void на WinRT-событии владельца исключений не имеет). Отдельно стоит рассмотреть e.Handled = true в App.UnhandledException хотя бы для случаев с непустой очередью — но это уже за границей находки.

## ВАЖНО — 39

### 1. Fail() не объявляет отказ декодирования и оставляет на канвасе имя предыдущего удачного кадра
**Файл:** \MainWindow.xaml.cs (Fail) | **Объём:** ~4 строки

**Правка:** Правка верна, но это тот же механизм, что в находках 2-3, отдельного кода не нужно. В конце Fail(): `var text = $"Не удалось открыть {SafeName(name)}. {why}"; AutomationSetName(Img, text); Announce(text, important: true);`. Обратить внимание: Fail() вызывается ПОСЛЕ Render()/UpdateFrameBadges(), так что перезапись имени сработает; порядок менять не надо.

### 2. Нет признака активного скринридера — «Всегда показывать панель» не включается
**Файл:** \Chrome.cs (SetChrome) | **Объём:** 3 строки

**Правка:** Правка верна и минимальна: в начале ветки visible:false метода SetChrome добавить `if (AutomationPeer.ListenerExists(AutomationEvents.AutomationFocusChanged)) return;` (using Microsoft.UI.Xaml.Automation.Peers). Новое поле в Settings действительно не нужно. Побочный эффект принять сознательно: при активном UIA-клиенте курсор тоже перестанет прятаться (_chromeVisible остаётся true) — для незрячего это безразлично, а лишний код не нужен.

### 3. Хром прячется, даже когда клавиатурный фокус внутри него
**Файл:** \Chrome.cs (InitChrome, SetChrome) | **Объём:** ~5 строк

**Правка:** Обход дерева через VisualTreeHelper не нужен — дешевле два обработчика. В InitChrome: `StatusBar.GotFocus += (_, __) => _focusInChrome = true; StatusBar.LostFocus += (_, __) => _focusInChrome = false;` (GotFocus/LostFocus — bubbling routed events, всплывают от кнопок внутри). В ветке visible:false добавить `_focusInChrome` в существующее условие раннего выхода. TitleBarArea содержит только TextBlock и таб-стопов не имеет — его проверять незачем.

### 4. Канвас не фокусируем, Focus() не вызывается нигде — Esc не возвращает фокус на канвас
**Файл:** \Chrome.cs (TryEscapeLayer, ToggleSettings, OnCheatSheet) | **Объём:** ~8 строк

**Правка:** НЕ ставить IsTabStop на PanSurface/ScrollView: сфокусированный ScrollView сам обрабатывает стрелки (прокрутка), а акселератор не срабатывает, если сфокусированный элемент уже пометил KeyDown обработанным — это сломает главную навигацию ←/→. Носителем фокуса сделать Image: в MainWindow.xaml на <Image x:Name="Img"> добавить IsTabStop="True" (UIElement.IsTabStop, не Control) — там же уже живёт UIA-имя из находки 1, и стрелки Image не перехватывает. В TryEscapeLayer() после каждого закрытия панели/шпаргалки и после ResetZoom вызвать Img.Focus(FocusState.Programmatic); в ToggleSettings()/OnCheatSheet() при открытии — SettingsPanel.Focus(FocusState.Programmatic) / CheatSheet.Focus(...). Стилей фокуса нигде не переопределено — родное кольцо появится само.

### 5. Не реализованы Enter, `+`/`=`/`-` основного ряда и Ctrl+W из карты клавиш §4.1
**Файл:** \MainWindow.xaml (KeyboardAccelerators) | **Объём:** ~14 строк

**Правка:** Правка верна, но одного акселератора на OEM_PLUS мало: KeyboardAccelerator сопоставляет и модификаторы, а `+` на большинстве раскладок — это Shift+`=`, поэтому нужны ДВА акселератора на (VirtualKey)187 (Modifiers=None и Modifiers=Shift) → OnZoomIn и один-два на (VirtualKey)189 → OnZoomOut. Задавать кодом в InitZoom (Zoom.cs) по образцу settingsAccel из Chrome.cs — числовой Key в XAML не записывается. Ctrl+W: обычный акселератор в XAML (Key="W" Modifiers="Control") на обработчик, вызывающий Close() — OnClosing уже перехватывает недоделанную очередь. Enter: в Copying.cs расширить условие до `e.Key is VirtualKey.Space or VirtualKey.Enter` — ветка уже под защитой WasKeyDown, а Button съедает Enter сам и до Root.KeyDown он не всплывает. Заодно дописать `+ =` и `Ctrl+W` в BuildCheatSheet (Chrome.cs), сейчас там значится только «Num+ Num−».

### 6. UISettings.TextScaleFactor не читается, подписки на изменение нет, все FontSize захардкожены
**Файл:** \Copying.cs (RenderSlots) | **Объём:** ~25 строк

**Правка:** Предложенная правка НЕ компилируется: Root — это Grid, у Panel нет свойства FontSize, и наследования шрифта от Grid в WinUI нет. Правильно и лениво: в Chrome.cs прочитать _textScale = _ui.TextScaleFactor в том же try/catch-стиле, что SafeAnimations, подписаться на _ui.TextScaleFactorChanged → DispatcherQueue.TryEnqueue(ApplyTextScale). ApplyTextScale — один проход по визуальному дереву Root через VisualTreeHelper, умножающий базовый FontSize каждого TextBlock/ContentControl на _textScale (базовые значения запомнить при первом проходе, иначе повторные вызовы возведут коэффициент в степень). Тот же метод дёргать в конце RenderSlots/RenderSlotRows/BuildCheatSheet, так как они пересоздают элементы. Размеры бейджей и свотчей не трогать — §7.9 явно требует не привязывать их к масштабу текста.

### 7. Фиксированные Height у титлбара и Width=120 у колонки клавиш в шпаргалке
**Файл:** \Chrome.cs (BuildCheatSheet) | **Объём:** 3 строки

**Правка:** Правка верна. TitleBarArea: Height="40" → MinHeight="40" (строка уже RowDefinition Auto, SetTitleBar считает drag-регион по фактическому размеру). В BuildCheatSheet первую ColumnDefinition сменить на GridLength.Auto и поставить у TextBlock k MinWidth=120, чтобы колонка не «дышала» между строками. Заодно проверить MinHeight="44" у Toast — он уже MinHeight, там всё в порядке.

### 8. У девяти кнопок «Выбрать…»/«Сменить» в настройках нет AutomationProperties.Name
**Файл:** \Chrome.cs (RenderSlotRows) | **Объём:** 2-3 строки

**Правка:** Правка верна, хелпер переиспользовать: AutomationSetName(pick, bound ? $"Слот {slot+1}, папка {_slots.NameOf(slot)}, сменить" : $"Слот {slot+1}, папка не выбрана, выбрать"). Про свотч: AutomationProperties.SetAccessibilityView(dot, AccessibilityView.Raw) НЕ убирает из дерева его дочерний TextBlock с цифрой — ставить Raw надо на сам TextBlock (или на оба). Это опционально; обязательна только строка с именем кнопки.

### 9. Цифра непривязанного слота #6E6E73 на #33FFFFFF — контраст ~1,6:1
**Файл:** \MainWindow.xaml (Placeholder) | **Объём:** ~5 строк

**Правка:** Правка верна, но заявленные ~9:1 завышены — #F2F2F3 на композитном #4F4F51 даёт ~7,3:1, чего достаточно. Заодно закрыть тот же токен в остальных местах, где #6E6E73 несёт содержательный текст на фоне #141414 (контраст 3,6:1, тоже ниже 4.5): TextBlock Placeholder (главное пустое состояние «Перетащите папку или изображение»), «Esc — закрыть», «Esc или F1 — закрыть» в MainWindow.xaml и TextBlock path в ветке «не выбрана» в RenderSlotRows — перевести их на #A9A9AE (7,8:1, штатный text-secondary из §7.5). Заголовки-капсы («ЦЕЛЕВЫЕ ПАПКИ», НАВИГАЦИЯ и т.п.) в #6E6E73 оставить можно — это декоративные разделители, но дешевле поднять и их.

### 10. Тема жёстко Dark, цвета литералами, высококонтрастная тема Windows игнорируется
**Файл:** \Chrome.cs | **Объём:** ~40 строк, самая дорогая находка списка

**Правка:** Механизм в правке выбран неверный. (1) Windows.UI.ViewManagement.AccessibilitySettings в unpackaged WinUI 3 без CoreWindow ненадёжен — не строить на нём. (2) Ручное отключение бэкдропа не нужно: встроенный MicaBackdrop сам гасится по SystemBackdropConfiguration.HighContrast — проверить это первым, одним запуском в HC. (3) RequestedTheme="Dark" в HC игнорируется платформой, снимать его кодом не надо. Правильный и притом бесплатный в рантайме путь — декларативный: в Root.Resources завести ResourceDictionary.ThemeDictionaries с ключами Default и HighContrast (кисти CanvasBg, SurfaceBg, TextPrimary, TextSecondary, StrokeSubtle; в Default — нынешние #141414/#1B1B1D/#F2F2F3/#A9A9AE/#1AFFFFFF, в HighContrast — SystemColorWindowColor/WindowTextColor/HighlightColor), заменить литералы в XAML на {ThemeResource ...}, а в Copying.cs/Chrome.cs брать кисти через Root.Resources["..."] вместо new SolidColorBrush. Подписка не нужна — фреймворк переразрешает ThemeResource при смене темы сам. Цвета слотов оставить как есть: цифра на свотче уже несёт смысл (§7.5), и это явно допущено спецификацией.

### 11. Нигде нет определения высокой контрастности
**Файл:** \Chrome.cs | **Объём:** ~14 строк

**Правка:** Одно поле `bool _hc;` и один хелпер в Chrome.cs: SystemParametersInfoW(SPI_GETHIGHCONTRAST=0x0042, (uint)Marshal.SizeOf<HIGHCONTRAST>(), ref hc, 0), результат — (hc.dwFlags & 0x1) != 0, всё в try/catch с false по умолчанию. Читать один раз в InitChrome() и переcчитывать в ApplyTheme (находка 5). Не заводить абстракцию «поставщик темы» — флаг используется в двух местах.

### 12. Девять цветов слотов и Colors.Black для цифры не подменяются в HC
**Файл:** \Copying.cs | **Объём:** ~20 строк

**Правка:** Один метод в Copying.cs рядом со SlotColors: `void SlotDot(int slot, bool bound, out Brush bg, out Brush fg)` — вне HC как сейчас (SlotColors[slot] / Colors.Black), в HC bg=Res("SystemColorButtonFaceColorBrush"), fg=Res("SystemColorButtonTextColorBrush"), плюс на Border BorderThickness=1 с SystemColorWindowTextColorBrush, чтобы кружок не сливался. Вызвать из всех трёх мест. Различимость слотов держит цифра, она уже во всех трёх отрисована.

### 13. App.xaml.cs:53 — MicaBackdrop включается безусловно
**Файл:** \App.xaml.cs | **Объём:** 1 строка

**Правка:** Удалить строку 53. Никаких условий добавлять не нужно: нет Mica — нечего гасить ни в HC, ни в RDP, и два требования спецификации закрываются удалением одной строки. Вернуть вместе с пустым состоянием вехи 6, тогда же и с проверкой GetSystemMetrics(SM_REMOTESESSION=0x1000)==0.

### 14. Обводки панелей — литерал #1AFFFFFF; предложено также обвести ImgHost и Overlay
**Файл:** \MainWindow.xaml | **Объём:** ~6 строк

**Правка:** Строки 57/76/94/122: BorderBrush="{ThemeResource GalleryStrokeBrush}" (Default #1AFFFFFF, HighContrast SystemColorWindowTextColorBrush). ImgHost обводку НЕ добавлять — вместо этого перевести его собственный литерал Background="#2A2A2C" (строка 35) на токен, иначе под прозрачным PNG в HC остаётся тёмное пятно. Overlay не трогать.

### 15. DropHint и PositionRail используют {ThemeResource SystemAccentColorLight2}, не переопределяемый в HC
**Файл:** \MainWindow.xaml | **Объём:** ~2 строки

**Правка:** Свой токен из находки 1: Fill/BorderBrush="{ThemeResource GalleryAccentBrush}"; в Default/Light — SolidColorBrush поверх SystemAccentColorLight2/Dark1, в HighContrast — SystemColorHighlightColorBrush. Никаких чужих легаси-ключей.

### 16. Нет поля темы в настройках и переключателя «Система / Тёмная / Светлая»
**Файл:** \Settings.cs | **Объём:** ~25 строк

**Правка:** Settings.cs: `public string Theme { get; set; } = "dark";` MainWindow.xaml:6 — снять RequestedTheme. В SettingsPanel рядом с двумя чекбоксами ComboBox из трёх пунктов; обработчик пишет SettingsStore.Current.Theme, зовёт SettingsStore.Touch() и ставит Root.RequestedTheme = ElementTheme.Default/Light/Dark. Начальное применение — в InitChrome() рядом с чтением ChkAdvance (Chrome.cs:77). Переключение обязано звать ApplyTheme из находки 5, иначе поддеревья, собранные кодом, останутся в старых цветах.

### 17. crash.log дописывается бесконечно (нет ограничения «последние 10 записей») и падение остаётся немым — полоски «что-то сломалось, открыть лог» нет
**Файл:** \App.xaml.cs:14,27-38 | **Объём:** ~15 строк

**Правка:** (1) Обрезка: прочитать файл, если он есть, разбить по "\n\n" (стектрейсы пустых строк не содержат — маркер безопасен), взять последние 9, дописать новую и записать целиком через File.WriteAllText; всё внутри существующего try/catch. (2) ПОПРАВКА: Crash() статический и до _window не дотянется — сделать поле _window статическим (static MainWindow? _window) и вызывать _window?.DispatcherQueue.TryEnqueue(() => _window!.ShowToast("Что-то сломалось — журнал в %LocalAppData%\\Gallery", error: true)). Отдельной кнопки в тосте не изобретать: этот тост станет неисчезающим сам по себе после находки 3; если нужен переход к файлу — добавить в ShowToast необязательный Action? onTap и вешать его на Toast.Tapped (3 строки, пригодится и другим сообщениям).

### 18. Недоступная папка (нет прав, отвалившийся UNC) показывается как «В папке нет изображений» — FolderList.Load глотает исключение перечисления
**Файл:** \MainWindow.xaml.cs:87-94 | **Объём:** ~12 строк

**Правка:** В FolderList: public string? LoadError { get; private set; }; в начале Load — LoadError = null; в catch — LoadError = ex switch { UnauthorizedAccessException => "нет доступа к папке", DirectoryNotFoundException => "папка недоступна", IOException => "папка недоступна", _ => "не удалось прочитать папку" }. Дополнительно, чтобы поймать и молчаливый ACCESS_DENIED: после успешного перечисления, если Items.Count == 0 и !Directory.Exists(folder) — тоже LoadError = "папка недоступна". В OpenFolder ветку _list.Count == 0 разветвить: LoadError != null → заголовок «Не удалось прочитать папку» + причина + кнопка «Выбрать другую» (тот же контейнер, что в находках 1 и 11), иначе — состояние пустой папки.

### 19. При переходе на новый кадр Placeholder не сбрасывается — текст ошибки о предыдущем файле висит поверх нового кадра до конца декода
**Файл:** \MainWindow.xaml.cs:104-153 (вставка после 111) | **Объём:** 2 строки

**Правка:** В Show() сразу после _currentPath = entry.Path (строка 111), до Render(): Placeholder.Visibility = Visibility.Collapsed (и ErrorCard.Visibility = Collapsed, когда карточка появится). Совместить с ClearFrame() из находки 12 — это один и тот же вызов в одном и том же месте.

### 20. Имя файла в тексте ошибки и в тостах подставляется сырым, мимо SafeName — защита от bidi-спуфинга работает только на FileNameText
**Файл:** \MainWindow.xaml.cs:90,248 | **Объём:** ~6 строк

**Правка:** ПОПРАВКА: не обходить восемь мест вызова, а закрыть воронки. (1) В ShowToast первой строкой: ToastText.Text = SafeName(text) — через неё проходят ВСЕ сообщения (Undo, Failed, drop, буфер, пикер), это корневое исправление. (2) То же в ToastCopied для обеих веток текста. (3) В Fail(): SafeName(name). (4) В ветке пустой папки: SafeName(folder). Slots.NameOf трогать не нужно — его результат уходит только в тосты и тултипы, а тосты уже очищены; для тултипов слотов (Copying.cs:293) добавить SafeName отдельной строкой. В Checks добавить одну проверку, что SafeName применён к строке вида «Не удалось открыть a‮gpj.jpg».

### 21. В Why() не разобраны 0x88982F8C, 0x88982F0B, 0x88982F04 и OutOfMemoryException; фолбэк выводит пользователю сырой ex.Message с HRESULT
**Файл:** \MainWindow.xaml.cs:355-366,219-223 | **Объём:** ~12 строк

**Правка:** В switch добавить: 0x88982F8C => «Файл слишком большой», 0x88982F0B => «Версия формата не поддерживается», 0x88982F04 => «Внутренняя ошибка декодера» (плюс Log.Error). Перед switch — `if (ex is OutOfMemoryException) return "Файл слишком большой для показа";`, и в catch OnSettle при OOM вызвать _pipe.Clear() (метод уже есть, ImagePipeline.cs:101) — §8.10 требует освободить кэш. Фолбэк заменить на «Не удалось прочитать файл»; ex.ToString() положить в ToolTipService на карточке ошибки (подпись «Подробнее») и продублировать в Log.Error.

### 22. Кнопки «Установить из Store» нет нигде, а ветка «нужен кодек» для RAW мертва: в FolderList.Exts нет ни одного RAW-расширения
**Файл:** \Chrome.cs:199-200 | **Объём:** ~25 строк

**Правка:** (1) Один общий список: static readonly string[] RawExts = { .3fr .ari .arw .bay .cap .cr2 .cr3 .crw .dcs .dcr .drf .eip .erf .fff .iiq .k25 .kdc .mef .mos .mrw .nef .nrw .orf .ori .pef .ptx .pxn .raf .raw .rw2 .rwl .sr2 .srf .srw .x3f .dng } (§8.1). FolderList.Exts = базовый список + RawExts; NeedsCodec = { .heic .heif .avif .jxl .webp } + RawExts; фильтр FileOpenPicker в Chrome.cs дополнить теми же. (2) На карточке ошибки при вердикте «нужен кодек» — кнопка «Установить из Store»: Process.Start(new ProcessStartInfo($"ms-windows-store://search/?query={query}") { UseShellExecute = true }) в try/catch; query по расширению: heic/heif → "HEIF Image Extensions" (в подписи упомянуть, что нужен ещё HEVC Video Extensions — §8.2), avif → "AV1 Video Extension", webp → "Webp Image Extensions", jxl → "JPEG XL Image Extension", RAW → "Raw Image Extension". В catch — заменить кнопку текстом с названием пакета.

### 23. Пустая папка — две строки текста: нет списка поддерживаемых форматов и кнопок «Открыть другую» / «Показать в Проводнике»
**Файл:** \MainWindow.xaml.cs:87-94 | **Объём:** ~15 строк

**Правка:** Тот же контейнер EmptyState, что и в находке 1, с подменой содержимого: заголовок «В папке нет изображений», подзаголовок SafeName(folder), строка 12 px «Поддерживаются JPEG, PNG, WEBP, HEIC, AVIF, GIF, BMP, TIFF, RAW», кнопки «Выбрать другую» (PickFolderAsync) и «Показать в Проводнике» (FileOps.RevealInExplorer(folder) — сработает и на папке). Третий режим того же контейнера — ошибка чтения папки из находки 6.

### 24. Нет акселераторов `+` `=` `-` (OEM), только Num+ / Num−
**Файл:** \MainWindow.xaml:164-165 | **Объём:** ~8 строк

**Правка:** Приём уже есть в Chrome.cs (стр.66-72). В InitChrome добавить после блока settingsAccel: два KeyboardAccelerator с Key=(VirtualKey)187 (VK_OEM_PLUS) и Key=(VirtualKey)189 (VK_OEM_MINUS), Modifiers=None, Invoked → тот же ZoomBy(ZoomStep,null)/ZoomBy(1f/ZoomStep,null) с e.Handled=true, и Root.KeyboardAccelerators.Add(...). Обработчики OnZoomIn/OnZoomOut (Zoom.cs:266-270) имеют сигнатуру KeyboardAccelerator-делегата, поэтому их можно подписать напрямую: `accel.Invoked += OnZoomIn;`. В Chrome.BuildCheatSheet заменить строку "Num+  Num−" (стр.354) на "+ =  −".

### 25. Enter не копирует в слот 1 (§4.1 назначает `Space` / `Enter`)
**Файл:** \Copying.cs:83-91 | **Объём:** ~3 строки

**Правка:** Copying.cs:83 → `if (e.Key is VirtualKey.Space or VirtualKey.Enter && !ctrl && !shift)`, а внутри переключатель SpaceIsNext применять только к пробелу: `if (SettingsStore.Current.SpaceIsNext && e.Key == VirtualKey.Space) Step(+1); else _ = CopyToSlotAsync(0);` (Step вместо Show(CurrentIndex+1) — см. находку 16). Отдельно учесть находку 11: без гейта по панелям Enter начнёт копировать при фокусе на кнопке в настройках.

### 26. Нет акселератора Ctrl+W
**Файл:** \Chrome.cs | **Объём:** ~5 строк

**Правка:** В MainWindow.xaml добавить `<KeyboardAccelerator Key="W" Modifiers="Control" Invoked="OnClose"/>` и в Chrome.cs метод `void OnClose(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; Close(); }`. Close() поднимает AppWindow.Closing → Copying.OnClosing (стр.361) сам сольёт очередь копий и настройки, отдельного кода не нужно. Заодно добавить строку "Ctrl+W" в CheatMisc (Chrome.cs:359-364).

### 27. F5 не пересканирует целевые папки слотов
**Файл:** \Copying.cs:44 | **Объём:** ~4 строки

**Правка:** 1) В OnRescan после `_pipe.SetList(_list)` (MainWindow.xaml.cs:406): `for (int i = 0; i < Slots.Count; i++) _slots.RescanSlot(i);` — метод public, пустой путь он обрабатывает сам (Slots.cs:88). 2) Вместо предложенного «вызвать UpdateFrameBadges() после Show» (не сработает: скан асинхронный и завершится позже) исправить корень — Copying.cs:44: `_slots.Changed += () => DispatcherQueue.TryEnqueue(() => { RenderSlots(); UpdateFrameBadges(); });`. Это чинит и устаревание бейджей после любого фонового скана, и после Undo/Enqueue.

### 28. Кап 50 000 отключает отметки молча, без строки в тултипе
**Файл:** \Copying.cs:273-297 | **Объём:** ~10 строк

**Правка:** В Slots: `public readonly bool[] MarksOff = new bool[Count];`. В RescanSlot сбрасывать `MarksOff[slot] = false;` рядом с `_copied[slot].Clear()` (стр.87), а в теле Task.Run при срабатывании капа ставить `MarksOff[slot] = true;` ПЕРЕД `set.Clear(); break;` (стр.102). В Copying.RenderSlots (стр.275 и 293): при MarksOff[slot] в лейбле не печатать счётчик (`Text = _slots.NameOf(slot)`), а в ToolTipService.SetToolTip дописать строку «Больше 50 000 файлов — отметки отключены». Ту же оговорку добавить в AutomationSetName (стр.297), иначе экранный диктор сообщит «скопировано 0».

### 29. Недоступная папка слота: нет красного бейджа и текст тоста не по спецификации
**Файл:** \Copying.cs:45-46,259-262 | **Объём:** ~15 строк

**Правка:** В Slots: `public readonly bool[] Broken = new bool[Count];`. В ConsumeAsync catch (Slots.cs:180-184) ставить `Broken[op.Slot] = ex is DirectoryNotFoundException;` — ТОЛЬКО DirectoryNotFoundException: у «нет прав» и «нет места» спецификация (SPEC.md:448-450) требует свой текст ошибки, а не «недоступен». Снимать `Broken[slot] = false` после успешного CopyAtomic (стр.177) и в Bind (стр.72). В Copying.cs:45 подписку сделать разветвлённой: `if (_slots.Broken[slot]) ShowToast($"Слот {slot+1} недоступен · Ctrl+Shift+{slot+1} — выбрать заново", error:true); else ShowToast($"Слот {slot+1}: {msg}", error:true);`. В RenderSlots (стр.262) при Broken[i] красить фон dot в #FF7B72 (danger из §7.5), цифра остаётся.

### 30. lastPath пишется только в OnClosing — после падения позиция не восстанавливается
**Файл:** \Copying.cs:364 | **Объём:** ~2 строки

**Правка:** Не в Show, как предложено (Show вызывается при автоповторе ~30 раз в секунду, и каждый вызов дёргал бы Stop/Start таймера на горячем пути), а в OnSettle — там индекс уже осевший: MainWindow.xaml.cs после стр.197 (`var entry = _list[index];`) добавить `SettingsStore.Current.LastPath = entry.Path; SettingsStore.Touch();`. Дебаунс 1 с (Settings.cs:28) сам склеит серию. Строку в OnClosing оставить.

### 31. Нет единственного экземпляра (AppInstance.FindOrRegisterForKey + RedirectActivationToAsync)
**Файл:** \MainWindow.xaml.cs:61-77 | **Объём:** ~30 строк

**Правка:** В App.OnLaunched, ПОСЛЕ ветки --self-test и до SettingsStore.Load(). Метод придётся пометить `async void` (Environment.Exit в текущем виде уже так работает). Уточнения к предложенной правке: (а) аргументы редиректа брать из `AppInstance.GetCurrent().GetActivatedEventArgs()` и передавать в RedirectActivationToAsync целиком; (б) вместо Environment.Exit(0) в перенаправляющем экземпляре документация WinAppSDK предписывает `Process.GetCurrentProcess().Kill()` — Exit ждёт финализаторов и на STA после редиректа зависает; (в) обязательно пропускать редирект для служебных флагов, иначе `--bench`, `--bench-decode` и `--startup-probe` уедут в уже запущенный экземпляр и молча завершатся: `if (!argv0.Any(a => a.StartsWith("--")))` вокруг всего блока; (г) в главном экземпляре подписаться на `keyed.Activated`, а внутри обработчика идти через `_window.DispatcherQueue.TryEnqueue(...)` (событие приходит на фоновом потоке) — вызвать OpenFromArgs с аргументами из e.Data и `_window.AppWindow.MoveInZOrderAtTop()` / Activate() для вывода на передний план. Аргументы у unpackaged-экземпляра лежат в LaunchActivatedEventArgs.Arguments, а не в Environment.GetCommandLineArgs — под это OpenFromArgs (MainWindow.xaml.cs:61, срезает argv[1..]) нужно либо перегрузить, либо звать с массивом, где первый элемент — заглушка.

### 32. Перечисление папки идёт синхронно на UI-потоке до показа кадра из argv
**Файл:** \FolderList.cs:35-64 | **Объём:** ~30 строк

**Правка:** Направление верное, но предложенную реализацию править: `_list` — readonly-поле (MainWindow.xaml.cs:20), и его нельзя «заменить», а _pipe держит ту же ссылку. Правильнее так: в FolderList добавить `public void LoadOne(string file)` (Folder = папка, Items = один Entry из FileInfo) и `public void Adopt(string folder, List<Entry> items)` (замена содержимого того же объекта, Items.Clear + AddRange). OpenFolder сделать `async void`: при focus != null → LoadOne(focus); _pipe.SetList(_list); ApplyTier(); Show(0); затем `var items = await Task.Run(() => FolderList.Scan(folder));` (вынести тело Load в static Scan, возвращающий отсортированный список — он не трогает состояние и потому безопасен вне UI-потока), потом _list.Adopt(folder, items); _pipe.SetList(_list); и восстановление позиции по пути: `var i = _currentPath is null ? 0 : _list.IndexOf(_currentPath); Show(i < 0 ? 0 : i);` — Show по индексу, полученному из пути, как требует §6.1. Ветку focus == null (открыли папку) можно оставить как есть, но перечисление всё равно унести в Task.Run: пустой холст лучше замороженного окна.

### 33. Файловые клавиши срабатывают при фокусе в панели настроек / шпаргалке
**Файл:** \Copying.cs:51,60-64 | **Объём:** ~2 строки

**Правка:** Ранний выход в начале Copying.OnKeyDown, сразу после гейта WasKeyDown (стр.63): `if (SettingsPanel.Visibility == Visibility.Visible || CheatSheet.Visibility == Visibility.Visible) return;`. Переносить на акселераторы со ScopeOwner нельзя — KeyboardAcceleratorInvokedEventArgs не даёт KeyStatus.WasKeyDown, а на нём держится защита §4.3 от автоповтора (это и написано в комментарии Copying.cs:48-50).

### 34. Нет поля theme и переключателя темы; цвета захардкожены
**Файл:** \Chrome.cs:77-88 | **Объём:** ~70-90 строк

**Правка:** Порядок верный (сначала токены, потом переключатель), одно упрощение: подписка на UISettings.ColorValuesChanged для варианта «Система» не нужна — WinUI 3 сам переключает Application.RequestedTheme при смене системной темы, пока на элементах стоит ElementTheme.Default; UISettings нужен только для акцента, который и так берётся из SystemAccentColorLight2. Итого: (1) в App.xaml завести ResourceDictionary.ThemeDictionaries с ключами Default/Light/HighContrast и токенами §7.5 (canvas, surface-elevated, stroke-default, text-primary/secondary/tertiary, danger, warning, success); (2) заменить хексы в MainWindow.xaml на {ThemeResource ...}, а `new SolidColorBrush(Color.FromArgb(...))` в Copying.RenderSlots/UpdateFrameBadges и Chrome.RenderSlotRows/BuildCheatSheet — на `(Brush)Application.Current.Resources["..."]`; (3) `public string Theme { get; set; } = "dark";` в Settings; (4) RadioButtons в панели настроек → `Root.RequestedTheme = ElementTheme.Dark/Light/Default` + SettingsStore.Touch(); (5) применить сохранённую тему при старте вместо RequestedTheme="Dark" в XAML. SlotColors (Copying.cs:27-34) оставить фиксированными — §7.5 запрещает отдавать им семантику системного акцента.

### 35. OnFullTimer не освобождает frame.Bitmap на путях раннего выхода; та же дыра в ImagePipeline.DecodeAsync при броске OnUiAsync
**Файл:** \ImagePipeline.cs:164-169 | **Объём:** ~4 строки на два файла, 15 мин

**Правка:** Минимум без лесов: в Zoom.cs заменить строку 232 на `if (ct.IsCancellationRequested || _currentPath != path) { frame.Bitmap.Dispose(); return; }`. В ImagePipeline.cs обернуть только вызов OnUiAsync: `SoftwareBitmapSource src; try { src = await OnUiAsync(...); } catch { frame.Bitmap.Dispose(); throw; }`. Флаг handed/finally из находки не нужен — путей передачи владения ровно один. Дописывать src.Dispose() в catch не надо: неизвестно, забрал ли битмап упавший SetBitmapAsync, а двойное освобождение — ровно тот крэш RO_E_CLOSED, о котором предупреждает комментарий Ready.Dispose.

### 36. OnSettle не отличает ready == null от отмены: тихий выход без плейсхолдера и без повторной попытки
**Файл:** \MainWindow.xaml.cs:203 | **Объём:** 2 строки, 5 мин

**Правка:** Разделить два случая, без счётчиков попыток и без перевзвода таймера (цикл ретраев на горячем пути — лишняя сущность): `if (ct.IsCancellationRequested) return; if (ready is null) { Fail(entry.Name, "Не удалось декодировать", entry.Size); return; }`. Перевзвод _settle из находки отвергаю: он маскирует причину и при устойчивом null крутит декод по кругу.

### 37. Повторная постановка одного файла в один слот до записи на диск даёт тот же target: вторая копия падает на File.Move и снимает бейдж слота
**Файл:** \Slots.cs:151-191 | **Объём:** ~8 строк, 25 мин

**Правка:** Учесть запланированное, переиспользовав саму ResolveTarget, а не дублируя её логику. Добавить в Slots поле `readonly Dictionary<string,long>[] _planned` (имя цели → размер источника, OrdinalIgnoreCase, инициализация рядом с _copied на строке 43). В Enqueue вызывать `ResolveTarget(dir, name, source.Size, p => _planned[slot].TryGetValue(Path.GetFileName(p), out var sz) ? sz : SizeOnDisk(p))` и после успешного резолва `_planned[slot][Path.GetFileName(target)] = source.Size;`; в finally ConsumeAsync — `_planned[op.Slot].Remove(Path.GetFileName(op.Target))`. Тогда повтор того же файла честно даёт AlreadyThere, а другой файл с тем же именем — «(2)». HashSet из находки не годится: он заставил бы считать «уже там» и другой файл того же имени, то есть тихо не скопировать его — потеря данных. Спецкейс по HRESULT в catch после этого не нужен.

### 38. При промахе кэша на экране остаётся предыдущий кадр, а имя, счётчик и бейджи уже относятся к новому
**Файл:** \MainWindow.xaml.cs:136-147 | **Объём:** ~6 строк, 20 мин

**Правка:** Дешёвый визуальный признак «кадр ещё не тот»: в ветке промаха Show() выставлять `Img.Opacity = 0.35;` и возвращать `Img.Opacity = 1.0;` в четырёх местах присвоения источника — ветка попадания (строка 125), ShowThumbAsync (176), OnSettle (206) и Fail (246). Гасить Img.Source в ноль не надо: мигание серым прямоугольником хуже. Формально требование §6.1 (отбрасывать ввод) этим не закрывается, и полноценно оно закрывается только шелл-фолбэком миниатюры — это отдельная задача вехи 6, не правка на 6 строк.

### 39. Fail() не зовёт DropFull/DropThumb: _full остаётся ненулевым, навсегда блокирует полноразмерный декод и держит память
**Файл:** \MainWindow.xaml.cs:244-250 | **Объём:** 1 строка, 5 мин

**Правка:** Одна строка в Fail() сразу после `Img.Source = null;`: `DropFull(); DropThumb();` — оба идемпотентны и после обнуления источника отработают до конца. Вторую часть находки (поле _fullPath вместо раннего выхода по `_full is not null`) отвергаю: ранний выход корректен — он означает «для этого кадра полный декод уже есть», а состояние синхронизируют DropFull из ResetZoom и эта правка.

## МЕЛОЧЬ — 17

### 1. Бейджи на кадре попадают в UIA-дерево как голые «1», «3» без контекста
**Файл:** \Copying.cs (UpdateFrameBadges) | **Объём:** 1-2 строки

**Правка:** Из двух предложенных вариантов брать второй (скрыть), но ставить его на нужный элемент: AutomationProperties.SetAccessibilityView Raw на РОДИТЕЛЬСКОМ Border цифру не прячет — атрибут не наследуется дочерними. Ставить Raw на сам TextBlock с цифрой (или на оба узла). Именование бейджей не нужно и вредно: продублирует хвост имени канваса.

### 2. Четыре поверхности с альфой поверх фотографии; предложено подписаться на AdvancedEffectsEnabledChanged
**Файл:** \MainWindow.xaml | **Объём:** 0 сверх находки 1

**Правка:** Ничего сверх находок 1 и 3: в HighContrast-словаре GallerySurfaceBrush и GallerySurfaceElevatedBrush задать полностью непрозрачными (SystemColorWindowColorBrush). Overlay #CC000000 (строка 114) — отладочный, оставить как есть. Подписку на AdvancedEffectsEnabledChanged не заводить.

### 3. Постоянная Opacity: PositionRail 0.5 и «тихий» тост 0.7
**Файл:** \Copying.cs | **Объём:** ~3 строки

**Правка:** PositionRail: убрать Opacity из XAML и заложить 50% альфы прямо в GalleryAccentBrush для Default/Light, а в HighContrast дать кисть непрозрачной — тогда никакого кода. Copying.cs:216 → `Toast.Opacity = quiet && !_hc ? 0.7 : 1.0;` с флагом из находки 6.

### 4. Три литерала, перебивающих системное поведение: Foreground ссылки, зелёный оверлея, заливка DropHint
**Файл:** \MainWindow.xaml | **Объём:** ~2 строки

**Правка:** Строка 105: удалить атрибут Foreground целиком. Строка 65: Background="Transparent" в HighContrast через токен (или просто оставить, рамка справляется). Строку 116 не трогать. Толщину рамки DropHint не менять.

### 5. В Checks.cs нет ни одной проверки по темам
**Файл:** \Checks.cs | **Объём:** ~12 строк

**Правка:** Одна группа: перебрать Application.Current.Resources.ThemeDictionaries["Default"/"Light"/"HighContrast"] и проверить, что наборы ключей совпадают, а значение каждого — Brush. Именно расхождение наборов ловится дёшево и именно оно ломает HC незаметно.

### 6. Img.Source = null, но ImgHost сохраняет размеры и заливку #2A2A2C — за текстом ошибки остаётся серый прямоугольник; не сбрасываются Meta и ZoomPct
**Файл:** \Zoom.cs:56-77 | **Объём:** ~10 строк

**Правка:** void ClearFrame() { Img.Source = null; ImgHost.Visibility = Visibility.Collapsed; ZoomPct.Text = ""; } — Visibility, а не Width=0, потому что LayoutFrame всё равно задаёт Width/Height; в LayoutFrame первой строкой вернуть ImgHost.Visibility = Visibility.Visible. Вызывать ClearFrame() в Fail(), в ветке пустой папки (плюс там же Meta.Text = "") и в начале Show() вместе со сбросом Placeholder из находки 7. В Fail() Meta НЕ трогать — размер файла там уместен.

### 7. Брошенный или переданный аргументом файл неподдерживаемого формата либо несуществующий путь не даёт никакой обратной связи
**Файл:** \MainWindow.xaml.cs:61-77,96-97 | **Объём:** ~8 строк

**Правка:** (1) В OpenFolder после вычисления i: `if (focus is not null && i < 0) ShowToast($"{SafeName(Path.GetFileName(focus))}: формат не поддерживается", error: true);` — покрывает и drop, и аргумент, и пикер, одно место вместо трёх. (2) В OpenFromArgs завести флаг: если непереключательные аргументы были, но ни один не существует — ShowToast($"Файл не найден: {SafeName(...)}", error: true) и НЕ восстанавливать LastPath молча (либо восстановить, но с тостом — иначе подмена контекста без объяснения).

### 8. MicaBackdrop назначается окну, но Root залит непрозрачным #141414 — Mica не видна нигде, строка бэкдропа мёртвая
**Файл:** \App.xaml.cs:53 | **Объём:** 2-3 строки

**Правка:** Снять Background с Root (MainWindow.xaml:6); задать Background="#141414" на CanvasRoot (StatusBar уже имеет свой #1B1B1D) — тогда полоса заголовка показывает Mica, как в §7.5, а фон канваса остаётся плоским. Делать это только вместе с находкой 1; если состояние первого запуска по §7.8 не делается — удалить строку App.xaml.cs:53 как мёртвый код, а не оставлять как есть.

### 9. В строке слота настроек нет кнопки «×» — слот нельзя отвязать
**Файл:** \Slots.cs:71-77 | **Объём:** ~15 строк

**Правка:** Сигнатуру сделать `public void Bind(int slot, string? folder)` (тело менять не нужно: RescanSlot на пустом пути чистит набор и выходит — Slots.cs:87-88; Touch и Changed уже есть). В RenderSlotRows добавить четвёртую Auto-колонку с кнопкой «×» (Visibility = bound ? Visible : Collapsed, MinHeight 32, AutomationProperties.SetName «Отвязать слот N» — иначе диктору достаётся голый крестик), по клику `_slots.Bind(slot, null); RenderSlotRows();`. RenderSlots перерисуется сам по событию Changed (Copying.cs:44), отдельного вызова не нужно.

### 10. Геометрия окна не сохраняется и не восстанавливается
**Файл:** \Copying.cs:361-366 | **Объём:** ~35 строк

**Правка:** Как предложено, с уточнениями: `public string? WindowPlacement { get; set; }` в Settings; P/Invoke user32 GetWindowPlacement/SetWindowPlacement + структуры WINDOWPLACEMENT/RECT/POINT (можно положить рядом с FileOps.cs, чтобы не плодить файл); сериализовать структуру в компактную строку чисел, а не в JSON-объект (проще, и формат в спецификации не зафиксирован). Читать/писать в Copying.OnClosing ДО args.Cancel (стр.364), иначе при непустой очереди первый проход запишет, а второй — уже нет. Применять SetWindowPlacement в App.OnLaunched после new MainWindow() и до Activate(); при showCmd == SW_SHOWMINIMIZED заменять на SW_SHOWNORMAL — иначе приложение стартует свёрнутым. Всё в try/catch: битая строка не должна ронять старт (Settings.Load уже держит эту дисциплину).

### 11. Остаток _wheelAccum не сбрасывается при смене направления прокрутки
**Файл:** \Zoom.cs:128-144 | **Объём:** ~2 строки

**Правка:** Zoom.cs, в OnWheel перед накоплением: `var delta = p.Properties.MouseWheelDelta; if (Math.Sign(delta) != Math.Sign(_wheelAccum)) _wheelAccum = 0; _wheelAccum += delta;` (Math.Sign(0) == 0, поэтому первое движение после сброса корректно обнуляет пустой накопитель и ничего не ломает).

### 12. Переход после копирования идёт через Show(CurrentIndex+1) и молча клампится на границе
**Файл:** \Copying.cs:88,132-133 | **Объём:** ~2 строки

**Правка:** Обе строки заменить на `Step(+1)`. Про перебивание тоста: в CopyToSlotAsync тост копирования показывается на стр.125 (ToastCopied) до перехода, и на последнем кадре BounceEdge заменит его на «Последний файл · N» — это допустимо и даже полезно (конец папки важнее подсказки Ctrl+Z), отдельного подавления писать не надо; достаточно оставить порядок как есть (ToastCopied → RenderSlots → Step).

### 13. Фильтр диалога Ctrl+O уже списка поддерживаемых расширений
**Файл:** \FolderList.cs:22-27 | **Объём:** ~2 строки

**Правка:** FolderList.Exts сделать `internal static readonly string[] Exts` и в Chrome.OnOpenFile: `foreach (var ext in FolderList.Exts) picker.FileTypeFilter.Add(ext);`. Один источник истины вместо двух списков — расхождение больше не вернётся.

### 14. Бейдж «уже в целевой папке» рисуется полной яркостью вместо приглушённой
**Файл:** \Copying.cs:316-338 | **Объём:** ~1 строка

**Правка:** В Copying.cs:324-326 добавить в инициализатор Border `Opacity = 0.6`. Цифра сохраняется, требование §7.5 «цвет никогда не единственный носитель смысла» не нарушается. Ставить Opacity на Border, а не на Background-кисть, чтобы приглушилась и чёрная цифра — иначе контраст цифры на приглушённом фоне вырастет и бейдж станет заметнее, а не тише.

### 15. RescanSlot: проверка отмены и публикация _copied[slot] = set не атомарны, отменённый скан может опубликовать имена из старой папки
**Файл:** \Slots.cs:94-110 | **Объём:** 2 строки, 10 мин

**Правка:** Сверять поколение, а не токен: сохранить `var mine = cts;` в замыкании и публиковать под `if (!ReferenceEquals(_scan[slot], mine)) return;` вместо проверки ct на строке 107. Про Dispose старого CTS — см. вердикт по находке 12, отдельной ценности он тут не даёт.

### 16. Ранний выход SetChrome(false) при открытой панели не перевзводит _chromeHide
**Файл:** \Chrome.cs:113-115 | **Объём:** 1 строка, 5 мин

**Правка:** `_chromeHide.Start();` перед return в ветке раннего выхода (Chrome.cs:113-115). Пока панель открыта, таймер будет вхолостую тикать раз в 2,4 с — это дешевле, чем чинить каждое место закрытия панели. Попутно, вне этой находки, там же рядом реальный дефект: _cursorHide (2000 мс) всегда срабатывает раньше _chromeHide (2400 мс), и его условие `if (!_chromeVisible)` (строка 45) в штатном потоке никогда не истинно — курсор не прячется вообще никогда. Лечится тем же однострочником в обратную сторону: прятать курсор из ветки скрытия хрома, а не отдельным таймером.

### 17. CancellationTokenSource нигде не диспозятся при замене (Show, OnFullTimer, RescanSlot)
**Файл:** \Slots.cs:86-91 | **Объём:** 0 (сознательно не делаем)

**Правка:** Не чинить — экономия нулевая, а риск ненулевой: токены отменённых CTS живут внутри уже запущенных декодов (Decoder.ReadAsync(ct), _gate.WaitAsync(ct)), и порядок Cancel→Dispose здесь обязателен, иначе появляется шанс словить ObjectDisposedException вместо OperationCanceledException на пути, который сейчас работает. Если правку всё же делать — только в виде `var old = _cts; _cts = new(); old?.Cancel(); old?.Dispose();` и только после правки №2, когда токен перестанет уходить внутрь общего декода.
