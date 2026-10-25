using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Views;

public partial class ImportPage : UserControl
{
    public ImportPage()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().Any() == true ? DragDropEffects.Copy : DragDropEffects.None;

    /// <summary>Перетащенная картинка — QR-код, другой файл — текст со ссылками, подпиской или конфигом.</summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ImportPageViewModel page
            || e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().FirstOrDefault() is not { } file)
        {
            return;
        }

        try
        {
            if (AvaloniaFileOpenService.IsImage(file.Name))
            {
                var (_, image) = await AvaloniaFileOpenService.ReadImageAsync(file).ConfigureAwait(true);
                await page.FromImageAsync(image).ConfigureAwait(true);
            }
            else
            {
                var (name, text) = await AvaloniaFileOpenService.ReadAsync(file).ConfigureAwait(true);
                await page.FromDroppedFileAsync(name, text).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            page.ShowFileError(ex.Message);
        }
    }
}
