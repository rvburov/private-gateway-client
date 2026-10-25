using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>
/// Ядро Xray отдельным процессом. Конфиг — через stdin (<c>run -config stdin:</c>): в нем ключи сервера,
/// а файлы и аргументы командной строки видны другим процессам. Вывод ядра — построчно в
/// <see cref="Output"/>; адресов сайтов в нем нет — журнал соединений в конфиге выключен.
/// </summary>
public sealed class XrayProcess : IDisposable
{
    private readonly Process _process;

    private XrayProcess(Process process)
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

    public int ExitCode => _process.ExitCode;

    /// <summary>Запустить ядро с конфигом. Путь к программе — из папки службы, не от интерфейса.</summary>
    public static XrayProcess Start(string executable, JsonObject config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var info = new ProcessStartInfo(executable)
        {
            ArgumentList = { "run", "-config", "stdin:" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            // geoip.dat и geosite.dat ядро ищет рядом с собой.
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
        };
        info.Environment["XRAY_LOCATION_ASSET"] = info.WorkingDirectory;
        var process = Process.Start(info) ?? throw new InvalidOperationException("Ядро Xray не запустилось");
        var xray = new XrayProcess(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Write(config.ToJsonString());
        process.StandardInput.Close();
        return xray;
    }

    /// <summary>Остановить ядро и дождаться выхода.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
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

    private void Line(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Output?.Invoke(this, text);
        }
    }
}
