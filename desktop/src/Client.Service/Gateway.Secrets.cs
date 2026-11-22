using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Service;

public sealed partial class Gateway
{
    private void HideConnectionSecrets(CoreRequest core, bool append = false)
    {
        var credentials = core.Server.Credentials;
        var forwardProxy = core.Server.Protocol is ProxyProtocol.Http or ProxyProtocol.Socks5;
        var basic = forwardProxy && credentials.Username is { Length: > 0 } login && credentials.Password is { Length: > 0 } password
            ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(login + ":" + password)) : null;
        var headers = TransportParameterRules.SensitiveExtraValues(core.Server);
        _journal.Hide([credentials.Uuid, credentials.Password, credentials.ObfsPassword, credentials.PrivateKey, credentials.PresharedKey,
            credentials.Username, basic, .. headers], includeShort: forwardProxy || headers.Count > 0, append: append);
    }
}
