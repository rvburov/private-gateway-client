using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace PrivateGateway.Client.Network.SingBox;

/// <summary>
/// Адреса TUN по IPv4: адрес адаптера (/30) и DNS туннеля — следующий за ним. Подсеть выбирается при запуске туннеля
/// (<see cref="Choose"/>), а не задана раз и навсегда: 172.19.0.1/30 — адрес TUN по умолчанию у самого sing-box, его держат
/// и TUN других клиентов на нем (v2rayN, Hiddify, NekoBox — в том числе адаптеры, оставшиеся после их закрытия), и сети
/// Hyper-V, WSL и Docker, которые берут случайную подсеть из 172.16.0.0/12. Занят — Windows не дает поставить адрес второй
/// раз («set ipv4 address: The object already exists»), и туннель не поднимается.
/// </summary>
/// <param name="Address">Адрес адаптера TUN.</param>
/// <param name="Dns">DNS туннеля — его sing-box прописывает ОС, запросы к нему идут в Xray.</param>
public sealed record TunSubnet(IPAddress Address, IPAddress Dns)
{
    private const int Prefix = 30;

    /// <summary>Первая из подсетей — прежняя постоянная (<see cref="SingBoxConfigBuilder.TunAddress"/>).</summary>
    public static TunSubnet Default { get; } = From("172.19.0.0");

    /// <summary>
    /// Подсети по порядку: прежняя, затем редкие — во всех трех частных диапазонах (в 10.0.0.0/8 и 172.16.0.0/12 — по две, в
    /// разных /16), чтобы одна сеть адаптера, даже /8, не заняла все. 198.18.0.0/15 (FakeDNS) и 100.64.0.0/10 (CGNAT,
    /// Tailscale) не берутся.
    /// </summary>
    public static IReadOnlyList<TunSubnet> Candidates { get; } =
        [Default, From("10.255.253.0"), From("192.168.253.252"), From("172.29.253.252"), From("10.199.199.0")];

    /// <summary>Адрес адаптера с длиной префикса — для конфига sing-box.</summary>
    public string Cidr => $"{Address}/{Prefix}";

    /// <summary>
    /// Первая подсеть, которая не пересекается ни с одной сетью машины (<paramref name="taken"/>: адрес адаптера и длина его
    /// префикса); заняты все — первая: туннель скажет, что не так, сам. Сети короче /8 (вроде маршрута по умолчанию в
    /// записи адаптера) не в счет — они «пересекаются» со всем.
    /// </summary>
    public static TunSubnet Choose(IEnumerable<(IPAddress Address, int Prefix)> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var networks = taken.Where(t => t.Address.AddressFamily == AddressFamily.InterNetwork && t.Prefix is >= 8 and <= 32).ToList();
        return Candidates.FirstOrDefault(c => !networks.Any(n => Overlaps(c.Address, Prefix, n.Address, n.Prefix))) ?? Default;
    }

    // Две сети пересекаются, если совпадают по более короткому из двух префиксов.
    private static bool Overlaps(IPAddress left, int leftPrefix, IPAddress right, int rightPrefix)
    {
        var prefix = Math.Min(leftPrefix, rightPrefix);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return (Value(left) & mask) == (Value(right) & mask);
    }

    private static uint Value(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    // Подсеть /30 по ее началу: адаптер — первый адрес, DNS — второй.
    private static TunSubnet From(string network)
    {
        var start = Value(IPAddress.Parse(network));
        return new TunSubnet(Address(start + 1), Address(start + 2));

        static IPAddress Address(uint value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            return new IPAddress(bytes);
        }
    }
}
