using System.Diagnostics;
using PrivateGateway.Client.Infrastructure.Service;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Перезапуск окна после установщика обновления (<see cref="AppRelauncher"/>): ждать процесс установщика, потом открыть окно.</summary>
public sealed class AppRelauncherTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pg-relaunch-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Windows_script_waits_for_the_installer_then_opens_the_window_and_removes_itself()
    {
        var script = AppRelauncher.WindowsScript(4242, @"C:\Program Files\Private Gateway\PrivateGateway.exe");

        script.ShouldContain("tasklist /fi \"PID eq 4242\" /nh | find \"4242\" > nul && (timeout /t 2 /nobreak > nul & goto wait)");
        script.ShouldContain("start \"\" \"C:\\Program Files\\Private Gateway\\PrivateGateway.exe\"", customMessage: "путь с пробелом — в кавычках");
        script.IndexOf("goto wait", StringComparison.Ordinal).ShouldBeLessThan(script.IndexOf("start", StringComparison.Ordinal), "окно — только после установщика");
        script.ShouldEndWith("del \"%~f0\"\r\n");
    }

    [Fact]
    public void Unix_helper_gets_the_number_and_the_path_as_parameters_not_as_code()
    {
        var arguments = AppRelauncher.UnixArguments(4242, "/opt/private gateway/PrivateGateway; rm -rf /");

        arguments.TakeLast(2).ShouldBe(["4242", "/opt/private gateway/PrivateGateway; rm -rf /"]);
        arguments[1].ShouldNotContain("PrivateGateway");
    }

    [Fact]
    public async Task Window_opens_only_after_the_installer_is_gone()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("помощник Linux и macOS — sh");
            return;
        }

        var marker = Path.Combine(_root, "opened");
        var window = Path.Combine(_root, "window.sh");
        File.WriteAllText(window, $"#!/bin/sh\necho opened > '{marker}'\n");
        File.SetUnixFileMode(window, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var installer = Process.Start(new ProcessStartInfo("/bin/sleep", "2") { UseShellExecute = false })!;

        new AppRelauncher(window).RelaunchAfter(installer.Id);

        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        File.Exists(marker).ShouldBeFalse("установщик еще работает");
        await installer.WaitForExitAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 50 && !File.Exists(marker); i++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        File.Exists(marker).ShouldBeTrue("установщик закончил — окно открыто");
        new AppRelauncher(null).RelaunchAfter(installer.Id);
    }
}
