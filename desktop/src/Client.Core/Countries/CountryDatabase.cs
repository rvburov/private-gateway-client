using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;

namespace PrivateGateway.Client.Core.Countries;

/// <summary>Локальная оценка страны IP. Нет записи — нет флага; названия серверов и подписок не используются.</summary>
public sealed class CountryDatabase
{
    private const int HeaderSize = 12;
    private const int RecordSize = 34;
    private const int MaxRecords = 2_000_000;
    private readonly byte[] _data;
    private readonly int _count;
    private static readonly Lazy<CountryDatabase?> Bundled = new(LoadBundled);
    private static readonly IPNetwork[] Special4 =
    [
        .. new[] { "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12",
            "192.0.0.0/24", "192.0.2.0/24", "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3" }
            .Select(IPNetwork.Parse),
    ];
    private static readonly IPNetwork Global6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] Special6 = [IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("3fff::/20")];

    /// <summary>Читает проверяемый формат PGCTRY1: заголовок, число записей, начало/конец (16+16 байт) и ISO-код (2).</summary>
    public CountryDatabase(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Span<byte> header = stackalloc byte[HeaderSize];
        input.ReadExactly(header);
        if (!header[..8].SequenceEqual("PGCTRY1\0"u8))
        {
            throw new InvalidDataException("Неизвестный формат геобазы");
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        if (count > MaxRecords)
        {
            throw new InvalidDataException("Геобаза слишком велика");
        }

        _count = (int)count;
        _data = new byte[_count * RecordSize];
        input.ReadExactly(_data);
        if (input.ReadByte() != -1)
        {
            throw new InvalidDataException("Лишние данные геобазы");
        }

        for (var i = 0; i < _count; i++)
        {
            var row = _data.AsSpan(i * RecordSize, RecordSize);
            if (row[..16].SequenceCompareTo(row.Slice(16, 16)) > 0
                || row[32] is < (byte)'A' or > (byte)'Z' || row[33] is < (byte)'A' or > (byte)'Z'
                || (i > 0 && _data.AsSpan((i - 1) * RecordSize + 16, 16).SequenceCompareTo(row[..16]) >= 0))
            {
                throw new InvalidDataException("Некорректный диапазон геобазы");
            }
        }
    }

    public int Count => _count;

    /// <summary>Поиск в диапазонах без сети. Используется также для тестовых баз на адресах документации.</summary>
    public string? Find(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            return null;
        }

        Span<byte> bytes = stackalloc byte[16];
        address.MapToIPv6().TryWriteBytes(bytes, out _);
        var low = 0;
        var high = _count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var row = _data.AsSpan(middle * RecordSize, RecordSize);
            if (bytes.SequenceCompareTo(row[..16]) < 0)
            {
                high = middle - 1;
            }
            else if (bytes.SequenceCompareTo(row.Slice(16, 16)) > 0)
            {
                low = middle + 1;
            }
            else
            {
                return string.Create(2, (row[32], row[33]), (span, code) => { span[0] = (char)code.Item1; span[1] = (char)code.Item2; });
            }
        }

        return null;
    }

    /// <summary>Встроенная база: частные, служебные и демонстрационные адреса всегда без страны.</summary>
    public static string? Lookup(string? value) => IPAddress.TryParse(value, out var address) ? Lookup(address) : null;

    public static string? Lookup(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        var publicAddress = ip.AddressFamily == AddressFamily.InterNetwork
            ? !Special4.Any(n => n.Contains(ip))
            : ip.ScopeId == 0 && Global6.Contains(ip) && !Special6.Any(n => n.Contains(ip));
        return publicAddress ? Bundled.Value?.Find(ip) : null;
    }

    private static CountryDatabase? LoadBundled()
    {
        try
        {
            using var input = typeof(CountryDatabase).Assembly.GetManifestResourceStream("PrivateGateway.Countries.Database");
            if (input is null)
            {
                return null;
            }

            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            return new CountryDatabase(gzip);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Поврежденная геобаза не должна мешать запуску VPN: неизвестный адрес получает Землю.
            return null;
        }
    }
}
