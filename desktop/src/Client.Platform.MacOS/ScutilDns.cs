using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.MacOS;

/// <summary>
/// DNS macOS для туннеля через динамическое хранилище configd (<c>scutil</c>): у основной сети
/// (<c>State:/Network/Global/IPv4</c> → PrimaryService) серверы DNS заменяются адресом DNS туннеля — и в выданных DHCP
/// (<c>State:</c>), и в заданных вручную (<c>Setup:</c>, если есть: они важнее); остальное в записи (домены поиска)
/// остается. Прежние серверы — в файле копии в папке состояния службы: <see cref="Restore"/> возвращает их и после
/// аварии службы (вызывается при ее запуске). Изменения динамического хранилища не переживают перезагрузку и не
/// трогают настройки сети пользователя на диске.
/// </summary>
/// <param name="scutil">Вызов scutil: команды на stdin → вывод.</param>
/// <param name="backupFile">Файл копии прежних серверов.</param>
/// <param name="primaryService">Основная сеть (тесты дают свою); null — из <c>State:/Network/Global/IPv4</c>.</param>
public sealed class ScutilDns(Func<string, string> scutil, string backupFile, Func<string?>? primaryService = null) : ISystemDns
{
    private const string Program = "/usr/sbin/scutil";

    /// <summary>scutil этой машины, копия — в папке состояния; не macOS — null.</summary>
    public static ScutilDns? ForThisMachine(string stateFolder) =>
        OperatingSystem.IsMacOS() && File.Exists(Program) ? new ScutilDns(Run, Path.Combine(stateFolder, "dns-backup.json")) : null;

    public void Apply(IPAddress server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var primary = (primaryService ?? PrimaryService)() ?? throw new IOException("У macOS нет основной сети — DNS туннеля прописать некуда");
        Check(primary);
        if (Load() is { } saved && saved.Service != primary)
        {
            // Основная сеть сменилась (другой Wi-Fi, кабель): прежней — ее DNS, новой — DNS туннеля.
            Restore();
        }

        if (Load() is null)
        {
            Save(new Backup(primary, Servers(StateKey(primary)), Servers(SetupKey(primary))));
        }

        var backup = Load()!;
        Set(StateKey(primary), [server.ToString()]);
        if (backup.Setup is not null)
        {
            Set(SetupKey(primary), [server.ToString()]);
        }
    }

    public void Restore()
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

    /// <summary>Основная сеть из вывода <c>show State:/Network/Global/IPv4</c>: строка «PrimaryService : …»; нет — null.</summary>
    public static string? ParsePrimary(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("PrimaryService : ", StringComparison.Ordinal))
            .Select(l => l["PrimaryService : ".Length..].Trim()).FirstOrDefault();
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

    private string? PrimaryService() => ParsePrimary(scutil("show State:/Network/Global/IPv4\nquit\n"));

    private IReadOnlyList<string>? Servers(string key) => ParseServers(scutil($"show {key}\nquit\n"));

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

    private void Save(Backup backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupFile))!);
        File.WriteAllText(backupFile, JsonSerializer.Serialize(backup));
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
