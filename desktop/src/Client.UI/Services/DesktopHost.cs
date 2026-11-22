using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using PrivateGateway.Client.Application.Account;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Application.Updates;
using PrivateGateway.Client.Infrastructure.Storage;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.UI.Services;

/// <summary>
/// Окно и значок в трее на компьютере: закрыть окно — спрятать в трей (настройка «Сворачивать в трей»),
/// «Закрыть» в меню — выйти, вторая копия программы — показать окно этой. При автозапуске (<c>--minimized</c>) окно не
/// показывается: программа ждет в трее, — но только после первого запуска и с действующим входом в аккаунт сайта:
/// приветствие и вход показываются всегда. Доступ потерян (отозван на сайте, срок истек) — окно показывает вход и из трея:
/// защита выключена, и пользователь должен это увидеть.
/// </summary>
public sealed class DesktopHost
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly MainWindow _window;
    private readonly SettingsService _settings;
    private readonly Dictionary<(Tone, string), WindowIcon> _icons = [];
    private bool _quitting;
    private bool _askingQuit;
    private readonly BlockPinGate? _pin;
    private readonly IGatewayClient? _gateway;

    public DesktopHost(
        Avalonia.Application app,
        IClassicDesktopStyleApplicationLifetime desktop,
        MainWindow window,
        TrayViewModel tray,
        SettingsService settings,
        SingleInstance? instance,
        bool minimized,
        NoticeWindow notices,
        UpdateWatcher updates,
        AccountService? account = null,
        BlockPinGate? pin = null,
        IGatewayClient? gateway = null)
    {
        _desktop = desktop;
        _window = window;
        _settings = settings;
        _pin = pin;
        _gateway = gateway;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (OperatingSystem.IsMacOS() && MacStatusItem.IsMainThread())
        {
            var statusItem = new MacStatusItem(tray, Show);
            desktop.Exit += (_, _) => statusItem.Dispose();
            if (app.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += OnActivation;
                desktop.Exit += (_, _) => activatable.Activated -= OnActivation;
            }
        }
        else
        {
            var icon = new TrayIcon { Menu = tray.Menu, ToolTipText = tray.ToolTip, Icon = Icon(tray) };
            icon.Clicked += (_, _) => Show();
            tray.PropertyChanged += (_, _) =>
            {
                icon.ToolTipText = tray.ToolTip;
                icon.Icon = Icon(tray);
            };
            TrayIcon.SetIcons(app, [icon]);
            desktop.Exit += (_, _) => icon.Dispose();
        }
        tray.ShowWindowRequested += (_, _) => Show();
        tray.QuitRequested += (_, _) => Quit();

        // Установщик обновления запущен, окно откроется после него само (AppRelauncher): закрыться, освободив свои файлы.
        updates.RestartRequested += (_, _) => Dispatcher.UIThread.Post(Quit);

        notices.Attach(window, Show);
        window.Closing += OnClosing;
        if (instance is not null)
        {
            instance.ActivationRequested += (_, _) => Dispatcher.UIThread.Post(Show);
        }

        if (account is not null)
        {
            account.AccessLost += (_, _) => Dispatcher.UIThread.Post(Show);
        }

        if (!minimized || !settings.Current.OnboardingDone || account is { IsRequired: true, HasAccess: false })
        {
            desktop.MainWindow = window;
            window.Show();
        }
    }

    private void OnActivation(object? sender, ActivatedEventArgs e)
    {
        if (e.Kind == ActivationKind.Reopen)
        {
            Show();
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
        if (!_quitting && _gateway?.Status.LocalSiteFilter == true)
        {
            _ = QuitFilterAsync();
            return;
        }
        _quitting = true;
        _desktop.Shutdown();
    }

    private async Task QuitFilterAsync()
    {
        if (_askingQuit)
        {
            return;
        }
        _askingQuit = true;
        try
        {
            Show();
            if (_pin is not null && !await _pin.AllowAsync(
                Localization.Loc.Instance["block.local.stop"], Localization.Loc.Instance["block.local.stop.guard"]).ConfigureAwait(true))
            {
                return;
            }
            try
            {
                await _gateway!.DropProtectionAsync().ConfigureAwait(true);
            }
            catch (IOException)
            {
                // Оставить доступ к управлению, пока служба не подтвердила возврат маршрутов и DNS.
                return;
            }
            _quitting = true;
            _desktop.Shutdown();
        }
        finally
        {
            _askingQuit = false;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Cmd+Q, «Завершить» в Dock и выход из ОС завершают процесс, даже если крестик сворачивает окно в трей.
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            if (!_quitting && e.CloseReason == WindowCloseReason.ApplicationShutdown && _gateway?.Status.LocalSiteFilter == true)
            {
                e.Cancel = true;
                Quit();
                return;
            }
            _quitting = true;
            return;
        }

        if (!_quitting && _settings.Current.MinimizeToTray)
        {
            e.Cancel = true;
            _window.Hide();
            return;
        }

        if (!_quitting)
        {
            e.Cancel = _gateway?.Status.LocalSiteFilter == true;
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
