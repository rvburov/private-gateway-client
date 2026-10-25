using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Совместимость с PRIVATE GATEWAY: профиль устройства из его ссылки (эталон
/// shared/testdata/links/vless/private-gateway) работает с сервером Xray, настроенным так же, как его
/// настраивает PRIVATE GATEWAY (internal/xray/config.go, Render): VLESS + REALITY + Vision на raw.
/// Маска REALITY — свой сервер TLS 1.3 на 127.0.0.1 (второй Xray с самоподписанным сертификатом: TLS
/// на Go, как у настоящих сайтов), ключи — заведомо тестовые.
/// </summary>
public sealed class PrivateGatewayCompatibilityTests
{
    /// <summary>32 байта 0x01 — заведомо тестовый закрытый ключ X25519.</summary>
    private const string RealityPrivate = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE";

    private const string Page = "через REALITY";

    [Fact]
    public async Task Device_profile_connects_through_vless_reality_vision()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var maskPort = XrayKit.FreePort();
        using var mask = await XrayKit.Start(XrayKit.Pair("trojan-tls", maskPort).Config, maskPort);
        using var site = new LocalSite(Page);
        var link = LinkParser.Parse(File.ReadAllText(Path.Combine(XrayKit.Root, "shared", "testdata", "links", "vless", "private-gateway.txt")))
            .Server.ShouldNotBeNull();
        var serverPort = XrayKit.FreePort();
        using var server = await XrayKit.Start(ServerConfig(serverPort, link.Credentials.Uuid!, $"127.0.0.1:{maskPort}", link.Security.RealityShortId!), serverPort);
        var device = link with
        {
            Host = "127.0.0.1",
            Port = serverPort,
            Security = link.Security with { RealityPublicKey = PublicKey(RealityPrivate) },
        };
        var proxyPort = XrayKit.FreePort();
        var config = XrayConfigBuilder.Build(new CoreRequest(device, ConnectionMode.Proxy, ProtectionSettings.Default,
            ConnectionSettings.Default with { ProxyPort = proxyPort }, RoutingSettings.Default)).Config.ShouldNotBeNull();
        // Сайт теста — на 127.0.0.1, а локальная сеть в конфиге закрыта: для проверки туннеля правило убирается.
        config["routing"]!["rules"]!.AsArray().RemoveAt(0);
        using var client = await XrayKit.Start(config, proxyPort);

        using var handler = new SocketsHttpHandler { Proxy = new FixedProxy(new Uri($"socks5://127.0.0.1:{proxyPort}")), UseProxy = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        (await http.GetStringAsync(site.Url, TestContext.Current.CancellationToken)).ShouldBe(Page);
        site.Requests.ShouldBe(1);
    }

    /// <summary>Как Render в PRIVATE GATEWAY: один вход VLESS + REALITY, клиенты с flow Vision, выход freedom.</summary>
    private static JsonObject ServerConfig(int port, string uuid, string target, string shortId) => JsonNode.Parse($$"""
        {
          "log": { "loglevel": "warning" },
          "inbounds": [{
            "tag": "vless-reality", "listen": "127.0.0.1", "port": {{port}}, "protocol": "vless",
            "settings": {
              "clients": [{ "id": "{{uuid}}", "email": "{{uuid}}@private-gateway", "flow": "xtls-rprx-vision" }],
              "decryption": "none"
            },
            "streamSettings": {
              "network": "raw", "security": "reality",
              "realitySettings": {
                "show": false, "target": "{{target}}", "xver": 0,
                "serverNames": ["www.example.com"], "privateKey": "{{RealityPrivate}}", "shortIds": ["{{shortId}}"]
              }
            }
          }],
          "outbounds": [{ "tag": "direct", "protocol": "freedom", "settings": {} }]
        }
        """)!.AsObject();

    /// <summary>Открытый ключ к закрытому — у самого ядра (xray x25519 -i), как делает PRIVATE GATEWAY.</summary>
    private static string PublicKey(string privateKey)
    {
        using var process = Process.Start(new ProcessStartInfo(XrayKit.Xray!)
        {
            ArgumentList = { "x25519", "-i", privateKey },
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        // v25.8: «Public key: …»; новые версии пишут его как «Password: …».
        return output.Split('\n').Select(l => l.Trim())
            .First(l => l.StartsWith("Public key:", StringComparison.Ordinal) || l.StartsWith("Password:", StringComparison.Ordinal))
            .Split(':', 2)[1].Trim();
    }

    private sealed class FixedProxy(Uri proxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxy;

        public bool IsBypassed(Uri host) => false;
    }
}
