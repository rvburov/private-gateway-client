using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using PrivateGateway.Client.Core.Countries;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class CountryDatabaseTests
{
    [Theory]
    [InlineData("192.0.2.0", null)]
    [InlineData("192.0.2.1", "JP")]
    [InlineData("192.0.2.9", "JP")]
    [InlineData("192.0.2.10", null)]
    [InlineData("198.51.100.1", "NL")]
    [InlineData("198.51.100.255", "NL")]
    [InlineData("203.0.113.1", null)]
    [InlineData("::ffff:192.0.2.1", "JP")]
    [InlineData("2001:db8::1", "DE")]
    [InlineData("2001:db8::ffff", "DE")]
    [InlineData("2001:db8::1:0", null)]
    [InlineData("2001:db8::1%3", null)]
    public void Finds_only_the_containing_range_including_both_edges(string ip, string? country)
    {
        using var input = Ranges();
        var database = new CountryDatabase(input);
        database.Count.ShouldBe(3);
        database.Find(IPAddress.Parse(ip)).ShouldBe(country);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("example.com")]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("192.0.2.1")]
    [InlineData("198.51.100.10")]
    [InlineData("203.0.113.1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:192.0.2.1")]
    [InlineData("fe80::1%4")]
    [InlineData("fc00::1")]
    [InlineData("::1")]
    [InlineData("ff02::1")]
    public void Bundled_database_never_assigns_a_country_to_special_addresses(string? value) => CountryDatabase.Lookup(value).ShouldBeNull();

    [Fact]
    public void Shipped_database_is_valid_and_has_expected_scale()
    {
        using var resource = typeof(CountryDatabase).Assembly.GetManifestResourceStream("PrivateGateway.Countries.Database").ShouldNotBeNull();
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        var database = new CountryDatabase(gzip);
        database.Count.ShouldBeGreaterThan(600_000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(44)]
    public void Rejects_corrupt_format_count_or_country(int offset)
    {
        using var input = Ranges();
        var bytes = input.ToArray();
        bytes[offset] = 255;
        using var corrupted = new MemoryStream(bytes);
        Should.Throw<InvalidDataException>(() => new CountryDatabase(corrupted));
    }

    [Fact]
    public void Rejects_overlapping_ranges_and_truncation()
    {
        using var input = Ranges();
        var bytes = input.ToArray();
        Array.Copy(bytes, 12, bytes, 12 + 34, 32);
        using var overlapping = new MemoryStream(bytes);
        Should.Throw<InvalidDataException>(() => new CountryDatabase(overlapping));
        using var truncated = new MemoryStream(bytes[..^1]);
        Should.Throw<EndOfStreamException>(() => new CountryDatabase(truncated));
    }

    private static MemoryStream Ranges()
    {
        var stream = new MemoryStream();
        stream.Write("PGCTRY1\0"u8);
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(count, 3);
        stream.Write(count);
        foreach (var (start, end, code) in new[] { ("192.0.2.1", "192.0.2.9", "JP"), ("198.51.100.1", "198.51.100.255", "NL"), ("2001:db8::1", "2001:db8::ffff", "DE") })
        {
            stream.Write(IPAddress.Parse(start).MapToIPv6().GetAddressBytes());
            stream.Write(IPAddress.Parse(end).MapToIPv6().GetAddressBytes());
            stream.WriteByte((byte)code[0]);
            stream.WriteByte((byte)code[1]);
        }

        stream.Position = 0;
        return stream;
    }
}
