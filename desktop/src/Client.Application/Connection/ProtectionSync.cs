using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Application.Connection;

/// <summary>
/// Настройки на ходу: изменилось то, что уходит службе в подключении (режим, блок при обрыве, защита,
/// подключение и ядро, маршрутизация), пока защита включена, — подключиться заново к тому же серверу.
/// Служба меняет подключение без щели: блок при обрыве заменяется, а не снимается. Несколько изменений
/// подряд — одно переподключение через <see cref="Quiet"/> после последнего. Защита выключена, сломана
/// («Ошибка») или на паузе для входа в Wi-Fi — ничего: новые настройки войдут в следующее подключение.
/// «Блокировка» после ручного отключения — не подключаться, а открыть сеть, если Kill Switch выключен
/// или пользователь выбрал режим прокси. Служба помнит блок и после своего перезапуска; выключение
/// тумблера во время ее недоступности применяется после восстановления связи. Блок, который это окно
/// не выключало (другой пользователь машины, сброшенные настройки), окно само не открывает.
/// </summary>
public sealed class ProtectionSync(SettingsService settings, IGatewayClient gateway, IServerCatalog catalog, TimeProvider time,
    ConnectionTarget? target = null)
{
    /// <summary>Сколько ждать тишины после изменения, прежде чем переподключаться.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(700);

    private readonly Lock _gate = new();
    private AppSettings? _last;
    private ITimer? _timer;
    private bool _modeOnly;

    // 1 — Kill Switch выключили в этом окне, а ручной блок еще не открыли: служба была недоступна
    // или прислала ее позже. Открывается один раз.
    private int _openStrict;

    public void Start()
    {
        _last = settings.Current;
        settings.Changed += (_, next) => OnChanged(next);
        gateway.StatusChanged += (_, status) => OnStatus(status);
    }

    private void OnStatus(ConnectionStatus status)
    {
        if (status.StrictBlock && !ConnectionController.KeepsBlock(settings.Current) && Interlocked.Exchange(ref _openStrict, 0) == 1)
        {
            _ = gateway.DisconnectAsync();
        }
    }

    /// <summary>То, что служба получает в Connect: остальное (язык, тема, автозапуск) подключения не меняет.</summary>
    public static bool Affects(AppSettings previous, AppSettings next)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        return previous.Mode != next.Mode || previous.KillSwitch != next.KillSwitch || previous.Protection != next.Protection
            || previous.Connection != next.Connection || previous.Routing != next.Routing
            || (next.Mode == ConnectionMode.Proxy && previous.ExternalProxy != next.ExternalProxy);
    }

    private void OnChanged(AppSettings next)
    {
        if (!ReferenceEquals(next, settings.Current))
        {
            return;
        }

        var previous = _last ?? next;
        _last = next;
        Interlocked.Exchange(ref _openStrict, ConnectionController.KeepsBlock(next) ? 0
            : ConnectionController.KeepsBlock(previous) ? 1 : Volatile.Read(ref _openStrict));
        if (!Affects(previous, next))
        {
            return;
        }

        lock (_gate)
        {
            _modeOnly = previous.Mode != next.Mode && !Affects(previous with { Mode = next.Mode }, next);
            _timer?.Dispose();
            _timer = time.CreateTimer(_ => _ = ApplyAsync(), null, Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task ApplyAsync()
    {
        var status = gateway.Status;
        if (status.StrictBlock)
        {
            if (!ConnectionController.KeepsBlock(settings.Current) && Interlocked.Exchange(ref _openStrict, 0) == 1)
            {
                await gateway.DisconnectAsync().ConfigureAwait(false);
            }

            return;
        }

        if (status.IsPaused || status.State is not (ConnectionState.Connecting or ConnectionState.Connected
                or ConnectionState.Reconnecting or ConnectionState.Blocked))
        {
            return;
        }

        var server = target is null ? catalog.Selected : target.Selected;
        if (server is null)
        {
            if (target is not null && (target.IsExternalProxy || status.Mode != settings.Current.Mode))
            {
                // Режим сменили явно; не оставлять прежний путь активным под названием нового режима.
                await gateway.DisconnectAsync().ConfigureAwait(false);
            }

            return;
        }

        lock (_gate)
        {
            if (_modeOnly && status.Mode == settings.Current.Mode && status.ServerId == server.Id
                && status.State is ConnectionState.Connected or ConnectionState.Connecting)
            {
                // Карточка сервера уже отправила явное подключение после выбора VPN.
                return;
            }
        }

        await gateway.ConnectAsync(server).ConfigureAwait(false);
    }
}
