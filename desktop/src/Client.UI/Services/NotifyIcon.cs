using PrivateGateway.Client.Infrastructure.Storage;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Services;

/// <summary>
/// Значок программы для уведомлений Windows — тот же щит, что в трее, файлом PNG в папке данных: Windows берет значок
/// уведомлений программы без установщика из реестра по пути к файлу.
/// </summary>
internal static class NotifyIcon
{
    public static void Write(AppPaths paths)
    {
        if (!OperatingSystem.IsWindows() || File.Exists(paths.NotifyIcon))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            using var bitmap = TrayIconRenderer.Render(Tone.Connected, "Icon.Mark.Ok");
            bitmap.Save(paths.NotifyIcon, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Без значка Windows покажет свой.
        }
    }
}
