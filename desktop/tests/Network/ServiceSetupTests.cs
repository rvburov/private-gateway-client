using System.Diagnostics;
using System.Security.Principal;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Platform.MacOS;
using PrivateGateway.Client.Platform.Windows;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Установка службы: копия в защищенную папку (только файлы службы), команда запуска с владельцем и папкой состояния,
/// атомарная замена работающей службы с возвратом прежней (служба ОС — подделка), юнит systemd (с подделкой systemctl),
/// удаление со снятием блока и состояния. На Windows с правами администратора (CI) — настоящая служба Windows с тестовым
/// именем и своим адресом IPC: ставится, ставится поверх, отвечает окну и удаляется.
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
    public void Copy_into_a_folder_inside_the_program_takes_only_the_program()
    {
        // Как после установки пакета: программа в /opt/private-gateway, служба ставится в /opt/private-gateway/service.
        var program = Touch("program", Exe, "PrivateGateway.Service.dll");
        Touch(Path.Combine("program", "cores", "xray"), "xray");
        var target = Path.Combine(program, "service");

        ServiceSetup.Copy(program, Path.Combine(program, "cores", "xray", "xray"), null, target);
        ServiceSetup.Copy(program, Path.Combine(program, "cores", "xray", "xray"), null, target);

        Files(target).ShouldBe(new[] { Exe, "PrivateGateway.Service.dll", Path.Combine("cores", "xray", "xray") }.Order(StringComparer.Ordinal).ToList(),
            customMessage: "копия не подхватывает саму себя, и повторная установка не вкладывает service/service");
    }

    [Fact]
    public void Copy_takes_only_the_service_files_named_in_its_deps_json()
    {
        // Как самодостаточная сборка выпуска: окно и служба в одной папке, у службы свой deps.json.
        var build = Touch("build", Exe, "PrivateGateway.Service.dll", "PrivateGateway.Service.deps.json", "PrivateGateway.Service.runtimeconfig.json",
            "Grpc.Core.Api.dll", "libSystem.Native.so", "PrivateGateway.dll", "Avalonia.Base.dll", "libSkiaSharp.so");
        Touch(Path.Combine("build", "ru"), "Grpc.Core.Api.resources.dll");
        Touch(Path.Combine("build", "runtimes", "win", "lib", "net10.0"), "System.ServiceProcess.ServiceController.dll");
        File.WriteAllText(Path.Combine(build, "PrivateGateway.Service.deps.json"), """
            { "targets": { ".NETCoreApp,Version=v10.0/linux-x64": {
                "Grpc.Core.Api/2.71.0": { "runtime": { "lib/net8.0/Grpc.Core.Api.dll": {} },
                                          "resources": { "lib/net8.0/ru/Grpc.Core.Api.resources.dll": { "locale": "ru" } } },
                "System.ServiceProcess.ServiceController/10.0.0": { "runtimeTargets": {
                    "runtimes/win/lib/net10.0/System.ServiceProcess.ServiceController.dll": { "rid": "win", "assetType": "runtime" } } },
                "runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/10.0.0": { "native": { "libSystem.Native.so": {} } },
                "Grpc.Tools/2.71.0": { "runtime": { "lib/net8.0/Missing.In.Folder.dll": {} } } } } }
            """);

        ServiceSetup.Copy(build, null, null, Path.Combine(_root, "installed"));

        Files(Path.Combine(_root, "installed")).ShouldBe(new[]
        {
            Exe, "Grpc.Core.Api.dll", "PrivateGateway.Service.deps.json", "PrivateGateway.Service.dll", "PrivateGateway.Service.runtimeconfig.json",
            "libSystem.Native.so", Path.Combine("ru", "Grpc.Core.Api.resources.dll"),
            Path.Combine("runtimes", "win", "lib", "net10.0", "System.ServiceProcess.ServiceController.dll"),
        }.Order(StringComparer.Ordinal).ToList(), customMessage: "окно (PrivateGateway.dll, Avalonia, Skia) служба не возит, а библиотеки ОС сборки без RID — на своих местах");
    }

    [Fact]
    public void Command_runs_the_installed_copy_for_its_owner_with_its_state_folder()
    {
        var folder = Path.Combine(_root, "svc");
        var state = Path.Combine(_root, "state");

        ServiceSetup.Command(new SetupOptions("pg-test", folder, "1000", State: state))
            .ShouldBe([Path.Combine(folder, Exe), "--service-name", "pg-test", "--owner", "1000", "--state", state]);
        ServiceSetup.Command(new SetupOptions("pg-test", folder, "1000", "pg.sock", state))
            .ShouldBe([Path.Combine(folder, Exe), "--service-name", "pg-test", "--owner", "1000", "--state", state, "--ipc", "pg.sock"]);
        new SetupOptions("pg-test", folder, "1000").StateFolder.ShouldBe(ServiceSetup.DefaultStateFolder);
    }

    [Fact]
    public void First_install_copies_registers_and_waits_until_the_service_is_ready()
    {
        var options = Options();
        var os = new FakeOs(options.StateFolder, works: _ => true);

        ServiceSetup.Install(options, Touch("build", Exe), null, null, os, TimeSpan.FromSeconds(5));

        os.Calls.ShouldBe(["stop", "register", "start"]);
        os.Command!.TakeLast(2).ShouldBe(["--state", options.StateFolder]);
        File.Exists(Path.Combine(options.Folder, Exe)).ShouldBeTrue();
        Directory.Exists(options.Folder + ".new").ShouldBeFalse();
        Directory.Exists(options.Folder + ".old").ShouldBeFalse();
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(options.StateFolder).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, "состояние — только для root");
            File.GetUnixFileMode(Path.Combine(options.Folder, Exe)).HasFlag(UnixFileMode.UserExecute).ShouldBeTrue();
        }
    }

    [Fact]
    public void Update_replaces_the_running_copy_and_moves_its_state_out_of_it()
    {
        var options = Options();
        var old = Old(options, "journal.jsonl", "boot-block.json");
        var os = new FakeOs(options.StateFolder, works: _ => true) { Installed = true };

        ServiceSetup.Install(options, New(), null, null, os, TimeSpan.FromSeconds(5));

        File.ReadAllText(Path.Combine(options.Folder, Exe)).ShouldBe("новая");
        os.Calls.ShouldBe(["stop", "register", "start"], "прежняя служба работала, пока собиралась новая копия, и остановлена только перед заменой");
        File.ReadAllText(Path.Combine(options.StateFolder, "boot-block.json")).ShouldBe("boot-block.json", "блок при загрузке пережил обновление");
        File.Exists(Path.Combine(options.StateFolder, "journal.jsonl")).ShouldBeTrue();
        Directory.Exists(old + ".old").ShouldBeFalse("прежняя копия убрана после того, как новая сказала «готова»");
    }

    [Fact]
    public void Update_that_does_not_start_brings_the_previous_service_back()
    {
        var options = Options();
        Old(options, "boot-block.json");
        Directory.CreateDirectory(options.StateFolder);
        ServiceReady.Mark(options.StateFolder, ServiceVersion.Current);
        var os = new FakeOs(options.StateFolder, works: exe => File.ReadAllText(exe) == "прежняя") { Installed = true };

        var failure = Should.Throw<IOException>(() => ServiceSetup.Install(options, New(), null, null, os, TimeSpan.FromSeconds(1)));

        failure.Message.ShouldContain("работает прежняя", customMessage: "готовность прежней службы той же версии — не готовность новой");
        File.ReadAllText(Path.Combine(options.Folder, Exe)).ShouldBe("прежняя", "на месте прежняя копия");
        os.Calls.ShouldBe(["stop", "register", "start", "stop", "register", "start"]);
        File.ReadAllText(Path.Combine(options.StateFolder, ServiceReady.FileName)).ShouldStartWith(ServiceVersion.Current + " ", customMessage: "прежняя запущена и готова");
        Directory.Exists(options.Folder + ".old").ShouldBeFalse();
        Directory.Exists(options.Folder + ".new").ShouldBeFalse();
        File.Exists(Path.Combine(options.StateFolder, "boot-block.json")).ShouldBeTrue();
    }

    [Fact]
    public void First_install_that_does_not_start_leaves_nothing_registered()
    {
        var options = Options();
        var os = new FakeOs(options.StateFolder, works: _ => false);

        Should.Throw<IOException>(() => ServiceSetup.Install(options, New(), null, null, os, TimeSpan.FromSeconds(1)))
            .Message.ShouldStartWith("служба не запустилась");

        os.Calls[^1].ShouldBe("remove");
        Directory.Exists(options.Folder).ShouldBeFalse();

        os.StartError = new System.ComponentModel.Win32Exception(1067, "процесс завершился неожиданно");
        Should.Throw<IOException>(() => ServiceSetup.Install(options, New(), null, null, os, TimeSpan.FromSeconds(1)))
            .Message.ShouldContain("процесс завершился неожиданно", customMessage: "отказ ОС при запуске — в тексте ошибки, а не «ready не появился»");
    }

    [Fact]
    public void Leftovers_of_an_interrupted_install_are_cleared_but_foreign_folders_are_not()
    {
        var options = Options();
        Old(options);
        Touch("opt.new", Exe);
        var foreign = Touch("opt.old", "notes.txt");
        var os = new FakeOs(options.StateFolder, works: _ => true) { Installed = true };

        Should.Throw<IOException>(() => ServiceSetup.Install(options, New(), null, null, os, TimeSpan.FromSeconds(5)),
            "чужая папка на месте .old — установка не удаляет и не затирает ее");
        File.Exists(Path.Combine(foreign, "notes.txt")).ShouldBeTrue();
        os.Calls.ShouldBeEmpty("работающая служба не остановлена");
        File.ReadAllText(Path.Combine(options.Folder, Exe)).ShouldBe("прежняя");

        Directory.Delete(foreign, recursive: true);
        ServiceSetup.Install(options, New(), null, null, os, TimeSpan.FromSeconds(5));
        File.ReadAllText(Path.Combine(options.Folder, Exe)).ShouldBe("новая");
    }

    [Fact]
    public void Uninstall_takes_the_service_its_copy_its_state_and_the_block_away()
    {
        var options = Options();
        Old(options);
        Directory.CreateDirectory(options.StateFolder);
        foreach (var file in new[] { "journal.jsonl", "boot-connect.sealed", "update-bucket", "notes.txt" })
        {
            File.WriteAllText(Path.Combine(options.StateFolder, file), file);
        }

        var os = new FakeOs(options.StateFolder, works: _ => true) { Installed = true };
        var killSwitch = new FakeKillSwitch();

        ServiceSetup.Uninstall(options.Name, options.Folder, options.StateFolder, killSwitch, os);

        os.Calls.ShouldBe(["remove"]);
        (Directory.Exists(options.Folder), killSwitch.Disabled).ShouldBe((false, true));
        Directory.GetFiles(options.StateFolder).Select(Path.GetFileName).ShouldBe(["notes.txt"], "свое состояние удалено (и запечатанное подключение), чужой файл — нет");
        File.Delete(Path.Combine(options.StateFolder, "notes.txt"));
        ServiceSetup.DeleteState(options.StateFolder);
        Directory.Exists(options.StateFolder).ShouldBeFalse("пустая папка состояния — тоже");
    }

    [Fact]
    public void Ready_mark_tells_the_version_and_is_cleared()
    {
        var state = Directory.CreateDirectory(Path.Combine(_root, "ready")).FullName;

        ServiceReady.Wait(state, "0.2.0", TimeSpan.Zero).ShouldBeFalse();
        ServiceReady.Mark(state, "0.1.0");
        ServiceReady.Wait(state, "0.2.0", TimeSpan.FromMilliseconds(300)).ShouldBeFalse("готова прежняя, а ждут новую");
        ServiceReady.Mark(state, "0.2.0");
        ServiceReady.Wait(state, "0.2.0", TimeSpan.Zero).ShouldBeTrue();
        ServiceReady.Clear(state);
        ServiceReady.Wait(state, "0.2.0", TimeSpan.Zero).ShouldBeFalse();
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
        var owner = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User!.Value : "1000";
        var os = new FakeOs(_root, works: _ => true);
        Should.Throw<ArgumentException>(() => ServiceSetup.Install(new SetupOptions("pg test", _root, owner, State: _root), _root, null, null, os),
            "имя службы с пробелом");
        Should.Throw<ArgumentException>(() => ServiceSetup.Install(new SetupOptions("pg-test", _root, owner, State: "state"), _root, null, null, os),
            "папка состояния — только полный путь");
        os.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Systemd_unit_runs_the_service_as_root_and_restarts_it()
    {
        SystemdService.Unit("Private Gateway", ["/opt/private gateway/svc", "--owner", "1000", "50%"]).ShouldBe("""
            [Unit]
            Description=Private Gateway
            Wants=network-pre.target
            Before=network-pre.target

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
    public void Systemd_unit_is_registered_started_stopped_and_removed()
    {
        var units = Directory.CreateDirectory(Path.Combine(_root, "units")).FullName;
        var calls = new List<string>();
        var systemd = new SystemdService(arguments => calls.Add(string.Join(' ', arguments)), units);

        systemd.Stop("pg-test");
        calls.ShouldBeEmpty("юнита нет — останавливать нечего");
        systemd.Register("pg-test", "Private Gateway", ["/opt/pg/svc", "--owner", "1000"]);
        systemd.Start("pg-test");
        systemd.Stop("pg-test");

        calls.ShouldBe(["daemon-reload", "enable pg-test.service", "start pg-test.service", "stop pg-test.service"]);
        File.ReadAllText(Path.Combine(units, "pg-test.service")).ShouldContain("ExecStart=\"/opt/pg/svc\" \"--owner\" \"1000\"");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(Path.Combine(units, "pg-test.service")).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        calls.Clear();
        systemd.Uninstall("pg-test");
        calls.ShouldBe(["disable --now pg-test.service", "daemon-reload"]);
        File.Exists(Path.Combine(units, "pg-test.service")).ShouldBeFalse();
    }

    [Fact]
    public void Launchd_plist_runs_the_service_at_boot_and_restarts_it_after_a_failure()
    {
        LaunchdService.Plist("io.example.pg", ["/Library/Application Support/Private Gateway/Service/svc", "--owner", "501", "<&>"]).ShouldBe("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>io.example.pg</string>
              <key>ProgramArguments</key>
              <array>
                <string>/Library/Application Support/Private Gateway/Service/svc</string>
                <string>--owner</string>
                <string>501</string>
                <string>&lt;&amp;&gt;</string>
              </array>
              <key>RunAtLoad</key>
              <true/>
              <key>KeepAlive</key>
              <dict>
                <key>SuccessfulExit</key>
                <false/>
              </dict>
              <key>ThrottleInterval</key>
              <integer>5</integer>
            </dict>
            </plist>

            """.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void Launchd_service_is_registered_loaded_reloaded_and_removed()
    {
        var daemons = Directory.CreateDirectory(Path.Combine(_root, "daemons")).FullName;
        var calls = new List<string>();
        var loaded = false;
        var launchd = new LaunchdService(arguments =>
        {
            calls.Add(string.Join(' ', arguments));
            switch (arguments[0])
            {
                case "print":
                    return (loaded ? 0 : 113, loaded ? string.Empty : "Could not find service");
                case "bootstrap":
                    loaded = true;
                    return (0, string.Empty);
                case "bootout":
                    loaded = false;
                    return (0, string.Empty);
                default:
                    return (1, "?");
            }
        }, daemons);
        var plist = Path.Combine(daemons, "io.example.pg.plist");

        launchd.Stop("io.example.pg");
        calls.ShouldBeEmpty("описания нет — останавливать нечего");
        launchd.Register("io.example.pg", ["/opt/pg/svc", "--owner", "501"]);
        calls.ShouldBeEmpty("регистрация только пишет описание");
        launchd.Start("io.example.pg");
        launchd.Start("io.example.pg");
        launchd.Stop("io.example.pg");
        launchd.Stop("io.example.pg");

        calls.ShouldBe([
            "print system/io.example.pg", $"bootstrap system {plist}",
            "print system/io.example.pg", "bootout system/io.example.pg", $"bootstrap system {plist}",
            "print system/io.example.pg", "bootout system/io.example.pg",
            "print system/io.example.pg",
        ], "загруженная — перезагружается с новым описанием; выгруженная второй раз не выгружается");
        File.ReadAllText(plist).ShouldContain("<string>/opt/pg/svc</string>");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(plist).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        launchd.Start("io.example.pg");
        calls.Clear();
        launchd.Uninstall("io.example.pg");
        calls.ShouldBe(["print system/io.example.pg", "bootout system/io.example.pg"]);
        File.Exists(plist).ShouldBeFalse();

        var failing = new LaunchdService(_ => (5, "Bootstrap failed: 5: Input/output error"), daemons);
        failing.Register("io.example.pg", ["/opt/pg/svc"]);
        Should.Throw<IOException>(() => failing.Start("io.example.pg")).Message.ShouldContain("Input/output error");
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
        var state = Path.Combine(_root, "service state");
        try
        {
            (await Run(service, "--install", "--service-name", name, "--install-dir", folder, "--state-dir", state, "--ipc", pipe,
                "--owner", WindowsIdentity.GetCurrent().User!.Value)).ShouldBe(0);

            WindowsServices.IsInstalled(name).ShouldBeTrue();
            File.Exists(Path.Combine(folder, Exe)).ShouldBeTrue();
            File.Exists(Path.Combine(state, "journal.jsonl")).ShouldBeTrue("состояние — в своей папке, путь с пробелом дошел до службы целиком");
            using var channel = IpcChannel.Create(pipe, system: true);
            var info = await RetryAsync(() => new V1.GatewayService.GatewayServiceClient(channel).GetServiceInfoAsync(new V1.GetServiceInfoRequest(),
                cancellationToken: Ct).ResponseAsync);
            info.ServiceVersion.ShouldBe(ServiceVersion.Current, "окно говорит со службой, которую запустил диспетчер служб от SYSTEM");

            // Поверх работающей: копия собирается рядом, работающая останавливается, новая встает на место и говорит «готова».
            (await Run(service, "--install", "--service-name", name, "--install-dir", folder, "--state-dir", state, "--ipc", pipe,
                "--owner", WindowsIdentity.GetCurrent().User!.Value)).ShouldBe(0);
            Directory.Exists(folder + ".old").ShouldBeFalse();
            (await RetryAsync(() => new V1.GatewayService.GatewayServiceClient(channel).GetServiceInfoAsync(new V1.GetServiceInfoRequest(),
                cancellationToken: Ct).ResponseAsync)).ServiceVersion.ShouldBe(ServiceVersion.Current);

            (await Run(service, "--uninstall", "--service-name", name, "--install-dir", folder, "--state-dir", state)).ShouldBe(0);

            WindowsServices.IsInstalled(name).ShouldBeFalse();
            Directory.Exists(folder).ShouldBeFalse();
            Directory.Exists(state).ShouldBeFalse();
        }
        finally
        {
            WindowsServices.Uninstall(name, TimeSpan.FromSeconds(20));
        }
    }

    [Fact]
    public async Task Macos_service_installs_under_launchd_answers_the_window_and_uninstalls()
    {
        if (!OperatingSystem.IsMacOS() || !Environment.IsPrivilegedProcess)
        {
            Assert.Skip("служба macOS ставится только от root (CI)");
            return;
        }

        var service = Path.Combine(XrayKit.Root, "artifacts", "bin", "Client.Service",
            AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "release" : "debug",
            Exe);
        Assert.SkipUnless(File.Exists(service), "нет сборки службы");
        var id = Guid.NewGuid().ToString("N")[..8];
        var name = "io.github.rvburov.pg-test-" + id;
        var plist = $"/Library/LaunchDaemons/{name}.plist";

        // Путь сокета на macOS — не длиннее 104 байт: временная папка для него длинна.
        var socket = $"/var/run/pg-test-{id}.sock";
        var folder = Path.Combine(_root, "service");
        var state = Path.Combine(_root, "service state");
        try
        {
            (await Run(service, "--install", "--service-name", name, "--install-dir", folder, "--state-dir", state, "--ipc", socket, "--owner", "0")).ShouldBe(0);

            File.Exists(plist).ShouldBeTrue("описание службы — в /Library/LaunchDaemons");
            File.ReadAllText(plist).ShouldContain($"<string>{state}</string>", customMessage: "папка состояния с пробелом — одним параметром");
            File.Exists(Path.Combine(folder, Exe)).ShouldBeTrue();
            File.Exists(Path.Combine(state, "journal.jsonl")).ShouldBeTrue("служба, которую запустил launchd, пишет журнал в папку состояния");
            using var channel = IpcChannel.Create(socket, system: true);
            var info = await RetryAsync(() => new V1.GatewayService.GatewayServiceClient(channel).GetServiceInfoAsync(new V1.GetServiceInfoRequest(),
                cancellationToken: Ct).ResponseAsync);
            info.ServiceVersion.ShouldBe(ServiceVersion.Current, "окно говорит со службой, которую запустил launchd от root");

            // Поверх работающей: копия собирается рядом, работающая выгружается, новая загружается и говорит «готова».
            (await Run(service, "--install", "--service-name", name, "--install-dir", folder, "--state-dir", state, "--ipc", socket, "--owner", "0")).ShouldBe(0);
            Directory.Exists(folder + ".old").ShouldBeFalse();
            (await RetryAsync(() => new V1.GatewayService.GatewayServiceClient(channel).GetServiceInfoAsync(new V1.GetServiceInfoRequest(),
                cancellationToken: Ct).ResponseAsync)).ServiceVersion.ShouldBe(ServiceVersion.Current);

            (await Run(service, "--uninstall", "--service-name", name, "--install-dir", folder, "--state-dir", state)).ShouldBe(0);

            File.Exists(plist).ShouldBeFalse();
            Directory.Exists(folder).ShouldBeFalse();
            Directory.Exists(state).ShouldBeFalse();
        }
        finally
        {
            // Упал посередине — убрать службу с машины CI.
            if (File.Exists(plist))
            {
                using var bootout = Process.Start("/bin/launchctl", ["bootout", "system/" + name]);
                await bootout.WaitForExitAsync(Ct);
                File.Delete(plist);
            }
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

    private SetupOptions Options() => new("pg-test", Path.Combine(_root, "opt"),
        OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User!.Value : "1000", State: Path.Combine(_root, "state"));

    /// <summary>Работающая прежняя служба: копия с файлом «прежняя» и, как у служб до отдельной папки состояния, ее файлы рядом.</summary>
    private static string Old(SetupOptions options, params string[] stateFiles)
    {
        var folder = Directory.CreateDirectory(options.Folder).FullName;
        File.WriteAllText(Path.Combine(folder, Exe), "прежняя");
        foreach (var file in stateFiles)
        {
            File.WriteAllText(Path.Combine(folder, file), file);
        }

        return folder;
    }

    /// <summary>Сборка новой версии: файл службы «новая».</summary>
    private string New()
    {
        var build = Touch("build", Exe);
        File.WriteAllText(Path.Combine(build, Exe), "новая");
        return build;
    }

    /// <summary>Служба ОС в памяти: запуск «работает», если <paramref name="works"/> говорит так о файле службы из команды — тогда служба отмечает готовность.</summary>
    private sealed class FakeOs(string state, Func<string, bool> works) : IOsService
    {
        public List<string> Calls { get; } = [];

        public bool Installed { get; set; }

        public IReadOnlyList<string>? Command { get; private set; }

        /// <summary>Чем ОС отказывает в запуске (служба падает сразу); null — запускает.</summary>
        public Exception? StartError { get; set; }

        public bool IsInstalled(string name) => Installed;

        public void Register(string name, IReadOnlyList<string> command)
        {
            Calls.Add("register");
            Installed = true;
            Command = command;
        }

        public void Start(string name)
        {
            Calls.Add("start");
            if (StartError is not null)
            {
                throw StartError;
            }

            if (works(Command![0]))
            {
                ServiceReady.Mark(state, ServiceVersion.Current);
            }
        }

        public void StopAndWait(string name) => Calls.Add("stop");

        public void Remove(string name)
        {
            Calls.Add("remove");
            Installed = false;
        }
    }

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
