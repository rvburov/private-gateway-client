using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// Подробности сети Windows для инспектора: маршруты — <c>GetIpForwardTable2</c> (IPv4 и IPv6, метрика маршрута без
/// метрики адаптера), аренда DHCP — сведения адаптера (сервер; остаток аренды .NET не дает). Строка таблицы читается по
/// смещениям 64-битной Windows (netioapi.h, MIB_IPFORWARD_ROW2), адрес — SOCKADDR_INET (ws2ipdef.h).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkDetails : INetworkDetails
{
    private const ushort Unspecified = 0;
    private const int TableHeader = 8;
    private const int RowSize = 104;
    private const int RowIndex = 8;
    private const int RowPrefix = 12;
    private const int RowPrefixLength = 40;
    private const int RowNextHop = 44;
    private const int RowMetric = 84;

    public IReadOnlyList<NetworkRoute> Routes()
    {
        var names = new Dictionary<int, string>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var index in Indexes(adapter))
            {
                names[index] = adapter.Name;
            }
        }

        var error = GetIpForwardTable2(Unspecified, out var table);
        if (error != 0)
        {
            throw new IOException($"Таблица маршрутов не прочитана: GetIpForwardTable2 0x{error:X8}");
        }

        try
        {
            var routes = new List<NetworkRoute>();
            var count = Marshal.ReadInt32(table);
            for (var i = 0; i < count; i++)
            {
                var row = table + TableHeader + (i * RowSize);
                if (!names.TryGetValue(Marshal.ReadInt32(row, RowIndex), out var name) || Address(row + RowPrefix) is not { } prefix)
                {
                    continue;
                }

                var length = Marshal.ReadByte(row, RowPrefixLength);
                var destination = length == 0 ? "default" : $"{prefix}/{length}";
                var hop = Address(row + RowNextHop);
                var gateway = hop is null || hop.Equals(IPAddress.Any) || hop.Equals(IPAddress.IPv6Any) ? null : hop;
                routes.Add(new NetworkRoute(destination, gateway, name, Marshal.ReadInt32(row, RowMetric)));
            }

            return routes;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    /// <summary>Сокеты — таблицы IP Helper с владельцами (<see cref="WindowsSockets"/>).</summary>
    public IReadOnlyList<SocketEntry> Sockets() => WindowsSockets.Read();

    public DhcpLease? Dhcp(string adapter)
    {
        try
        {
            var found = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(a => a.Name == adapter);
            var properties = found?.GetIPProperties();
            return properties?.GetIPv4Properties() is { IsDhcpEnabled: true }
                ? new DhcpLease(properties.DhcpServerAddresses.FirstOrDefault(), null)
                : null;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>Адрес из SOCKADDR_INET: семейство — первые два байта, IPv4 — со смещения 4, IPv6 — со смещения 8.</summary>
    private static IPAddress? Address(IntPtr sockaddr)
    {
        var family = (AddressFamily)Marshal.ReadInt16(sockaddr);
        var bytes = new byte[family == AddressFamily.InterNetwork ? 4 : 16];
        switch (family)
        {
            case AddressFamily.InterNetwork:
                Marshal.Copy(sockaddr + 4, bytes, 0, 4);
                return new IPAddress(bytes);
            case AddressFamily.InterNetworkV6:
                Marshal.Copy(sockaddr + 8, bytes, 0, 16);
                return new IPAddress(bytes);
            default:
                return null;
        }
    }

    private static IEnumerable<int> Indexes(NetworkInterface adapter)
    {
        var properties = adapter.GetIPProperties();
        int? v4 = null, v6 = null;
        try
        {
            v4 = properties.GetIPv4Properties()?.Index;
        }
        catch (NetworkInformationException)
        {
            // IPv4 на адаптере выключен.
        }

        try
        {
            v6 = properties.GetIPv6Properties()?.Index;
        }
        catch (NetworkInformationException)
        {
            // IPv6 на адаптере выключен.
        }

        return new[] { v4, v6 }.OfType<int>().Distinct();
    }

    // Без строк в структурах — LibraryImport не нужен.
    [DllImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void FreeMibTable(IntPtr memory);
}
