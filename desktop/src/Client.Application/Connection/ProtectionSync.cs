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
/// </summary>
public sealed class ProtectionSync(SettingsService settings, IGatewayClient gateway, IServerCatalog catalog, TimeProvider time)
{
    /// <summary>Сколько ждать тишины после изменения, прежде чем переподключаться.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(700);

    private readonly Lock _gate = new();
    private AppSettings? _last;
    private ITimer? _timer;

    public void Start()
    {
        _last = settings.Current;
        settings.Changed += (_, next) => OnChanged(next);
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
        if (status.IsPaused || status.State is not (ConnectionState.Connecting or ConnectionState.Connected
                or ConnectionState.Reconnecting or ConnectionState.Blocked) || catalog.Selected is not { } server)
        {
            return;
        }

        await gateway.ConnectAsync(server).ConfigureAwait(false);
    }
}
