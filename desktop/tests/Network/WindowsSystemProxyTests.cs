using PrivateGateway.Client.Infrastructure.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Настоящий WinINet. Первый тест возвращает его же снимок и ничего не меняет. Второй проходит весь путь окна (решение 516):
/// включить прокси 127.0.0.1, сверить записанное, вернуть исходное — он меняет «Параметры Интернета» пользователя и идет
/// только на одноразовой машине CI.
/// </summary>
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

    [Fact]
    public void Window_turns_the_proxy_on_reads_it_back_and_returns_the_original()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("параметры Интернета есть только на Windows");
            return;
        }

        if (Environment.GetEnvironmentVariable("CI") != "true")
        {
            Assert.Skip("меняет «Параметры Интернета» пользователя — только на одноразовой машине CI");
            return;
        }

        // Аудит 2026-11-01: проверялось только «прочитать → вернуть тот же снимок». Здесь — то, что делает окно в режиме
        // «Только прокси» с установленной службой: запись через FLAGS и сверка чтением через FLAGS_UI должны сойтись.
        const int Port = 18466;
        var proxy = new CurrentUserWindowsProxy();
        var before = proxy.Read();
        var folder = Directory.CreateTempSubdirectory("pg-proxy-");
        try
        {
            var lease = new UserProxyLease(proxy, new FileUserProxyBackupStore(Path.Combine(folder.FullName, "proxy.json")));
            lease.Apply(Port).ShouldBeTrue("запись и сверка сошлись — иначе Apply бросил бы IOException");
            var applied = proxy.Read();
            (applied.Values["ProxyEnable"], applied.Values["ProxyServer"], applied.Values["AutoConfigURL"])
                .ShouldBe(("1", "127.0.0.1:" + Port, (string?)null), "прокси включен, PAC на время подключения снят");
            lease.IsOwned.ShouldBeTrue();

            lease.Release().ShouldBeTrue();
            UserProxyLease.Same(proxy.Read(), before).ShouldBeTrue("вернулись прежние флаги, адрес, исключения и PAC");

            // Пользователь или другая программа сменили прокси, пока он был наш: поверх их настроек прежние не пишутся.
            lease.Apply(Port).ShouldBeTrue();
            var someone = CurrentUserWindowsProxy.ForPort(Port + 1);
            proxy.Restore(someone);
            lease.Release().ShouldBeFalse();
            UserProxyLease.Same(proxy.Read(), someone).ShouldBeTrue("чужие настройки остались");
        }
        finally
        {
            proxy.Restore(before);
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Sign_in_launch_is_written_to_run_once_and_removed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("параметры Интернета есть только на Windows");
            return;
        }

        if (Environment.GetEnvironmentVariable("CI") != "true")
        {
            Assert.Skip("меняет «Параметры Интернета» пользователя — только на одноразовой машине CI");
            return;
        }

        // Решение 534: свое имя записи — настоящая «Private Gateway proxy» не трогается.
        var recovery = new RunOnceProxyRecovery(@"C:\Program Files\Private Gateway\PrivateGateway.exe", "Private Gateway proxy test");
        try
        {
            recovery.Require(true);
            recovery.Read().ShouldBe("\"C:\\Program Files\\Private Gateway\\PrivateGateway.exe\" --minimized", "путь в кавычках — пробелы в нем");
            recovery.Require(false);
            recovery.Read().ShouldBeNull();
        }
        finally
        {
            recovery.Require(false);
        }
    }
}
