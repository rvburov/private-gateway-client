using System.Net;
using System.Text;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Platform.MacOS;
using PrivateGateway.Client.Platform.Windows;
using PrivateGateway.Client.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Одинаковые контракты платформенных частей службы на каждой ОС: сценарии одни, а проверяется то, что служба берет на этой
/// ОС (ServiceHost), — блок при обрыве, хранилище секретов, таблица маршрутов, часы сна. Правила блока ставятся
/// по-настоящему там, где это не отрезает машину CI: Windows — фильтры WFP только для одной программы, Linux — nftables в
/// своем сетевом пространстве, macOS — pf в своем якоре, который основные правила не вызывают. Что блок пропускает и
/// закрывает трафик, проверяют тесты каждой ОС (KillSwitchTests, WfpKillSwitchTests, PfKillSwitchTests).
/// </summary>
public sealed class PlatformContractTests
{
    private static readonly IPAddress Server = IPAddress.Parse("198.51.100.20");

    [Fact]
    public void Kill_switch_of_this_os_holds_the_contract()
    {
        using var subject = KillSwitchOfThisOs();
        var killSwitch = subject.KillSwitch;
        killSwitch.IsEnabled.ShouldBeFalse("до Enable блока нет");

        var policy = new KillSwitchPolicy(subject.Tunnel, [new(Server, 443), new(IPAddress.Parse("2001:db8::20"), 8443)]);
        KillSwitchPolicy[] variants =
        [
            policy,
            policy with { AllowLan = true, LanDiscovery = true, LanDns = IPAddress.Parse("192.168.1.53") },
            policy with { BlockOutsideDns = false, Ipv6Outside = true },
            policy with { Lookup = new ServiceLookup(subject.Program, 0, [new(IPAddress.Parse("192.0.2.53"), 443)]) },
            policy with { TunnelInterface = null },
        ];
        foreach (var variant in variants)
        {
            killSwitch.Enable(variant);
            (killSwitch.IsEnabled, killSwitch.IsIntact).ShouldBe((true, true), $"{killSwitch.Name}: {variant}");
        }

        Should.Throw<ArgumentException>(() => killSwitch.Enable(policy with { Servers = [new(Server, 0)] }));
        (killSwitch.IsEnabled, killSwitch.IsIntact).ShouldBe((true, true), "отказ в новой политике оставляет прежний блок, а не открытую сеть");

        killSwitch.Disable();
        killSwitch.IsEnabled.ShouldBeFalse();
        killSwitch.Disable();
        killSwitch.IsEnabled.ShouldBeFalse("снятый блок снимается еще раз без ошибки");
    }

    [Fact]
    public void Secret_store_of_this_os_holds_the_contract()
    {
        var sealer = BootConnectionTests.OsSealer();
        var secret = "ключ сервера 11111111-2222-3333-4444-555555555555"u8.ToArray();

        var first = sealer.Seal(secret);
        var second = sealer.Seal(secret);

        Encoding.UTF8.GetString(first).ShouldNotContain("11111111-2222", customMessage: $"{sealer.Name}: секрет — только зашифрованным");
        second.ShouldNotBe(first, "одинаковый секрет — разный шифр");
        sealer.Unseal(first).ShouldBe(secret);
        sealer.Unseal(second).ShouldBe(secret);
        var broken = (byte[])first.Clone();
        broken[^1] ^= 0xFF;
        sealer.Unseal(broken).ShouldBeNull("испорченное не открывается");
        sealer.Unseal("не шифр"u8.ToArray()).ShouldBeNull("чужое не открывается");
    }

    [Fact]
    public void Route_table_of_this_os_sees_the_way_to_the_internet()
    {
        var routes = ServiceHost.Routes().ShouldNotBeNull("маршруты служба читает на каждой ОС").DefaultRoutes();

        routes.ShouldContain(r => !r.IsTunnel, "у машины маршрут ко всему интернету — через сетевую карту");
        routes.ShouldAllBe(r => r.Adapter.Length > 0 && r.Adapter.Trim() == r.Adapter, "имя адаптера — без пробелов и концов строк");
    }

    [Fact]
    public async Task Sleep_clock_of_this_os_does_not_count_time_awake()
    {
        var clock = ServiceHost.SuspendClock().ShouldNotBeNull("часы сна есть на каждой ОС");
        var first = clock.Suspended;

        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        first.ShouldBeGreaterThanOrEqualTo(-TimeSpan.FromMilliseconds(50), "сон с загрузки — не меньше нуля (с погрешностью двух чтений)");
        (clock.Suspended - first).Duration().ShouldBeLessThan(TimeSpan.FromSeconds(1), "без сна часы сна стоят");
    }

    /// <summary>Блок при обрыве этой ОС там, где он не отрезает машину; нет прав — тест пропускается.</summary>
    private static KillSwitchSubject KillSwitchOfThisOs()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.SkipUnless(Environment.IsPrivilegedProcess, "фильтры WFP ставятся только на Windows с правами администратора (CI)");
            var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
            var wfp = new WfpKillSwitch(onlyProgram: curl);
            return new(wfp, "Private Gateway", curl, wfp.Disable);
        }

        if (OperatingSystem.IsMacOS())
        {
            Assert.SkipUnless(Environment.IsPrivilegedProcess, "pf — только на macOS от root (CI)");
            var folder = Directory.CreateTempSubdirectory("pg-contract-").FullName;
            var anchor = "pg-test-" + Guid.NewGuid().ToString("N")[..8];
            var pf = PfKillSwitch.ForThisMachine(Path.Combine(folder, "pf.token"), anchor).ShouldNotBeNull();
            return new(pf, "utun225", "/usr/bin/true", () =>
            {
                pf.Disable();
                Directory.Delete(folder, recursive: true);
            });
        }

        NetworkNamespace.SkipUnlessAvailable("nft");
        var netns = NetworkNamespace.Create();
        return new(new NftablesKillSwitch((arguments, input) => netns.Run(input, ["nft", .. arguments])), "pgtun0", "/usr/bin/true", netns.Dispose);
    }

    /// <summary>Блок при обрыве под проверкой: имя адаптера туннеля и программа поиска сервера этой ОС, уборка после теста.</summary>
    private sealed record KillSwitchSubject(IKillSwitch KillSwitch, string Tunnel, string Program, Action Cleanup) : IDisposable
    {
        public void Dispose() => Cleanup();
    }
}
