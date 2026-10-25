using System.Diagnostics;
using System.Text.RegularExpressions;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Platform.Windows;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Service;

/// <summary>Сведения о службе для GetServiceInfo, путь к ядру по умолчанию и жизнь службы пользователя.</summary>
public static partial class ServiceHost
{
    /// <summary>
    /// Ядро рядом со службой: cores/xray/xray(.exe), так его кладет установщик. Служба запущена из
    /// папки сборки (artifacts/bin/Client.Service/&lt;конфигурация&gt;) — ядро из artifacts/cores/xray,
    /// куда его кладет scripts/get-xray.sh. Нет — null, подключение откажет с PGC-SVC-002.
    /// </summary>
    public static string? DefaultXray() => DefaultXray(AppContext.BaseDirectory);

    /// <param name="serviceFolder">Папка исполняемого файла службы.</param>
    public static string? DefaultXray(string serviceFolder)
    {
        var name = OperatingSystem.IsWindows() ? "xray.exe" : "xray";
        var installed = Path.Combine(serviceFolder, "cores", "xray", name);
        if (File.Exists(installed))
        {
            return installed;
        }

        // Только точное расположение папки сборки, без поиска вверх: чужой файл выше по дереву ядром не станет.
        var build = new DirectoryInfo(serviceFolder);
        if (build.Parent is { Name: "Client.Service" } project && project.Parent is { Name: "bin" } bin && bin.Parent is { Name: "artifacts" } artifacts
            && Path.Combine(artifacts.FullName, "cores", "xray", name) is var built && File.Exists(built))
        {
            return built;
        }

        return null;
    }

    /// <summary>
    /// Служба пользователя живет, пока живет окно, которое ее запустило (--parent): прокси без окна
    /// некому выключить. Окно закрылось или его уже нет — <paramref name="stop"/>.
    /// </summary>
    public static async Task StopWithParentAsync(int parentId, Func<Task> stop, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stop);
        try
        {
            using var parent = Process.GetProcessById(parentId);
            await parent.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // Окна уже нет.
        }

        await stop().ConfigureAwait(false);
    }

    /// <summary>
    /// Системный прокси этой ОС для службы пользователя: Windows — параметры Интернета, Linux — GNOME;
    /// нет такого (macOS — нужны права, другой рабочий стол) — null, и программы направляются вручную.
    /// </summary>
    public static ISystemProxy? SystemProxy() =>
        OperatingSystem.IsWindows() ? new WindowsSystemProxy()
        : OperatingSystem.IsLinux() ? GnomeSystemProxy.ForThisSession()
        : null;

    public static V1.ServiceInfo Info(string? xray)
    {
        var info = new V1.ServiceInfo
        {
            ServiceVersion = ServiceVersion.Current,
            ApiVersion = ContractMapping.ApiVersion,
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        };
        var version = xray is not null && File.Exists(xray) ? XrayVersion(xray) : null;
        info.Cores.Add(new V1.CoreInfo { Name = "xray", Version = version ?? string.Empty, Available = version is not null });
        return info;
    }

    /// <summary>«Xray 25.8.3 (…)» → «25.8.3»; не запустилось — null.</summary>
    private static string? XrayVersion(string xray)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(xray)
            {
                ArgumentList = { "version" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return Version().Match(output) is { Success: true } match ? match.Groups[1].Value : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"Xray (\d+\.\d+\.\d+)")]
    private static partial Regex Version();
}
