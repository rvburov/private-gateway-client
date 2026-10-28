using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Infrastructure.Service;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>«Установить службу» из окна: запуск с правами, отказ в правах, ожидание ответа службы. Запуск — подделка.</summary>
public sealed class ServiceInstallerTests : IDisposable
{
    private readonly string _executable = Path.Combine(Path.GetTempPath(), "pg-installer-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeTimeProvider _time = new();
    private readonly List<string> _calls = [];

    public ServiceInstallerTests() => File.WriteAllText(_executable, "служба");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => File.Delete(_executable);

    [Fact]
    public async Task Install_runs_the_service_with_rights_for_this_user_and_waits_for_its_answer()
    {
        var answers = 0;
        var installer = Installer(0, () => Interlocked.Increment(ref answers) >= 3);

        (await Run(installer.InstallAsync(Ct))).ShouldBe(ServiceInstallResult.Installed);

        _calls.ShouldBe([$"{_executable} --install --owner {ServiceInstaller.Owner()}"], "владельца передает окно: права может дать другой администратор");
        answers.ShouldBe(3, "окно ждет, пока установленная служба ответит");
    }

    [Theory]
    [InlineData(null, ServiceInstallResult.Cancelled)]
    [InlineData(1, ServiceInstallResult.Failed)]
    public async Task Refused_rights_and_failed_install_are_told_apart(int? exitCode, ServiceInstallResult result)
    {
        var installer = Installer(exitCode, () => throw new InvalidOperationException("службу не ищут, если установка не вышла"));

        (await installer.InstallAsync(Ct)).ShouldBe(result);
    }

    [Fact]
    public async Task Service_that_never_answers_is_a_failure()
    {
        var installer = Installer(0, () => false);

        (await Run(installer.InstallAsync(Ct))).ShouldBe(ServiceInstallResult.Failed);
    }

    [Fact]
    public async Task Without_a_service_file_there_is_nothing_to_install()
    {
        var installer = new ServiceInstaller(null, (_, _) => throw new InvalidOperationException(), () => true, _time);

        installer.IsAvailable.ShouldBeFalse("кнопки «Установить службу» нет");
        (await installer.InstallAsync(Ct)).ShouldBe(ServiceInstallResult.Failed);
    }

    /// <summary>
    /// Установка с правами идет в окне ОС, вывод которого окну не виден (UAC на Windows): причину отказа она пишет в файл
    /// итога, окно берет ее оттуда. Файл прежней попытки — не про эту: причины нет.
    /// </summary>
    [Fact]
    public async Task Reason_of_a_failed_install_comes_from_the_result_file_of_this_attempt()
    {
        var result = _executable + "-result.txt";
        try
        {
            var installer = new ServiceInstaller(_executable, (_, _) =>
            {
                File.WriteAllText(result, "служба работает от имени системы, а ей не виден .NET 10\n");
                return Task.FromResult<int?>(1);
            }, () => true, _time, result);

            (await installer.InstallAsync(Ct)).ShouldBe(ServiceInstallResult.Failed);
            installer.LastError.ShouldBe("служба работает от имени системы, а ей не виден .NET 10");

            File.SetLastWriteTimeUtc(result, DateTime.UtcNow.AddMinutes(-10));
            var stale = new ServiceInstaller(_executable, (_, _) => Task.FromResult<int?>(1), () => true, _time, result);
            (await stale.InstallAsync(Ct)).ShouldBe(ServiceInstallResult.Failed);
            stale.LastError.ShouldBeNull("итог прежней попытки — не причина этой");

            (await installer.InstallAsync(Ct)).ShouldBe(ServiceInstallResult.Failed);
            File.Delete(result);
            var cancelled = new ServiceInstaller(_executable, (_, _) => Task.FromResult<int?>(null), () => true, _time, result);
            (await cancelled.InstallAsync(Ct)).ShouldBe(ServiceInstallResult.Cancelled);
            cancelled.LastError.ShouldBeNull("права не дали — причина и так ясна");
        }
        finally
        {
            File.Delete(result);
        }
    }

    [Fact]
    public void Mac_elevation_passes_the_command_as_arguments_not_inside_the_script()
    {
        var arguments = ServiceInstaller.MacArguments("/Applications/Private Gateway.app/Contents/MacOS/PrivateGateway.Service", ["--install", "--owner", "501"]);

        arguments.TakeLast(4).ShouldBe(["/Applications/Private Gateway.app/Contents/MacOS/PrivateGateway.Service", "--install", "--owner", "501"]);
        var script = arguments.SkipLast(4).ToList();
        script.Where((_, i) => i % 2 == 0).ShouldAllBe(flag => flag == "-e");
        var lines = script.Where((_, i) => i % 2 == 1).ToList();
        lines.ShouldContain("do shell script command with administrator privileges");
        lines.ShouldContain(line => line.Contains("quoted form of", StringComparison.Ordinal), "каждый параметр — в кавычках оболочки");
        lines.ShouldAllBe(line => !line.Contains("Private Gateway", StringComparison.Ordinal) && !line.Contains("501", StringComparison.Ordinal),
            "путь и владелец в текст скрипта не попадают");
    }

    /// <summary>Двигать часы, пока установка ждет ответа службы; не дольше 2 минут по часам теста.</summary>
    private async Task<ServiceInstallResult> Run(Task<ServiceInstallResult> install)
    {
        for (var i = 0; i < 120 && !install.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, Ct);
        }

        return await install;
    }

    private ServiceInstaller Installer(int? exitCode, Func<bool> answers) => new(_executable, (file, arguments) =>
    {
        _calls.Add(file + " " + string.Join(' ', arguments));
        return Task.FromResult(exitCode);
    }, answers, _time);
}
