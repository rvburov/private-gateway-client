using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Меню значка в трее (доска 8) — системное меню ОС с теми же пунктами: состояние, включить или
/// отключить, быстрая смена сервера, обновить серверы, открыть окно, проверка, закрыть. Меню
/// пересобирается при каждой смене состояния, списка серверов или языка.
/// </summary>
public sealed partial class TrayViewModel : ObservableObject
{
    /// <summary>Сколько серверов в быстрой смене: выбранный, избранные, недавние.</summary>
    public const int QuickServers = 5;

    private readonly ProtectionViewModel _protection;
    private readonly IServerCatalog _catalog;
    private readonly ConnectionController _controller;
    private readonly IGatewayClient _gateway;
    private readonly ShellViewModel _navigator;

    public TrayViewModel(
        ProtectionViewModel protection, IServerCatalog catalog, ConnectionController controller, IGatewayClient gateway, ShellViewModel shell)
    {
        _protection = protection;
        _catalog = catalog;
        _controller = controller;
        _gateway = gateway;
        _navigator = shell;
        protection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ProtectionViewModel.Status) or "")
            {
                Rebuild();
            }
        };
        // Смену языка щит передает тем же PropertyChanged("") — отдельная подписка не нужна.
        catalog.Changed += (_, _) => Dispatcher.UIThread.Post(Rebuild);
        Rebuild();
    }

    /// <summary>Попросили показать окно: «Открыть окно», «Проверка», «Все серверы…», щелчок по значку.</summary>
    public event EventHandler? ShowWindowRequested;

    /// <summary>«Закрыть»: выйти из программы, а не спрятать окно.</summary>
    public event EventHandler? QuitRequested;

    public NativeMenu Menu { get; } = new();

    public Tone Tone => _protection.Tone;

    /// <summary>Знак внутри щита на значке: ключ Icon.Mark.*.</summary>
    public string Mark => _protection.Mark;

    /// <summary>Подсказка над значком: «Private Gateway · IP скрыт».</summary>
    public string ToolTip => Loc.Instance["app.title"] + " · " + _protection.Title;

    /// <summary>Строка состояния вверху меню: «IP скрыт · NL · Нидерланды 01 · 42 мс».</summary>
    public string Header => _catalog.Selected is { } server
        ? string.Join(" · ", _protection.Title, ServerLine(server, _protection.Status))
        : _protection.Title;

    [RelayCommand]
    private void ShowWindow() => ShowWindowRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Включить или отключить. Снять блокировку — значит открыть сайтам реальный IP: вопрос задается в
    /// окне, поэтому сначала его показать.
    /// </summary>
    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (_protection.Status is { State: ConnectionState.Blocked, IsPaused: false })
        {
            ShowWindow();
        }

        await _protection.ToggleCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task SwitchServerAsync(string serverId) => _controller.SwitchServerAsync(serverId);

    [RelayCommand]
    private Task<bool> RefreshServersAsync() => _catalog.RefreshAsync();

    [RelayCommand]
    private void Open(string page)
    {
        _navigator.Navigate(page);
        ShowWindow();
    }

    [RelayCommand]
    private void Quit() => QuitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Быстрая смена: выбранный, потом избранные, потом недавние — не больше <see cref="QuickServers"/>.</summary>
    public IReadOnlyList<ServerProfile> Quick()
    {
        var servers = _catalog.Servers;
        return
        [
            .. new[] { _catalog.Selected }.OfType<ServerProfile>()
                .Concat(servers.Where(s => s.IsFavorite))
                .Concat(servers.Where(s => s.LastUsedAt is not null).OrderByDescending(s => s.LastUsedAt))
                .DistinctBy(s => s.Id)
                .Take(QuickServers),
        ];
    }

    private void Rebuild()
    {
        Menu.Items.Clear();
        Menu.Items.Add(new NativeMenuItem(Header) { IsEnabled = false });
        Menu.Items.Add(new NativeMenuItemSeparator());
        Menu.Items.Add(new NativeMenuItem(_protection.ActionText) { Command = ToggleCommand });

        var servers = new NativeMenu();
        var selected = _catalog.Selected?.Id;
        foreach (var server in Quick())
        {
            servers.Items.Add(new NativeMenuItem(ServerLine(server, null))
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = server.Id == selected,
                Command = SwitchServerCommand,
                CommandParameter = server.Id,
            });
        }

        if (servers.Items.Count > 0)
        {
            servers.Items.Add(new NativeMenuItemSeparator());
        }

        servers.Items.Add(new NativeMenuItem(Loc.Instance["tray.allServers"]) { Command = OpenCommand, CommandParameter = Pages.Servers });
        Menu.Items.Add(new NativeMenuItem(Loc.Instance["tray.switch"]) { Menu = servers });
        Menu.Items.Add(new NativeMenuItem(Loc.Instance["tray.refresh"]) { Command = RefreshServersCommand });
        Menu.Items.Add(new NativeMenuItemSeparator());
        Menu.Items.Add(new NativeMenuItem(Loc.Instance["tray.open"]) { Command = ShowWindowCommand });
        Menu.Items.Add(new NativeMenuItem(Loc.Instance["tray.check"]) { Command = OpenCommand, CommandParameter = Pages.Check });
        Menu.Items.Add(new NativeMenuItemSeparator());

        // Установленная служба работает и без окна — закрывается только интерфейс; служба пользователя и
        // имитация уходят вместе с окном — закрывается программа, а с ней и прокси.
        Menu.Items.Add(_gateway.Service switch
        {
            ServiceKind.Installed => new NativeMenuItem(Loc.Instance["tray.quit"]) { Command = QuitCommand, ToolTip = Loc.Instance["tray.quit.note"] },
            ServiceKind.User => new NativeMenuItem(Loc.Instance["tray.quit.app"]) { Command = QuitCommand, ToolTip = Loc.Instance["tray.quit.user.note"] },
            _ => new NativeMenuItem(Loc.Instance["tray.quit.app"]) { Command = QuitCommand },
        });
        OnPropertyChanged(string.Empty);
    }

    /// <summary>«NL · Нидерланды 01 · 42 мс»: пинг — измеренный в туннеле, иначе из списка.</summary>
    private static string ServerLine(ServerProfile server, ConnectionStatus? status)
    {
        var latency = status is { State: ConnectionState.Connected } && status.ServerId == server.Id && status.Traffic.LatencyMs is { } live
            ? live
            : server.LatencyMs;
        return string.Join(" · ", new[]
        {
            server.CountryCode,
            server.Name,
            latency is { } ms ? ms.ToString(CultureInfo.InvariantCulture) + " " + Loc.Instance["unit.ms"] : null,
        }.Where(s => !string.IsNullOrEmpty(s)));
    }
}
