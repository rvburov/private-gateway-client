using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// «Установить службу»: файл службы рядом с программой запускается с правами — на Windows через запрос
/// UAC, на Linux через pkexec, на macOS через запрос пароля администратора (osascript) — с
/// <c>--install --owner &lt;пользователь окна&gt;</c>. Владельца передает
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
    private const string Osascript = "/usr/bin/osascript";
    private static readonly string[] PkexecPrograms = ["/usr/bin/pkexec", "/bin/pkexec"];

    // Команда собирается внутри AppleScript из argv, каждый параметр — quoted form: путь и значения в текст скрипта не
    // подставляются, кавычки и пробелы в них ничего не ломают.
    private static readonly string[] MacScript =
    [
        "on run argv",
        "set command to \"\"",
        "repeat with argument in argv",
        "set command to command & quoted form of (argument as text) & \" \"",
        "end repeat",
        "do shell script command with administrator privileges",
        "end run",
    ];

    public bool IsAvailable => executable is not null && File.Exists(executable);

    /// <summary>Установка для этого окна: Windows, Linux с pkexec и macOS, файл службы рядом с программой.</summary>
    public static ServiceInstaller ForThisWindow(TimeProvider time) => new(
        OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && PkexecPrograms.Any(File.Exists)) || (OperatingSystem.IsMacOS() && File.Exists(Osascript))
            ? UserServiceLauncher.Find(AppContext.BaseDirectory) : null,
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

    /// <summary>Параметры osascript для запуска <paramref name="executable"/> от администратора на macOS.</summary>
    public static IReadOnlyList<string> MacArguments(string executable, IReadOnlyList<string> arguments) =>
        [.. MacScript.SelectMany(line => new[] { "-e", line }), executable, .. arguments];

    private static async Task<int?> ElevateAsync(string executable, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start;
        if (OperatingSystem.IsMacOS())
        {
            start = new ProcessStartInfo(Osascript) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var argument in MacArguments(executable, arguments))
            {
                start.ArgumentList.Add(argument);
            }
        }
        else if (OperatingSystem.IsWindows())
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

            var error = start.RedirectStandardError ? await process.StandardError.ReadToEndAsync().ConfigureAwait(false) : string.Empty;
            await process.WaitForExitAsync().ConfigureAwait(false);

            // osascript: запрос пароля закрыт — «User canceled. (-128)»; pkexec: 126 — запрос закрыт, 127 — не дали прав.
            return OperatingSystem.IsMacOS() ? error.Contains("(-128)", StringComparison.Ordinal) ? null : process.ExitCode
                : !OperatingSystem.IsWindows() && process.ExitCode is 126 or 127 ? null : process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Cancelled)
        {
            return null;
        }
    }
}
