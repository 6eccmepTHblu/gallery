using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Gallery;

static class FileOps
{
    /// <summary>
    /// В Корзину, а не File.Delete: отмена не должна быть необратимой.
    /// Одна строка из состава .NET вместо сорока строк P/Invoke SHFileOperation.
    /// </summary>
    public static void Recycle(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    /// <summary>
    /// Ctrl+C кладёт ФАЙЛ, а не пиксели: в подавляющем большинстве случаев человек
    /// хочет переложить файл, а не вставить растр (§5.8).
    /// </summary>
    public static async Task<string> CopyFileToClipboardAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            data.SetStorageItems(new List<IStorageItem> { file });

            // Ретрай: буфер обмена держат RDP-клиенты, менеджеры буфера, OneDrive.
            // Без ретрая копирование будет случайно падать примерно у каждого десятого.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Clipboard.SetContent(data);
                    Clipboard.Flush();      // иначе буфер опустеет при закрытии приложения
                    return "Файл скопирован в буфер";
                }
                catch (Exception) when (attempt < 2)
                {
                    await Task.Delay(50);
                }
            }
        }
        catch (Exception ex)
        {
            return $"Не удалось скопировать в буфер: {ex.Message}";
        }
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            { UseShellExecute = true });
        }
        catch (Exception) { }
    }
}
