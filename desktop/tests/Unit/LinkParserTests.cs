using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Разбор ссылок и подписок на эталонах shared/testdata — общих для десктопа, Android и iOS.
/// Результат переводится в тот же JSON-вид, что в .expected.json: пустые и ложные поля не пишутся.
/// </summary>
public class LinkParserTests
{
    private static readonly string TestData = Path.Combine(FindRoot(), "shared", "testdata");

    public static TheoryData<string> Links() => Cases("links");

    public static TheoryData<string> Subscriptions() => Cases("subscriptions");

    [Theory]
    [MemberData(nameof(Links))]
    public void Link_parses_as_in_the_shared_fixture(string fixture)
    {
        var (input, expected) = Load(fixture);

        var actual = ToJson(LinkParser.Parse(input));

        JsonNode.DeepEquals(actual, expected).ShouldBeTrue($"{fixture}: получено {actual.ToJsonString(Indented)}");
    }

    [Theory]
    [MemberData(nameof(Subscriptions))]
    public void Subscription_parses_as_in_the_shared_fixture(string fixture)
    {
        var (input, expected) = Load(fixture);

        var batch = ImportParser.Parse(input);
        var actual = new JsonObject
        {
            ["format"] = batch.Format,
            ["lines"] = new JsonArray([.. batch.Lines.Select(l => (JsonNode)(l.Result.Server is { } s
                ? new JsonObject { ["line"] = l.Number, ["protocol"] = Name(s.Protocol), ["name"] = s.Name }
                : new JsonObject { ["line"] = l.Number, ["error"] = l.Result.Error }))]),
        };

        JsonNode.DeepEquals(actual, expected).ShouldBeTrue($"{fixture}: получено {actual.ToJsonString(Indented)}");
    }

    [Fact]
    public void Every_protocol_and_error_code_has_a_fixture()
    {
        var expected = Directory.EnumerateFiles(Path.Combine(TestData, "links"), "*.expected.json", SearchOption.AllDirectories)
            .Select(f => JsonNode.Parse(File.ReadAllText(f))!)
            .ToList();

        var protocols = expected.Select(e => (string?)e["protocol"]).Where(p => p is not null).ToHashSet();
        protocols.ShouldBe(Enum.GetValues<ProxyProtocol>().Select(Name), ignoreOrder: true);
        var errors = expected.Select(e => (string?)e["error"]).Where(e => e is not null).ToHashSet();
        errors.ShouldBe(
            typeof(ImportErrors).GetFields().Select(f => (string)f.GetValue(null)!),
            ignoreOrder: true);
    }

    [Fact]
    public void Credentials_never_show_up_in_text()
    {
        var server = LinkParser.Parse("trojan://pw-very-secret@example.com:443#X").Server!;

        server.ToString().ShouldNotContain("pw-very-secret");
        server.Credentials.ToString().ShouldBe("ServerCredentials { *** }");
    }

    [Theory]
    [InlineData("🇳🇱 Нидерланды 01", "NL", "Нидерланды 01")]
    [InlineData("🇺🇸", "US", "")]
    [InlineData("Нидерланды 🇳🇱", "", "Нидерланды 🇳🇱")]
    [InlineData("  Без флага ", "", "Без флага")]
    public void Leading_flag_becomes_country_code(string name, string country, string rest)
    {
        CountryFlag.Split(name).ShouldBe((country, rest));
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static TheoryData<string> Cases(string folder)
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(TestData, folder), "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(TestData, file).Replace('\\', '/')[..^".txt".Length]);
        }

        return data;
    }

    private static (string Input, JsonNode Expected) Load(string fixture)
    {
        var path = Path.Combine(TestData, fixture);
        return (File.ReadAllText(path + ".txt"), JsonNode.Parse(File.ReadAllText(path + ".expected.json"))!);
    }

    private static JsonObject ToJson(LinkResult result)
    {
        if (result.Server is not { } s)
        {
            return new JsonObject { ["error"] = result.Error };
        }

        var json = new JsonObject { ["name"] = s.Name };
        Put(json, "country", s.CountryCode);
        json["protocol"] = Name(s.Protocol);
        json["host"] = s.Host;
        json["port"] = s.Port;

        var transport = new JsonObject { ["kind"] = s.Transport.Kind switch
        {
            TransportKind.WebSocket => "ws",
            _ => s.Transport.Kind.ToString().ToLowerInvariant(),
        } };
        Put(transport, "path", s.Transport.Path);
        Put(transport, "host", s.Transport.Host);
        Put(transport, "serviceName", s.Transport.ServiceName);
        json["transport"] = transport;

        var security = new JsonObject { ["kind"] = s.Security.Kind.ToString().ToLowerInvariant() };
        Put(security, "sni", s.Security.Sni);
        Put(security, "fingerprint", s.Security.Fingerprint);
        Put(security, "alpn", s.Security.Alpn);
        Put(security, "allowInsecure", s.Security.AllowInsecure);
        Put(security, "realityPublicKey", s.Security.RealityPublicKey);
        Put(security, "realityShortId", s.Security.RealityShortId);
        Put(security, "realitySpiderX", s.Security.RealitySpiderX);
        json["security"] = security;

        var options = new JsonObject();
        Put(options, "flow", s.Options.Flow);
        Put(options, "cipher", s.Options.Cipher);
        Put(options, "obfs", s.Options.Obfs);
        Put(options, "congestionControl", s.Options.CongestionControl);
        Put(options, "peerPublicKey", s.Options.PeerPublicKey);
        Put(options, "addresses", s.Options.Addresses);
        if (s.Options.Mtu is { } mtu)
        {
            options["mtu"] = mtu;
        }

        if (options.Count > 0)
        {
            json["options"] = options;
        }

        var credentials = new JsonObject();
        Put(credentials, "uuid", s.Credentials.Uuid);
        Put(credentials, "password", s.Credentials.Password);
        Put(credentials, "obfsPassword", s.Credentials.ObfsPassword);
        Put(credentials, "privateKey", s.Credentials.PrivateKey);
        Put(credentials, "presharedKey", s.Credentials.PresharedKey);
        json["credentials"] = credentials;
        return json;
    }

    private static void Put(JsonObject json, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            json[key] = value;
        }
    }

    private static void Put(JsonObject json, string key, bool value)
    {
        if (value)
        {
            json[key] = true;
        }
    }

    private static void Put(JsonObject json, string key, IReadOnlyList<string>? values)
    {
        if (values is { Count: > 0 })
        {
            json[key] = new JsonArray([.. values.Select(v => (JsonNode)v)]);
        }
    }

    private static string Name(ProxyProtocol protocol) => protocol.ToString().ToLowerInvariant();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PrivateGateway.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Не найден корень репозитория (PrivateGateway.slnx)");
    }
}
