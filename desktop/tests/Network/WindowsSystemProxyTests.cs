using PrivateGateway.Client.Infrastructure.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Настоящий WinINet: вернуть его же снимок. Новый прокси не включается, настройки машины остаются прежними.</summary>
[Collection(FirewallSerial.Name)]
public sealed class WindowsSystemProxyTests
{
    [Fact]
    public void Wininet_reads_and_restores_the_same_connection_settings()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("параметры Интернета есть только на Windows");
            return;
        }

        var proxy = new CurrentUserWindowsProxy();
        var before = proxy.Read();
        try
        {
            proxy.Restore(before);
            UserProxyLease.Same(proxy.Read(), before).ShouldBeTrue("WinINet возвращает те же флаги, адрес, исключения и PAC");
        }
        finally
        {
            proxy.Restore(before);
        }
    }
}
