using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// Адреса TUN, которые остались на адаптерах от прошлых запусков. Windows не ставит адаптеру адрес, который уже держит
/// другой адаптер — пусть выключенный или скрытый, и sing-box выходит при запуске: «set ipv4 address: The object already
/// exists» (soak #4; у пользователя первая попытка VPN не проходила, вторая проходила). Перед запуском sing-box служба снимает
/// адреса со своего адаптера (<see cref="RemoveStale"/>: он еще не создан, а уже есть — остался
/// от прошлого) и адреса TUN с адаптеров, которые не работают; работающих чужих не трогает — их адрес TUN обходит выбором
/// подсети. Так же поступает WireGuard для Windows (cleanupAddressesOnDisconnectedInterfaces). Таблица — <c>GetUnicastIpAddressTable</c>,
/// строка читается по смещениям 64-битной Windows (netioapi.h, MIB_UNICASTIPADDRESS_ROW), адрес — SOCKADDR_INET (ws2ipdef.h).
/// </summary>
public static class WindowsTunAddresses
{
    private const ushort Unspecified = 0;
    private const ushort InterNetwork = 2;
    private const ushort InterNetworkV6 = 23;
    private const int TableHeader = 8;
    private const int RowSize = 80;
    private const int RowIpv4 = 4;
    private const int RowIpv6 = 8;
    private const int RowIndex = 40;
    private const int RowPrefixLength = 60;

    /// <summary>
    /// Снять со своего адаптера <paramref name="adapter"/> все адреса, а с неработающих адаптеров — адреса из
    /// <paramref name="addresses"/>. Возвращает снятое — «адрес/длина (адаптер)» — для журнала; снимать нечего — пусто.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> RemoveStale(string adapter, IReadOnlyCollection<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(addresses);
        var adapters = new Dictionary<int, NetworkInterface>();
        foreach (var each in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var index in Indexes(each))
            {
                adapters[index] = each;
            }
        }

        var error = GetUnicastIpAddressTable(Unspecified, out var table);
        if (error != 0)
        {
            throw new IOException($"Адреса адаптеров не прочитаны: GetUnicastIpAddressTable 0x{error:X8}");
        }

        try
        {
            var removed = new List<string>();
            var count = Marshal.ReadInt32(table);
            for (var i = 0; i < count; i++)
            {
                var row = table + TableHeader + (i * RowSize);
                if (Address(row) is not { } address)
                {
                    continue;
                }

                adapters.TryGetValue(Marshal.ReadInt32(row, RowIndex), out var owner);
                if (!IsStale(address, owner?.Name, owner?.OperationalStatus, adapter, addresses))
                {
                    continue;
                }

                var entry = $"{address}/{Marshal.ReadByte(row, RowPrefixLength)} ({owner?.Name ?? "скрытый адаптер"})";
                var deleted = DeleteUnicastIpAddressEntry(row);
                removed.Add(deleted == 0 ? entry : $"{entry} — не снят: 0x{deleted:X8}");
            }

            return removed;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    /// <summary>
    /// Снимать ли адрес <paramref name="address"/> адаптера <paramref name="owner"/> (null — адаптера нет среди видимых: скрытый,
    /// без драйвера) в состоянии <paramref name="status"/>: со своего адаптера <paramref name="adapter"/> — любой, с не
    /// работающего — адрес TUN из <paramref name="addresses"/>, с работающего чужого — никакой.
    /// </summary>
    public static bool IsStale(IPAddress address, string? owner, OperationalStatus? status, string adapter, IReadOnlyCollection<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(addresses);
        return owner == adapter || (status != OperationalStatus.Up && addresses.Contains(address));
    }

    private static IPAddress? Address(IntPtr row)
    {
        var family = (ushort)Marshal.ReadInt16(row);
        if (family is not (InterNetwork or InterNetworkV6))
        {
            return null;
        }

        var bytes = new byte[family == InterNetworkV6 ? 16 : 4];
        Marshal.Copy(row + (family == InterNetwork ? RowIpv4 : RowIpv6), bytes, 0, bytes.Length);
        var address = new IPAddress(bytes);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal ? null : address;
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
    private static extern uint GetUnicastIpAddressTable(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint DeleteUnicastIpAddressEntry(IntPtr row);

    [DllImport("iphlpapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void FreeMibTable(IntPtr memory);
}
