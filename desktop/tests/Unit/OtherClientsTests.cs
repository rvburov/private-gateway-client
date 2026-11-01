using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Другие VPN и прокси-клиенты (<see cref="OtherClients"/>, решение 525): помеха — работающее ядро прокси, туннель ко всему
/// интернету и слушающий вход системного прокси; окно программы без ядра и незнакомые процессы — не помеха; свои ядра — не в
/// счет. Названия программ не определяются: только признаки.
/// </summary>
public sealed class OtherClientsTests
{
    private const string OwnXray = @"C:\Program Files\Private Gateway\cores\xray.exe";

    private static readonly string[] Own = [OwnXray, @"C:\Program Files\Private Gateway\cores\sing-box.exe"];

    [Fact]
    public void A_running_proxy_core_is_a_sign_and_the_own_core_is_not()
    {
        RunningProcess[] processes =
        [
            new(10, "ClientApp", @"C:\Tools\ClientApp\ClientApp.exe"),
            new(11, "xray", @"C:\Tools\ClientApp\bin\xray.exe"),
            new(13, "xray", OwnXray),
            new(14, "explorer", null),
        ];

        OtherClients.Find(processes, Own, []).ShouldBe([new OtherClient(OtherClientSign.Process, "xray")],
            "чужое ядро — признак; свое ядро службы и окна программ — нет");
    }

    [Fact]
    public void Cores_are_found_by_the_engine_name_without_naming_the_client()
    {
        OtherClients.Find([new(1, "mihomo", "/usr/local/bin/mihomo")], Own, []).ShouldBe([new OtherClient(OtherClientSign.Process, "mihomo")]);
        OtherClients.Find([new(1, "sing-box", "/opt/other/sing-box")], Own, []).ShouldBe([new OtherClient(OtherClientSign.Process, "sing-box")],
            "sing-box другой программы — чужой по пути");
        OtherClients.Find([new(1, "XRAY", null)], Own, []).ShouldBe([new OtherClient(OtherClientSign.Process, "XRAY")],
            "путь не прочитан — своим не признать; имя — без учета регистра");
        OtherClients.Find([new(1, "xray", OwnXray.ToUpperInvariant())], Own, []).ShouldBe(OperatingSystem.IsLinux() ? [new OtherClient(OtherClientSign.Process, "xray")] : [],
            "пути Windows и macOS — без учета регистра");
    }

    [Fact]
    public void Windows_of_clients_and_unknown_processes_are_not_a_hindrance()
    {
        OtherClients.Find([new(1, "ClientApp", null), new(3, "chrome", null), new(4, "wireguard", null)], Own, []).ShouldBeEmpty();
        OtherClients.IsKnown("xray").ShouldBeTrue();
        OtherClients.IsKnown("chrome").ShouldBeFalse();
    }

    [Fact]
    public void A_tunnel_to_the_whole_internet_is_a_sign_whatever_its_name()
    {
        OtherClients.Find([], Own, ["Client Tunnel", "wg0"]).ShouldBe(
        [
            new OtherClient(OtherClientSign.Adapter, "Client Tunnel"),
            new OtherClient(OtherClientSign.Adapter, "wg0"),
        ]);
    }

    [Fact]
    public void A_listening_system_proxy_is_a_sign_unless_the_own_core_listens()
    {
        RunningProcess[] processes = [new(20, "ClientApp", @"C:\ClientApp\ClientApp.exe"), new(21, "xray", OwnXray)];

        OtherClients.Find(processes, Own, [], ("127.0.0.1:2334", 20)).ShouldBe([new OtherClient(OtherClientSign.SystemProxy, "127.0.0.1:2334")],
            "ядро внутри окна клиента: его выдает вход прокси");
        OtherClients.Find(processes, Own, [], ("127.0.0.1:2334", null)).ShouldBe([new OtherClient(OtherClientSign.SystemProxy, "127.0.0.1:2334")],
            "владелец неизвестен — все равно признак");
        OtherClients.Find(processes, Own, [], ("127.0.0.1:10808", 21)).ShouldBeEmpty("вход слушает свое ядро (режим прокси)");
    }

    [Theory]
    [InlineData("127.0.0.1:2334", 2334)]
    [InlineData("localhost:7890", 7890)]
    [InlineData("[::1]:1080", 1080)]
    [InlineData("192.0.2.10:3128", null)]
    [InlineData("proxy.example.com:8080", null)]
    [InlineData("127.0.0.1", null)]
    [InlineData("127.0.0.1:70000", null)]
    [InlineData("http://127.0.0.1:2334", 2334)]
    [InlineData("socks5://user:secret@[::1]:1080/", 1080)]
    [InlineData("http=127.0.0.1:2334;https=127.0.0.1:2335", 2334)]
    [InlineData("https://proxy.example.com:443", null)]
    public void Only_a_proxy_on_this_machine_has_a_local_port(string address, int? port) => OtherClients.LocalPort(address).ShouldBe(port);

    [Fact]
    public void The_list_compares_by_content()
    {
        var one = new OtherClientList([new OtherClient(OtherClientSign.Process, "xray")]);
        var same = new OtherClientList([new OtherClient(OtherClientSign.Process, "xray")]);

        one.ShouldBe(same);
        one.GetHashCode().ShouldBe(same.GetHashCode());
        one.ShouldNotBe(new OtherClientList([new OtherClient(OtherClientSign.SystemProxy, "127.0.0.1:2334")]));
    }
}
