using System.Text;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Конфиги sing-box и Clash сверх общих эталонов: битый и враждебный текст. Подписка приходит из
/// сети — ни вложенность, ни ссылки-«бомбы» YAML не должны уронить программу или занять память.
/// </summary>
public sealed class ConfigImportTests
{
    private const string Vless = """{ "type": "vless", "tag": "A", "server": "192.0.2.1", "server_port": 443, "uuid": "11111111-2222-3333-4444-555555555555" }""";

    [Theory]
    [InlineData("{\"outbounds\":[],\"outbounds\":[]}")]
    [InlineData("{\"outbounds\":[{\"type\":\"vless\",\"type\":\"trojan\"}]}")]
    [InlineData("{\"outbounds\":[{\"type\":\"trojan\",\"tls\":{\"enabled\":true,\"enabled\":false}}]}")]
    [InlineData("{\"outbounds\":[{\"type\":\"trojan\",\"tls\":{\"enabled\":true,\"\\u0065nabled\":false}}]}")]
    public void Duplicate_json_properties_are_a_regular_import_error(string text)
    {
        var batch = ImportParser.Parse(text);

        batch.Lines.ShouldHaveSingleItem().Result.Error.ShouldBe(ImportErrors.BadEncoding);
    }

    [Fact]
    public void Broken_json_is_one_error_on_its_line()
    {
        var batch = ImportParser.Parse("{\n  \"outbounds\": [\n    { \"type\": \"vless\" \n    \"tag\": \"A\" }\n  ]\n}");

        batch.Format.ShouldBe(ImportParser.FormatSingBox);
        batch.Lines.ShouldHaveSingleItem().ShouldBe(new ImportLine(4, new LinkResult(null, ImportErrors.BadEncoding)));
    }

    [Theory]
    [InlineData("proxies:\n  - name: A\n    type: vless\n   bad: x\n", 4)]
    [InlineData("proxies:\n  - name: A\n    port: *nope\n", 3)]
    [InlineData("proxies:\n  - {name: A, type: vless\n  - name: B\n", 2)]
    public void Broken_yaml_is_one_error_on_its_line(string text, int line)
    {
        var batch = ImportParser.Parse(text);

        batch.Format.ShouldBe(ImportParser.FormatClash);
        batch.Lines.ShouldHaveSingleItem().ShouldBe(new ImportLine(line, new LinkResult(null, ImportErrors.BadEncoding)));
    }

    [Fact]
    public void Plain_array_of_outbounds_and_bom_are_understood()
    {
        var batch = ImportParser.Parse("﻿[\n" + Vless + "\n]");

        batch.Format.ShouldBe(ImportParser.FormatSingBox);
        var line = batch.Lines.ShouldHaveSingleItem();
        (line.Number, line.Result.Server?.Protocol).ShouldBe((2, ProxyProtocol.Vless));
    }

    [Fact]
    public void Lines_go_in_text_order_even_when_endpoints_come_first()
    {
        var batch = ImportParser.Parse("{\n\"endpoints\": [{ \"type\": \"socks\" }],\n\"outbounds\": [\n" + Vless + "\n]}");

        batch.Lines.Select(l => l.Number).ShouldBe([2, 4]);
    }

    [Fact]
    public void Hysteria2_and_tuic_keep_tls_fields_without_enabled_flag()
    {
        var batch = ImportParser.Parse("""
            [
              { "type": "hysteria2", "server": "192.0.2.1", "server_port": 443, "password": "x", "tls": { "server_name": "hy.example.com" } },
              { "type": "tuic", "server": "192.0.2.1", "server_port": 443, "uuid": "11111111-2222-3333-4444-555555555555", "password": "x",
                "tls": { "server_name": "tuic.example.com" } }
            ]
            """);

        batch.Lines.Select(l => l.Result.Server?.Security.Sni).ShouldBe(["hy.example.com", "tuic.example.com"]);
    }

    [Fact]
    public void Deep_json_is_refused_not_overflowed()
    {
        var batch = ImportParser.Parse("{\"outbounds\": " + new string('[', 10_000) + new string(']', 10_000) + "}");

        batch.Lines.ShouldHaveSingleItem().Result.Error.ShouldBe(ImportErrors.BadEncoding);
    }

    [Fact]
    public void Deep_yaml_is_refused_not_overflowed()
    {
        var batch = ImportParser.Parse("proxies: " + new string('[', 10_000) + new string(']', 10_000) + "\n");

        batch.Lines.ShouldHaveSingleItem().Result.Error.ShouldBe(ImportErrors.BadEncoding);
    }

    [Fact]
    public void Yaml_alias_bomb_is_refused_before_it_takes_memory()
    {
        // Каждый уровень — десять ссылок на предыдущий: 10⁹ узлов, если копировать без счета.
        var text = new StringBuilder("a0: &a0 [x, x, x, x, x, x, x, x, x, x]\n");
        for (var level = 1; level <= 9; level++)
        {
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"a{level}: &a{level} [")
                .Append(string.Join(", ", Enumerable.Repeat($"*a{level - 1}", 10))).Append("]\n");
        }

        text.Append("proxies: *a9\n");
        var before = GC.GetTotalAllocatedBytes();

        var batch = ImportParser.Parse(text.ToString());

        batch.Lines.ShouldHaveSingleItem().Result.Error.ShouldBe(ImportErrors.BadEncoding);
        (GC.GetTotalAllocatedBytes() - before).ShouldBeLessThan(500L * 1024 * 1024);
    }

    [Fact]
    public void Yaml_merge_keeps_explicit_keys()
    {
        var batch = ImportParser.Parse("""
            base: &base {type: trojan, port: 1, sni: from-base.example.com}
            proxies:
              - {<<: *base, name: A, server: 192.0.2.1, port: 443, password: x}
            """);

        var server = batch.Lines.ShouldHaveSingleItem().Result.Server.ShouldNotBeNull();
        (server.Protocol, server.Port, server.Security.Sni).ShouldBe((ProxyProtocol.Trojan, 443, "from-base.example.com"));
    }

    [Fact]
    public void Yaml_tilde_and_null_are_missing_values_but_quoted_ones_are_text()
    {
        var batch = ImportParser.Parse("""
            proxies:
              - {name: A, type: trojan, server: 192.0.2.1, port: 443, password: x, sni: ~, client-fingerprint: null}
              - {name: B, type: trojan, server: 192.0.2.1, port: 443, password: "~"}
            """);

        var first = batch.Lines[0].Result.Server.ShouldNotBeNull();
        (first.Security.Sni, first.Security.Fingerprint).ShouldBe((null, null));
        batch.Lines[1].Result.Server.ShouldNotBeNull().Credentials.Password.ShouldBe("~");
    }

    [Fact]
    public void Links_with_a_word_proxies_stay_links()
    {
        var batch = ImportParser.Parse("trojan://x@192.0.2.1:443#proxies: test");

        batch.Format.ShouldBe(ImportParser.FormatLinks);
    }

    [Fact]
    public void Config_without_servers_finds_nothing()
    {
        ImportParser.Parse("""{ "outbounds": [{ "type": "direct" }] }""").Lines.ShouldBeEmpty();
        ImportParser.Parse("proxies:\nrules: []\n").Lines.ShouldBeEmpty();
        ImportParser.Parse("[]").Lines.ShouldBeEmpty();
    }
}
