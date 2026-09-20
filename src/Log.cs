using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Windows.Graphics.Imaging;

namespace Gallery;

/// <summary>
/// Логи (SPEC.md §8.10). Local, не Roaming — логи не должны синхронизироваться.
/// Три строки вместо Serilog: для просмотрщика картинок библиотека логирования —
/// это зависимость ради ничего.
/// </summary>
static class Log
{
    static string Dir => Path.Combine(SettingsStore.Dir, "logs");
    static string FilePath => Path.Combine(Dir, $"app-{DateTime.Now:yyyyMMdd}.log");

    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss} {level} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Логирование никогда не должно ронять приложение
        }
    }

    /// <summary>
    /// Одна строка при старте. Именно она отвечает на 90% будущих вопросов
    /// «почему у меня не открывается HEIC» — без неё придётся гадать.
    /// </summary>
    public static void Startup()
    {
        Rotate();
        try
        {
            var sb = new StringBuilder();
            sb.Append("Gallery ").Append(typeof(Log).Assembly.GetName().Version);
            sb.Append(" | Windows ").Append(Environment.OSVersion.Version);
            sb.Append(" | .NET ").Append(Environment.Version);
            sb.Append(" | декодеры: ").Append(string.Join(" ", DecoderExtensions()));
            Write("INFO", sb.ToString());
        }
        catch (Exception ex)
        {
            Write("WARN", $"не удалось собрать сведения о среде: {ex.Message}");
        }
    }

    /// <summary>
    /// Расширения, которые реально умеет WIC на ЭТОЙ машине.
    /// Перечисление реестра для этого не работает — MSIX-упакованные кодеки
    /// (HEIF, WebP, AV1, RAW) там не видны (§8.3). Спрашиваем сам WIC.
    /// </summary>
    static IEnumerable<string> DecoderExtensions()
    {
        var exts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var info in BitmapDecoder.GetDecoderInformationEnumerator())
            {
                try
                {
                    foreach (var e in info.FileExtensions) exts.Add(e.ToLowerInvariant());
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        return exts.Count == 0 ? new[] { "(не удалось перечислить)" } : exts.ToArray();
    }

    /// <summary>Файл на день, всё старше недели удаляем. Без библиотеки ротации.</summary>
    static void Rotate()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(Dir, "app-*.log"))
            {
                try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }
}
