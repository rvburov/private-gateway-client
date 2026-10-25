using System.Globalization;
using System.Security.Principal;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Platform.Windows;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Что ставить: имя службы, папка, куда копируется служба, кого пускать к ней (владелец: SID на
/// Windows, uid на Linux) и адрес IPC (только тесты; null — обычный).
/// </summary>
public sealed record SetupOptions(string Name, string Folder, string Owner, string? Ipc = null);

/// <summary>
/// Установка службы (<c>--install</c>) и удаление (<c>--uninstall</c>) — от администратора или root.
/// Служба и ядра копируются в защищенную папку (Program Files, /opt): служба работает от SYSTEM или
/// root, и запускать ее из папки, куда может писать пользователь, — значит отдать права любой его
/// программе. Затем — служба Windows или systemd с запуском вместе с ОС. Удаление останавливает
/// службу, снимает блок при обрыве (иначе машина осталась бы без сети) и удаляет папку.
/// </summary>
public static class ServiceSetup
{
    public const string DisplayName = "Private Gateway";
    public const string Description = "Private Gateway: VPN, блок при обрыве и DNS для окна Private Gateway.";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Имя службы: служба Windows — PrivateGateway, юнит systemd — private-gateway.</summary>
    public static string DefaultName => OperatingSystem.IsWindows() ? "PrivateGateway" : "private-gateway";

    /// <summary>Защищенная папка службы: на запись — только администраторам или root.</summary>
    public static string DefaultFolder => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Private Gateway", "Service")
        : "/opt/private-gateway/service";

    private static string Executable => OperatingSystem.IsWindows() ? "PrivateGateway.Service.exe" : "PrivateGateway.Service";

    /// <summary>Владелец — SID на Windows (S-1-5-…), uid на Linux; другое не пускается в команду службы.</summary>
    public static bool IsValidOwner(string owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!OperatingSystem.IsWindows())
        {
            return uint.TryParse(owner, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        }

        try
        {
            return new SecurityIdentifier(owner).Value == owner;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Копия службы: файлы из папки исполняемого файла службы, ядра — в cores/xray и cores/sing-box
    /// со всем, что лежит рядом с ними (геобазы, LICENSE). Прежние файлы заменяются.
    /// </summary>
    public static void Copy(string serviceFolder, string? xray, string? singBox, string target)
    {
        CopyFolder(serviceFolder, target);
        foreach (var (core, name) in new[] { (xray, "xray"), (singBox, "sing-box") })
        {
            if (core is not null && File.Exists(core))
            {
                CopyFolder(Path.GetDirectoryName(Path.GetFullPath(core))!, Path.Combine(target, "cores", name));
            }
        }
    }

    /// <summary>Команда запуска службы из ее папки.</summary>
    public static IReadOnlyList<string> Command(SetupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> command = [Path.Combine(options.Folder, Executable), "--service-name", options.Name, "--owner", options.Owner];
        if (options.Ipc is { } ipc)
        {
            command.AddRange(["--ipc", ipc]);
        }

        return command;
    }

    /// <summary>Скопировать и зарегистрировать службу, запустить ее; уже стоит — остановить, заменить и запустить снова.</summary>
    public static void Install(SetupOptions options, string serviceFolder, string? xray, string? singBox)
    {
        Validate(options);
        if (OperatingSystem.IsWindows())
        {
            WindowsServices.Stop(options.Name, StopTimeout);
            Copy(serviceFolder, xray, singBox, options.Folder);
            var command = Command(options);
            WindowsServices.Install(options.Name, DisplayName, Description, $"\"{command[0]}\" {string.Join(' ', command.Skip(1))}");
            WindowsServices.Start(options.Name);
        }
        else if (OperatingSystem.IsLinux())
        {
            var systemd = SystemdService.ForThisMachine() ?? throw new PlatformNotSupportedException("Нет systemd");
            Install(options, serviceFolder, xray, singBox, systemd);
        }
        else
        {
            throw new PlatformNotSupportedException("Установка службы на этой ОС еще не готова");
        }
    }

    /// <summary>Linux: копия и юнит systemd через <paramref name="systemd"/> (тесты дают свой).</summary>
    public static void Install(SetupOptions options, string serviceFolder, string? xray, string? singBox, SystemdService systemd)
    {
        Validate(options);
        ArgumentNullException.ThrowIfNull(systemd);

        // Работающий файл на Linux поверх не перезаписать — сначала остановить.
        systemd.Uninstall(options.Name);
        Copy(serviceFolder, xray, singBox, options.Folder);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(options.Folder, Executable),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        systemd.Install(options.Name, Description, Command(options));
    }

    /// <summary>В команду службы попадают только имя, владелец и адрес IPC проверенного вида.</summary>
    private static void Validate(SetupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsValidOwner(options.Owner))
        {
            throw new ArgumentException("Владелец — SID на Windows или uid на Linux: " + options.Owner, nameof(options));
        }

        if (options.Name.Length == 0 || options.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            || (options.Ipc ?? string.Empty).Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            throw new ArgumentException("Имя службы и адрес IPC — без пробелов и кавычек", nameof(options));
        }
    }

    /// <summary>Остановить и удалить службу, снять блок при обрыве, удалить папку службы.</summary>
    public static void Uninstall(string name, string folder, IKillSwitch? killSwitch)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsServices.Uninstall(name, StopTimeout);
        }
        else if (OperatingSystem.IsLinux())
        {
            SystemdService.ForThisMachine()?.Uninstall(name);
        }

        Finish(folder, killSwitch);
    }

    /// <summary>После удаления регистрации: блок снят, папка службы удалена (только если это папка службы).</summary>
    public static void Finish(string folder, IKillSwitch? killSwitch)
    {
        killSwitch?.Disable();
        if (File.Exists(Path.Combine(folder, Executable)))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void CopyFolder(string source, string target)
    {
        // Переустановка из самой папки службы — копировать нечего.
        if (string.Equals(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
        }
    }
}
