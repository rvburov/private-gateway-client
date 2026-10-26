using System.Text.Json;
using Google.Protobuf;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Сервер, настройки и состояние через контракт IPC туда и обратно: интерфейс и служба видят одно и
/// то же. Серверы — все разобранные эталоны shared/testdata/links.
/// </summary>
public sealed class ContractRoundTripTests
{
    private static readonly string Links = Path.Combine(FindRoot(), "shared", "testdata", "links");

    public static TheoryData<string> Servers()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Links, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (LinkParser.Parse(File.ReadAllText(file)).IsOk)
            {
                data.Add(Path.GetRelativePath(Links, file).Replace('\\', '/')[..^".txt".Length]);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public void Server_survives_the_trip_to_the_service(string fixture)
    {
        var server = LinkParser.Parse(File.ReadAllText(Path.Combine(Links, fixture + ".txt"))).Server!;

        var endpoint = server.ToContract("srv-1");
        var back = V1.ServerEndpoint.Parser.ParseFrom(endpoint.ToByteArray()).ToCore();

        endpoint.Id.ShouldBe("srv-1");
        // Код страны службе не нужен — значок рисует интерфейс.
        Json(back).ShouldBe(Json(server with { CountryCode = string.Empty }));
    }

    [Fact]
    public void Server_with_settings_of_another_protocol_is_rejected()
    {
        var endpoint = LinkParser.Parse("trojan://pw@192.0.2.1:443#T").Server!.ToContract("x");
        endpoint.Vless = new V1.VlessSettings { Uuid = "11111111-2222-3333-4444-555555555555" };

        Should.Throw<ArgumentException>(() => endpoint.ToCore());
        Should.Throw<ArgumentOutOfRangeException>(() => new V1.ServerEndpoint { Trojan = new V1.TrojanSettings() }.ToCore());
    }

    [Fact]
    public void Settings_survive_the_trip_to_the_service()
    {
        var settings = AppSettings.Default with
        {
            KillSwitch = true,
            Protection = new ProtectionSettings(false, true, DnsChoice.Custom, "https://dns.example.com/dns-query", false, true, false,
                false, AddressPreference.Ipv4Only, true, true),
            Connection = new ConnectionSettings(CoreLogLevel.Debug, TunEngine.Gvisor, 1420, false, false, TlsFingerprint.Firefox,
                true, true, 25, ReconnectPolicy.Never, 20808, true),
            Routing = new RoutingSettings(RoutingMode.Split, [new BypassApp("/usr/bin/game", "/usr/bin/game")],
                [new DomainRule("*.example.org", RuleAction.Direct), new DomainRule("geosite:ads", RuleAction.Block)]),
        };

        var (protection, killSwitch) = settings.ToContract().ToCore();
        (protection, killSwitch).ShouldBe((settings.Protection, true));
        (settings with { KillSwitch = false }).ToContract().ToCore().Protection.StrictMode.ShouldBeFalse("строгий режим — только с блоком при обрыве");
        settings.ToConnection().ToCore().ShouldBe(settings.Connection);
        settings.ToRouting().ToCore().ShouldBe(settings.Routing);
        foreach (var dns in new[] { DnsChoice.Profile, DnsChoice.CloudflareDoh, DnsChoice.Quad9Doh })
        {
            var choice = AppSettings.Default with { Protection = ProtectionSettings.Default with { Dns = dns } };
            choice.ToContract().ToCore().Protection.Dns.ShouldBe(dns);
        }
    }

    [Fact]
    public void Proxy_port_out_of_range_is_rejected()
    {
        var connection = AppSettings.Default.ToConnection();
        connection.ProxyPort = 70000;

        Should.Throw<ArgumentOutOfRangeException>(() => connection.ToCore());
    }

    [Fact]
    public void Status_survives_the_trip_to_the_window()
    {
        var at = new DateTimeOffset(2026, 10, 25, 4, 30, 0, TimeSpan.Zero);
        var status = new ConnectionStatus(
            ConnectionState.Connected, ConnectionMode.Proxy, "srv-1", at, 2,
            new ProtectionChecks(CheckState.Warning, CheckState.Ok, CheckState.Off, CheckState.Stopped, at.AddSeconds(5)),
            new TrafficStats(42, 18.5, 4.25), new ExitInfo("203.0.113.24", "Амстердам", "дата-центр", "AS64500", "198.51.100.7"),
            new NetworkInfo(NetworkKind.WiFi, "Кафе", true, false), "PGC-NET-001", at.AddMinutes(3));

        var back = V1.Status.Parser.ParseFrom(status.ToContract().ToByteArray()).ToCore();

        back.ShouldBe(status);
        var off = ConnectionStatus.Initial(at);
        V1.Status.Parser.ParseFrom(off.ToContract().ToByteArray()).ToCore().ShouldBe(off);
        var strict = off with { State = ConnectionState.Blocked, StrictBlock = true };
        V1.Status.Parser.ParseFrom(strict.ToContract().ToByteArray()).ToCore().StrictBlock.ShouldBeTrue("«Блокировка» строгого режима доходит до окна");
        var strictPause = strict with { CaptivePauseUntil = at.AddMinutes(5), Protection = ProtectionChecks.Paused };
        V1.Status.Parser.ParseFrom(strictPause.ToContract().ToByteArray()).ToCore().ShouldBe(strictPause, "пауза строгой «Блокировки» — тоже");
        var tunneled = status with { Mode = ConnectionMode.Vpn, Protection = ProtectionChecks.For(ConnectionState.Connected) with { Ipv6Tunnel = true } };
        V1.Status.Parser.ParseFrom(tunneled.ToContract().ToByteArray()).ToCore().Protection.Ipv6Tunnel.ShouldBeTrue("IPv6 через туннель доходит до окна");
        var moved = status with { NetworkChangedAt = at.AddSeconds(-40) };
        V1.Status.Parser.ParseFrom(moved.ToContract().ToByteArray()).ToCore().ShouldBe(moved, "время смены сети доходит до окна");
    }

    [Fact]
    public void New_enums_round_trip_and_unspecified_is_rejected()
    {
        foreach (var mode in Enum.GetValues<ConnectionMode>())
        {
            mode.ToContract().ToCore().ShouldBe(mode);
        }

        foreach (var kind in Enum.GetValues<TransportKind>())
        {
            kind.ToContract().ToCore().ShouldBe(kind);
        }

        foreach (var kind in Enum.GetValues<NetworkKind>())
        {
            kind.ToContract().ToCore().ShouldBe(kind);
        }

        Enum.GetValues<V1.Mode>().Length.ShouldBe(Enum.GetValues<ConnectionMode>().Length + 1);
        Enum.GetValues<V1.TransportType>().Length.ShouldBe(Enum.GetValues<TransportKind>().Length + 1);
        Enum.GetValues<V1.NetworkKind>().Length.ShouldBe(Enum.GetValues<NetworkKind>().Length + 1);
        Should.Throw<ArgumentOutOfRangeException>(() => V1.Mode.Unspecified.ToCore());
        Should.Throw<ArgumentOutOfRangeException>(() => V1.TransportType.Unspecified.ToCore());
        Should.Throw<ArgumentOutOfRangeException>(() => V1.NetworkKind.Unspecified.ToCore());
    }

    private static string Json(ServerDefinition server) => JsonSerializer.Serialize(new
    {
        server.Name, server.CountryCode, server.Protocol, server.Host, server.Port, server.Transport, server.Security, server.Options,
        Credentials = new
        {
            server.Credentials.Uuid, server.Credentials.Password, server.Credentials.ObfsPassword,
            server.Credentials.PrivateKey, server.Credentials.PresharedKey,
        },
    });

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
