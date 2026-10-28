using System.Diagnostics;
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

    /// <summary>Как <see cref="Pair(string, int)"/>, но сервер слушает <paramref name="listen"/>, а не 127.0.0.1.</summary>
    public static (ServerDefinition Server, JsonObject Config) Pair(string kind, int port, IPAddress listen)
    {
        var (definition, config) = Pair(kind, port);
        config["inbounds"]![0]!["listen"] = listen.ToString();
        return (definition with { Host = listen.ToString() }, config);
    }

    /// <summary>
    /// Описание сервера для клиента и конфиг того же сервера в Xray. Без шифрования транспорта — только
    /// для теста на localhost; с TLS — сертификат <see cref="Certificate"/>, клиент закрепляет его SHA-256.
    /// </summary>
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
                    security: Pinned),
                ServerConfig(port, "trojan", $$"""{ "clients": [{ "password": "{{Password}}" }] }""",
                    $$"""{ "network": "raw", "security": "tls", "tlsSettings": { "certificates": [{{Certificate.Json}}] } }""")),
            "shadowsocks" => (Definition(ProxyProtocol.Shadowsocks, port, new ServerCredentials(Password: Password),
                    options: new ProtocolOptions(Cipher: "aes-256-gcm")),
                ServerConfig(port, "shadowsocks", $$"""{ "method": "aes-256-gcm", "password": "{{Password}}", "network": "tcp" }""", raw)),
            "vless-xhttp" => (Definition(ProxyProtocol.Vless, port, withUuid, new TransportOptions(TransportKind.Xhttp, Path: "/pg")),
                ServerConfig(port, "vless", vlessClients, """{ "network": "xhttp", "xhttpSettings": { "path": "/pg" } }""")),
            "vless-ws-tls" => (Definition(ProxyProtocol.Vless, port, withUuid, new TransportOptions(TransportKind.WebSocket, Path: "/pg"),
                    Pinned),
                ServerConfig(port, "vless", vlessClients,
                    $$"""{ "network": "ws", "wsSettings": { "path": "/pg" }, "security": "tls", "tlsSettings": { "certificates": [{{Certificate.Json}}] } }""")),
            "vmess-grpc-tls" => (Definition(ProxyProtocol.Vmess, port, withUuid, new TransportOptions(TransportKind.Grpc, ServiceName: "pg"),
                    Pinned),
                ServerConfig(port, "vmess", $$"""{ "clients": [{ "id": "{{Uuid}}" }] }""",
                    $$"""{ "network": "grpc", "grpcSettings": { "serviceName": "pg" }, "security": "tls", "tlsSettings": { "certificates": [{{Certificate.Json}}] } }""")),
            "shadowsocks-2022" => (Definition(ProxyProtocol.Shadowsocks, port, new ServerCredentials(Password: Ss2022Key),
                    options: new ProtocolOptions(Cipher: "2022-blake3-aes-128-gcm")),
                ServerConfig(port, "shadowsocks", $$"""{ "method": "2022-blake3-aes-128-gcm", "password": "{{Ss2022Key}}", "network": "tcp" }""", raw)),
            "wireguard" => WireGuardPair(port),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    /// <summary>
    /// Самоподписанный сертификат example.com на сутки — один на прогон — и его SHA-256. Проверку сертификата Xray-core 26.x не
    /// выключает (allowInsecure удален), поэтому клиент теста закрепляет его, как пользователь — сертификат своего сервера
    /// («pcs» в ссылке, решение 480).
    /// </summary>
    public static (string Json, string Sha256) Certificate { get; } = Create("example.com");

    /// <summary>TLS на example.com с закрепленным <see cref="Certificate"/>.</summary>
    public static SecurityOptions Pinned { get; } =
        new(TransportSecurity.Tls, Sni: "example.com", PinnedPeerCertSha256: [Certificate.Sha256]);

    /// <summary>Ключ Shadowsocks 2022 — 16 байт base64, заведомо ненастоящий (байты 0..15).</summary>
    private static string Ss2022Key { get; } = Convert.ToBase64String([.. Enumerable.Range(0, 16).Select(b => (byte)b)]);

    /// <summary>
    /// WireGuard: сервер — вход wireguard того же Xray, ключи обеих сторон — свежие от <c>xray wg</c> на каждый тест (в
    /// репозитории ключей нет). Вход слушает UDP — готовность <see cref="Start"/> узнает по строке запуска ядра.
    /// </summary>
    private static (ServerDefinition Server, JsonObject Config) WireGuardPair(int port)
    {
        var (clientKey, clientPublic) = WireGuardKeys();
        var (serverKey, serverPublic) = WireGuardKeys();
        var definition = Definition(ProxyProtocol.WireGuard, port, new ServerCredentials(PrivateKey: clientKey),
            options: new ProtocolOptions(PeerPublicKey: serverPublic, Addresses: ["10.0.0.2/32"], Mtu: 1420));
        // У входа WireGuard нет транспорта Xray — без streamSettings.
        var config = ServerConfig(port, "wireguard",
            $$"""{ "secretKey": "{{serverKey}}", "peers": [{ "publicKey": "{{clientPublic}}", "allowedIPs": ["10.0.0.2/32"] }] }""", "{}");
        config["inbounds"]![0]!.AsObject().Remove("streamSettings");
        return (definition, config);
    }

    private static (string Private, string Public) WireGuardKeys()
    {
        var start = new ProcessStartInfo(Xray!, "wg") { RedirectStandardOutput = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (KeyLine(output, isPublic: false), KeyLine(output, isPublic: true));
    }

    /// <summary>
    /// Ключ из вывода <c>xray wg</c> или <c>xray x25519</c>. Метки менялись от версии к версии: v25.8 — «Private key» и
    /// «Public key», v26 — «PrivateKey» и «Password (PublicKey)».
    /// </summary>
    public static string KeyLine(string output, bool isPublic)
    {
        foreach (var line in output.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var label = line[..colon].Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            if (isPublic ? label is "publickey" || label.StartsWith("password", StringComparison.Ordinal) : label == "privatekey")
            {
                return line[(colon + 1)..].Trim();
            }
        }

        throw new InvalidOperationException("Нет ключа в выводе ядра:\n" + output);
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
    public static string SelfSigned(string host) => Create(host).Json;

    private static (string Json, string Sha256) Create(string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        var json = new JsonObject
        {
            ["certificate"] = Lines(certificate.ExportCertificatePem()),
            ["key"] = Lines(key.ExportPkcs8PrivateKeyPem()),
        }.ToJsonString();
        return (json, certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant());

        static JsonArray Lines(string pem) => new([.. pem.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonValue.Create(l.TrimEnd('\r')))]);
    }

    public static async Task<CoreProcess> Start(JsonObject config, int port)
    {
        AllowLocalSites(config);
        var output = new List<string>();
        var xray = CoreProcess.StartXray(Xray!, config, output: line =>
        {
            lock (output)
            {
                output.Add(line);
            }
        });
        Outputs.AddOrUpdate(xray, output);

        // Сервер слушает адрес из конфига: 127.0.0.1 или адрес машины на паре veth стенда.
        var listen = IPAddress.Parse(config["inbounds"]?[0]?["listen"]?.GetValue<string>() ?? "127.0.0.1");
        // Вход WireGuard слушает только UDP — подключиться к нему по TCP нельзя, готовность — по строке «Xray … started».
        var udp = config["inbounds"]?[0]?["protocol"]?.GetValue<string>() == "wireguard";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (xray.HasExited)
            {
                break;
            }

            if (udp)
            {
                lock (output)
                {
                    if (output.Any(l => l.Contains(" started", StringComparison.Ordinal)))
                    {
                        return xray;
                    }
                }

                await Task.Delay(100).ConfigureAwait(false);
                continue;
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

    /// <summary>
    /// Порт для сервера и сайтов теста — тот же выбор, что у входов ядра службы (<see cref="LocalPort"/>): вне временных портов
    /// ОС и не выданный недавно. Сервер, который тест останавливает и запускает снова на том же порту (сбои долгого прогона),
    /// иначе застал бы свой порт у чужого исходящего соединения: на Windows такое соединение держит порт исключительно, и Xray
    /// получал «bind: … forbidden by its access permissions» (soak 5, 2026-10-28).
    /// </summary>
    public static int FreePort() => LocalPort.FreeForTcpAndUdp();

    /// <summary>Вывод ядра, запущенного <see cref="Start"/>, — для сообщения об ошибке теста; последние 40 строк.</summary>
    public static string Log(CoreProcess core)
    {
        if (!Outputs.TryGetValue(core, out var output))
        {
            return "(вывода нет)";
        }

        lock (output)
        {
            return string.Join('\n', output.TakeLast(40));
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CoreProcess, List<string>> Outputs = [];

    /// <summary>
    /// Xray-core 26.x у сервера по умолчанию не выпускает трафик входов VLESS, VMess, Trojan, Shadowsocks и WireGuard на частные
    /// адреса (finalRules выхода freedom), а сайты и DNS тестов — на 127.0.0.1. Серверу теста выход freedom разрешает все. Клиенту
    /// это не нужно: такого правила у его входов (SOCKS, HTTP) нет.
    /// </summary>
    private static void AllowLocalSites(JsonObject config)
    {
        var server = config["inbounds"]?.AsArray().Any(i => i?["protocol"]?.GetValue<string>() is "vless" or "vmess" or "trojan" or "shadowsocks" or "wireguard")
            ?? false;
        if (!server)
        {
            return;
        }

        foreach (var outbound in config["outbounds"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (outbound["protocol"]?.GetValue<string>() == "freedom")
            {
                if (outbound["settings"] is not JsonObject settings)
                {
                    settings = [];
                    outbound["settings"] = settings;
                }

                settings["finalRules"] ??= new JsonArray(new JsonObject { ["action"] = "allow" });
            }
        }
    }

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
