using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.Services;

/// <summary>Окно «Открыть» главного окна: текстовый файл со ссылками, подпиской или конфигом, программа.</summary>
public sealed class AvaloniaFileOpenService : IFileOpenService
{
    public async Task<(string Name, string Text)?> OpenTextAsync()
    {
        if (Storage() is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.Instance["import.file.types"]) { Patterns = ["*.txt", "*.json", "*.yaml", "*.yml"] },
                FilePickerFileTypes.All,
            ],
        }).ConfigureAwait(true);
        return files.Count == 0 ? null : await ReadAsync(files[0]).ConfigureAwait(true);
    }

    /// <summary>Текст файла, выбранного в окне или перетащенного в окно программы.</summary>
    /// <exception cref="InvalidDataException">Файл больше <see cref="IFileOpenService.MaxBytes"/> или не UTF-8.</exception>
    public static async Task<(string Name, string Text)> ReadAsync(IStorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        await using var stream = await file.OpenReadAsync().ConfigureAwait(true);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(true);
        if (buffer.Length > IFileOpenService.MaxBytes)
        {
            throw new InvalidDataException("Файл больше 1 МБ");
        }

        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            return (file.Name, text);
        }
        catch (DecoderFallbackException e)
        {
            throw new InvalidDataException("Файл не текстовый (не UTF-8)", e);
        }
    }

    public async Task<(string Path, string Name)?> PickProgramAsync()
    {
        if (Storage() is not { } storage)
        {
            return null;
        }

        var programs = new FilePickerFileType(Loc.Instance["routing.apps.filter"])
        {
            Patterns = OperatingSystem.IsWindows() ? ["*.exe"] : OperatingSystem.IsMacOS() ? ["*.app"] : ["*"],
            AppleUniformTypeIdentifiers = ["com.apple.application-bundle"],
        };
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [programs, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        return files.Count > 0 && files[0].TryGetLocalPath() is { } path ? (path, ProgramName(path)) : null;
    }

    private static IStorageProvider? Storage()
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return TopLevel.GetTopLevel(window)?.StorageProvider is { CanOpen: true } storage ? storage : null;
    }

    /// <summary>Имя для подписи: на Windows — описание из файла («Google Chrome»), иначе — имя файла без расширения.</summary>
    private static string ProgramName(string path)
    {
        var fallback = Path.GetFileNameWithoutExtension(path.TrimEnd('/', '\\'));
        if (!OperatingSystem.IsWindows())
        {
            return fallback;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() is { Length: > 0 } description ? description : fallback;
        }
        catch (IOException)
        {
            return fallback;
        }
    }
}
