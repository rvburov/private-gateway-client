using System.Diagnostics;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Служба сама по себе: где она берет ядро, чей системный прокси меняет и как служба пользователя уходит вместе с окном.</summary>
public sealed class ServiceHostTests : IDisposable
{
    private static readonly string Xray = OperatingSystem.IsWindows() ? "xray.exe" : "xray";
    private static readonly string SingBox = OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pg-host-" + Guid.NewGuid().ToString("N")[..8]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Service_host_watches_no_files_wherever_it_is_started()
    {
        // Служба systemd запускается с текущей папкой «/»: слежение за настройками в ней обходило бы всю файловую систему.
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = Path.GetPathRoot(previous)!;
        try
        {
            var journal = new ServiceJournal(TimeProvider.System);
            await using var gateway = new Gateway(new GatewayOptions(null, CanVpn: false, LatencyProbe.DefaultTarget), TimeProvider.System, journal);
            await using var app = PrivateGateway.Client.Service.Ipc.IpcServer.Build(
                new PrivateGateway.Client.Service.Ipc.IpcOptions(Path.Combine(Folder("ipc"), "ipc.sock")), gateway, journal, ServiceHost.Info(null));

            app.Environment.ContentRootPath.TrimEnd(Path.DirectorySeparatorChar).ShouldBe(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                "корень содержимого — папка службы, а не текущая");
            ((Microsoft.Extensions.Configuration.IConfigurationRoot)app.Configuration).Providers
                .OfType<Microsoft.Extensions.Configuration.FileConfigurationProvider>()
                .Where(p => p.Source.ReloadOnChange)
                .Select(p => p.Source.Path)
                .ShouldBeEmpty("файлы настроек не отслеживаются");
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    /// <summary>Служба называет коммит своих исходников — по нему окно узнает установленную службу из прежних исходников.</summary>
    [Fact]
    public void Service_info_names_the_build_of_its_sources()
    {
        Assert.SkipWhen(!Path.Exists(Path.Combine(XrayKit.Root, ".git")), "сборка не из git — коммита в версии нет");
        var info = ServiceHost.Info(null);

        info.ServiceBuild.ShouldBe(ServiceVersion.Build);
        info.ServiceBuild.ShouldMatch("^[0-9a-f]{7,40}$", "сборка из git — коммит в метаданных версии");
        info.ServiceVersion.ShouldNotContain("+", customMessage: "версия — без метаданных, ее сверяет SemVer");
    }

    [Fact]
    public void System_proxy_belongs_to_the_service_that_may_change_it()
    {
        // Windows и Linux — настройки пользователя: их меняет служба пользователя, установленная (SYSTEM, root) не трогает.
        // macOS — настройка машины: служба пользователя без прав ее не меняет, установленная от root — меняет.
        if (OperatingSystem.IsMacOS())
        {
            ServiceHost.SystemProxy(user: true).ShouldBeNull();
            (ServiceHost.SystemProxy(user: false) is not null).ShouldBe(Environment.IsPrivilegedProcess);
        }
        else
        {
            ServiceHost.SystemProxy(user: false).ShouldBeNull();
        }

        if (OperatingSystem.IsWindows())
        {
            ServiceHost.SystemProxy(user: true).ShouldNotBeNull();
        }
    }

    [Fact]
    public void Core_lies_next_to_the_installed_service()
    {
        var service = Folder("Program");
        var core = Touch(Path.Combine(service, "cores", "xray", Xray));
        var tun = Touch(Path.Combine(service, "cores", "sing-box", SingBox));
        Touch(Path.Combine(_root, "artifacts", "cores", "xray", Xray));

        ServiceHost.DefaultXray(service).ShouldBe(core);
        ServiceHost.DefaultSingBox(service).ShouldBe(tun);
    }

    [Fact]
    public void Built_service_takes_the_core_from_artifacts()
    {
        var service = Folder("artifacts", "bin", "Client.Service", "debug");
        var core = Touch(Path.Combine(_root, "artifacts", "cores", "xray", Xray));
        var tun = Touch(Path.Combine(_root, "artifacts", "cores", "sing-box", SingBox));

        ServiceHost.DefaultXray(service + Path.DirectorySeparatorChar).ShouldBe(core);
        ServiceHost.DefaultSingBox(service).ShouldBe(tun);
    }

    [Theory]
    [InlineData("other", "bin", "Client.Service")]
    [InlineData("artifacts", "out", "Client.Service")]
    [InlineData("artifacts", "bin", "Client.UI")]
    public void Other_folders_give_no_core(string a, string b, string c)
    {
        var service = Folder(a, b, c, "debug");
        Touch(Path.Combine(_root, a, "cores", "xray", Xray));
        Touch(Path.Combine(_root, a, "cores", "sing-box", SingBox));

        (ServiceHost.DefaultXray(service), ServiceHost.DefaultSingBox(service)).ShouldBe((null, null));
    }

    [Fact]
    public async Task User_service_stops_when_the_window_is_gone()
    {
        var stopped = 0;
        Task Stop()
        {
            Interlocked.Increment(ref stopped);
            return Task.CompletedTask;
        }

        // Процесс-«окно»: служба на своем адресе — долгий процесс, который тест закрывает сам.
        var executable = Path.Combine(XrayKit.Root, "artifacts", "bin", "Client.Service", new DirectoryInfo(AppContext.BaseDirectory).Name,
            UserServiceLauncher.FileName);
        var endpoint = OperatingSystem.IsWindows()
            ? "PrivateGateway.Test." + Guid.NewGuid().ToString("N")
            : Path.Combine(_root, "window", "ipc.sock");
        using var window = Process.Start(new ProcessStartInfo(executable) { ArgumentList = { "--user", "--ipc", endpoint, "--state", _root } })!;
        try
        {
            var watching = ServiceHost.StopWithParentAsync(window.Id, Stop, Ct);
            await Task.Delay(500, Ct);
            stopped.ShouldBe(0, "окно открыто — служба работает");

            window.Kill();
            await watching.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            stopped.ShouldBe(1);
        }
        finally
        {
            if (!window.HasExited)
            {
                window.Kill();
            }
        }

        await ServiceHost.StopWithParentAsync(window.Id, Stop, Ct);
        stopped.ShouldBe(2, "окна уже нет — останавливаться сразу");
    }

    private string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
