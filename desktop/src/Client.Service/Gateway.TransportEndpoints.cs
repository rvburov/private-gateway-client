using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Service;

public sealed partial class Gateway
{
    private static readonly JsonSerializerOptions TransportJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Отдельный канал загрузки XHTTP соединяется до маршрутизатора ядра: его адрес разрешается тем же защищенным
    /// способом, что адрес основного сервера, до нового TUN. В ядро передается закрепленный IP, в брандмауэр — только
    /// соответствующая пара IP/порт. Имена и параметры профиля в сообщения об ошибках не попадают.
    /// </summary>
    private async Task<CoreRequest?> PrepareTransportEndpointsAsync(CoreRequest core, IReadOnlyList<IPAddress> addresses,
        int generation, CancellationToken cancellationToken)
    {
        var downloads = TransportParameterRules.DownloadEndpoints(core.Server);
        if (core.LocalSiteFilter || downloads.Count == 0)
        {
            return core;
        }
        if (downloads.Count > KillSwitchPolicy.MaxServers)
        {
            return null;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(core.Connection.ConnectTimeoutSeconds), _time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var resolved = new Dictionary<TransportEndpoint, IPAddress>();
            foreach (var endpoint in downloads)
            {
                if (!IsLatestConnect(generation))
                {
                    return null;
                }

                IReadOnlyList<IPAddress> found;
                if (string.Equals(endpoint.Host.Trim('[', ']'), core.Server.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
                {
                    found = addresses;
                }
                else
                {
                    var lookup = core with { Server = core.Server with { Host = endpoint.Host, Port = endpoint.Port } };
                    if (IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var literal))
                    {
                        found = PickAddresses([literal], core.Protection);
                    }
                    else if (_blocking && _held is { Open: false } && Volatile.Read(ref _tunnel) is null)
                    {
                        // DNS ОС заведомо закрыт: не тратить на него весь срок до разрешенного DoH самой службы.
                        found = await LookupPastBlockAsync(lookup, generation, stop.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        found = await ResolveAsync(lookup, generation, stop.Token).ConfigureAwait(false);
                    }
                    if (found.Count == 0)
                    {
                        found = await LookupPastBlockAsync(lookup, generation, stop.Token).ConfigureAwait(false);
                    }
                }

                if (found.Count == 0 || !IsLatestConnect(generation))
                {
                    return null;
                }

                resolved.Add(endpoint, found[0]);
            }

            return WithTransportEndpoints(core, addresses, resolved);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Готовый план транспорта. Исходный сервер сохраняется для сравнения профиля, IPC и загрузочного восстановления;
    /// только конфиг ядра использует закрепленные адреса дополнительных каналов.
    /// </summary>
    public static CoreRequest WithTransportEndpoints(CoreRequest core, IReadOnlyList<IPAddress> addresses,
        IReadOnlyDictionary<TransportEndpoint, IPAddress> resolved)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(resolved);
        var endpoints = TransportParameterRules.DownloadEndpoints(core.Server);
        if (endpoints.Count > KillSwitchPolicy.MaxServers)
        {
            throw new ArgumentException("Слишком много адресов транспорта XHTTP", nameof(resolved));
        }
        var allowed = addresses.Select(address => new KillSwitchEndpoint(address, core.Server.Port)).ToList();
        foreach (var endpoint in endpoints)
        {
            if (!resolved.TryGetValue(endpoint, out var address) || FakeDnsPool.Contains(address))
            {
                throw new ArgumentException("Адрес дополнительного канала XHTTP не найден", nameof(resolved));
            }
            allowed.Add(new KillSwitchEndpoint(address, endpoint.Port));
        }

        var exact = allowed.Distinct().ToArray();
        if (exact.Length == 0 || exact.Length > KillSwitchPolicy.MaxServers)
        {
            throw new ArgumentException("Слишком много адресов транспорта XHTTP", nameof(resolved));
        }

        var server = PinDownloads(core.Server, resolved);
        return core with { ResolvedTransportServer = server, TransportEndpoints = exact };
    }

    /// <summary>В список не превращаются произведения всех адресов на все порты: каждая пара остается точной.</summary>
    private static IReadOnlyList<KillSwitchEndpoint> TransportEndpointsFor(CoreRequest core, IReadOnlyList<IPAddress> addresses) =>
        core.TransportEndpoints ?? [.. addresses.Select(address => new KillSwitchEndpoint(address, core.Server.Port))];

    private static ServerDefinition PinDownloads(ServerDefinition server, IReadOnlyDictionary<TransportEndpoint, IPAddress> resolved)
    {
        if (TransportParameterRules.ExtraForServer(server) is not { } extra)
        {
            return server;
        }

        var root = JsonNode.Parse(extra)!.AsObject();
        Visit(root, server.Host, server.Port);
        if (!TransportParameterRules.TryXhttpExtra(root.ToJsonString(TransportJsonOptions), out var pinned))
        {
            throw new ArgumentException("Некорректные параметры дополнительного канала XHTTP", nameof(server));
        }

        return server with { Transport = server.Transport with { ExtraJson = pinned } };

        void Visit(JsonObject node, string inheritedHost, int inheritedPort)
        {
            if (node["downloadSettings"] is not JsonObject download)
            {
                return;
            }

            var host = download["address"]?.GetValue<string>() is { Length: > 0 } address ? address : inheritedHost;
            var port = download["port"]?.GetValue<int>() ?? inheritedPort;
            var endpoint = new TransportEndpoint(host, port);
            if (!resolved.TryGetValue(endpoint, out var selected))
            {
                throw new ArgumentException("Адрес дополнительного канала XHTTP не найден", nameof(resolved));
            }

            // Замена адреса на IP не заменяет имя сертификата и заголовок HTTP. Иначе исправление DNS само ломало бы TLS.
            if (!IPAddress.TryParse(host.Trim('[', ']'), out _))
            {
                var security = download["security"]?.GetValue<string>();
                var key = security switch { "tls" => "tlsSettings", "reality" => "realitySettings", _ => null };
                if (key is not null)
                {
                    if (download[key] is not JsonObject settings)
                    {
                        download[key] = settings = new JsonObject();
                    }
                    if (settings["serverName"]?.GetValue<string>() is not { Length: > 0 })
                    {
                        settings["serverName"] = host;
                    }
                }
            }

            foreach (var key in new[] { "xhttpSettings", "splithttpSettings" })
            {
                if (download[key] is null && download["network"]?.GetValue<string>() == key[..^"Settings".Length])
                {
                    download[key] = new JsonObject();
                }
                if (download[key] is JsonObject nested)
                {
                    if (nested["host"]?.GetValue<string>() is not { Length: > 0 })
                    {
                        nested["host"] = host.Contains(':', StringComparison.Ordinal) ? $"[{host.Trim('[', ']')}]:{port}" : $"{host}:{port}";
                    }
                    Visit(nested["extra"] as JsonObject ?? nested, host, port);
                }
            }

            download["address"] = selected.ToString();
            download["port"] = port;
        }
    }
}
