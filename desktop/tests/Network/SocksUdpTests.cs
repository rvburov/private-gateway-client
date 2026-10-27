using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// UDP входа с паролем у настоящего Xray (как вход TUN режима VPN): до UDP ASSOCIATE датаграммы с 127.0.0.1 вход
/// отбрасывает («Unauthorized UDP access») — так пропадал первый запрос DNS sing-box после подключения (CI, прогон 322);
/// после <see cref="SocksUdp.AssociateAsync"/> — пропускает.
/// </summary>
public sealed class SocksUdpTests
{
    private const string Password = "test-password-11111111";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Udp_of_a_password_inbound_opens_only_after_associate_with_the_right_password()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var echo = new UdpEcho();
        var port = XrayKit.FreePort();
        var config = JsonNode.Parse($$"""
            {
              "log": { "loglevel": "warning" },
              "inbounds": [{
                "listen": "127.0.0.1", "port": {{port}}, "protocol": "socks",
                "settings": { "auth": "password", "accounts": [{ "user": "pg", "pass": "{{Password}}" }], "udp": true }
              }],
              "outbounds": [{ "protocol": "freedom" }]
            }
            """)!.AsObject();
        using var xray = await XrayKit.Start(config, port);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        // Датаграмма SOCKS5 (RFC 1928, 7): без фрагментов, к эху на 127.0.0.1.
        byte[] datagram = [0, 0, 0, 1, 127, 0, 0, 1, (byte)(echo.Port >> 8), (byte)echo.Port, .. "ping"u8];
        async Task<byte[]?> SendAsync()
        {
            await udp.SendAsync(datagram, new IPEndPoint(IPAddress.Loopback, port), Ct);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            wait.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                return (await udp.ReceiveAsync(wait.Token)).Buffer;
            }
            catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
            {
                return null;
            }
        }

        (await SendAsync()).ShouldBeNull("до UDP ASSOCIATE вход отбрасывает датаграммы");
        (await SocksUdp.AssociateAsync(port, "pg", "wrong-password", TimeSpan.FromSeconds(5), Ct)).ShouldBeFalse("чужой пароль");
        (await SendAsync()).ShouldBeNull("с чужим паролем UDP не открылся");
        echo.Datagrams.ShouldBe(0);

        (await SocksUdp.AssociateAsync(port, "pg", Password, TimeSpan.FromSeconds(5), Ct)).ShouldBeTrue();

        var reply = (await SendAsync()).ShouldNotBeNull("после UDP ASSOCIATE вход пропускает датаграммы с 127.0.0.1");
        reply[^4..].ShouldBe("ping"u8.ToArray());
        echo.Datagrams.ShouldBe(1);
    }

    [Fact]
    public async Task Associate_with_nothing_listening_is_false_without_waiting_out_the_limit()
    {
        var port = XrayKit.FreePort();
        var started = TimeProvider.System.GetTimestamp();
        (await SocksUdp.AssociateAsync(port, "pg", Password, TimeSpan.FromSeconds(10), Ct)).ShouldBeFalse();
        TimeProvider.System.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }
}
