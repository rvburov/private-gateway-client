using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace PrivateGateway.Client.UI.Services;

/// <summary>Буфер обмена главного окна.</summary>
public sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        if (Clipboard() is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }

    public async Task<string?> GetTextAsync() =>
        Clipboard() is { } clipboard ? await clipboard.TryGetTextAsync().ConfigureAwait(true) : null;

    private static IClipboard? Clipboard()
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return TopLevel.GetTopLevel(window)?.Clipboard;
    }
}
