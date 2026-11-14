using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.MacOS;

/// <summary>
/// DNS macOS для туннеля: дополнительный резолвер всех имен в своей записи configd.
/// DNS физической сети не подменяется: иначе macOS привязывает адрес туннеля к en0,
/// и системные запросы идут мимо TUN. Запись не имеет InterfaceName; маршрут определяет TUN.
/// Restore удаляет только свою запись. Копия старого формата не доказывает принадлежность нынешнего DNS:
/// если физическая сеть уже вернулась к исходным настройкам, копия удаляется; иначе требуется ручная проверка.
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
        if (Read(StateKey(service)) is not null)
        {
            throw new IOException("Удаление DNS туннеля не подтверждено macOS");
        }
        RestoreLegacy();
    }

    private void RestoreLegacy()
    {
        if (Load() is not { } backup)
        {
            return;
        }

        // В прежнем формате есть только исходные DNS, но нет подтвержденных настроек, которые поставили мы.
        // Даже знакомый адрес туннеля не доказывает владение: DNS мог изменить пользователь или другой VPN.
        if (!Same(Read(StateKey(backup.Service)), backup.State) || !Same(Read(SetupKey(backup.Service)), backup.Setup))
        {
            throw new IOException("Принадлежность прежних настроек DNS не подтверждена: требуется ручная проверка сети; копия сохранена");
        }

        File.Delete(backupFile);
    }

    /// <summary>Серверы DNS из вывода <c>show</c>: элементы массива ServerAddresses; записи нет — null, массива нет — пусто.</summary>
    public static IReadOnlyList<string>? ParseServers(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var text = output.Trim();
        if (text == "No such key")
        {
            return null;
        }
        if (!text.StartsWith("<dictionary> {", StringComparison.Ordinal) || !text.EndsWith('}')
            || text.Count(c => c == '{') != text.Count(c => c == '}'))
        {
            throw new IOException("macOS не подтвердила содержимое записи DNS");
        }

        var servers = new List<string>();
        var inside = false;
        foreach (var line in output.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("ServerAddresses : <array>", StringComparison.Ordinal))
            {
                inside = true;
            }
            else if (line.StartsWith("ServerAddresses :", StringComparison.Ordinal))
            {
                throw new IOException("macOS вернула неизвестный формат адресов DNS");
            }
            else if (inside && line == "}")
            {
                inside = false;
                break;
            }
            else if (inside && line.IndexOf(" : ", StringComparison.Ordinal) is var at and > 0)
            {
                servers.Add(line[(at + 3)..].Trim());
            }
        }

        if (inside)
        {
            throw new IOException("macOS вернула неполный список адресов DNS");
        }

        return servers;
    }

    private static string StateKey(string service) => $"State:/Network/Service/{service}/DNS";

    private static string SetupKey(string service) => $"Setup:/Network/Service/{service}/DNS";

    // В команды scutil попадают только номер сети и адреса — проверенного вида.
    private static void Check(string? service)
    {
        if (string.IsNullOrEmpty(service) || !service.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_'))
        {
            throw new IOException("Непонятный номер основной сети: " + service);
        }
    }

    private static string Address(string? server) => !string.IsNullOrEmpty(server) && IPAddress.TryParse(server, out _)
        && server.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or ':' or '%')
        ? server
        : throw new IOException("Непонятный адрес DNS: " + server);

    private IReadOnlyList<string>? Read(string key) => ParseServers(scutil($"show {key}\nquit\n"));

    private static bool Same(IReadOnlyList<string>? current, IReadOnlyList<string>? saved) =>
        current is null ? saved is null : saved is not null && current.SequenceEqual(saved, StringComparer.Ordinal);

    private Backup? Load()
    {
        try
        {
            var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(backupFile))
                ?? throw new IOException("Копия прежних DNS пуста; требуется ручная проверка сети");
            Check(backup.Service);
            foreach (var address in (backup.State ?? []).Concat(backup.Setup ?? []))
            {
                _ = Address(address);
            }
            return backup;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or JsonException)
        {
            throw new IOException("Копия прежних DNS не прочитана; требуется ручная проверка сети", e);
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
    private sealed record Backup([property: JsonRequired] string Service, [property: JsonRequired] IReadOnlyList<string>? State,
        [property: JsonRequired] IReadOnlyList<string>? Setup);
}
