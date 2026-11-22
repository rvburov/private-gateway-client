using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Platform.MacOS;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Местный фильтр не открывает обычным приложениям физическую карту и не меняет правила обычного VPN.</summary>
public sealed class PfLocalSiteFilterPolicyTests
{
    private static KillSwitchPolicy Local => new("utun225", [], Direct: new DirectExit("/opt/pg/sing-box", 0), LocalSiteFilterRootExit: true);

    [Fact]
    public void Root_exit_is_explicit_ephemeral_and_does_not_change_the_normal_vpn_or_split_policy()
    {
        var normal = PfKillSwitch.Ruleset(Local with { LocalSiteFilterRootExit = false });
        normal.ShouldNotContain("from any to any user root", customMessage: "обычный VPN и раздельный режим не получают местное разрешение");
        var lines = PfKillSwitch.Ruleset(Local).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        var exits = lines.Where(line => line.Contains("proto { tcp, udp } from any to any", StringComparison.Ordinal)).ToArray();

        exits.ShouldBe(
        [
            "pass out quick inet proto { tcp, udp } from any to any user root",
            "pass out quick inet6 proto { tcp, udp } from any to any user root",
        ]);
        lines.IndexOf(exits[0]).ShouldBeLessThan(lines.FindIndex(line => line.StartsWith("block return out quick proto tcp from any to any port", StringComparison.Ordinal)),
            "собственный DNS ядра идет напрямую, DNS обычных приложений остается в TUN");
        lines.ShouldContain("block return out quick proto tcp all");
        lines.ShouldContain("block drop out quick all");
        lines.ShouldNotContain("pass out quick inet all");
        lines.ShouldNotContain("pass out quick inet6 all");
    }

    [Fact]
    public void Root_exit_cannot_be_made_persistent_or_exist_without_the_local_tunnel()
    {
        Should.Throw<ArgumentException>(() => PfKillSwitch.Ruleset(Local with { Persistent = true }));
        Should.Throw<ArgumentException>(() => PfKillSwitch.Ruleset(Local with { TunnelInterface = null }));
        Should.Throw<ArgumentException>(() => PfKillSwitch.Ruleset(Local with { Direct = null }));
    }

    [Fact]
    public void Unsupported_split_warning_is_removed_only_for_the_explicit_local_policy()
    {
        var directory = Directory.CreateTempSubdirectory("pg-pf-local-").FullName;
        try
        {
            var pf = new PfKillSwitch((arguments, _) => string.Join(' ', arguments) switch
            {
                "-E" => (0, "Token : 12345", string.Empty),
                "-s info" => (0, "Status: Enabled", string.Empty),
                _ => (0, string.Empty, string.Empty),
            }, Path.Combine(directory, "pf.token"));
            var normal = Local with { LocalSiteFilterRootExit = false };
            pf.Enable(normal);
            pf.Warning.ShouldNotBeNull();
            pf.Enable(Local);
            pf.Warning.ShouldBeNull();
            pf.Enable(normal);
            pf.Warning.ShouldNotBeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
