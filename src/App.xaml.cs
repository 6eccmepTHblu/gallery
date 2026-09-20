using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Xaml.Media;

namespace Gallery;

public partial class App : Application
{
    MainWindow? _window;

    public App()
    {
        InitializeComponent();

        // Приложение декодирует чужие файлы чужими кодеками — падения будут.
        // Немое падение недопустимо (§8.10).
        UnhandledException += (_, e) => Crash("UnhandledException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Crash("UnobservedTask", e.Exception);
    }

    static readonly string[] Sep = { Environment.NewLine + Environment.NewLine };

    static void Crash(string where, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gallery");
            Directory.CreateDirectory(dir);
            // Держим последние 10 записей: без обрезки файл растёт вечно,
            // а нужны всегда только свежие (§8.10)
            var path = Path.Combine(dir, "crash.log");
            var entries = File.Exists(path)
                ? new List<string>(File.ReadAllText(path).Split(Sep, StringSplitOptions.RemoveEmptyEntries))
                : new List<string>();
            entries.Add($"{DateTime.Now:O}  {where}{Environment.NewLine}{ex}");
            if (entries.Count > 10) entries.RemoveRange(0, entries.Count - 10);
            File.WriteAllText(path, string.Join(Sep[0], entries) + Sep[0]);

            Log.Error($"{where}: {ex?.GetType().Name} {ex?.Message}");
        }
        catch { }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv0 = Environment.GetCommandLineArgs();
        if (argv0.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(Checks.Run());
            return;
        }

        // Один экземпляр: 20 двойных кликов по файлам не должны дать 20 окон,
        // а один прогретый кэш соседей лучше двадцати холодных (§4.5).
        // Служебные флаги пропускаем мимо: иначе --bench и --startup-probe
        // уедут в уже запущенный экземпляр и молча завершатся.
        if (!argv0.Skip(1).Any(a => a.StartsWith("--", StringComparison.Ordinal)))
        {
            try
            {
                var keyed = AppInstance.FindOrRegisterForKey("gallery-main");
                if (!keyed.IsCurrent)
                {
                    // Путь передаём файлом, а не в аргументах активации: у
                    // unpackaged-приложения ILaunchActivatedEventArgs.Arguments
                    // пуст, а своего канала для полезной нагрузки WinAppSDK не даёт.
                    // Проверено запуском: без этого редирект работает, а файл не открывается.
                    WriteHandoff(argv0);

                    var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
                    await keyed.RedirectActivationToAsync(activated);
                    // Не Environment.Exit: он ждёт финализаторов и после редиректа
                    // подвисает на STA — так предписывает документация WinAppSDK
                    Process.GetCurrentProcess().Kill();
                    return;
                }
                keyed.Activated += OnRedirected;
            }
            catch (Exception ex)
            {
                // Не смогли зарегистрироваться — работаем как обычно, а не падаем
                Log.Warn($"single-instance: {ex.Message}");
            }
        }

        SettingsStore.Load();
        // В фон: перечисление WIC-декодеров стоит ~80 мс, а на пути запуска
        // им платить не за что — строка нужна для разбора будущих жалоб, не сейчас.
        _ = Task.Run(Log.Startup);

        _window = new MainWindow();

        // Геометрию применяем до Activate: иначе окно мигнёт на месте по умолчанию
        WindowPlacement.Restore(
            WinRT.Interop.WindowNative.GetWindowHandle(_window),
            SettingsStore.Current.WindowPlacement);

        Startup.MeasureFirstFrame();
        _window.Activate();

        var argv = Environment.GetCommandLineArgs();
        _window.OpenFromArgs(argv);

        var bi = Array.FindIndex(argv, a =>
            string.Equals(a, "--bench", StringComparison.OrdinalIgnoreCase));
        if (bi >= 0)
        {
            var steps = bi + 1 < argv.Length && int.TryParse(argv[bi + 1], out var n) ? n : 200;
            var interval = bi + 2 < argv.Length && int.TryParse(argv[bi + 2], out var m) ? m : 33;
            _ = _window.RunBenchAsync(steps, interval);
        }

        var i = Array.FindIndex(argv, a =>
            string.Equals(a, "--bench-decode", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < argv.Length)
            _ = RunBenchAsync(argv[i + 1]);

        var t = Array.FindIndex(argv, a =>
            string.Equals(a, "--translate", StringComparison.OrdinalIgnoreCase));
        if (t >= 0 && t + 1 < argv.Length)
            _ = RunTranslateAsync(argv[(t + 1)..]);

        var cl = Array.FindIndex(argv, a =>
            string.Equals(a, "--clean", StringComparison.OrdinalIgnoreCase));
        if (cl >= 0 && cl + 1 < argv.Length)
            _ = RunCleanAsync(argv[cl + 1]);

        var pg = Array.FindIndex(argv, a =>
            string.Equals(a, "--page", StringComparison.OrdinalIgnoreCase));
        if (pg >= 0 && pg + 1 < argv.Length)
            _ = RunPageAsync(argv[pg + 1],
                argv.Any(a => string.Equals(a, "--api", StringComparison.OrdinalIgnoreCase)),
                argv.Any(a => string.Equals(a, "--legacy", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// gallery.exe --page страница.jpg — весь тракт без окна: распознавание,
    /// склейка в блоки, перевод. Отдельный вход нужен потому, что по картинке
    /// не видно, ЧТО именно распозналось и как сгруппировалось.
    /// </summary>
    /// <summary>
    /// gallery.exe --clean страница.png — весь тракт очистки без окна:
    /// поиск надписей, разметка букв, дорисовка. Рядом с исходником кладёт
    /// «-mask» и «-clean». Нужно, чтобы проверять движок числом и глазами,
    /// не воюя за передний план с чужими окнами.
    /// </summary>
    static async Task RunCleanAsync(string path)
    {
        var sb = new StringBuilder();
        try
        {
            // Папку обрабатываем за ОДИН процесс: иначе в каждый замер попадает
            // построение сессии на 206 МБ, и цифры врут в разы
            var files = Directory.Exists(path)
                ? Directory.GetFiles(path).Where(f => FolderList.Exts.Contains(Path.GetExtension(f))
                                                   && !f.Contains("-mask") && !f.Contains("-clean"))
                           .OrderBy(f => f).ToArray()
                : new[] { path };

            await Task.Run(() =>
            {
              foreach (var one in files)
              {
                var t0 = Stopwatch.GetTimestamp();
                using var page = SkiaSharp.SKBitmap.Decode(one);
                if (page is null) { sb.AppendLine("не открылось"); continue; }

                // Оба источника рамок, как и в панели: поодиночке каждый молчит
                // на части кадров
                var boxes = Cleanup.FindAsync(page, one, CancellationToken.None)
                                   .GetAwaiter().GetResult();
                var found = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

                var t1 = Stopwatch.GetTimestamp();
                var mask = TextMask.Refine(page, boxes);
                var marked = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;

                var lit = 0;
                foreach (var v in mask) if (v >= 128) lit++;

                sb.AppendLine($"{Path.GetFileName(one)}: {page.Width}x{page.Height}");
                sb.AppendLine($"надписей {boxes.Count}, поиск {found:F0} мс");
                // Рамки списком: по картинке не видно, СКОЛЬКО надписей попало
                // в одну рамку, а именно это и решает, годится ли поиск
                foreach (var b in boxes.OrderBy(b => b.Top).ThenBy(b => b.Left))
                    sb.AppendLine($"   {b.Left},{b.Top} {b.Width}x{b.Height}");
                sb.AppendLine($"букв {lit} px ({100.0 * lit / (page.Width * page.Height):F2}%), разметка {marked:F0} мс");

                // Маска поверх кадра — так видно, что именно уйдёт под стирание
                var over = page.Copy();
                var op = over.Pixels;
                for (var i = 0; i < mask.Length; i++)
                    if (mask[i] >= 128) op[i] = new SkiaSharp.SKColor(255, 40, 200, 255);
                over.Pixels = op;
                Save(over, one, "-mask");
                over.Dispose();

                if (lit == 0) { sb.AppendLine("стирать нечего"); continue; }

                var t2 = Stopwatch.GetTimestamp();
                using var clean = Inpaint.Run(page, mask);
                sb.AppendLine($"дорисовка {Stopwatch.GetElapsedTime(t2).TotalMilliseconds:F0} мс, "
                              + $"вырезка {Inpaint.LastW}x{Inpaint.LastH}, потоков {Inpaint.LastThreads}");
                if (clean is not null) Save(clean, one, "-clean");
                sb.AppendLine();
              }
            });
        }
        catch (Exception ex) { sb.AppendLine($"ОШИБКА: {ex}"); }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gallery");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "clean.txt"), sb.ToString());
        Environment.Exit(0);
    }

    static void Save(SkiaSharp.SKBitmap bmp, string src, string suffix)
    {
        var dst = Path.Combine(Path.GetDirectoryName(src)!,
                               Path.GetFileNameWithoutExtension(src) + suffix + ".png");
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 95);
        using var fs = File.Create(dst);
        data.SaveTo(fs);
    }

    static async Task RunPageAsync(string path, bool viaApi = false, bool legacy = false)
    {
        var sb = new StringBuilder();
        try
        {
            var t0 = Stopwatch.GetTimestamp();
            Ocr.ForceLegacy = legacy;
            var page = await Ocr.ReadAsync(path, CancellationToken.None);
            sb.AppendLine($"{Path.GetFileName(path)}: кадр {page.ImgW}x{page.ImgH}, " +
                          $"блоков {page.Boxes.Count}, распознавание {(legacy ? Ocr.LastMs : RapidOcrEngine.LastMs):F0} мс, строка {page.MedianLineH:F0} px");
            if (page.Lines is { Count: > 0 })
            {
                // Строки детектора до сшивки: по репликам не видно, что именно
                // он нашёл, а очистка от текста работает как раз со строками
                sb.AppendLine($"строк детектора {page.Lines.Count}:");
                foreach (var r in page.Lines.OrderBy(r => r.Top).ThenBy(r => r.Left))
                    sb.AppendLine($"   {r.Left},{r.Top} {r.Width}x{r.Height}");
            }
            sb.AppendLine();

            // Через API: модель сверяет распознанное с картинкой и возвращает
            // ВОССТАНОВЛЕННЫЙ оригинал — именно его и надо мерить, потому что
            // переводится он, а не то, что выдал движок распознавания
            if (viaApi)
            {
                var api = new ApiTranslator();
                string? img = SettingsStore.Current.ApiSendImage
                    ? await PageImage.EncodeAsync(path, CancellationToken.None) : null;

                var lines = page.Boxes.Select(b => b.Source).ToList();
                var map = await api.TranslatePageAsync(lines, CancellationToken.None, img);

                for (var i = 0; i < page.Boxes.Count; i++)
                {
                    var b = page.Boxes[i];
                    var fixedEn = ApiTranslator.Fixed.TryGetValue(i + 1, out var f) ? f : b.Source;
                    sb.AppendLine($"[{b.X:F0},{b.Y:F0} {b.W:F0}x{b.H:F0}]");
                    sb.AppendLine($"  EN  {fixedEn}");
                    sb.AppendLine($"  RU  {(map.TryGetValue(i + 1, out var ru) ? ru : "(нет)")}");
                    sb.AppendLine();
                }
                foreach (var (x0, y0, x1, y1, en, ru) in ApiTranslator.Extra)
                {
                    sb.AppendLine($"[найдено по картинке {x0:F2},{y0:F2}-{x1:F2},{y1:F2}]");
                    sb.AppendLine($"  EN  {en}");
                    sb.AppendLine($"  RU  {ru}");
                    sb.AppendLine();
                }
                sb.AppendLine($"картинка: {(img is null ? "не отправлялась" : $"{PageImage.LastBytes / 1024} КБ")}" +
                              $", запрос {api.LastMs:F0} мс");
                var dir2 = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gallery");
                Directory.CreateDirectory(dir2);
                await File.WriteAllTextAsync(Path.Combine(dir2, "page.txt"), sb.ToString());
                Environment.Exit(0);
                return;
            }

            using var tr = new Translator();
            if (Translator.ModelPresent) await Task.Run(tr.Load);

            var total = 0.0;
            foreach (var b in page.Boxes)
            {
                sb.AppendLine($"[{b.X:F0},{b.Y:F0} {b.W:F0}x{b.H:F0}] " +
                              $"фон #{b.BgR:X2}{b.BgG:X2}{b.BgB:X2}{(b.DarkBg ? " (тёмный)" : "")}");
                sb.AppendLine($"  EN  {b.Source}");
                if (Translator.ModelPresent)
                {
                    var ru = await Task.Run(() => tr.Translate(b.Source, CancellationToken.None));
                    total += tr.LastMs;
                    sb.AppendLine($"  RU  {ru}");
                }
                sb.AppendLine();
            }
            sb.AppendLine($"перевод всего: {total:F0} мс, страница целиком: " +
                          $"{Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} мс");
        }
        catch (Exception ex)
        {
            sb.AppendLine("ОШИБКА: " + ex);
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gallery");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "page.txt"), sb.ToString());
        Environment.Exit(0);
    }

    /// <summary>
    /// gallery.exe --translate "текст" ["ещё"] — прямая сверка перевода
    /// с эталоном, без окна и без OCR. Отдельный вход нужен потому, что
    /// ошибку в цикле декодирования по красивой картинке не увидишь: он
    /// выдаёт не мусор, а правдоподобную чушь.
    /// </summary>
    static async Task RunTranslateAsync(string[] texts)
    {
        var sb = new StringBuilder();
        try
        {
            if (!Translator.ModelPresent)
            {
                sb.AppendLine($"модели нет в {Translator.ModelDir}");
            }
            else
            {
                using var tr = new Translator();
                var t0 = Stopwatch.GetTimestamp();
                await Task.Run(tr.Load);
                sb.AppendLine($"загрузка модели: {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} мс");
                sb.AppendLine();

                foreach (var text in texts)
                {
                    var ru = await Task.Run(() => tr.Translate(text, CancellationToken.None));
                    sb.AppendLine($"EN  {text}");
                    sb.AppendLine($"RU  {ru}");
                    sb.AppendLine($"    {tr.LastMs:F0} мс");
                    sb.AppendLine();
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("ОШИБКА: " + ex);
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gallery");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "translate.txt"), sb.ToString());
        Environment.Exit(0);
    }

    static string HandoffPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Gallery", "handoff.txt");

    /// <summary>Перенаправляемый экземпляр оставляет путь главному.</summary>
    static void WriteHandoff(string[] argv)
    {
        try
        {
            var path = argv.Skip(1).FirstOrDefault(
                a => !a.StartsWith("--", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(path)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(HandoffPath)!);
            File.WriteAllText(HandoffPath, path);
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Второй запуск отдал нам свой путь. Событие приходит на фоновом потоке.
    /// </summary>
    void OnRedirected(object? sender, AppActivationArguments e)
    {
        var w = _window;
        if (w is null) return;

        string? path = null;
        try
        {
            if (File.Exists(HandoffPath))
            {
                path = File.ReadAllText(HandoffPath).Trim();
                File.Delete(HandoffPath);       // эстафета одноразовая
            }
        }
        catch (Exception) { }

        w.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // OpenFromArgs срезает argv[0] — подставляем заглушку на его место
                if (!string.IsNullOrWhiteSpace(path))
                    w.OpenFromArgs(new[] { "gallery", path! });

                w.AppWindow.Show();
                w.Activate();
            }
            catch (Exception ex) { Log.Warn($"redirect: {ex.Message}"); }
        });
    }

    static async System.Threading.Tasks.Task RunBenchAsync(string folder)
    {
        string report;
        try { report = await Bench.RunDecodeAsync(folder); }
        catch (Exception ex) { report = $"бенч упал: {ex}"; }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gallery");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "bench-decode.md"), report);
        Environment.Exit(0);
    }
}

/// <summary>
/// Веха 0: холодный старт до ПЕРВОГО ОТРИСОВАННОГО КАДРА, а не до создания HWND.
/// HWND появляется раньше, чем XAML что-либо нарисовал, поэтому ходовые цифры
/// старта WinUI измеряют не то.
/// </summary>
static class Startup
{
    static bool _done;

    public static void MeasureFirstFrame()
    {
        CompositionTarget.Rendering += OnRendering;
    }

    static void OnRendering(object? sender, object e)
    {
        if (_done) return;
        _done = true;
        CompositionTarget.Rendering -= OnRendering;

        using var p = Process.GetCurrentProcess();
        var ms = (DateTime.Now - p.StartTime).TotalMilliseconds;
        var wsMb = p.WorkingSet64 / 1024.0 / 1024.0;
        var privMb = p.PrivateMemorySize64 / 1024.0 / 1024.0;

        Report(ms, wsMb, privMb);

        // --startup-probe: замерил и вышел, чтобы гонять прогоны из скрипта
        if (Environment.GetCommandLineArgs().Any(a =>
                string.Equals(a, "--startup-probe", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(0);
        }
    }

    static void Report(double ms, double wsMb, double privMb)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Gallery");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "startup.csv");
            if (!File.Exists(path))
                File.AppendAllText(path, "utc,first_frame_ms,working_set_mb,private_mb\n");
            File.AppendAllText(path, string.Format(CultureInfo.InvariantCulture,
                "{0:O},{1:F1},{2:F1},{3:F1}\n", DateTime.UtcNow, ms, wsMb, privMb));
        }
        catch
        {
            // замер никогда не должен ронять приложение
        }
    }
}
