using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.Services;

/// <summary>Окно «Сохранить как» главного окна.</summary>
public sealed class AvaloniaFileSaveService : IFileSaveService
{
    public async Task<bool> SaveTextAsync(string suggestedName, string text)
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (TopLevel.GetTopLevel(window)?.StorageProvider is not { CanSave: true } storage)
        {
            return false;
        }

        // Тип файла — по расширению предложенного имени: отчет — .txt, резервная копия — .pgbackup.
        var extension = Path.GetExtension(suggestedName).TrimStart('.');
        var type = extension is "" or "txt"
            ? FilePickerFileTypes.TextPlain
            : new FilePickerFileType(Loc.Instance["backup.fileType"]) { Patterns = [$"*.{extension}"] };
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = extension is "" ? "txt" : extension,
            FileTypeChoices = [type],
        }).ConfigureAwait(true);
        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        await writer.WriteAsync(text).ConfigureAwait(true);
        return true;
    }
}
