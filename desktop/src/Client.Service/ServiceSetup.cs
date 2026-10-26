using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Platform.MacOS;
using PrivateGateway.Client.Platform.Windows;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Что ставить: имя службы, папка, куда копируется служба, кого пускать к ней (владелец: SID на
/// Windows, uid на Linux), адрес IPC (только тесты; null — обычный) и папка состояния службы (журнал,
/// блок при загрузке, номер раскатки; null — <see cref="ServiceSetup.DefaultStateFolder"/>).
/// </summary>
public sealed record SetupOptions(string Name, string Folder, string Owner, string? Ipc = null, string? State = null)
{
    public string StateFolder => State ?? ServiceSetup.DefaultStateFolder;
}

/// <summary>Служба ОС для установки: systemd на Linux, диспетчер служб на Windows; тесты дают свою.</summary>
public interface IOsService
{
    bool IsInstalled(string name);

    /// <summary>Зарегистрировать или обновить команду, запуск вместе с ОС; не запускает.</summary>
    void Register(string name, IReadOnlyList<string> command);

    void Start(string name);

    /// <summary>Остановить и дождаться остановки; не работает — ничего.</summary>
    void StopAndWait(string name);

    /// <summary>Остановить и удалить регистрацию; нет — ничего.</summary>
    void Remove(string name);
}

/// <summary>
/// Установка службы (<c>--install</c>) и удаление (<c>--uninstall</c>) — от администратора или root.
/// Служба и ядра копируются в защищенную папку (Program Files, /opt): служба работает от SYSTEM или
/// root, и запускать ее из папки, куда может писать пользователь, — значит отдать права любой его
/// программе. Затем — служба Windows или systemd с запуском вместе с ОС. Удаление останавливает
/// службу, снимает блок при обрыве (иначе машина осталась бы без сети) и удаляет папки службы и ее состояния.
/// </summary>
/// <remarks>
/// Установка поверх атомарна: новая копия собирается рядом (<c>&lt;папка&gt;.new</c>), пока работает прежняя; прежняя
/// останавливается и откладывается (<c>.old</c>), новая встает на ее место и запускается. Новая не сказала «готова»
/// (<see cref="ServiceReady"/>) за <see cref="ReadyTimeout"/> — она убирается, прежняя возвращается и запускается снова,
/// установка отвечает ошибкой. Состояние службы живет отдельно от копии (<see cref="SetupOptions.StateFolder"/>) и
/// переживает и обновление, и откат.
/// </remarks>
public static class ServiceSetup
{
    public const string DisplayName = "Private Gateway";
    public const string Description = "Private Gateway: VPN, блок при обрыве и DNS для окна Private Gateway.";

    /// <summary>Сколько ждать, что новая служба запустилась и слушает IPC.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Файлы состояния, которые служба пишет в свою папку состояния: журнал, признак работы, замок одной копии, ядра
    /// прошлого запуска, блок и подключение при загрузке, номер раскатки, признак готовности, история версий (откат). Их переносит установка из
    /// прежней папки службы и удаляет удаление; другие файлы в папке состояния не трогаются.
    /// </summary>
    public static readonly IReadOnlyList<string> StateFiles =
        ["journal.jsonl", "journal.1.jsonl", "running", "service.lock", "cores.json", "boot-block.json", "boot-connect.sealed", "update-bucket", ServiceReady.FileName,
        Updates.VersionHistory.CurrentFile, Updates.VersionHistory.PreviousFile, Updates.VersionHistory.LeftFile];

    /// <summary>Имя службы: служба Windows — PrivateGateway, юнит systemd — private-gateway, метка launchd — обратным доменом.</summary>
    public static string DefaultName => OperatingSystem.IsWindows() ? "PrivateGateway"
        : OperatingSystem.IsMacOS() ? MacLabel
        : "private-gateway";

    /// <summary>Метка службы launchd на macOS.</summary>
    public const string MacLabel = "io.github.rvburov.private-gateway";

    /// <summary>Защищенная папка службы: на запись — только администраторам или root.</summary>
    public static string DefaultFolder => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Private Gateway", "Service")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/Private Gateway/Service"
        : "/opt/private-gateway/service";

    /// <summary>Папка состояния установленной службы: тоже только для администраторов или root, отдельно от копии службы.</summary>
    public static string DefaultStateFolder => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Private Gateway", "ServiceState")
        : OperatingSystem.IsMacOS() ? "/Library/Application Support/Private Gateway/ServiceState"
        : "/var/lib/private-gateway";

    private static string Executable => OperatingSystem.IsWindows() ? "PrivateGateway.Service.exe" : "PrivateGateway.Service";

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
    /// Копия службы: файлы службы из папки ее исполняемого файла, ядра — в cores/xray и cores/sing-box со всем, что
    /// лежит рядом с ними (геобазы, LICENSE). Файлы службы — по ее <c>PrivateGateway.Service.deps.json</c>: окно с его
    /// библиотеками (Avalonia, Skia) служба не возит; нет такого файла — вся папка. Прежние файлы заменяются.
    /// </summary>
    public static void Copy(string serviceFolder, string? xray, string? singBox, string target)
    {
        CopyFolder(serviceFolder, target, ServiceFiles(serviceFolder));
        foreach (var (core, name) in new[] { (xray, "xray"), (singBox, "sing-box") })
        {
            if (core is not null && File.Exists(core))
            {
                CopyFolder(Path.GetDirectoryName(Path.GetFullPath(core))!, Path.Combine(target, "cores", name), only: null);
            }
        }
    }

    /// <summary>
    /// Файлы службы в папке сборки: исполняемый файл, сборка, deps.json, runtimeconfig.json и все библиотеки из deps.json
    /// (свои пакеты, а у самодостаточной сборки — и среда .NET с ASP.NET Core). Нет deps.json — null: копировать все.
    /// </summary>
    public static IReadOnlySet<string>? ServiceFiles(string serviceFolder)
    {
        var deps = Path.Combine(serviceFolder, "PrivateGateway.Service.deps.json");
        if (!File.Exists(deps))
        {
            return null;
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Executable, "PrivateGateway.Service.dll", "PrivateGateway.Service.deps.json", "PrivateGateway.Service.runtimeconfig.json",
        };
        using var document = JsonDocument.Parse(File.ReadAllBytes(deps));
        foreach (var target in document.RootElement.GetProperty("targets").EnumerateObject())
        {
            foreach (var library in target.Value.EnumerateObject())
            {
                foreach (var kind in new[] { "runtime", "native" })
                {
                    if (library.Value.TryGetProperty(kind, out var assets))
                    {
                        files.UnionWith(assets.EnumerateObject().Select(a => Path.GetFileName(a.Name)));
                    }
                }

                // Сборка без RID (тесты, отладка) кладет библиотеки отдельных ОС в runtimes/<rid>/… и ищет их там же:
                // служба Windows без runtimes/win/lib/…/System.ServiceProcess.ServiceController.dll не запускается.
                if (library.Value.TryGetProperty("runtimeTargets", out var targets))
                {
                    files.UnionWith(targets.EnumerateObject().Select(a => a.Name.Replace('/', Path.DirectorySeparatorChar)));
                }

                if (library.Value.TryGetProperty("resources", out var resources))
                {
                    files.UnionWith(resources.EnumerateObject().Select(r => Path.Combine(r.Value.GetProperty("locale").GetString()!, Path.GetFileName(r.Name))));
                }
            }
        }

        // Чего нет в папке (ссылки сборки отладки на пакеты из кеша NuGet), копировать нечего: среда найдет их сама.
        return files.Where(f => File.Exists(Path.Combine(serviceFolder, f))).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Команда запуска службы из ее папки.</summary>
    public static IReadOnlyList<string> Command(SetupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> command = [Path.Combine(options.Folder, Executable), "--service-name", options.Name, "--owner", options.Owner,
            "--state", options.StateFolder];
        if (options.Ipc is { } ipc)
        {
            command.AddRange(["--ipc", ipc]);
        }

        return command;
    }

    /// <summary>Служба этой ОС: Windows — диспетчер служб, Linux — systemd, macOS — launchd.</summary>
    public static IOsService ForThisMachine() =>
        OperatingSystem.IsWindows() ? new WindowsOsService()
        : OperatingSystem.IsLinux() ? new SystemdOsService(SystemdService.ForThisMachine() ?? throw new PlatformNotSupportedException("Нет systemd"))
        : OperatingSystem.IsMacOS() ? new LaunchdOsService(LaunchdService.ForThisMachine() ?? throw new PlatformNotSupportedException("Нет launchctl"))
        : throw new PlatformNotSupportedException("Установка службы на этой ОС еще не готова");

    /// <summary>Поставить службу или заменить работающую атомарно (см. описание класса).</summary>
    /// <exception cref="IOException">Новая служба не запустилась; прежняя, если была, возвращена и работает.</exception>
    public static void Install(SetupOptions options, string serviceFolder, string? xray, string? singBox, IOsService os, TimeSpan? readyTimeout = null)
    {
        Validate(options);
        ArgumentNullException.ThrowIfNull(os);
        var folder = Path.GetFullPath(options.Folder).TrimEnd(Path.DirectorySeparatorChar);
        var staged = folder + ".new";
        var previous = folder + ".old";
        DeleteServiceFolder(staged);
        DeleteServiceFolder(previous);
        if (Directory.Exists(staged) || Directory.Exists(previous))
        {
            // Чужая папка на нужном месте: ни удалить, ни занять — и работающую службу не трогать.
            throw new IOException($"{staged} или {previous} — не папка службы, установка ее не трогает");
        }

        // Новая копия — рядом, пока прежняя служба работает.
        Copy(serviceFolder, xray, singBox, staged);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(staged, Executable),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        PrepareState(options.StateFolder, folder);
        var hadService = os.IsInstalled(options.Name) && File.Exists(Path.Combine(folder, Executable));
        os.StopAndWait(options.Name);
        if (Directory.Exists(folder))
        {
            Move(folder, previous);
        }

        Move(staged, folder);
        if (Started(options, os, readyTimeout ?? ReadyTimeout, out var failure))
        {
            DeleteServiceFolder(previous);
            return;
        }

        // Откат: новая копия — прочь, прежняя — на место и снова в работу. Новая могла не остановиться (падает и
        // перезапускается) — удаление ее папки само ждет, пока файлы освободятся.
        try
        {
            os.StopAndWait(options.Name);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("Новая служба не остановилась: " + e.Message);
        }

        DeleteServiceFolder(folder);
        if (hadService && Directory.Exists(previous))
        {
            Move(previous, folder);
            if (Started(options, os, readyTimeout ?? ReadyTimeout, out _))
            {
                throw new IOException("новая служба не запустилась — работает прежняя" + failure);
            }

            throw new IOException("новая служба не запустилась, прежняя — тоже");
        }

        os.Remove(options.Name);
        throw new IOException("служба не запустилась" + failure);
    }

    /// <summary>Остановить и удалить службу, снять блок при обрыве, удалить ее папку и состояние.</summary>
    public static void Uninstall(string name, string folder, string state, IKillSwitch? killSwitch, IOsService os)
    {
        ArgumentNullException.ThrowIfNull(os);
        os.Remove(name);
        Finish(folder, killSwitch);
        DeleteState(state);
    }

    /// <summary>После удаления регистрации: блок снят, папка службы удалена (только если это папка службы).</summary>
    public static void Finish(string folder, IKillSwitch? killSwitch)
    {
        killSwitch?.Disable();
        DeleteServiceFolder(folder);
    }

    /// <summary>Файлы состояния (<see cref="StateFiles"/>), скачанные обновления и пустая папка; чужое остается.</summary>
    public static void DeleteState(string state)
    {
        if (!Directory.Exists(state))
        {
            return;
        }

        foreach (var file in StateFiles)
        {
            File.Delete(Path.Combine(state, file));
        }

        // Скачанные обновления и журнал их установки (UpdateInstaller).
        if (Directory.Exists(Path.Combine(state, "updates")))
        {
            Directory.Delete(Path.Combine(state, "updates"), recursive: true);
        }

        if (!Directory.EnumerateFileSystemEntries(state).Any())
        {
            Directory.Delete(state);
        }
    }

    /// <summary>В команду службы попадают только имя, владелец, адрес IPC и папка состояния проверенного вида.</summary>
    private static void Validate(SetupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsValidOwner(options.Owner))
        {
            throw new ArgumentException("Владелец — SID на Windows или uid на Linux: " + options.Owner, nameof(options));
        }

        if (options.Name.Length == 0 || options.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
            || (options.Ipc ?? string.Empty).Any(c => char.IsWhiteSpace(c) || c == '"')
            || !Path.IsPathFullyQualified(options.StateFolder) || options.StateFolder.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("Имя службы и адрес IPC — без пробелов и кавычек, папка состояния — полный путь", nameof(options));
        }
    }

    /// <summary>
    /// Зарегистрировать и запустить; готова — true, нет за <paramref name="timeout"/> — false и в
    /// <paramref name="failure"/> — почему: отказ ОС при запуске или что служба успела сказать о готовности.
    /// </summary>
    private static bool Started(SetupOptions options, IOsService os, TimeSpan timeout, out string failure)
    {
        ServiceReady.Clear(options.StateFolder);
        os.Register(options.Name, Command(options));
        try
        {
            os.Start(options.Name);
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            failure = $" (ОС не запустила ее: {e.Message})";
            return false;
        }

        if (ServiceReady.Wait(options.StateFolder, ServiceVersion.Current, timeout))
        {
            failure = string.Empty;
            return true;
        }

        failure = Ready(options.StateFolder);
        return false;
    }

    // Что служба успела сказать о готовности — в текст ошибки: «готова» другой версии или ничего.
    private static string Ready(string state)
    {
        var path = Path.Combine(state, ServiceReady.FileName);
        return File.Exists(path) ? $" (в {path} — «{File.ReadAllText(path).Trim()}», а ждали {ServiceVersion.Current})" : $" ({path} не появился за отведенное время)";
    }

    /// <summary>
    /// Папка состояния — только для root (0700) на Linux; на Windows — права Program Files. Состояние прежней службы
    /// из ее папки (до отдельной папки состояния) переносится сюда, если здесь его еще нет.
    /// </summary>
    private static void PrepareState(string state, string serviceFolder)
    {
        Directory.CreateDirectory(state);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(state, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (string.Equals(Path.GetFullPath(state).TrimEnd(Path.DirectorySeparatorChar), serviceFolder, PathComparison))
        {
            return;
        }

        foreach (var file in StateFiles)
        {
            var old = Path.Combine(serviceFolder, file);
            var moved = Path.Combine(state, file);
            if (File.Exists(old) && !File.Exists(moved))
            {
                File.Move(old, moved);
            }
        }
    }

    /// <summary>Удалить папку, только если это папка службы (в ней ее исполняемый файл) — чужое не трогается.</summary>
    private static void DeleteServiceFolder(string folder)
    {
        if (File.Exists(Path.Combine(folder, Executable)))
        {
            Retry(() => Directory.Delete(folder, recursive: true));
        }
    }

    // Только что остановленная служба Windows еще может держать свои файлы: несколько попыток.
    private static void Move(string from, string to) => Retry(() => Directory.Move(from, to));

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 50)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static void CopyFolder(string source, string target, IReadOnlySet<string>? only)
    {
        // Переустановка из самой папки службы — копировать нечего.
        if (string.Equals(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar), PathComparison))
        {
            return;
        }

        // Папка службы бывает внутри папки программы (/opt/private-gateway/service, Program Files\Private Gateway\Service):
        // список файлов — заранее и без нее, иначе копия подхватывала бы сама себя.
        var inside = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = only is not null
            ? [.. only.Select(f => Path.Combine(source, f))]
            : Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFullPath(f).StartsWith(inside, PathComparison))
                .ToList();
        Directory.CreateDirectory(target);
        foreach (var file in files)
        {
            var to = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
        }
    }

    /// <summary>systemd: юнит в /etc/systemd/system.</summary>
    private sealed class SystemdOsService(SystemdService systemd) : IOsService
    {
        public bool IsInstalled(string name) => systemd.IsInstalled(name);

        public void Register(string name, IReadOnlyList<string> command) => systemd.Register(name, Description, command);

        public void Start(string name) => systemd.Start(name);

        public void StopAndWait(string name) => systemd.Stop(name);

        public void Remove(string name) => systemd.Uninstall(name);
    }

    /// <summary>launchd: описание в /Library/LaunchDaemons.</summary>
    private sealed class LaunchdOsService(LaunchdService launchd) : IOsService
    {
        public bool IsInstalled(string name) => launchd.IsInstalled(name);

        public void Register(string name, IReadOnlyList<string> command) => launchd.Register(name, command);

        public void Start(string name) => launchd.Start(name);

        public void StopAndWait(string name) => launchd.Stop(name);

        public void Remove(string name) => launchd.Uninstall(name);
    }

    /// <summary>Диспетчер служб Windows.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class WindowsOsService : IOsService
    {
        public bool IsInstalled(string name) => WindowsServices.IsInstalled(name);

        public void Register(string name, IReadOnlyList<string> command) =>
            WindowsServices.Install(name, DisplayName, Description, $"\"{command[0]}\" {string.Join(' ', command.Skip(1).Select(Quote))}");

        public void Start(string name) => WindowsServices.Start(name);

        public void StopAndWait(string name) => WindowsServices.Stop(name, StopTimeout);

        public void Remove(string name) => WindowsServices.Uninstall(name, StopTimeout);

        // Папка состояния в Program Files — с пробелом.
        private static string Quote(string argument) => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;
    }
}
