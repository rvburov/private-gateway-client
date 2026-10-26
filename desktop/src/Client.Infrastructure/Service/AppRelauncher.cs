using System.Diagnostics;
using System.Globalization;
using PrivateGateway.Client.Application.Updates;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// Перезапуск окна после установщика обновления. Окно должно закрыться до установки (MSI заменяет его файлы), поэтому
/// ждать установщик и открыть окно заново поручается маленькому процессу ОС, который переживает окно: на Windows — файл
/// команд в папке временных файлов пользователя (tasklist, пока процесс установщика есть), на Linux — sh (пока есть
/// /proc/&lt;номер&gt;). Окно откроется тем же файлом, из которого запущено сейчас: установщик кладет новое на то же место.
/// </summary>
/// <param name="executable">Файл окна; null — перезапускать нечего (тесты, демо).</param>
public sealed class AppRelauncher(string? executable) : IAppRelauncher
{
    public static AppRelauncher ForThisWindow() => new(Environment.ProcessPath);

    /// <summary>Команды для Windows: ждать процесс <paramref name="pid"/>, открыть <paramref name="exe"/>, удалить себя.</summary>
    public static string WindowsScript(int pid, string exe)
    {
        ArgumentNullException.ThrowIfNull(exe);
        var number = pid.ToString(CultureInfo.InvariantCulture);
        return string.Join("\r\n",
            "@echo off",
            ":wait",
            $"tasklist /fi \"PID eq {number}\" /nh | find \"{number}\" > nul && (timeout /t 2 /nobreak > nul & goto wait)",
            $"start \"\" \"{exe}\"",
            "del \"%~f0\"",
            string.Empty);
    }

    /// <summary>Команда для Linux и macOS: sh ждет, пока есть процесс, потом открывает окно; номер и путь — параметрами.</summary>
    public static IReadOnlyList<string> UnixArguments(int pid, string exe) =>
        ["-c", "while [ -d \"/proc/$1\" ] || kill -0 \"$1\" 2> /dev/null; do sleep 1; done; exec \"$2\"", "sh", pid.ToString(CultureInfo.InvariantCulture), exe];

    public void RelaunchAfter(int installerPid)
    {
        if (executable is null)
        {
            return;
        }

        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(Path.GetTempPath(), $"PrivateGateway-relaunch-{installerPid}.cmd");
            File.WriteAllText(script, WindowsScript(installerPid, executable));
            start = new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", script } };
        }
        else
        {
            start = new ProcessStartInfo("/bin/sh");
            foreach (var argument in UnixArguments(installerPid, executable))
            {
                start.ArgumentList.Add(argument);
            }
        }

        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        using var process = Process.Start(start);
    }
}
