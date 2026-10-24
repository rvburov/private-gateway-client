using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace PrivateGateway.Client.UI.Services;

/// <summary>Окно «Открыть» главного окна: текстовый файл со ссылками или подпиской.</summary>
public sealed class AvaloniaFileOpenService : IFileOpenService
{
    public async Task<(string Name, string Text)?> OpenTextAsync()
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (TopLevel.GetTopLevel(window)?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.TextPlain, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync().ConfigureAwait(true);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(true);
        if (buffer.Length > IFileOpenService.MaxBytes)
        {
            throw new InvalidDataException("Файл больше 1 МБ");
        }

        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            return (files[0].Name, text);
        }
        catch (DecoderFallbackException e)
        {
            throw new InvalidDataException("Файл не текстовый (не UTF-8)", e);
        }
    }
}
