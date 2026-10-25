using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core.Settings;
using CoreCheck = PrivateGateway.Client.Core.DiagnosticCheck;
using CoreCheckState = PrivateGateway.Client.Core.CheckState;
using CoreLevel = PrivateGateway.Client.Core.JournalLevel;
using CoreProtocol = PrivateGateway.Client.Core.ProxyProtocol;
using CoreSecurity = PrivateGateway.Client.Core.TransportSecurity;
using CoreState = PrivateGateway.Client.Core.ConnectionState;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Unit;

// Новое значение в перечислении ядра или контракта без строки в ContractMapping роняет эти тесты.
public class ContractMappingTests
{
    [Fact]
    public void Every_state_round_trips()
    {
        foreach (var state in Enum.GetValues<CoreState>())
        {
            state.ToContract().ToCore().ShouldBe(state);
        }
    }

    [Fact]
    public void Every_protocol_round_trips()
    {
        foreach (var protocol in Enum.GetValues<CoreProtocol>())
        {
            protocol.ToContract().ToCore().ShouldBe(protocol);
        }
    }

    [Fact]
    public void Every_security_round_trips()
    {
        foreach (var security in Enum.GetValues<CoreSecurity>())
        {
            security.ToContract().ToCore().ShouldBe(security);
        }
    }

    [Fact]
    public void Every_check_state_level_and_check_id_round_trips()
    {
        foreach (var state in Enum.GetValues<CoreCheckState>())
        {
            state.ToContract().ToCore().ShouldBe(state);
        }

        foreach (var state in Enum.GetValues<V1.CheckState>().Where(s => s != V1.CheckState.Unspecified))
        {
            state.ToCore().ToContract().ShouldBe(state);
        }

        foreach (var level in Enum.GetValues<CoreLevel>())
        {
            level.ToContract().ToCore().ShouldBe(level);
        }

        foreach (var level in Enum.GetValues<V1.LogLevel>().Where(l => l != V1.LogLevel.Unspecified))
        {
            level.ToCore().ToContract().ShouldBe(level);
        }

        foreach (var check in Enum.GetValues<CoreCheck>())
        {
            ContractMapping.ToCoreCheck(check.ToContractId()).ShouldBe(check);
        }
    }

    [Fact]
    public void Unknown_check_from_a_newer_service_is_skipped_not_fatal()
    {
        ContractMapping.ToCoreCheck("quantum_tunnel").ShouldBeNull();
        Should.Throw<ArgumentOutOfRangeException>(() => V1.CheckState.Unspecified.ToCore());
        Should.Throw<ArgumentOutOfRangeException>(() => V1.LogLevel.Unspecified.ToCore());
    }

    [Fact]
    public void Every_contract_value_except_unspecified_maps_to_core()
    {
        foreach (var state in Enum.GetValues<V1.ConnectionState>().Where(s => s != V1.ConnectionState.Unspecified))
        {
            state.ToCore().ToContract().ShouldBe(state);
        }

        foreach (var protocol in Enum.GetValues<V1.Protocol>().Where(p => p != V1.Protocol.Unspecified))
        {
            protocol.ToCore().ToContract().ShouldBe(protocol);
        }

        foreach (var security in Enum.GetValues<V1.SecurityType>().Where(s => s != V1.SecurityType.Unspecified))
        {
            security.ToCore().ToContract().ShouldBe(security);
        }
    }

    [Fact]
    public void Unspecified_from_sender_is_rejected_not_guessed()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => V1.ConnectionState.Unspecified.ToCore());
        Should.Throw<ArgumentOutOfRangeException>(() => V1.Protocol.Unspecified.ToCore());
        Should.Throw<ArgumentOutOfRangeException>(() => V1.SecurityType.Unspecified.ToCore());
    }

    [Fact]
    public void Every_address_preference_round_trips_and_unspecified_is_rejected()
    {
        foreach (var preference in Enum.GetValues<AddressPreference>())
        {
            preference.ToContract().ToCore().ShouldBe(preference);
        }

        foreach (var preference in Enum.GetValues<V1.AddressPreference>().Where(p => p != V1.AddressPreference.Unspecified))
        {
            preference.ToCore().ToContract().ShouldBe(preference);
        }

        Should.Throw<ArgumentOutOfRangeException>(() => V1.AddressPreference.Unspecified.ToCore());
    }

    [Fact]
    public void Default_protection_reaches_the_service_closed()
    {
        var contract = AppSettings.Default.ToContract();

        contract.KillSwitch.ShouldBeTrue();
        contract.BootProtection.ShouldBeTrue();
        contract.StrictMode.ShouldBeFalse();
        contract.BlockIpv6.ShouldBeTrue();
        contract.AllowLan.ShouldBeFalse();
        contract.LanDiscovery.ShouldBeFalse();
        contract.AddressPreference.ShouldBe(V1.AddressPreference.PreferIpv4);
        contract.CheckLeaksAfterConnect.ShouldBeTrue();
        contract.Dns.Servers.ShouldBeEmpty("DNS из профиля сервера");
        contract.Dns.BlockOutsideDns.ShouldBeTrue();
        contract.Dns.FakeDns.ShouldBeFalse();
    }

    [Fact]
    public void Strict_mode_and_discovery_are_sent_only_with_what_they_depend_on()
    {
        var settings = AppSettings.Default with
        {
            KillSwitch = false,
            Protection = ProtectionSettings.Default with { StrictMode = true, LanDiscovery = true, AllowLan = false },
        };

        var contract = settings.ToContract();

        contract.StrictMode.ShouldBeFalse("строгий режим — часть блока при обрыве");
        contract.LanDiscovery.ShouldBeFalse("обнаружение устройств — только в разрешенной локальной сети");
    }

    [Theory]
    [InlineData(DnsChoice.Profile, null, new string[0])]
    [InlineData(DnsChoice.CloudflareDoh, null, new[] { "https://1.1.1.1/dns-query" })]
    [InlineData(DnsChoice.Quad9Doh, null, new[] { "https://9.9.9.9/dns-query" })]
    [InlineData(DnsChoice.Custom, "192.0.2.53", new[] { "192.0.2.53" })]
    [InlineData(DnsChoice.Custom, null, new string[0])]
    public void Dns_choice_becomes_server_addresses(DnsChoice choice, string? custom, string[] expected)
    {
        var settings = AppSettings.Default with { Protection = ProtectionSettings.Default with { Dns = choice, CustomDns = custom } };

        settings.ToContract().Dns.Servers.ShouldBe(expected);
    }

    [Theory]
    [InlineData("192.0.2.53", "192.0.2.53")]
    [InlineData(" 2001:db8::53 ", "2001:db8::53")]
    [InlineData("https://dns.example.com/dns-query?x=1", "https://dns.example.com/dns-query")]
    [InlineData("1.2", null)]
    [InlineData("1.2.3", null)]
    [InlineData("http://dns.example.com/dns-query", null)]
    [InlineData("tls://dns.example.com", null)]
    [InlineData("dns.example.com", null)]
    [InlineData("", null)]
    public void Own_dns_address_is_an_ip_or_doh(string text, string? expected)
    {
        DnsAddress.Normalize(text).ShouldBe(expected);
    }
}
