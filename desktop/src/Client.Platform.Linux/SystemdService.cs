using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace PrivateGateway.Client.Platform.Linux;

/// <summary>
/// Служба systemd: файл юнита в /etc/systemd/system и <c>systemctl</c> — включить с загрузкой ОС и
/// запустить, остановить и удалить. Служба — от root, при сбое systemd перезапускает ее через 5 с.
/// </summary>
/// <param name="systemctl">Вызов systemctl: аргументы; не вышло — <see cref="IOException"/>.</param>
/// <param name="unitFolder">Папка юнитов; тесты дают свою.</param>
public sealed class SystemdService(Action<IReadOnlyList<string>> systemctl, string unitFolder = "/etc/systemd/system")
{
    private static readonly string[] Programs = ["/usr/bin/systemctl", "/bin/systemctl"];

    /// <summary>systemctl этой машины — по полному пути; нет systemd — null.</summary>
    public static SystemdService? ForThisMachine() =>
        Programs.FirstOrDefault(File.Exists) is { } program && Directory.Exists("/run/systemd/system")
            ? new SystemdService(arguments => Run(program, arguments))
            : null;

    /// <summary>
    /// Текст юнита: команда — путь и параметры, каждый в кавычках systemd. Служба стартует раньше сети
    /// (network-pre.target), как брандмауэр: systemd ждет READY=1 после установки сохраненного блока, а не только
    /// создания процесса. Подключение к серверу готовности не задерживает — для него сеть еще должна подняться.
    /// </summary>
    public static string Unit(string description, IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var execStart = string.Join(' ', command.Select(Quote));
        return new StringBuilder()
            .Append("[Unit]\n")
            .Append("Description=").Append(description).Append('\n')
            .Append("Wants=network-pre.target\n")
            .Append("Before=network-pre.target\n")
            .Append('\n')
            .Append("[Service]\n")
            .Append("Type=notify\n")
            .Append("NotifyAccess=main\n")
            .Append("ExecStart=").Append(execStart).Append('\n')
            .Append("Restart=on-failure\n")
            .Append("RestartSec=5\n")
            .Append('\n')
            .Append("[Install]\n")
            .Append("WantedBy=multi-user.target\n")
            .ToString();

        // В кавычках systemd «\» и «"» экранируются, «%» — спецификатор, его удваивают.
        static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// Сообщить systemd, что ранняя инициализация закончена: сохраненный блок при обрыве уже установлен. NOTIFY_SOCKET
    /// бывает путем или абстрактным Unix-сокетом (начинается с «@»); сообщение — локальная датаграмма, без сети.
    /// </summary>
    public static void NotifyReady(string socketPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        var address = socketPath[0] == '@' ? "\0" + socketPath[1..] : socketPath;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        socket.SendTo("READY=1"u8, new UnixDomainSocketEndPoint(address));
    }

    public bool IsInstalled(string name) => File.Exists(UnitPath(name));

    /// <summary>Записать юнит (права 0644), перечитать и включить запуск с ОС; не запускает.</summary>
    public void Register(string name, string description, IReadOnlyList<string> command)
    {
        var path = UnitPath(name);
        File.WriteAllText(path, Unit(description, command));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        systemctl(["daemon-reload"]);
        systemctl(["enable", name + ".service"]);
    }

    public void Start(string name) => systemctl(["start", name + ".service"]);

    /// <summary>Остановить (systemctl ждет конца остановки); юнита нет — ничего.</summary>
    public void Stop(string name)
    {
        if (File.Exists(UnitPath(name)))
        {
            systemctl(["stop", name + ".service"]);
        }
    }

    /// <summary>Остановить, выключить и удалить юнит; его нет — ничего.</summary>
    public void Uninstall(string name)
    {
        var path = UnitPath(name);
        if (!File.Exists(path))
        {
            return;
        }

        systemctl(["disable", "--now", name + ".service"]);
        File.Delete(path);
        systemctl(["daemon-reload"]);
    }

    private string UnitPath(string name) => Path.Combine(unitFolder, name + ".service");

    private static void Run(string program, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new IOException("systemctl не запустился");
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new IOException($"systemctl {string.Join(' ', arguments)}: {error.GetAwaiter().GetResult().Trim()}");
            }
        }
        catch (Win32Exception e)
        {
            throw new IOException("systemctl не запустился: " + e.Message, e);
        }
    }
}
