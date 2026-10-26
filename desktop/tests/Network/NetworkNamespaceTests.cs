using System.Diagnostics;
using Shouldly;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Стенд тестов сети: номера сетей пар veth не совпадают с уже занятыми на машине.</summary>
public sealed class NetworkNamespaceTests
{
    [Fact]
    public void Octets_of_the_machine_are_read_from_its_addresses() =>
        NetworkNamespace.Octets("""
            2: pgv5abf6bfd    inet 10.252.34.1/30 scope global pgv5abf6bfd\       valid_lft forever preferred_lft forever
            2: pgv5abf6bfd    inet 10.252.34.5/32 scope global pgv5abf6bfd\       valid_lft forever preferred_lft forever
            3: eth0    inet 192.0.2.10/24 brd 192.0.2.255 scope global eth0\       valid_lft forever preferred_lft forever
            4: pgv1    inet 203.0.113.9/32 scope global pgv1\       valid_lft forever preferred_lft forever
            5: pgv2    inet 10.254.200.1/30 scope global pgv2\       valid_lft forever preferred_lft forever
            6: eth1    inet 10.25.3.1/24 scope global eth1\       valid_lft forever preferred_lft forever
            """).ShouldBe([34, 9, 200], ignoreOrder: true);

    [Fact]
    public void Network_of_a_live_pair_is_taken_on_the_machine()
    {
        NetworkNamespace.SkipUnlessAvailable("ip");
        using var netns = NetworkNamespace.Create();
        netns.LinkAsMachine();

        NetworkNamespace.MachineOctets().ShouldContain(netns.Octet, "пара пространства, оставшаяся от другого прогона, так же видна новому");
    }

    [Fact]
    public void Pairs_go_away_with_the_namespace_at_once_even_while_a_process_still_holds_it()
    {
        NetworkNamespace.SkipUnlessAvailable("ip", "sleep");
        var netns = NetworkNamespace.Create();
        netns.LinkAsMachine();
        var octet = netns.Octet;
        // Процесс в пространстве держит его живым и после размонтирования — как ядро, которое еще выходит.
        using var holder = Process.Start("nsenter", ["--net=" + netns.Path, "sleep", "30"]);
        try
        {
            netns.Dispose();

            NetworkNamespace.MachineOctets().ShouldNotContain(octet, "номер сети свободен на машине сразу");
        }
        finally
        {
            holder.Kill();
        }
    }
}
