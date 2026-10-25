using System.Diagnostics;
using System.Security.Principal;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Platform.Windows;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Установка службы: копия в защищенную папку, команда запуска с владельцем, юнит systemd (с подделкой
/// systemctl), удаление со снятием блока. На Windows с правами администратора (CI) — настоящая служба
/// Windows с тестовым именем и своим адресом IPC: ставится, отвечает окну и удаляется.
/// </summary>
[Collection(WfpSerial.Name)]
public sealed class ServiceSetupTests : IDisposable
{
    private static readonly string Exe = OperatingSystem.IsWindows() ? "PrivateGateway.Service.exe" : "PrivateGateway.Service";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pg-setup-" + Guid.NewGuid().ToString("N")[..8]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Copy_puts_the_service_and_the_cores_into_one_folder()
    {
        var build = Touch("build", Exe, "PrivateGateway.Service.dll");
        var xray = Path.Combine(Touch("xray", "xray", "geoip.dat", "geosite.dat"), "xray");
        var singBox = Path.Combine(Touch("sing-box", "sing-box", "LICENSE"), "sing-box");
        var target = Path.Combine(_root, "installed");

        ServiceSetup.Copy(build, xray, singBox, target);
        ServiceSetup.Copy(build, xray, singBox, target);
        ServiceSetup.Copy(target, null, null, target);

        Files(target).ShouldBe(new[]
        {
            Exe, "PrivateGateway.Service.dll",
            Path.Combine("cores", "sing-box", "LICENSE"), Path.Combine("cores", "sing-box", "sing-box"),
            Path.Combine("cores", "xray", "geoip.dat"), Path.Combine("cores", "xray", "geosite.dat"), Path.Combine("cores", "xray", "xray"),
        }.Order(StringComparer.Ordinal).ToList(), customMessage: "повторная копия заменяет, а копия папки в саму себя ничего не ломает");
    }

    [Fact]
    public void Command_runs_the_installed_copy_for_its_owner()
    {
        var folder = Path.Combine(_root, "svc");

        ServiceSetup.Command(new SetupOptions("pg-test", folder, "1000")).ShouldBe([Path.Combine(folder, Exe), "--service-name", "pg-test", "--owner", "1000"]);
        ServiceSetup.Command(new SetupOptions("pg-test", folder, "1000", "pg.sock"))
            .ShouldBe([Path.Combine(folder, Exe), "--service-name", "pg-test", "--owner", "1000", "--ipc", "pg.sock"]);
    }

    [Fact]
    public void Owner_is_a_sid_on_windows_and_a_uid_elsewhere()
    {
        if (OperatingSystem.IsWindows())
        {
            ServiceSetup.IsValidOwner(WindowsIdentity.GetCurrent().User!.Value).ShouldBeTrue();
            ServiceSetup.IsValidOwner("1000").ShouldBeFalse();
        }
        else
        {
            ServiceSetup.IsValidOwner("1000").ShouldBeTrue();
            ServiceSetup.IsValidOwner("S-1-5-18").ShouldBeFalse();
        }

        ServiceSetup.IsValidOwner("1000 --xray /tmp/evil").ShouldBeFalse("в команду службы чужие параметры не попадут");
        ServiceSetup.IsValidOwner(string.Empty).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => ServiceSetup.Install(new SetupOptions("pg test", _root, "1000"), _root, null, null,
            new SystemdService(_ => { }, _root)), "имя службы с пробелом");
    }

    [Fact]
    public void Systemd_unit_runs_the_service_as_root_and_restarts_it()
    {
        SystemdService.Unit("Private Gateway", ["/opt/private gateway/svc", "--owner", "1000", "50%"]).ShouldBe("""
            [Unit]
            Description=Private Gateway
            Wants=network-online.target
            After=network-online.target

            [Service]
            Type=simple
            ExecStart="/opt/private gateway/svc" "--owner" "1000" "50%%"
            Restart=on-failure
            RestartSec=5

            [Install]
            WantedBy=multi-user.target

            """.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Linux_install_writes_the_unit_starts_it_and_uninstall_takes_everything_away()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("systemd — только Linux");
            return;
        }

        var units = Directory.CreateDirectory(Path.Combine(_root, "units")).FullName;
        var calls = new List<string>();
        var systemd = new SystemdService(arguments => calls.Add(string.Join(' ', arguments)), units);
        var build = Touch("build", Exe);
        var options = new SetupOptions("pg-test", Path.Combine(_root, "opt"), "1000");

        ServiceSetup.Install(options, build, null, null, systemd);

        calls.ShouldBe(["daemon-reload", "enable pg-test.service", "restart pg-test.service"]);
        File.ReadAllText(Path.Combine(units, "pg-test.service")).ShouldContain($"ExecStart=\"{Path.Combine(options.Folder, Exe)}\" \"--service-name\" \"pg-test\" \"--owner\" \"1000\"");
        File.GetUnixFileMode(Path.Combine(options.Folder, Exe)).HasFlag(UnixFileMode.UserExecute).ShouldBeTrue();

        calls.Clear();
        ServiceSetup.Install(options, build, null, null, systemd);

        calls[0].ShouldBe("disable --now pg-test.service", "работающий файл не перезаписать — служба сначала останавливается");

        calls.Clear();
        systemd.Uninstall("pg-test");
        var killSwitch = new FakeKillSwitch();
        ServiceSetup.Finish(options.Folder, killSwitch);

        calls.ShouldBe(["disable --now pg-test.service", "daemon-reload"]);
        (File.Exists(Path.Combine(units, "pg-test.service")), Directory.Exists(options.Folder), killSwitch.Disabled).ShouldBe((false, false, true),
            "удаленная служба не оставляет ни юнита, ни папки, ни блока при обрыве");
    }

    [Fact]
    public void Finish_deletes_only_a_service_folder()
    {
        var other = Touch("other", "notes.txt");

        ServiceSetup.Finish(other, killSwitch: null);

        Directory.Exists(other).ShouldBeTrue("папка без файла службы — не ее, удалять нельзя");
    }

    [Fact]
    public async Task Windows_service_installs_answers_the_window_and_uninstalls()
    {
        if (!OperatingSystem.IsWindows() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("служба Windows ставится только с правами администратора (CI)");
            return;
        }

        var service = Path.Combine(XrayKit.Root, "artifacts", "bin", "Client.Service",
            AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "release" : "debug",
            Exe);
        Assert.SkipUnless(File.Exists(service), "нет сборки службы");
        var name = "PrivateGatewayTest" + Guid.NewGuid().ToString("N")[..8];
        var pipe = "PrivateGateway.Test." + Guid.NewGuid().ToString("N");
        var folder = Path.Combine(_root, "service");
        try
        {
            (await Run(service, "--install", "--service-name", name, "--install-dir", folder, "--ipc", pipe,
                "--owner", WindowsIdentity.GetCurrent().User!.Value)).ShouldBe(0);

            WindowsServices.IsInstalled(name).ShouldBeTrue();
            File.Exists(Path.Combine(folder, Exe)).ShouldBeTrue();
            using var channel = IpcChannel.Create(pipe, system: true);
            var info = await RetryAsync(() => new V1.GatewayService.GatewayServiceClient(channel).GetServiceInfoAsync(new V1.GetServiceInfoRequest(),
                cancellationToken: Ct).ResponseAsync);
            info.ServiceVersion.ShouldBe(ServiceVersion.Current, "окно говорит со службой, которую запустил диспетчер служб от SYSTEM");

            (await Run(service, "--uninstall", "--service-name", name, "--install-dir", folder)).ShouldBe(0);

            WindowsServices.IsInstalled(name).ShouldBeFalse();
            Directory.Exists(folder).ShouldBeFalse();
        }
        finally
        {
            WindowsServices.Uninstall(name, TimeSpan.FromSeconds(20));
        }
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> call)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (Grpc.Core.RpcException) when (attempt < 40)
            {
                await Task.Delay(500, Ct);
            }
        }
    }

    private static async Task<int> Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = await process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(await output + error);
        return process.ExitCode;
    }

    private string Touch(string folder, params string[] files)
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, folder)).FullName;
        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(path, file), file);
        }

        return path;
    }

    private static List<string> Files(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f)).Order(StringComparer.Ordinal)];

    private sealed class FakeKillSwitch : IKillSwitch
    {
        public bool Disabled { get; private set; }

        public string Name => "подделка";

        public bool IsEnabled => !Disabled;

        public void Enable(KillSwitchPolicy policy)
        {
        }

        public void Disable() => Disabled = true;
    }
}

/// <summary>Тесты, которые трогают фильтры WFP своего провайдера, — по одному: удаление службы снимает и блок.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WfpSerial
{
    public const string Name = "WFP";
}
