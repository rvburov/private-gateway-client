using System.Text;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// «Что на этом компьютере может мешать» (решение 508): адрес прокси из настройки, чтение переменных, системного прокси, WPAD и
/// WinHTTP, что окно исправляет само и что кладет в копию.
/// </summary>
public sealed class MachineSettingsTests : IDisposable
{
    private readonly string _backups = Path.Combine(Path.GetTempPath(), "pg-backups-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<Hindrance> _removed = [];

    public void Dispose()
    {
        if (Directory.Exists(_backups))
        {
            Directory.Delete(_backups, recursive: true);
        }
    }

    [Theory]
    [InlineData("http://127.0.0.1:2334", "127.0.0.1:2334")]
    [InlineData("127.0.0.1:2334", "127.0.0.1:2334")]
    [InlineData("http://user:pass@127.0.0.1:2334/", "127.0.0.1:2334")]
    [InlineData("socks5h://localhost", "localhost:1080")]
    [InlineData("https://proxy.example.com", "proxy.example.com:443")]
    [InlineData("proxy.example.com", "proxy.example.com:80")]
    [InlineData("http://[::1]:8080", "[::1]:8080")]
    [InlineData("http://127.0.0.1:99999", null)]
    [InlineData("localhost,127.0.0.1,.example.com", null)]
    [InlineData("", null)]
    public void Endpoint_of_a_proxy_setting(string value, string? endpoint) => ProxyAddress.Endpoint(value).ShouldBe(endpoint);

    [Theory]
    [InlineData("http://user:pass@127.0.0.1:2334", "http://***@127.0.0.1:2334", true)]
    [InlineData("user:pass@127.0.0.1:2334", "***@127.0.0.1:2334", true)]
    [InlineData("http://127.0.0.1:2334/path@x", "http://127.0.0.1:2334/path@x", false)]
    [InlineData("127.0.0.1:2334", "127.0.0.1:2334", false)]
    public void Name_and_password_are_masked(string value, string shown, bool credentials)
    {
        ProxyAddress.Mask(value).ShouldBe(shown);
        ProxyAddress.HasCredentials(value).ShouldBe(credentials);
    }

    [Theory]
    [InlineData("127.0.0.1:2334", true)]
    [InlineData("localhost:7890", true)]
    [InlineData("[::1]:1080", true)]
    [InlineData("198.51.100.5:3128", false)]
    public void Local_endpoint(string endpoint, bool local) => ProxyAddress.IsLocal(endpoint).ShouldBe(local);

    [Fact]
    public void Win_http_proxy_and_auto_detection_are_read_from_their_registry_blobs()
    {
        MachineSettings.WinHttpProxy(WinHttp(3, "198.51.100.5:3128", "<local>")).ShouldBe("198.51.100.5:3128");
        MachineSettings.WinHttpProxy(WinHttp(1, string.Empty, string.Empty)).ShouldBeNull("прямое подключение — как после netsh winhttp reset proxy");
        MachineSettings.WinHttpProxy([1, 2, 3]).ShouldBeNull("битая запись — без прокси");
        MachineSettings.WinHttpProxy(null).ShouldBeNull();
        MachineSettings.AutoDetect(Connections(flags: 0x09)).ShouldBeTrue();
        MachineSettings.AutoDetect(Connections(flags: 0x01)).ShouldBeFalse();
        MachineSettings.AutoDetect(null).ShouldBeFalse();
    }

    [Fact]
    public void On_windows_variables_of_the_user_and_the_machine_the_foreign_proxy_wpad_and_win_http_are_found()
    {
        var settings = Settings(new UserProxy("127.0.0.1:7890"));

        settings.Read(AppSettings.Default).ShouldBe(
        [
            new Hindrance(HindranceKind.EnvironmentProxy, "https_proxy", "http://127.0.0.1:2334", HindranceScope.User),
            new Hindrance(HindranceKind.EnvironmentProxy, "no_proxy", "localhost", HindranceScope.User),
            new Hindrance(HindranceKind.EnvironmentProxy, "HTTP_PROXY", "proxy.example.com:3128", HindranceScope.Machine),
            new Hindrance(HindranceKind.SystemProxy, "ProxyServer", "127.0.0.1:7890", HindranceScope.User),
            new Hindrance(HindranceKind.AutoDetect, "AutoDetect", string.Empty, HindranceScope.User),
            new Hindrance(HindranceKind.WinHttpProxy, "WinHttpSettings", "198.51.100.5:3128", HindranceScope.Machine),
        ]);

        var own = AppSettings.Default with { Mode = Core.ConnectionMode.Proxy, Connection = ConnectionSettings.Default with { ProxyPort = 7890, SystemProxy = true } };
        settings.Read(own).ShouldNotContain(h => h.Kind == HindranceKind.SystemProxy, "свой системный прокси — вход своего ядра — не помеха");
        Settings(new UserProxy("http://127.0.0.1:1/proxy.pac", IsScript: true)).Read(AppSettings.Default)
            .ShouldContain(new Hindrance(HindranceKind.AutoConfig, "AutoConfigURL", "http://127.0.0.1:1/proxy.pac", HindranceScope.User));
    }

    [Fact]
    public void Only_a_dead_user_proxy_without_credentials_is_fixed_by_the_window()
    {
        var settings = Settings(null);
        var user = new Hindrance(HindranceKind.EnvironmentProxy, "https_proxy", "http://127.0.0.1:2334", HindranceScope.User);
        var dead = new EntryCheck("127.0.0.1:2334", false, null);

        settings.CanFix(user, dead).ShouldBeTrue();
        settings.CanFix(user, dead with { Listens = true }).ShouldBeFalse("работающий прокси держит программа — ее надо закрыть");
        settings.CanFix(user, null).ShouldBeFalse("без ответа службы неизвестно, мертв ли прокси");
        settings.CanFix(user with { Scope = HindranceScope.Machine }, dead).ShouldBeFalse("переменная машины — права администратора");
        settings.CanFix(user with { Value = "http://u:p@127.0.0.1:2334" }, dead).ShouldBeFalse("имя и пароль в копию открытым текстом не кладутся");
        settings.CanFix(user with { Name = "no_proxy" }, dead).ShouldBeFalse();
        settings.CanFix(new Hindrance(HindranceKind.SystemProxy, "ProxyServer", "127.0.0.1:7890", HindranceScope.User), dead).ShouldBeTrue();
        settings.CanFix(new Hindrance(HindranceKind.WinHttpProxy, "WinHttpSettings", "127.0.0.1:2334", HindranceScope.Machine), dead).ShouldBeFalse();
        new MachineSettings(new FakeProxy(null), _ => new Dictionary<string, string>(), () => null, () => null, _ => { }, _backups, TimeProvider.System, canFix: false)
            .CanFix(user, dead).ShouldBeFalse("исправлять умеет только Windows");
    }

    [Fact]
    public void Fix_keeps_the_old_value_in_a_backup_and_removes_the_setting()
    {
        var settings = Settings(null);
        var user = new Hindrance(HindranceKind.EnvironmentProxy, "https_proxy", "http://127.0.0.1:2334", HindranceScope.User);

        var backup = settings.Fix(user);

        _removed.ShouldBe([user]);
        Path.GetDirectoryName(backup).ShouldBe(_backups);
        var text = File.ReadAllText(backup);
        text.ShouldContain("\"value\": \"http://127.0.0.1:2334\"");
        text.ShouldContain("\"scope\": \"User\"");
    }

    private MachineSettings Settings(UserProxy? systemProxy) =>
        new(new FakeProxy(systemProxy), scope => scope == HindranceScope.User
                ? new Dictionary<string, string> { ["https_proxy"] = " http://127.0.0.1:2334 ", ["no_proxy"] = "localhost", ["path"] = "/usr/bin", ["http_proxy"] = " " }
                : new Dictionary<string, string> { ["HTTP_PROXY"] = "proxy.example.com:3128" },
            () => WinHttp(3, "198.51.100.5:3128", "<local>"), () => Connections(flags: 0x09), _removed.Add, _backups, TimeProvider.System, canFix: true);

    private static byte[] WinHttp(int flags, string proxy, string bypass)
    {
        var data = new List<byte>();
        data.AddRange(BitConverter.GetBytes(0x28));
        data.AddRange(BitConverter.GetBytes(1));
        data.AddRange(BitConverter.GetBytes(flags));
        data.AddRange(BitConverter.GetBytes(proxy.Length));
        data.AddRange(Encoding.ASCII.GetBytes(proxy));
        data.AddRange(BitConverter.GetBytes(bypass.Length));
        data.AddRange(Encoding.ASCII.GetBytes(bypass));
        return [.. data];
    }

    private static byte[] Connections(int flags) => [.. BitConverter.GetBytes(0x46), .. BitConverter.GetBytes(5), .. BitConverter.GetBytes(flags), 0, 0, 0, 0];

    private sealed class FakeProxy(UserProxy? proxy) : IUserProxySettings
    {
        public UserProxy? Read() => proxy;
    }
}
