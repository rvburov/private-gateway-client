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
    public static string? DefaultXray(string serviceFolder) => Core(serviceFolder, "xray");

    /// <summary>
    /// sing-box (TUN режима VPN) — там же, где Xray: cores/sing-box рядом со службой, из папки сборки —
    /// artifacts/cores/sing-box (scripts/get-singbox.sh). Нет — null: режим VPN недоступен.
    /// </summary>
    /// <param name="serviceFolder">Папка исполняемого файла службы.</param>
    public static string? DefaultSingBox(string serviceFolder) => Core(serviceFolder, "sing-box");

    private static string? Core(string serviceFolder, string core)
    {
        var name = OperatingSystem.IsWindows() ? core + ".exe" : core;
        var installed = Path.Combine(serviceFolder, "cores", core, name);
        if (File.Exists(installed))
        {
            return installed;
        }

        // Только точное расположение папки сборки, без поиска вверх: чужой файл выше по дереву ядром не станет.
        var build = new DirectoryInfo(serviceFolder);
        return build.Parent is { Name: "Client.Service" } project && project.Parent is { Name: "bin" } bin && bin.Parent is { Name: "artifacts" } artifacts
            && Path.Combine(artifacts.FullName, "cores", core, name) is var built && File.Exists(built)
            ? built
            : null;
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
    /// Системный прокси этой ОС для службы пользователя: Windows — параметры Интернета, Linux — KDE в сеансе
    /// Plasma, иначе GNOME; нет такого (macOS — нужны права, другой рабочий стол) — null, и программы
    /// направляются вручную.
    /// </summary>
    public static ISystemProxy? SystemProxy() =>
        OperatingSystem.IsWindows() ? new WindowsSystemProxy()
        : OperatingSystem.IsLinux() ? (ISystemProxy?)KdeSystemProxy.ForThisSession() ?? GnomeSystemProxy.ForThisSession()
        : null;

    /// <summary>Блок при обрыве этой ОС для установленной службы: Windows — WFP, Linux — nftables, macOS — pf.</summary>
    public static IKillSwitch? KillSwitch() =>
        OperatingSystem.IsWindows() ? WfpKillSwitch.ForService()
        : OperatingSystem.IsLinux() ? NftablesKillSwitch.ForThisMachine()
        : OperatingSystem.IsMacOS() ? Platform.MacOS.PfKillSwitch.ForThisMachine()
        : null;

    /// <summary>
    /// Хранилище ОС для секретов установленной службы (подключение при загрузке): Windows — DPAPI от имени
    /// службы, Linux — <c>systemd-creds</c> с ключом машины; macOS и Linux без systemd-creds — нет, и при
    /// загрузке служба только держит блок.
    /// </summary>
    public static ISecretSealer? Sealer() =>
        OperatingSystem.IsWindows() ? new DpapiSealer()
        : OperatingSystem.IsLinux() ? SystemdCredsSealer.ForThisMachine("private-gateway-boot")
        : null;

    /// <summary>
    /// Маршруты этой ОС — найти чужой VPN: Windows — таблица маршрутов, Linux — <c>ip</c>, macOS — <c>netstat</c>.
    /// Читать маршруты можно и без прав: проверка есть и у службы пользователя.
    /// </summary>
    public static IRouteTable? Routes() =>
        OperatingSystem.IsWindows() ? new WindowsRouteTable()
        : OperatingSystem.IsLinux() ? IpRouteTable.ForThisMachine()
        : OperatingSystem.IsMacOS() ? Platform.MacOS.NetstatRouteTable.ForThisMachine()
        : null;

    /// <summary>
    /// DNS ОС на время туннеля, где sing-box его не прописывает: macOS — <c>scutil</c> (копия прежнего — в папке состояния
    /// <paramref name="state"/>); Windows и Linux — null, там DNS прописывает sing-box.
    /// </summary>
    public static ISystemDns? SystemDns(string state) =>
        OperatingSystem.IsMacOS() ? Platform.MacOS.ScutilDns.ForThisMachine(state) : null;

    /// <summary>Часы сна этой ОС (<see cref="ISuspendClock"/>): по ним служба узнает, что машина спала.</summary>
    public static ISuspendClock? SuspendClock() =>
        OperatingSystem.IsWindows() ? new WindowsSuspendClock()
        : OperatingSystem.IsLinux() ? new LinuxSuspendClock()
        : OperatingSystem.IsMacOS() ? new Platform.MacOS.MacSuspendClock()
        : null;

    /// <summary>Сведения для GetServiceInfo: версии службы и контракта, ОС, ядра — какие есть и какой версии.</summary>
    public static V1.ServiceInfo Info(string? xray, string? singBox = null)
    {
        var info = new V1.ServiceInfo
        {
            ServiceVersion = ServiceVersion.Current,
            ApiVersion = ContractMapping.ApiVersion,
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        };
        foreach (var (name, path, pattern) in new[] { ("xray", xray, XrayVersion()), ("sing-box", singBox, SingBoxVersion()) })
        {
            var version = path is not null && File.Exists(path) ? CoreVersion(path, pattern) : null;
            info.Cores.Add(new V1.CoreInfo { Name = name, Version = version ?? string.Empty, Available = version is not null });
        }

        return info;
    }

    /// <summary>Версия из вывода «<c>&lt;ядро&gt; version</c>»: «Xray 25.8.3 (…)» → «25.8.3»; не запустилось — null.</summary>
    private static string? CoreVersion(string executable, Regex pattern)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable)
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
            return pattern.Match(output) is { Success: true } match ? match.Groups[1].Value : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"Xray (\d+\.\d+\.\d+)")]
    private static partial Regex XrayVersion();

    [GeneratedRegex(@"sing-box version (\d+\.\d+\.\d+)")]
    private static partial Regex SingBoxVersion();
}
