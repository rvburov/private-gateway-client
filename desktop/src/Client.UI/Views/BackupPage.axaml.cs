using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Views;

public partial class BackupPage : UserControl
{
    public BackupPage()
    {
        InitializeComponent();
        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropZone.Classes.Set("over", false));
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().Any() == true;
        e.DragEffects = file ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.Classes.Set("over", file);
    }

    /// <summary>Перетащенный файл — как выбранный кнопкой: не копия — подсказка, копия — лист с паролем.</summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        DropZone.Classes.Set("over", false);
        if (DataContext is not BackupPageViewModel page)
        {
            return;
        }

        if (e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().FirstOrDefault() is not { } file)
        {
            page.ShowNotBackup();
            return;
        }

        try
        {
            var (name, text) = await AvaloniaFileOpenService.ReadAsync(file).ConfigureAwait(true);
            page.OpenFile(name, text);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            page.ShowNotBackup();
        }
    }
}
