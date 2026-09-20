using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Gallery;

/// <summary>
/// Сохранение и восстановление геометрии окна (SPEC.md §10.1).
///
/// Именно WINDOWPLACEMENT, а не Left/Top/Width/Height: он переживает состояние
/// «развёрнуто», координаты хранит в рабочем пространстве, и Windows сама
/// валидирует их против текущей конфигурации мониторов. Ручное сохранение
/// координат требует всех этих проверок вручную — больше кода и хуже результат.
/// </summary>
static class WindowPlacement
{
    const int SW_SHOWNORMAL = 1;
    const int SW_SHOWMINIMIZED = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct Placement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hWnd, ref Placement lpwndpl);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr hWnd, ref Placement lpwndpl);

    /// <summary>Компактная строка чисел: формат нигде не зафиксирован, JSON тут лишний.</summary>
    public static string? Capture(IntPtr hwnd)
    {
        try
        {
            var p = new Placement();
            p.Length = Marshal.SizeOf<Placement>();
            if (!GetWindowPlacement(hwnd, ref p)) return null;

            return string.Join(',', new[]
            {
                p.ShowCmd,
                p.NormalPosition.Left, p.NormalPosition.Top,
                p.NormalPosition.Right, p.NormalPosition.Bottom,
            });
        }
        catch (Exception) { return null; }
    }

    public static void Restore(IntPtr hwnd, string? saved)
    {
        if (string.IsNullOrWhiteSpace(saved)) return;
        try
        {
            var parts = saved.Split(',');
            if (parts.Length != 5) return;

            var v = new int[5];
            for (int i = 0; i < 5; i++)
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]))
                    return;

            var p = new Placement
            {
                Length = Marshal.SizeOf<Placement>(),
                // Свёрнутым не стартуем: иначе приложение как будто не открылось
                ShowCmd = v[0] == SW_SHOWMINIMIZED ? SW_SHOWNORMAL : v[0],
                NormalPosition = new Rect { Left = v[1], Top = v[2], Right = v[3], Bottom = v[4] },
            };
            SetWindowPlacement(hwnd, ref p);
        }
        catch (Exception)
        {
            // Битая строка не должна ронять старт — та же дисциплина, что в настройках
        }
    }
}
