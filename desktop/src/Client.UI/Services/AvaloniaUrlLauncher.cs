using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace PrivateGateway.Client.UI.Services;

/// <summary>Открытие адреса через Launcher окна Avalonia; в тестах подменяется.</summary>
public sealed class AvaloniaUrlLauncher : IUrlLauncher
{
    public async Task<bool> OpenAsync(Uri url)
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        return TopLevel.GetTopLevel(window) is { } top && await top.Launcher.LaunchUriAsync(url).ConfigureAwait(true);
    }
}
