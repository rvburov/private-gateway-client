using System.Net;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Сервер и сайт теста на одном адресе, но разных портах. Прямой сайт отвечает другим текстом: обход TUN не может
/// случайно выдать тот же результат. Только в конфиге теста адрес стенда разрешен до закрытия локальных сетей Xray.
/// </summary>
internal static class EndpointTunnelFixture
{
    public const string TunneledPage = "сайт на адресе сервера — через туннель";
    public const string DirectPage = "сайт на адресе сервера — напрямую";

    public static void RouteSite(JsonObject server, IPAddress address, int tunneledPort, int dnsPort)
    {
        server["outbounds"] = new JsonArray(
            new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" },
            new JsonObject { ["tag"] = "site", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{tunneledPort}" } },
            new JsonObject { ["tag"] = "dns", ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{dnsPort}" } });
        server["routing"] = new JsonObject
        {
            ["rules"] = new JsonArray(
                // DNS нового адаптера, в том числе фоновые запросы Windows, отвечает только DNS стенда.
                new JsonObject { ["type"] = "field", ["port"] = "53", ["outboundTag"] = "dns" },
                new JsonObject { ["type"] = "field", ["ip"] = new JsonArray(Cidr(address)), ["outboundTag"] = "site" }),
        };
    }

    public static void AllowTestAddress(JsonObject client, IPAddress address)
    {
        // TESTNET и адрес пары veth входят в LocalNetworks: без этого исключения тест проверял бы закрытие LAN,
        // а не маршрут к другому порту VPN-сервера. AllowLan остается false, исключение есть только у точного адреса стенда.
        client["routing"]!["rules"]!.AsArray().Insert(0, new JsonObject
        {
            ["type"] = "field",
            ["ip"] = new JsonArray(Cidr(address)),
            ["outboundTag"] = XrayConfigBuilder.ProxyTag,
        });
    }

    public static string Cidr(IPAddress address) => address + (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128");
}
