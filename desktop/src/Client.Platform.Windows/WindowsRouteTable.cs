using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// Маршруты Windows: таблица IPv4 и IPv6 из <c>GetIpForwardTable2</c>, адаптеры — из
/// <see cref="NetworkInterface"/>. Туннель — по типу адаптера (туннель, PPP, виртуальный: Wintun и
/// WireGuard) или по названию драйвера (TAP OpenVPN, клиенты корпоративных VPN). Выключенные адаптеры
/// не считаются. Строка таблицы читается по смещениям 64-битной Windows (netioapi.h, MIB_IPFORWARD_ROW2).
/// </summary>
public sealed class WindowsRouteTable : IRouteTable
{
    private const ushort Unspecified = 0;
    private const int TableHeader = 8;
    private const int RowSize = 104;
    private const int RowIndex = 8;
    private const int RowPrefixLength = 40;

    // IF_TYPE_PROP_VIRTUAL: так себя называют Wintun, WireGuard и OpenVPN DCO.
    private const NetworkInterfaceType Virtual = (NetworkInterfaceType)53;

    // Драйверы VPN с типом обычной сетевой карты: TAP OpenVPN (и его копии у NordVPN, ProtonVPN), Cisco,
    // Fortinet, GlobalProtect.
    private static readonly string[] VpnDrivers = ["TAP-", "Wintun", "WireGuard", "VPN", "AnyConnect", "PANGP"];

    // Туннели самой Windows для IPv6 через IPv4 — не VPN, хотя маршрут ::/0 через Teredo бывает.
    private static readonly string[] Transition = ["Teredo", "6to4", "ISATAP"];

    public IReadOnlyList<DefaultRoute> DefaultRoutes()
    {
        var adapters = new Dictionary<int, NetworkInterface>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(a => a.OperationalStatus == OperationalStatus.Up))
        {
            foreach (var index in Indexes(adapter))
            {
                adapters[index] = adapter;
            }
        }

        var error = GetIpForwardTable2(Unspecified, out var table);
        if (error != 0)
        {
            throw new IOException($"Таблица маршрутов не прочитана: GetIpForwardTable2 0x{error:X8}");
        }

        try
        {
            var routes = new List<DefaultRoute>();
            var count = Marshal.ReadInt32(table);
            for (var i = 0; i < count; i++)
            {
                var row = table + TableHeader + (i * RowSize);

                // Весь интернет (/0) или его половина (/1) — так VPN перебивает основной маршрут.
                if (Marshal.ReadByte(row, RowPrefixLength) <= 1 && adapters.TryGetValue(Marshal.ReadInt32(row, RowIndex), out var adapter))
                {
                    routes.Add(new DefaultRoute(adapter.Name, IsTunnel(adapter.NetworkInterfaceType, adapter.Description)));
                }
            }

            return routes;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    /// <summary>Туннель ли адаптер: по типу или по названию драйвера; петля и туннели IPv6 самой Windows — нет.</summary>
    public static bool IsTunnel(NetworkInterfaceType type, string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (type == NetworkInterfaceType.Loopback || Transition.Any(t => description.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp or Virtual
            || VpnDrivers.Any(d => description.Contains(d, StringComparison.OrdinalIgnoreCase));
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
