using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Отчет пользователя 2026-11-06 17:59: у сервера «VPS + прокси» в списке был только адрес VPS, а сайты видят выход прокси за
/// ним. Окно запоминает подтвержденный службой выход через сервер (решение 583).
/// </summary>
public sealed class ExitMemoryTests
{
    private static readonly ConnectionStatus Connected = ConnectionStatus.Initial(DateTimeOffset.UnixEpoch) with
    {
        State = ConnectionState.Connected,
        ServerId = "srv-vps",
        Protection = ProtectionChecks.For(ConnectionState.Connected),
        Exit = ExitInfo.Unknown with { Ip = "203.0.113.55", DirectIp = "198.51.100.7" },
    };

    [Fact]
    public void Only_an_exit_measured_through_the_server_is_remembered()
    {
        ExitMemory.Exit(Connected).ShouldBe("203.0.113.55");
        ExitMemory.Exit(Connected with { Mode = ConnectionMode.Proxy }).ShouldBe("203.0.113.55", "в режиме прокси замер идет через сервер");

        ExitMemory.Exit(Connected with { State = ConnectionState.Reconnecting }).ShouldBeNull("выход прежнего подключения не подтвержден");
        ExitMemory.Exit(Connected with { ServerId = null }).ShouldBeNull();
        ExitMemory.Exit(Connected with { Exit = Connected.Exit with { Ip = null } }).ShouldBeNull("еще не мерили");
        ExitMemory.Exit(Connected with { Exit = Connected.Exit with { Ip = "198.51.100.7" } })
            .ShouldBeNull("сайты видят реальный IP машины — это не выход сервера");
        ExitMemory.Exit(Connected with { CaptivePauseUntil = DateTimeOffset.UnixEpoch.AddMinutes(5) })
            .ShouldBeNull("пауза для входа в Wi-Fi: трафик мимо сервера");
        ExitMemory.Exit(Connected with
        {
            Protection = Connected.Protection with { AllTraffic = CheckState.Warning },
            Exit = Connected.Exit with { DirectIp = "203.0.113.55" },
        }).ShouldBeNull("утечка");
    }

    [Fact]
    public void Status_of_the_service_reaches_the_catalog()
    {
        var gateway = new ConnectionControllerTests.FakeGateway(ConnectionState.Connected, "srv-vps") { Status = Connected };
        var catalog = new ConnectionControllerTests.FakeCatalog(Server("srv-vps"));
        new ExitMemory(gateway, catalog).Start();

        gateway.Announce();
        gateway.Status = Connected with { State = ConnectionState.Reconnecting, Exit = Connected.Exit with { Ip = "203.0.113.99" } };
        gateway.Announce();

        catalog.Exits.ShouldBe(new Dictionary<string, string> { ["srv-vps"] = "203.0.113.55" });
    }

    private static ServerProfile Server(string id) =>
        new(id, "VPS", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, "198.51.100.10", 8444, "tcp", null);
}
