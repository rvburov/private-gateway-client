using PrivateGateway.Client.Core.Inspector;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Узел замера инспектора: служба принимает только IP или имя хоста — ни команд, ни адресов с путями и схемами.
/// </summary>
public sealed class ProbeTargetTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  Example.COM.  ", "example.com")]
    [InlineData("www.example.org", "www.example.org")]
    [InlineData("localhost", "localhost")]
    [InlineData("xn--e1afmkfd.xn--p1ai", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("пример.рф", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("203.0.113.10", "203.0.113.10")]
    [InlineData("2001:db8::10", "2001:db8::10")]
    [InlineData("[2001:DB8::10]", "2001:db8::10")]
    [InlineData("2001:0db8:0000::0010", "2001:db8::10")]
    public void Host_or_address_is_taken_in_canonical_form(string text, string target) => ProbeTarget.Normalize(text).ShouldBe(target);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com/")]
    [InlineData("example.com/path")]
    [InlineData("example.com:443")]
    [InlineData("example.com; reboot")]
    [InlineData("$(id).example.com")]
    [InlineData("exa mple.com")]
    [InlineData("-example.com")]
    [InlineData("example-.com")]
    [InlineData("example..com")]
    [InlineData("1.2.3")]
    [InlineData("12345")]
    [InlineData("user@example.com")]
    [InlineData("--help")]
    [InlineData("exa_mple.com")]
    [InlineData("[example.com]")]
    [InlineData("пример..рф")]
    public void Anything_else_is_not_a_host(string? text) => ProbeTarget.Normalize(text).ShouldBeNull();

    [Fact]
    public void Name_longer_than_dns_allows_is_not_a_host()
    {
        ProbeTarget.Normalize(new string('a', 63) + ".example.com").ShouldNotBeNull();
        ProbeTarget.Normalize(new string('a', 64) + ".example.com").ShouldBeNull("метка — не длиннее 63");
        ProbeTarget.Normalize(string.Join('.', Enumerable.Repeat(new string('a', 62), 4)) + ".com").ShouldBeNull("имя — не длиннее 253");
    }
}
