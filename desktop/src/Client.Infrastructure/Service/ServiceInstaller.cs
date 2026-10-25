using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// «Установить службу»: файл службы рядом с программой запускается с правами — на Windows через запрос
/// UAC, на Linux через pkexec — с <c>--install --owner &lt;пользователь окна&gt;</c>. Владельца передает
/// окно: права может дать и другой администратор, а пускать служба должна того, кто ею пользуется.
/// Потом окно ждет, пока установленная служба ответит.
/// </summary>
/// <param name="executable">Файл службы; null — установка на этой ОС недоступна.</param>
/// <param name="elevate">Запуск с правами: файл и аргументы → код выхода; пользователь отказал в правах — null.</param>
/// <param name="installedAnswers">Отвечает ли установленная служба.</param>
/// <param name="time">Часы для ожидания ответа.</param>
public sealed class ServiceInstaller(
    string? executable,
    Func<string, IReadOnlyList<string>, Task<int?>> elevate,
    Func<bool> installedAnswers,
    TimeProvider time) : IServiceInstaller
{
    /// <summary>Сколько ждать ответа службы после установки.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private const int Cancelled = 1223;
    private static readonly string[] PkexecPrograms = ["/usr/bin/pkexec", "/bin/pkexec"];

    public bool IsAvailable => executable is not null && File.Exists(executable);

    /// <summary>Установка для этого окна: Windows и Linux с pkexec, файл службы рядом с программой.</summary>
    public static ServiceInstaller ForThisWindow(TimeProvider time) => new(
        OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && PkexecPrograms.Any(File.Exists)) ? UserServiceLauncher.Find(AppContext.BaseDirectory) : null,
        ElevateAsync,
        () => IpcChannel.Answers(IpcEndpoints.System, system: true, ServiceLocator.ProbeTimeout),
        time);

    /// <summary>Пользователь окна для службы: SID на Windows, uid на Linux и macOS.</summary>
    public static string Owner() => OperatingSystem.IsWindows()
        ? WindowsIdentity.GetCurrent().User!.Value
        : PeerCredentials.CurrentUid().ToString(CultureInfo.InvariantCulture);

    public async Task<ServiceInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return ServiceInstallResult.Failed;
        }

        switch (await elevate(executable!, ["--install", "--owner", Owner()]).ConfigureAwait(false))
        {
            case null:
                return ServiceInstallResult.Cancelled;
            case not 0:
                return ServiceInstallResult.Failed;
        }

        var started = time.GetTimestamp();
        while (!await Task.Run(installedAnswers, cancellationToken).ConfigureAwait(false))
        {
            if (time.GetElapsedTime(started) >= StartTimeout)
            {
                return ServiceInstallResult.Failed;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), time, cancellationToken).ConfigureAwait(false);
        }

        return ServiceInstallResult.Installed;
    }

    private static async Task<int?> ElevateAsync(string executable, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            // Запрос UAC возможен только через оболочку; аргументы — SID и ключи, без пробелов.
            start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = string.Join(' ', arguments),
            };
        }
        else
        {
            start = new ProcessStartInfo(PkexecPrograms.First(File.Exists)) { UseShellExecute = false };
            start.ArgumentList.Add(executable);
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return 1;
            }

            await process.WaitForExitAsync().ConfigureAwait(false);

            // pkexec: 126 — запрос закрыт, 127 — не дали прав.
            return !OperatingSystem.IsWindows() && process.ExitCode is 126 or 127 ? null : process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Cancelled)
        {
            return null;
        }
    }
}
