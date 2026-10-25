using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Platform.Windows;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>
/// Ядро отдельным процессом: Xray (<see cref="StartXray"/>) или sing-box (<see cref="StartSingBox"/>).
/// Конфиг — через stdin: в нем ключи сервера, а файлы и аргументы командной строки видны другим
/// процессам. Вывод ядра — построчно в <see cref="Output"/>; адресов сайтов в нем нет — журнал
/// соединений в конфигах выключен.
/// </summary>
public sealed class CoreProcess : IDisposable
{
    private const int SigTerm = 15;

    private readonly Process _process;

    private CoreProcess(Process process)
    {
        _process = process;
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Строка вывода ядра. Приходит из потока чтения.</summary>
    public event EventHandler<string>? Output;

    /// <summary>Ядро завершилось — само или по <see cref="StopAsync"/>.</summary>
    public event EventHandler? Exited;

    public bool HasExited => _process.HasExited;

    /// <summary>Номер процесса — для журнала.</summary>
    public int Id => _process.Id;

    public int ExitCode => _process.ExitCode;

    /// <summary>
    /// Xray (<c>run -config stdin:</c>); geoip.dat и geosite.dat ядро ищет рядом с собой. Путь — из папки
    /// службы. <paramref name="netns"/> — запустить в сетевом пространстве Linux (тесты); null — в системе.
    /// </summary>
    public static CoreProcess StartXray(string executable, JsonObject config, string? netns = null)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        return Start(executable, ["run", "-config", "stdin:"], config, folder, new Dictionary<string, string> { ["XRAY_LOCATION_ASSET"] = folder }, netns);
    }

    /// <summary>sing-box (<c>run -c stdin</c>) — TUN режима VPN. <paramref name="netns"/> — как у <see cref="StartXray"/>.</summary>
    public static CoreProcess StartSingBox(string executable, JsonObject config, string? netns = null) =>
        Start(executable, ["run", "-c", "stdin", "--disable-color"], config, Path.GetDirectoryName(Path.GetFullPath(executable))!, null, netns);

    /// <summary>
    /// Остановить ядро и дождаться выхода. <paramref name="grace"/> — сначала попросить (SIGTERM на
    /// Linux и macOS): sing-box за это время убирает свои маршруты и правила; не ушел — снять.
    /// </summary>
    public async Task StopAsync(TimeSpan grace = default, CancellationToken cancellationToken = default)
    {
        if (!_process.HasExited && grace > TimeSpan.Zero && !OperatingSystem.IsWindows() && SendSignal(_process.Id, SigTerm) == 0)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(grace);
            try
            {
                await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Не ушло за отведенное время — снимается.
            }
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
    }

    private static CoreProcess Start(string executable, IReadOnlyList<string> arguments, JsonObject config, string folder,
        IReadOnlyDictionary<string, string>? environment, string? netns)
    {
        ArgumentNullException.ThrowIfNull(config);

        // В своем сетевом пространстве — через nsenter: он входит в пространство и заменяет себя ядром, процесс тот же.
        var info = new ProcessStartInfo(netns is null ? executable : "nsenter")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = folder,
        };
        if (netns is not null)
        {
            info.ArgumentList.Add("--net=" + netns);
            info.ArgumentList.Add(executable);
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            info.Environment[name] = value;
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("Ядро не запустилось: " + Path.GetFileName(executable));
        // Windows: ядро — в задании, которое ОС закрывает вместе со службой, и без службы оно не останется.
        // На Linux то же делает systemd: при сбое службы он останавливает весь ее cgroup.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                ChildProcesses.OfThisProcess.Add(process);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Задание не далось — ядро работает, только без этой страховки.
            }
        }

        var core = new CoreProcess(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Write(config.ToJsonString());
        process.StandardInput.Close();
        return core;
    }

    private void Line(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Output?.Invoke(this, text);
        }
    }

    // Без указателей и строк — LibraryImport (и небезопасный код) не нужен.
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
#pragma warning disable SYSLIB1054
    private static extern int SendSignal(int pid, int signal);
#pragma warning restore SYSLIB1054
}
