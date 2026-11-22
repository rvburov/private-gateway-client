using System.Net;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Service;

public sealed partial class Gateway
{
    // Внутренний адаптер существующего TUN-lifecycle; этот узел никогда не набирается и не показывается как сервер.
    private static readonly ServerDefinition LocalFilterEndpoint = new(
        "Local site filter", string.Empty, ProxyProtocol.Socks5, "127.0.0.1", 10808,
        TransportOptions.Tcp, SecurityOptions.None, ProtocolOptions.Empty, new ServerCredentials());

    /// <summary>Отдельная явная команда, без удаленного профиля и без сохранения подключения при загрузке.</summary>
    public Task<V1.ConnectReply> StartSiteFilterAsync(V1.ConnectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.Clone();
        request.Server = LocalFilterEndpoint.ToContract("local-site-filter");
        request.Mode = V1.Mode.Vpn;
        request.EntryProxy = null;
        return ConnectAsync(request, [IPAddress.Loopback], cancellationToken, localSiteFilter: true);
    }
}
