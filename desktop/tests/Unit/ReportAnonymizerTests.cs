using System.Net;
using PrivateGateway.Client.Core.Logging;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Обезличенный отчет: публичные адреса — метками, один адрес — одна метка во всем отчете; MAC скрыт; частные адреса,
/// петля и известные открытые резолверы остаются — без них маршрут не объяснить; секреты и ссылки скрывает SecretMasker.
/// </summary>
public sealed class ReportAnonymizerTests
{
    [Fact]
    public void Public_addresses_become_labels_and_the_same_address_keeps_its_label()
    {
        var anonymizer = new ReportAnonymizer();

        anonymizer.Clean("IP для сайтов: 203.0.113.24 → 198.51.100.7").ShouldBe("IP для сайтов: [адрес-1] → [адрес-2]");
        anonymizer.Clean("сервер 203.0.113.24:443, снова 203.0.113.24").ShouldBe("сервер [адрес-1]:443, снова [адрес-1]", "та же метка и порт на месте");
        anonymizer.Clean("IPv6 2001:db8:1::7 и [2001:db8:1::7]:443").ShouldBe("IPv6 [адрес-3] и [[адрес-3]]:443");
        anonymizer.Clean("fe80::1%en0").ShouldBe("[адрес-4]", "link-local IPv6 бывает с MAC внутри");
        new ReportAnonymizer("address").Clean("203.0.113.24").ShouldBe("[address-1]");
        anonymizer.Clean("сеть 203.0.113.0/24, адрес 203.0.113.24/24, IPv6 2001:db8:1::/64").ShouldBe(
            "сеть [адрес-6]/24, адрес [адрес-1]/24, IPv6 [адрес-5]/64", "узкая сеть — тоже личное, префикс остается");
    }

    [Theory]
    [InlineData("шлюз 192.168.1.1, DNS 10.0.0.1")]
    [InlineData("туннель 172.19.0.1/30, DNS 172.19.0.2")]
    [InlineData("петля 127.0.0.1 и ::1, CGNAT 100.64.0.1")]
    [InlineData("резолверы 1.1.1.1, 8.8.8.8, 2606:4700:4700::1111")]
    [InlineData("время 2026-10-26 15:42:00, версия 0.1.0")]
    [InlineData("метрика 25, MTU 1500")]
    [InlineData("0.0.0.0/1 и 128.0.0.0/1 — туннель, ::/1 и 8000::/1 — тоже")]
    public void Local_and_well_known_addresses_and_ordinary_numbers_stay(string text) =>
        new ReportAnonymizer().Clean(text).ShouldBe(text);

    [Fact]
    public void Mac_and_secrets_are_hidden()
    {
        var anonymizer = new ReportAnonymizer();

        anonymizer.Clean("MAC: 00:00:5e:00:53:01, 00-00-5E-00-53-02").ShouldBe("MAC: [MAC], [MAC]");
        var cleaned = anonymizer.Clean(
            "connect vless://11111111-2222-3333-4444-555555555555@203.0.113.24:443?security=reality#NL https://example.com/sub/aB3dEf9GhK2mN5pQ");
        cleaned.ShouldNotContain("11111111-2222-3333-4444-555555555555");
        cleaned.ShouldNotContain("aB3dEf9GhK2mN5pQ");
        cleaned.ShouldNotContain("203.0.113.24");
        cleaned.ShouldContain("vless://" + SecretMasker.Hidden);
    }

    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.127.0.1", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("169.254.0.1", true)]
    [InlineData("198.18.0.1", true)]
    [InlineData("224.0.0.251", true)]
    [InlineData("9.9.9.9", true)]
    [InlineData("203.0.113.24", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::", true)]
    public void Address_stays_only_when_it_says_nothing_about_the_user(string address, bool keep) =>
        ReportAnonymizer.Keep(IPAddress.Parse(address)).ShouldBe(keep);
}
