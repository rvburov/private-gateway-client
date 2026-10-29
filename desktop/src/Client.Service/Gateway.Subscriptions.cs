using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Service;

public sealed partial class Gateway
{
    /// <summary>Подписка через парольный probe-in: его маршрут всегда ведет через сервер, даже при обходе VPN программами.</summary>
    public async Task<SubscriptionDownload> DownloadSubscriptionAsync(Uri address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri || address.Scheme is not ("https" or "http"))
        {
            return SubscriptionDownload.Failed(DownloadFailure.Network);
        }

        ProbeLink link;
        VpnTunnel tunnel;
        int session;
        await _switch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Status is not { State: ConnectionState.Connected, Mode: ConnectionMode.Vpn, IsPaused: false }
                || _tunnel is null || _probe is null)
            {
                return SubscriptionDownload.Failed(DownloadFailure.Connection);
            }

            if (_request is { } request && SubscriptionDownload.RefuseUnsafeDns(request.Server.Protocol,
                XrayConfigBuilder.DnsMayBypassServer(request.Protection)) is { } refusal)
            {
                // WireGuard в Xray разрешает имя назначения глобальным DNS ядра, а свой LAN DNS идет напрямую.
                return refusal;
            }

            (link, session, tunnel) = (_probe, _session, _tunnel);
        }
        finally
        {
            _switch.Release();
        }

        using var ended = new CancellationTokenSource();
        void CoreExited(object? sender, EventArgs args)
        {
            try
            {
                _ = ended.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Завершение запроса и уведомление ядра пришли одновременно.
            }
        }

        tunnel.Exited += CoreExited;
        SubscriptionDownload result;
        try
        {
            if (tunnel.HasExited)
            {
                return SubscriptionDownload.Failed(DownloadFailure.Connection);
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ended.Token);
            result = await SubscriptionTransport.DownloadAsync(link, address, _time, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ended.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return SubscriptionDownload.Failed(DownloadFailure.Connection);
        }
        catch (InvalidOperationException)
        {
            // Туннель мог быть закрыт и освобожден между снимком и подпиской на его выход.
            return SubscriptionDownload.Failed(DownloadFailure.Connection);
        }
        finally
        {
            tunnel.Exited -= CoreExited;
        }
        // Переключение, пауза или обрыв за время загрузки: результат старой сессии не применять.
        await _switch.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _session != session || _probe != link
                || Status is not { State: ConnectionState.Connected, Mode: ConnectionMode.Vpn, IsPaused: false }
                ? SubscriptionDownload.Failed(DownloadFailure.Connection) : result;
        }
        finally
        {
            _switch.Release();
        }
    }
}
