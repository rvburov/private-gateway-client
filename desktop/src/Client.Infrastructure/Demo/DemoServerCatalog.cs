using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Infrastructure.Demo;

/// <summary>
/// Серверы для демо-режима этапа 1, пока нет импорта и базы. Адреса — из диапазонов документации,
/// подключиться к ним нельзя.
/// </summary>
public sealed class DemoServerCatalog : IServerCatalog
{
    private readonly List<ServerProfile> _servers =
    [
        new("demo-nl-01", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, "203.0.113.24", 443, "tcp", 42),
        new("demo-fi-01", "Финляндия 01", "FI", ProxyProtocol.Vless, TransportSecurity.Reality, "203.0.113.31", 443, "tcp", 38),
        new("demo-de-02", "Германия 02", "DE", ProxyProtocol.Hysteria2, TransportSecurity.Tls, "198.51.100.40", 8443, "quic", 51),
        new("demo-se-01", "Швеция 01", "SE", ProxyProtocol.Vless, TransportSecurity.Tls, "198.51.100.12", 443, "ws", 57),
        new("demo-pl-03", "Польша 03", "PL", ProxyProtocol.Trojan, TransportSecurity.Tls, "192.0.2.15", 443, "tcp", 64),
        new("demo-us-01", "США 01", "US", ProxyProtocol.Shadowsocks, TransportSecurity.None, "192.0.2.80", 8388, "tcp", 148),
        new("demo-tr-01", "Турция 01", "TR", ProxyProtocol.Vless, TransportSecurity.None, "192.0.2.99", 80, "tcp", null),
    ];

    private string? _selectedId = "demo-nl-01";

    public IReadOnlyList<ServerProfile> Servers => _servers;

    public ServerProfile? Selected => _servers.Find(s => s.Id == _selectedId);

    public event EventHandler? Changed;

    public void Choose(string serverId)
    {
        if (_servers.Exists(s => s.Id == serverId) && _selectedId != serverId)
        {
            _selectedId = serverId;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
