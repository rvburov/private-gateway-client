using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.MacOS;

/// <summary>
/// Системный прокси macOS (<c>networksetup</c>): прокси на macOS — настройка машины у каждой сети (Wi-Fi, Ethernet), а не
/// пользователя, и менять ее вправе только администратор — поэтому его ставит установленная служба от root. У всех
/// включенных сетей веб-прокси, защищенный веб-прокси и SOCKS направляются на вход ядра, локальные адреса и частные сети
/// идут напрямую, скрипт автонастройки на время подключения выключается (браузеры взяли бы его раньше ручного прокси).
/// В снимке — вывод <c>networksetup -get…</c> каждой сети, по нему все и возвращается. Логин прокси с паролем
/// networksetup не показывает: чужой прокси с паролем после возврата просит его заново.
/// </summary>
/// <param name="networksetup">Вызов networksetup: аргументы → вывод; не вышло — <see cref="IOException"/>.</param>
public sealed class NetworksetupSystemProxy(Func<IReadOnlyList<string>, string> networksetup) : ISystemProxy
{
    private const string Program = "/usr/sbin/networksetup";

    // Веб-прокси, защищенный веб-прокси и SOCKS: -get<вид>proxy, -set<вид>proxy, -set<вид>proxystate.
    private static readonly string[] Kinds = ["web", "secureweb", "socksfirewall"];

    /// <summary>Мимо прокси: локальные имена и адреса, частные сети, сети без DHCP и имена Bonjour.</summary>
    public static readonly IReadOnlyList<string> Bypass =
        ["localhost", "127.0.0.0/8", "::1", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254/16", "*.local"];

    public string Name => "macOS";

    /// <summary>networksetup этой машины; не macOS — null.</summary>
    public static NetworksetupSystemProxy? ForThisMachine() =>
        OperatingSystem.IsMacOS() && File.Exists(Program) ? new NetworksetupSystemProxy(Run) : null;

    public SystemProxySnapshot Read()
    {
        var values = new Dictionary<string, string?>();
        foreach (var service in Services(enabledOnly: true))
        {
            foreach (var kind in Kinds)
            {
                values[Key(service, kind)] = networksetup([$"-get{kind}proxy", service]).Trim();
            }

            values[Key(service, "bypass")] = networksetup(["-getproxybypassdomains", service]).Trim();
            values[Key(service, "auto")] = networksetup(["-getautoproxyurl", service]).Trim();
        }

        return new SystemProxySnapshot(values);
    }

    public void Enable(int port)
    {
        var text = port.ToString(CultureInfo.InvariantCulture);
        foreach (var service in Services(enabledOnly: true))
        {
            networksetup(["-setproxybypassdomains", service, .. Bypass]);
            foreach (var kind in Kinds)
            {
                // -set<вид>proxy включает этот прокси сразу.
                networksetup([$"-set{kind}proxy", service, "127.0.0.1", text]);
            }

            networksetup(["-setautoproxystate", service, "off"]);
        }
    }

    public void Restore(SystemProxySnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        var services = Services(enabledOnly: false);
        foreach (var service in previous.Values.Keys.Select(k => k[..k.IndexOf('\n', StringComparison.Ordinal)]).Distinct().Where(services.Contains))
        {
            foreach (var kind in Kinds)
            {
                if (previous.Values.GetValueOrDefault(Key(service, kind)) is not { } value)
                {
                    continue;
                }

                // Свой адрес был — вернуть его и прежнее «включен»; не было — только выключить (адрес networksetup не стирает).
                var (enabled, server, port) = ParseProxy(value);
                if (server.Length > 0 && port > 0)
                {
                    networksetup([$"-set{kind}proxy", service, server, port.ToString(CultureInfo.InvariantCulture)]);
                }

                if (!enabled || server.Length == 0 || port <= 0)
                {
                    networksetup([$"-set{kind}proxystate", service, "off"]);
                }
            }

            if (previous.Values.GetValueOrDefault(Key(service, "bypass")) is { } bypass)
            {
                var domains = ParseBypass(bypass);
                networksetup(["-setproxybypassdomains", service, .. domains.Count == 0 ? ["Empty"] : domains]);
            }

            if (previous.Values.GetValueOrDefault(Key(service, "auto")) is { } auto && ParseEnabled(auto))
            {
                networksetup(["-setautoproxystate", service, "on"]);
            }
        }
    }

    /// <summary>
    /// Сети из вывода <c>-listallnetworkservices</c>: строка «An asterisk…» — пояснение, «*» в начале — сеть выключена
    /// (<paramref name="enabledOnly"/> — такие пропустить).
    /// </summary>
    public static IReadOnlyList<string> ParseServices(string output, bool enabledOnly)
    {
        ArgumentNullException.ThrowIfNull(output);
        return [.. output.Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith("An asterisk", StringComparison.Ordinal) && !(enabledOnly && l.StartsWith('*')))
            .Select(l => l.TrimStart('*'))];
    }

    /// <summary>Прокси из вывода <c>-get…proxy</c>: строки «Enabled: Yes/No», «Server: …», «Port: …».</summary>
    public static (bool Enabled, string Server, int Port) ParseProxy(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return (ParseEnabled(output), Field(output, "Server") ?? string.Empty,
            int.TryParse(Field(output, "Port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 0);
    }

    /// <summary>Исключения из вывода <c>-getproxybypassdomains</c>: по одному в строке; «There aren't any…» — нет ни одного.</summary>
    public static IReadOnlyList<string> ParseBypass(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return [.. output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("There aren't any", StringComparison.Ordinal))];
    }

    private static bool ParseEnabled(string output) => Field(output, "Enabled") == "Yes";

    private static string? Field(string output, string name) =>
        output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(name + ":", StringComparison.Ordinal))
            .Select(l => l[(name.Length + 1)..].Trim()).FirstOrDefault();

    private static string Key(string service, string kind) => service + "\n" + kind;

    private IReadOnlyList<string> Services(bool enabledOnly) => ParseServices(networksetup(["-listallnetworkservices"]), enabledOnly);

    private static string Run(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(Program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new IOException("networksetup не запустился");
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // Об отказе networksetup пишет «** Error: …» и не всегда с кодом выхода.
            var failure = output.Split('\n').FirstOrDefault(l => l.StartsWith("** Error", StringComparison.Ordinal));
            return process.ExitCode == 0 && failure is null
                ? output
                : throw new IOException($"networksetup {arguments[0]}: " + (failure ?? error.GetAwaiter().GetResult()).Trim());
        }
        catch (Win32Exception e)
        {
            throw new IOException("networksetup не запустился: " + e.Message, e);
        }
    }
}
