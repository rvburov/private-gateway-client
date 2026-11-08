using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// «Установить службу»: файл службы рядом с программой запускается с правами — на Windows через запрос
/// UAC, на Linux через pkexec, на macOS через запрос пароля администратора (osascript) — с
/// <c>--install --owner &lt;пользователь окна&gt;</c>. Владельца передает
/// окно: права может дать и другой администратор, а пускать служба должна того, кто ею пользуется.
/// Потом окно ждет, пока установленная служба ответит.
/// </summary>
/// <param name="executable">Файл службы; null — установка на этой ОС недоступна.</param>
/// <param name="elevate">Запуск с правами: файл, аргументы и пояснение → код выхода; пользователь отказал в правах — null.</param>
/// <param name="installedAnswers">Отвечает ли установленная служба.</param>
/// <param name="time">Часы для ожидания ответа.</param>
/// <param name="resultFile">Файл итога установки (<see cref="SetupResult"/>): причина отказа; null — не читать.</param>
public sealed class ServiceInstaller(
    string? executable,
    Func<string, IReadOnlyList<string>, string, Task<int?>> elevate,
    Func<bool> installedAnswers,
    TimeProvider time,
    string? resultFile = null) : IServiceInstaller
{
    /// <summary>Сколько ждать ответа службы после установки.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(2);

    private Task<int?>? _pending;
    private int _active;

    public string? ErrorKey { get; private set; }

    private const int Cancelled = 1223;
    private const string Osascript = "/usr/bin/osascript";
    private static readonly string[] PkexecPrograms = ["/usr/bin/pkexec", "/bin/pkexec"];

    // Команда собирается внутри AppleScript из argv, каждый параметр — quoted form: путь и значения в текст скрипта не
    // подставляются, кавычки и пробелы в них ничего не ломают.
    private static readonly string[] MacScript =
    [
        "on run argv",
        "set promptText to item 1 of argv",
        "set command to \"\"",
        "repeat with argument in items 2 thru -1 of argv",
        "set command to command & quoted form of (argument as text) & \" \"",
        "end repeat",
        "do shell script command with administrator privileges with prompt promptText",
        "end run",
    ];

    public bool IsAvailable => executable is not null && File.Exists(executable);

    /// <summary>Причина, по которой не вышла последняя установка, — из файла итога; неизвестна — null.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Коммит файла службы, который поставит установка: версия сборки библиотеки службы рядом с ее файлом. У сборки из
    /// исходников его собирает <c>dotnet build PrivateGateway.slnx</c>, а <c>dotnet run</c> окна — нет: файл бывает из
    /// прежних исходников, и переустановка из него службу не обновит.
    /// </summary>
    public string? SourceBuild
    {
        get
        {
            var library = executable is null ? null : Path.Combine(Path.GetDirectoryName(executable)!, "PrivateGateway.Service.dll");
            return library is not null && File.Exists(library) ? SemVer.Build(FileVersionInfo.GetVersionInfo(library).ProductVersion) : null;
        }
    }

    /// <summary>Установка для этого окна: Windows, Linux с pkexec и macOS, файл службы рядом с программой.</summary>
    public static ServiceInstaller ForThisWindow(TimeProvider time) => new(
        OperatingSystem.IsWindows() || (OperatingSystem.IsLinux() && PkexecPrograms.Any(File.Exists)) || (OperatingSystem.IsMacOS() && File.Exists(Osascript))
            ? UserServiceLauncher.Find(AppContext.BaseDirectory) : null,
        ElevateAsync,
        () => IpcChannel.Answers(IpcEndpoints.System, system: true, ServiceLocator.ProbeTimeout),
        time,
        SetupResult.DefaultFile);

    /// <summary>Пользователь окна для службы: SID на Windows, uid на Linux и macOS.</summary>
    public static string Owner() => OperatingSystem.IsWindows()
        ? WindowsIdentity.GetCurrent().User!.Value
        : PeerCredentials.CurrentUid().ToString(CultureInfo.InvariantCulture);

    public Task<ServiceInstallResult> InstallAsync(CancellationToken cancellationToken = default) => InstallAsync(new Progress<int>(), cancellationToken);

    public Task<ServiceInstallResult> InstallAsync(IProgress<int> progress, CancellationToken cancellationToken = default) =>
        InstallAsync(string.Empty, progress, cancellationToken);

    public async Task<ServiceInstallResult> InstallAsync(string prompt, IProgress<int> progress, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            return ServiceInstallResult.Failed;
        }

        try
        {
            return await InstallCoreAsync(prompt, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            ErrorKey = "problem.install.failed";
            return ServiceInstallResult.Failed;
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    private async Task<ServiceInstallResult> InstallCoreAsync(string prompt, IProgress<int> progress, CancellationToken cancellationToken)
    {
        LastError = null;
        ErrorKey = null;
        if (_pending is { IsCompleted: false } || InstallationIsLocked())
        {
            ErrorKey = "problem.install.stillRunning";
            return ServiceInstallResult.Failed;
        }

        progress.Report(0);
        if (!IsAvailable)
        {
            return ServiceInstallResult.Failed;
        }

        // Время файла — настоящее, файловой системы: прежний итог (старше этой попытки) — не про нее.
        var attempt = DateTime.UtcNow.AddSeconds(-2);
        var attemptId = Guid.NewGuid().ToString("N");
        _pending = elevate(executable!, ["--install", "--owner", Owner(), "--setup-attempt", attemptId], prompt);
        var startedInstall = time.GetTimestamp();
        while (!_pending.IsCompleted)
        {
            ReportProgress(progress, attemptId);
            if (time.GetElapsedTime(startedInstall) >= InstallTimeout)
            {
                // Привилегированный процесс может менять файлы: не обрывать его посередине и не запускать второй.
                ErrorKey = "problem.install.timeout";
                _ = _pending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return ServiceInstallResult.Failed;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), time, cancellationToken).ConfigureAwait(false);
        }

        switch (await _pending.ConfigureAwait(false))
        {
            case null:
                return ServiceInstallResult.Cancelled;
            case not 0:
                LastError = Reason(attempt);
                return ServiceInstallResult.Failed;
        }

        progress.Report(90);
        var started = time.GetTimestamp();
        while (!await Task.Run(installedAnswers, cancellationToken).ConfigureAwait(false))
        {
            if (time.GetElapsedTime(started) >= StartTimeout)
            {
                ErrorKey = "problem.install.noAnswer";
                return ServiceInstallResult.Failed;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300), time, cancellationToken).ConfigureAwait(false);
        }

        progress.Report(100);
        return ServiceInstallResult.Installed;
    }

    private bool InstallationIsLocked()
    {
        try
        {
            if (resultFile is null)
            {
                return false;
            }

            // Старые установки держали файл в папке программы: их тоже нельзя запускать параллельно.
            foreach (var file in new[] { SetupResult.LockForResult(resultFile), resultFile + ".lock" })
            {
                if (File.Exists(file))
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                }
            }

            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private void ReportProgress(IProgress<int> progress, string attemptId)
    {
        try
        {
            if (resultFile is not null && File.Exists(resultFile + ".progress"))
            {
                var value = File.ReadAllText(resultFile + ".progress").Split(':');
                if (value.Length == 2 && value[0] == attemptId && int.TryParse(value[1], CultureInfo.InvariantCulture, out var percent)
                    && percent is 10 or 30 or 60 or 80)
                {
                    progress.Report(percent);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Короткое обновление файла не должно мешать установке.
        }
    }

    // Причина отказа из файла итога этой попытки; файла нет или он прежний — null.
    private string? Reason(DateTime attempt)
    {
        try
        {
            if (resultFile is null || !File.Exists(resultFile) || File.GetLastWriteTimeUtc(resultFile) < attempt)
            {
                return null;
            }

            var text = File.ReadAllText(resultFile).Trim();
            return text.Length == 0 ? null : text.Length > 500 ? text[..500] : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Параметры osascript для запуска <paramref name="executable"/> от администратора на macOS.</summary>
    public static IReadOnlyList<string> MacArguments(string executable, IReadOnlyList<string> arguments, string prompt) =>
        [.. MacScript.SelectMany(line => new[] { "-e", line }), "--", prompt, executable, .. arguments];

    private static async Task<int?> ElevateAsync(string executable, IReadOnlyList<string> arguments, string prompt)
    {
        ProcessStartInfo start;
        if (OperatingSystem.IsMacOS())
        {
            start = new ProcessStartInfo(Osascript) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var argument in MacArguments(executable, arguments, prompt))
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
