using PrivateGateway.Client.Platform.Windows;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>GUID и устройство адаптера Wintun — как их берут sing-tun и Wintun (решение 510); на настоящей Windows — WindowsTunnelTests.</summary>
public sealed class WintunAdapterTests
{
    // Имя адаптера на Windows — SingBoxConfigBuilder.InterfaceName; здесь — явно: тест идет на всех ОС.
    [Fact]
    public void Guid_is_md5_of_wintun_and_the_name_as_sing_tun_makes_it()
    {
        // MD5("wintunPrivate Gateway") = c003f506bc40df46701b6bb2f4f9cf17, байты GUID Windows — первые три поля младшим байтом вперед.
        WintunAdapter.AdapterGuid("Private Gateway").ShouldBe(new Guid("06F503C0-40BC-46DF-701B-6BB2F4F9CF17"));
        WintunAdapter.InstanceId("Private Gateway").ShouldBe(@"SWD\Wintun\{06F503C0-40BC-46DF-701B-6BB2F4F9CF17}");
    }
}
