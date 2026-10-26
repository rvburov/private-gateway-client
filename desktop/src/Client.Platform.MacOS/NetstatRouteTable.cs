using System.ComponentModel;
using System.Diagnostics;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.MacOS;

/// <summary>
/// Маршруты macOS через <c>netstat -rn</c> (IPv4 и IPv6): маршрут ко всему интернету — default или половины
/// (0/1 и 128.0/1, ::/1 и 8000::/1). Маршруты, привязанные к адаптеру (флаг I: штатные utun0–utun3 iCloud и
/// Continuity держат такой default для IPv6), трафик машины не забирают и не считаются. Туннель — по имени
/// адаптера: utun, ppp, ipsec, tun, tap, gif. Читать маршруты можно и без прав.
/// </summary>
/// <param name="netstat">Вызов netstat: аргументы → вывод; не вышло — <see cref="IOException"/>.</param>
public sealed class NetstatRouteTable(Func<IReadOnlyList<string>, string> netstat) : IRouteTable
{
    private const string Program = "/usr/sbin/netstat";

    private static readonly string[] Destinations = ["default", "0/1", "128.0/1", "::/1", "8000::/1"];
    private static readonly string[] Tunnels = ["utun", "ppp", "ipsec", "tun", "tap", "gif"];

    /// <summary>netstat этой машины; не macOS — null.</summary>
    public static NetstatRouteTable? ForThisMachine() =>
        OperatingSystem.IsMacOS() && File.Exists(Program) ? new NetstatRouteTable(Run) : null;

    public IReadOnlyList<DefaultRoute> DefaultRoutes() =>
        [.. Parse(netstat(["-rn", "-f", "inet"])).Concat(Parse(netstat(["-rn", "-f", "inet6"])))];

    /// <summary>Маршруты ко всему интернету из вывода <c>netstat -rn</c>: колонки Destination, Gateway, Flags, Netif.</summary>
    public static IEnumerable<DefaultRoute> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var line in output.Split('\n'))
        {
            var columns = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 4 || !Destinations.Contains(columns[0]) || !columns[2].Contains('U', StringComparison.Ordinal)
                || columns[2].Contains('I', StringComparison.Ordinal))
            {
                continue;
            }

            var adapter = columns[3];
            yield return new DefaultRoute(adapter, Tunnels.Any(t => adapter.StartsWith(t, StringComparison.Ordinal) && adapter.Length > t.Length
                && char.IsAsciiDigit(adapter[t.Length])));
        }
    }

    private static string Run(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(Program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new IOException("netstat не запустился");
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : throw new IOException("netstat: " + error.GetAwaiter().GetResult().Trim());
        }
        catch (Win32Exception e)
        {
            throw new IOException("netstat не запустился: " + e.Message, e);
        }
    }
}
