using System.Diagnostics;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Служба сама по себе: где она берет ядро и как служба пользователя уходит вместе с окном.</summary>
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
