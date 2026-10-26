using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Application.Updates;
using PrivateGateway.Client.Infrastructure.Storage;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.UI.Services;

/// <summary>
/// Окно и значок в трее на компьютере: закрыть окно — спрятать в трей (настройка «Сворачивать в трей»),
/// «Закрыть» в меню — выйти, вторая копия программы — показать окно этой. При автозапуске (<c>--minimized</c>) окно не
/// показывается: программа ждет в трее, — но только после первого запуска: приветствие показывается всегда.
/// </summary>
public sealed class DesktopHost
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MainWindow _window;
    private readonly SettingsService _settings;
    private readonly Dictionary<(Tone, string), WindowIcon> _icons = [];
    private bool _quitting;

    public DesktopHost(
        Avalonia.Application app,
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        TrayViewModel tray,
        SettingsService settings,
        SingleInstance? instance,
        bool minimized,
        NoticeWindow notices,
        UpdateWatcher updates)
    {
        _desktop = desktop;
        _window = window;
        _settings = settings;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var icon = new TrayIcon { Menu = tray.Menu, ToolTipText = tray.ToolTip, Icon = Icon(tray) };
        icon.Clicked += (_, _) => Show();
        tray.PropertyChanged += (_, _) =>
        {
            icon.ToolTipText = tray.ToolTip;
            icon.Icon = Icon(tray);
        };
        tray.ShowWindowRequested += (_, _) => Show();
        tray.QuitRequested += (_, _) => Quit();

        // Установщик обновления запущен, окно откроется после него само (AppRelauncher): закрыться, освободив свои файлы.
        updates.RestartRequested += (_, _) => Dispatcher.UIThread.Post(Quit);
        TrayIcon.SetIcons(app, [icon]);
        desktop.Exit += (_, _) => icon.Dispose();

        notices.Attach(window, Show);
        window.Closing += OnClosing;
        if (instance is not null)
        {
            instance.ActivationRequested += (_, _) => Dispatcher.UIThread.Post(Show);
        }

        if (!minimized || !settings.Current.OnboardingDone)
        {
            desktop.MainWindow = window;
        }
    }

    private void Show()
    {
        _desktop.MainWindow ??= _window;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void Quit()
    {
        _quitting = true;
        _desktop.Shutdown();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_quitting && _settings.Current.MinimizeToTray)
        {
            e.Cancel = true;
            _window.Hide();
            return;
        }

        if (!_quitting)
        {
            Quit();
        }
    }

    private WindowIcon Icon(TrayViewModel tray)
    {
        var key = (tray.Tone, tray.Mark);
        if (!_icons.TryGetValue(key, out var icon))
        {
            icon = new WindowIcon(TrayIconRenderer.Render(tray.Tone, tray.Mark));
            _icons[key] = icon;
        }

        return icon;
    }
}
