using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.MacOS;

/// <summary>
/// DNS macOS для туннеля: дополнительный резолвер всех имен в своей записи configd.
/// DNS физической сети не подменяется: иначе macOS привязывает адрес туннеля к en0,
/// и системные запросы идут мимо TUN. Запись не имеет InterfaceName; маршрут определяет TUN.
/// Restore удаляет только свою запись и восстанавливает копию старого формата после обновления службы.
/// </summary>
/// <param name="scutil">Вызов scutil: команды на stdin → вывод.</param>
/// <param name="backupFile">Копия DNS физической сети, оставленная прежней версией.</param>
/// <param name="service">Имя собственной записи configd (тесты дают свое).</param>
public sealed class ScutilDns(Func<string, string> scutil, string backupFile, string service = "PrivateGateway-Tunnel") : ISystemDns
{
    private const string Program = "/usr/sbin/scutil";

    /// <summary>scutil этой машины; не macOS — null.</summary>
    public static ScutilDns? ForThisMachine(string stateFolder) =>
        OperatingSystem.IsMacOS() && File.Exists(Program) ? new ScutilDns(Run, Path.Combine(stateFolder, "dns-backup.json")) : null;

    public void Apply(IPAddress server)
    {
        ArgumentNullException.ThrowIfNull(server);
        Check(service);
        RestoreLegacy();
        // Пустой домен охватывает все имена. Порядок — массив чисел, не строк; SearchOrder
        // у записи без IPv4/IPv6 configd удаляет. Не добавлять InterfaceName физической сети.
        scutil($"d.init\nd.add ServerAddresses * {Address(server.ToString())}\n"
            + "d.add SupplementalMatchDomains * \"\"\nd.add SupplementalMatchOrders * # 1\n"
            + $"set {StateKey(service)}\nquit\n");
    }

    public void Restore()
    {
        Check(service);
        scutil($"remove {StateKey(service)}\nquit\n");
        RestoreLegacy();
    }

    private void RestoreLegacy()
    {
        if (Load() is not { } backup)
        {
            return;
        }

        Check(backup.Service);
        Put(StateKey(backup.Service), backup.State);
        if (backup.Setup is not null)
        {
            Put(SetupKey(backup.Service), backup.Setup);
        }

        File.Delete(backupFile);
    }

    /// <summary>Серверы DNS из вывода <c>show</c>: элементы массива ServerAddresses; записи нет — null, массива нет — пусто.</summary>
    public static IReadOnlyList<string>? ParseServers(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (output.Contains("No such key", StringComparison.Ordinal) || !output.Contains("<dictionary>", StringComparison.Ordinal))
        {
            return null;
        }

        var servers = new List<string>();
        var inside = false;
        foreach (var line in output.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("ServerAddresses : <array>", StringComparison.Ordinal))
            {
                inside = true;
            }
            else if (inside && line == "}")
            {
                break;
            }
            else if (inside && line.IndexOf(" : ", StringComparison.Ordinal) is var at and > 0)
            {
                servers.Add(line[(at + 3)..].Trim());
            }
        }

        return servers;
    }

    private static string StateKey(string service) => $"State:/Network/Service/{service}/DNS";

    private static string SetupKey(string service) => $"Setup:/Network/Service/{service}/DNS";

    // В команды scutil попадают только номер сети и адреса — проверенного вида.
    private static void Check(string service)
    {
        if (service.Length == 0 || !service.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_'))
        {
            throw new IOException("Непонятный номер основной сети: " + service);
        }
    }

    private static string Address(string server) => server.Length > 0 && server.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or ':' or '%')
        ? server
        : throw new IOException("Непонятный адрес DNS: " + server);

    // Только серверы: остальное в записи (домены поиска) берется из нее же.
    private void Set(string key, IReadOnlyList<string> servers) =>
        scutil($"d.init\nget {key}\nd.add ServerAddresses * {string.Join(' ', servers.Select(Address))}\nset {key}\nquit\n");

    // Вернуть, как было: записи не было — убрать; серверов не было — убрать только их.
    private void Put(string key, IReadOnlyList<string>? servers)
    {
        if (servers is null)
        {
            scutil($"remove {key}\nquit\n");
        }
        else if (servers.Count == 0)
        {
            scutil($"d.init\nget {key}\nd.remove ServerAddresses\nset {key}\nquit\n");
        }
        else
        {
            Set(key, servers);
        }
    }

    private Backup? Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Backup>(File.ReadAllText(backupFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string Run(string commands)
    {
        var start = new ProcessStartInfo(Program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("scutil не запустился");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(commands);
            process.StandardInput.Close();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : throw new IOException("scutil: " + error.GetAwaiter().GetResult().Trim());
        }
        catch (Win32Exception e)
        {
            throw new IOException("scutil не запустился: " + e.Message, e);
        }
    }

    /// <summary>Что было у основной сети: серверы DHCP (State) и заданные вручную (Setup); null — записи не было.</summary>
    private sealed record Backup(string Service, IReadOnlyList<string>? State, IReadOnlyList<string>? Setup);
}
