using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.MacOS;

/// <summary>
/// Подробности сети macOS для инспектора: маршруты — <c>netstat -rn</c> (IPv4 и IPv6, метрик macOS не показывает), аренда
/// DHCP — <c>ipconfig getpacket</c> (сервер и срок аренды; остаток macOS не называет — срок аренды и показывается как
/// известный предел). Читать можно и без прав.
/// </summary>
/// <param name="run">Вызов программы: путь и аргументы → вывод; не вышло — <see cref="IOException"/>.</param>
public sealed class MacNetworkDetails(Func<string, IReadOnlyList<string>, string> run) : INetworkDetails
{
    private const string Netstat = "/usr/sbin/netstat";
    private const string Ipconfig = "/usr/sbin/ipconfig";

    /// <summary>netstat и ipconfig этой машины; не macOS — null.</summary>
    public static MacNetworkDetails? ForThisMachine() =>
        OperatingSystem.IsMacOS() && File.Exists(Netstat) && File.Exists(Ipconfig) ? new MacNetworkDetails(Run) : null;

    public IReadOnlyList<NetworkRoute> Routes() =>
        [.. ParseRoutes(run(Netstat, ["-rn", "-f", "inet"])), .. ParseRoutes(run(Netstat, ["-rn", "-f", "inet6"]))];

    public DhcpLease? Dhcp(string adapter)
    {
        ArgumentException.ThrowIfNullOrEmpty(adapter);
        try
        {
            return ParseLease(run(Ipconfig, ["getpacket", adapter]));
        }
        catch (IOException)
        {
            // Адрес не от DHCP — ipconfig ничего не печатает и выходит с ошибкой.
            return null;
        }
    }

    /// <summary>
    /// Маршруты из <c>netstat -rn</c>: строки из четырех и больше колонок, кроме заголовка «Destination»; шлюз «link#N» или
    /// MAC — сеть рядом, без шлюза.
    /// </summary>
    public static IEnumerable<NetworkRoute> ParseRoutes(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var line in output.Split('\n'))
        {
            var columns = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 4 || columns[0] == "Destination")
            {
                continue;
            }

            // Адрес IPv6 со зоной (fe80::1%en0) — шлюз по адресу без зоны.
            var gateway = columns[1].Split('%')[0];
            yield return new NetworkRoute(columns[0], IPAddress.TryParse(gateway, out var address) ? address : null, columns[3], null);
        }
    }

    /// <summary>Аренда из <c>ipconfig getpacket</c>: «server_identifier (ip): …» и «lease_time (uint32): 0x…»; пусто — null.</summary>
    public static DhcpLease? ParseLease(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        IPAddress? server = null;
        TimeSpan? lease = null;
        foreach (var line in output.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("server_identifier (ip): ", StringComparison.Ordinal)
                && IPAddress.TryParse(line["server_identifier (ip): ".Length..], out var address))
            {
                server = address;
            }
            else if (line.StartsWith("lease_time (uint32): 0x", StringComparison.Ordinal)
                && uint.TryParse(line["lease_time (uint32): 0x".Length..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var seconds)
                && seconds != uint.MaxValue)
            {
                lease = TimeSpan.FromSeconds(seconds);
            }
        }

        return server is null && lease is null ? null : new DhcpLease(server, lease);
    }

    private static string Run(string program, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new IOException(program + " не запустился");
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : throw new IOException($"{Path.GetFileName(program)}: " + error.GetAwaiter().GetResult().Trim());
        }
        catch (Win32Exception e)
        {
            throw new IOException(program + " не запустился: " + e.Message, e);
        }
    }
}
