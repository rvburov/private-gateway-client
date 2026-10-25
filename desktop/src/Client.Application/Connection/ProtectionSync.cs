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
/// «Блокировка» строгого режима — защиту выключили вручную: не подключаться, а открыть сеть, если строгий
/// режим больше не действует (выключен сам, блок при обрыве или режим VPN). Служба помнит строгий режим и после
/// своего перезапуска, а выключить его в окне могли, пока ее не было: такую «Блокировку» окно открывает, как только
/// служба ее прислала. Строгую «Блокировку», которую это окно не выключало (другой пользователь машины, сброшенные
/// настройки), окно само не открывает.
/// </summary>
public sealed class ProtectionSync(SettingsService settings, IGatewayClient gateway, IServerCatalog catalog, TimeProvider time)
{
    /// <summary>Сколько ждать тишины после изменения, прежде чем переподключаться.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(700);

    private readonly Lock _gate = new();
    private AppSettings? _last;
    private ITimer? _timer;

    // 1 — строгий режим выключили в этом окне, а «Блокировку» строгого режима еще не открыли: служба была недоступна
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
            || previous.Connection != next.Connection || previous.Routing != next.Routing;
    }

    private void OnChanged(AppSettings next)
    {
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
                or ConnectionState.Reconnecting or ConnectionState.Blocked) || catalog.Selected is not { } server)
        {
            return;
        }

        await gateway.ConnectAsync(server).ConfigureAwait(false);
    }
}
