using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Infrastructure.Demo;

/// <summary>
/// Серверы для демо-режима этапа 1, пока нет импорта и базы. Данные — как в макете (экраны 1, 2
/// и 11), адреса — из диапазонов документации, подключиться к ним нельзя.
/// </summary>
public sealed class DemoServerCatalog : IServerCatalog
{
    /// <summary>Сколько длится «обновление серверов» в демо.</summary>
    public static readonly TimeSpan RefreshDelay = TimeSpan.FromMilliseconds(1200);

    public const string SubscriptionId = "demo-main";

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<ServerProfile> _servers;
    private ServerSubscription[] _subscriptions;
    private string? _selectedId = "demo-nl-01";

    public DemoServerCatalog(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        var now = time.GetUtcNow();
        _subscriptions = [new ServerSubscription(SubscriptionId, "Основная подписка", now.AddMinutes(-32))];
        _servers =
        [
            Demo("demo-nl-01", "Нидерланды 01", "NL", "Амстердам", ProxyProtocol.Vless, TransportSecurity.Reality, "203.0.113.24", 443, "tcp", 42)
                with { IsFavorite = true, LastUsedAt = now.AddHours(-1) },
            Demo("demo-fi-01", "Финляндия 01", "FI", "Хельсинки", ProxyProtocol.Hysteria2, TransportSecurity.Tls, "203.0.113.57", 8443, "quic", 38)
                with { IsFavorite = true },
            Demo("demo-de-02", "Германия 02", "DE", "Франкфурт", ProxyProtocol.Vless, TransportSecurity.Tls, "198.51.100.140", 443, "xhttp", 51)
                with { IsFavorite = true, LastUsedAt = now.AddDays(-1) },
            Demo("demo-se-01", "Швеция 01", "SE", "Стокгольм", ProxyProtocol.WireGuard, TransportSecurity.None, "192.0.2.33", 51820, "udp", 57)
                with { LastUsedAt = now.AddDays(-3) },
            Demo("demo-pl-03", "Польша 03", "PL", "Варшава", ProxyProtocol.Tuic, TransportSecurity.Tls, "192.0.2.88", 443, "quic", 64),
            Demo("demo-us-01", "США 01", "US", "Нью-Йорк", ProxyProtocol.Trojan, TransportSecurity.Tls, "198.51.100.201", 443, "ws", 148),
            Demo("demo-tr-01", "Турция 01", "TR", "Стамбул", ProxyProtocol.Vless, TransportSecurity.Tls, "192.0.2.150", 443, "grpc", null),
        ];
    }

    public IReadOnlyList<ServerProfile> Servers
    {
        get
        {
            lock (_gate)
            {
                return [.. _servers];
            }
        }
    }

    public IReadOnlyList<ServerSubscription> Subscriptions
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions;
            }
        }
    }

    public ServerProfile? Selected
    {
        get
        {
            lock (_gate)
            {
                return _servers.Find(s => s.Id == _selectedId);
            }
        }
    }

    public event EventHandler? Changed;

    public void Choose(string serverId)
    {
        lock (_gate)
        {
            var index = _servers.FindIndex(s => s.Id == serverId);
            if (index < 0 || _selectedId == serverId)
            {
                return;
            }

            _selectedId = serverId;
            _servers[index] = _servers[index] with { LastUsedAt = _time.GetUtcNow() };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetFavorite(string serverId, bool isFavorite)
    {
        lock (_gate)
        {
            var index = _servers.FindIndex(s => s.Id == serverId);
            if (index < 0 || _servers[index].IsFavorite == isFavorite)
            {
                return;
            }

            _servers[index] = _servers[index] with { IsFavorite = isFavorite };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Rename(string serverId, string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        lock (_gate)
        {
            var index = _servers.FindIndex(s => s.Id == serverId);
            if (index < 0 || trimmed.Length == 0 || _servers[index].Name == trimmed)
            {
                return;
            }

            _servers[index] = _servers[index] with { Name = trimmed };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string serverId)
    {
        lock (_gate)
        {
            if (_servers.RemoveAll(s => s.Id == serverId) == 0)
            {
                return;
            }

            if (_selectedId == serverId)
            {
                _selectedId = null;
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(RefreshDelay, _time, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _subscriptions = [.. _subscriptions.Select(s => s with { UpdatedAt = now })];
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static ServerProfile Demo(
        string id, string name, string country, string city, ProxyProtocol protocol, TransportSecurity security,
        string host, int port, string transport, int? latencyMs) =>
        new(id, name, country, protocol, security, host, port, transport, latencyMs, city, SubscriptionId);
}
