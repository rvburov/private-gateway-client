using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Настоящее ядро: VLESS/TLS по TCP или XHTTP поверх SOCKS5, с отказом цепочки при потере первого участка.</summary>
public sealed class EntryProxyChainTests
{
    [Theory]
    [InlineData(TransportKind.Tcp, false)]
    [InlineData(TransportKind.Tcp, true)]
    [InlineData(TransportKind.Xhttp, false)]
    [InlineData(TransportKind.Xhttp, true)]
    public async Task Vless_reaches_the_site_through_the_entry_socks_and_never_falls_back_direct(
        TransportKind transport, bool fragment)
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("entry-chain-reached-site");
        var vlessPort = XrayKit.FreePort();
        var target = XrayKit.Definition(ProxyProtocol.Vless, vlessPort, new ServerCredentials(Uuid: XrayKit.Uuid),
            transport: transport == TransportKind.Xhttp ? new TransportOptions(TransportKind.Xhttp, Path: "/pg", Mode: "auto") : null,
            security: XrayKit.Pinned);
        var stream = transport == TransportKind.Xhttp
            ? $$"""{ "network": "xhttp", "xhttpSettings": { "path": "/pg" }, "security": "tls", "tlsSettings": { "certificates": [{{XrayKit.Certificate.Json}}] } }"""
            : $$"""{ "network": "raw", "security": "tls", "tlsSettings": { "certificates": [{{XrayKit.Certificate.Json}}] } }""";
        var vlessServer = XrayKit.ServerConfig(vlessPort, "vless",
            $$"""{ "clients": [{ "id": "{{XrayKit.Uuid}}" }], "decryption": "none" }""",
            stream);
        using var vless = await XrayKit.Start(vlessServer, vlessPort);

        var entryPort = XrayKit.FreePort();
        var (entry, socksServer) = ExternalProxyFixture.Pair("socks5", entryPort, authenticated: true);
        using var socks = await XrayKit.Start(socksServer, entryPort);
        var localPort = XrayKit.FreePort();
        var request = new CoreRequest(target, ConnectionMode.Proxy, ProtectionSettings.Default,
            ConnectionSettings.Default with { ProxyPort = localPort, TlsFragment = fragment }, RoutingSettings.Default, EntryProxy: entry);
        var config = XrayConfigBuilder.Build(request).Config.ShouldNotBeNull();
        // Тестовый сайт на loopback. Убираем только запрет LAN; прямого выхода для сайта не добавляем.
        var rules = config["routing"]!["rules"]!.AsArray();
        foreach (var rule in rules.Where(rule => rule?["ip"] is JsonArray networks
            && networks.Any(network => network?.GetValue<string>() == "127.0.0.0/8")).ToArray())
        {
            rules.Remove(rule);
        }

        using var client = await XrayKit.Start(config, localPort);
        (await ExternalProxyFixture.GetAsync(site.Url, localPort, socks: true)).ShouldBe("entry-chain-reached-site");
        site.Requests.ShouldBe(1);

        socks.Dispose();
        await Should.ThrowAsync<Exception>(() => ExternalProxyFixture.GetAsync(site.Url, localPort, socks: true));
        site.Requests.ShouldBe(1, "живой VLESS и сайт не открываются напрямую после отказа входного SOCKS");
    }
}
