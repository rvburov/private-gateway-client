using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class ExternalProxySettingsTests
{
    [Theory]
    [InlineData(" Proxy.Example.com ", "proxy.example.com")]
    [InlineData("пример.рф", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("192.0.2.25", "192.0.2.25")]
    [InlineData("[2001:db8::1]", "2001:db8::1")]
    [InlineData("localhost", "localhost")]
    public void A_host_is_normalized_without_resolving_it(string entered, string expected)
    {
        var settings = new ExternalProxySettings(ExternalProxyProtocol.Https, entered, 8443, Revision: 7);

        var normalized = settings.Normalize().ShouldNotBeNull();

        normalized.Host.ShouldBe(expected);
        var profile = normalized.ToProfile().ShouldNotBeNull();
        profile.Host.ShouldBe(expected);
        profile.Id.ShouldBe("external-proxy:7");
        profile.Protocol.ShouldBe(ProxyProtocol.Http);
        profile.Security.ShouldBe(TransportSecurity.Tls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://proxy.example.com")]
    [InlineData("user:password@proxy.example.com")]
    [InlineData("proxy.example.com:8080")]
    [InlineData("proxy.example.com/path")]
    [InlineData("proxy.example.com?token=example")]
    [InlineData("proxy example.com")]
    [InlineData("proxy.example.com\n")]
    [InlineData("proxy..example.com")]
    [InlineData("_proxy.example.com")]
    [InlineData("[2001:db8::1")]
    [InlineData("fe80::1%eth0")]
    [InlineData("0.0.0.0")]
    [InlineData("[::]")]
    public void A_url_or_ambiguous_endpoint_is_not_a_configured_proxy(string host)
    {
        var settings = new ExternalProxySettings(Host: host);

        settings.Normalize().ShouldBeNull();
        settings.IsConfigured.ShouldBeFalse();
        settings.ToProfile().ShouldBeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void The_external_port_must_be_a_real_remote_port(int port) =>
        new ExternalProxySettings(Host: "proxy.example.com", Port: port).IsConfigured.ShouldBeFalse();

    [Fact]
    public void Invalid_choices_and_negative_generation_are_not_connectable()
    {
        var valid = new ExternalProxySettings(Host: "proxy.example.com");

        (valid with { Protocol = (ExternalProxyProtocol)99 }).IsConfigured.ShouldBeFalse();
        (valid with { Authentication = (ProxyAuthentication)99 }).IsConfigured.ShouldBeFalse();
        (valid with { Revision = -1 }).IsConfigured.ShouldBeFalse();
        (valid with { Host = new string('a', 63) + "." + new string('b', 63) + "." + new string('c', 63) + "." + new string('d', 61) + "." }).IsConfigured.ShouldBeFalse();
    }

    [Fact]
    public void External_ids_and_secret_references_cannot_alias_a_vpn_profile()
    {
        ExternalProxySettings.IsExternalId("external-proxy").ShouldBeTrue();
        ExternalProxySettings.IsExternalId("external-proxy:7").ShouldBeTrue();
        ExternalProxySettings.IsExternalId("external-proxy:-1").ShouldBeFalse();
        ExternalProxySettings.IsExternalId("external-proxy:7:other").ShouldBeFalse();
        ExternalProxySettings.IsExternalId("srv-external-proxy").ShouldBeFalse();
        ExternalProxySettings.IsCredentialsKey("server:srv-example").ShouldBeFalse();
        (new ExternalProxySettings(Host: "proxy.example.com", CredentialsKey: "server:srv-example").Normalize())!
            .CredentialsKey.ShouldBeNull();
    }
}
