using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Параметры ссылки проверяются настоящим обменом через закрепленное ядро, а не только сравнением JSON.</summary>
public sealed class ExtendedTransportCompatibilityTests
{
    [Theory]
    [InlineData(false, "0rtt")]
    [InlineData(true, "0rtt")]
    [InlineData(true, "1rtt")]
    public async Task Vless_encryption_from_a_link_completes_a_real_request(bool postQuantumAuthentication, string rtt)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        // Ключи создаются на время теста, не записываются в репозиторий, файл конфига или аргументы ядра.
        var keys = TestCoreCommand.Run(new ProcessStartInfo(XrayKit.Xray!, "vlessenc"), null, TimeSpan.FromSeconds(30),
            cancellationToken: TestContext.Current.CancellationToken);
        keys.Code.ShouldBe(0);
        var decryption = Fields(keys.Output, "decryption")[postQuantumAuthentication ? 1 : 0];
        var encryption = Fields(keys.Output, "encryption")[postQuantumAuthentication ? 1 : 0].Replace(".0rtt.", "." + rtt + ".", StringComparison.Ordinal);
        var port = XrayKit.FreePort();
        var pair = XrayKit.Pair("vless", port);
        pair.Config["inbounds"]![0]!["settings"]!["decryption"] = decryption;
        var link = $"vless://{XrayKit.Uuid}@127.0.0.1:{port}?encryption=" + Uri.EscapeDataString(encryption);
        var imported = LinkParser.Parse(link).Server.ShouldNotBeNull();

        await Exchange(imported, pair.Config);
    }

    [Theory]
    [InlineData("packet-up")]
    [InlineData("stream-up")]
    public async Task Xhttp_extra_metadata_placement_from_a_link_matches_the_real_server(string mode)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var port = XrayKit.FreePort();
        var pair = XrayKit.Pair("vless-xhttp", port);
        // Сервер ожидает метаданные в этих заголовках. Если extra потерять, клиент отправит их в URL и обмен сорвется.
        const string extra = """
            {"noGRPCHeader":true,"sessionIDPlacement":"header","sessionIDKey":"X-Compat-Session","seqPlacement":"header","seqKey":"X-Compat-Sequence"}
            """;
        pair.Config["inbounds"]![0]!["streamSettings"]!["xhttpSettings"]!["mode"] = mode;
        pair.Config["inbounds"]![0]!["streamSettings"]!["xhttpSettings"]!["extra"] = JsonNode.Parse(extra);
        var link = $"vless://{XrayKit.Uuid}@127.0.0.1:{port}?type=xhttp&path=%2Fpg&mode={mode}&extra=" + Uri.EscapeDataString(extra);
        var imported = LinkParser.Parse(link).Server.ShouldNotBeNull();

        await Exchange(imported, pair.Config);
    }

    private static async Task Exchange(ServerDefinition profile, JsonObject serverConfig)
    {
        using var server = await XrayKit.Start(serverConfig, profile.Port);
        using var site = new LocalSite("через расширенный транспорт");
        var proxyPort = XrayKit.FreePort();
        var request = new CoreRequest(profile, ConnectionMode.Proxy, ProtectionSettings.Default,
            ConnectionSettings.Default with { ProxyPort = proxyPort }, RoutingSettings.Default);
        var config = XrayConfigBuilder.Build(request).Config.ShouldNotBeNull();
        // Единственное послабление стенда — разрешить адресу собственного HTTP-сайта проход через проверяемый сервер.
        config["routing"]!["rules"]!.AsArray().RemoveAt(0);
        using var client = await XrayKit.Start(config, proxyPort);
        using var handler = new SocketsHttpHandler { Proxy = new FixedProxy(new Uri($"socks5://127.0.0.1:{proxyPort}")), UseProxy = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        (await http.GetStringAsync(site.Url, TestContext.Current.CancellationToken)).ShouldBe("через расширенный транспорт");
        site.Requests.ShouldBe(1);
    }

    private static string[] Fields(string output, string name) =>
        [.. output.Split('\n').Where(line => line.TrimStart().StartsWith("\"" + name + "\":", StringComparison.Ordinal))
            .Select(line => JsonSerializer.Deserialize<string>(line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim())!)];

    private sealed class FixedProxy(Uri proxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxy;

        public bool IsBypassed(Uri host) => false;
    }
}
