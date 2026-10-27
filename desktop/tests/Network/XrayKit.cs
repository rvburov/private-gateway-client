using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Общее для сетевых тестов: сборка ядра, свой сервер Xray на localhost, свободный порт.</summary>
internal static class XrayKit
{
    public const string Uuid = "11111111-2222-3333-4444-555555555555";
    public const string Password = "not-a-real-password";

    /// <summary>Корень репозитория — по PrivateGateway.slnx.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>Сборка ядра: PG_XRAY или artifacts/cores/xray; null — тесты с ядром пропускаются.</summary>
    public static string? Xray { get; } = FindXray();

    /// <summary>sing-box (TUN режима VPN): artifacts/cores/sing-box — scripts/get-singbox.sh; null — тесты пропускаются.</summary>
    public static string? SingBox { get; } =
        Path.Combine(Root, "artifacts", "cores", "sing-box", OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box") is var path && File.Exists(path)
            ? path
            : null;

    /// <summary>
    /// Описание сервера для клиента и конфиг того же сервера в Xray. Без шифрования транспорта — только
    /// для теста на localhost; у Trojan — TLS с самоподписанным сертификатом на example.com.
    /// </summary>
    /// <summary>Как <see cref="Pair(string, int)"/>, но сервер слушает <paramref name="listen"/>, а не 127.0.0.1.</summary>
    public static (ServerDefinition Server, JsonObject Config) Pair(string kind, int port, IPAddress listen)
    {
        var (definition, config) = Pair(kind, port);
        config["inbounds"]![0]!["listen"] = listen.ToString();
        return (definition with { Host = listen.ToString() }, config);
    }

    public static (ServerDefinition Server, JsonObject Config) Pair(string kind, int port)
    {
        var withUuid = new ServerCredentials(Uuid: Uuid);
        var vlessClients = $$"""{ "clients": [{ "id": "{{Uuid}}" }], "decryption": "none" }""";
        const string raw = """{ "network": "raw" }""";
        return kind switch
        {
            "vless" => (Definition(ProxyProtocol.Vless, port, withUuid), ServerConfig(port, "vless", vlessClients, raw)),
            "vless-ws" => (Definition(ProxyProtocol.Vless, port, withUuid, new TransportOptions(TransportKind.WebSocket, Path: "/pg")),
                ServerConfig(port, "vless", vlessClients, """{ "network": "ws", "wsSettings": { "path": "/pg" } }""")),
            "vless-grpc" => (Definition(ProxyProtocol.Vless, port, withUuid, new TransportOptions(TransportKind.Grpc, ServiceName: "pg")),
                ServerConfig(port, "vless", vlessClients, """{ "network": "grpc", "grpcSettings": { "serviceName": "pg" } }""")),
            "vless-httpupgrade" => (Definition(ProxyProtocol.Vless, port, withUuid, new TransportOptions(TransportKind.HttpUpgrade, Path: "/pg")),
                ServerConfig(port, "vless", vlessClients, """{ "network": "httpupgrade", "httpupgradeSettings": { "path": "/pg" } }""")),
            "vmess" => (Definition(ProxyProtocol.Vmess, port, withUuid),
                ServerConfig(port, "vmess", $$"""{ "clients": [{ "id": "{{Uuid}}" }] }""", raw)),
            "trojan-tls" => (Definition(ProxyProtocol.Trojan, port, new ServerCredentials(Password: Password),
                    security: new SecurityOptions(TransportSecurity.Tls, Sni: "example.com", AllowInsecure: true)),
                ServerConfig(port, "trojan", $$"""{ "clients": [{ "password": "{{Password}}" }] }""",
                    $$"""{ "network": "raw", "security": "tls", "tlsSettings": { "certificates": [{{SelfSigned("example.com")}}] } }""")),
            "shadowsocks" => (Definition(ProxyProtocol.Shadowsocks, port, new ServerCredentials(Password: Password),
                    options: new ProtocolOptions(Cipher: "aes-256-gcm")),
                ServerConfig(port, "shadowsocks", $$"""{ "method": "aes-256-gcm", "password": "{{Password}}", "network": "tcp" }""", raw)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    /// <summary>
    /// Сервер, за который ядро Xray не возьмется при любых сборках: VLESS через QUIC — такого транспорта в Xray нет. (Hysteria2 и
    /// TUIC для этого не годятся: со сборкой sing-box служба ведет их через ретранслятор.)
    /// </summary>
    public static ServerDefinition Refused { get; } =
        Definition(ProxyProtocol.Vless, 443, new ServerCredentials(Uuid: Uuid)) with { Transport = TransportOptions.Quic };

    public static ServerDefinition Definition(ProxyProtocol protocol, int port, ServerCredentials credentials,
        TransportOptions? transport = null, SecurityOptions? security = null, ProtocolOptions? options = null) =>
        new("Тест", string.Empty, protocol, "127.0.0.1", port, transport ?? TransportOptions.Tcp, security ?? SecurityOptions.None,
            options ?? ProtocolOptions.Empty, credentials);

    public static JsonObject ServerConfig(int port, string protocol, string settings, string stream) => JsonNode.Parse($$"""
        {
          "log": { "loglevel": "warning", "access": "none" },
          "inbounds": [{ "listen": "127.0.0.1", "port": {{port}}, "protocol": "{{protocol}}", "settings": {{settings}}, "streamSettings": {{stream}} }],
          "outbounds": [{ "protocol": "freedom" }]
        }
        """)!.AsObject();

    /// <summary>Самоподписанный сертификат на сутки — строками PEM, как их принимает Xray.</summary>
    public static string SelfSigned(string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new JsonObject
        {
            ["certificate"] = Lines(certificate.ExportCertificatePem()),
            ["key"] = Lines(key.ExportPkcs8PrivateKeyPem()),
        }.ToJsonString();

        static JsonArray Lines(string pem) => new([.. pem.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonValue.Create(l.TrimEnd('\r')))]);
    }

    public static async Task<CoreProcess> Start(JsonObject config, int port)
    {
        var output = new List<string>();
        var xray = CoreProcess.StartXray(Xray!, config);
        xray.Output += (_, line) =>
        {
            lock (output)
            {
                output.Add(line);
            }
        };

        // Сервер слушает адрес из конфига: 127.0.0.1 или адрес машины на паре veth стенда.
        var listen = IPAddress.Parse(config["inbounds"]?[0]?["listen"]?.GetValue<string>() ?? "127.0.0.1");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (xray.HasExited)
            {
                break;
            }

            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(listen, port).ConfigureAwait(false);
                return xray;
            }
            catch (SocketException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        xray.Dispose();
        lock (output)
        {
            throw new InvalidOperationException("Ядро не открыло порт " + port + ":\n" + string.Join('\n', output));
        }
    }

    /// <summary>Порт, свободный для TCP и UDP: вход SOCKS5 ядра занимает оба.</summary>
    public static int FreePort() => LocalPort.FreeForTcpAndUdp();

    private static string? FindXray()
    {
        if (Environment.GetEnvironmentVariable("PG_XRAY") is { Length: > 0 } path && File.Exists(path))
        {
            return path;
        }

        var xray = Path.Combine(Root, "artifacts", "cores", "xray", OperatingSystem.IsWindows() ? "xray.exe" : "xray");
        return File.Exists(xray) ? xray : null;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PrivateGateway.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Не найден корень репозитория (PrivateGateway.slnx)");
    }
}
