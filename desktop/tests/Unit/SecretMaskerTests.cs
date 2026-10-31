using PrivateGateway.Client.Core.Logging;

namespace PrivateGateway.Client.Tests.Unit;

// Значения в тестах заведомо ненастоящие: адреса из документационных диапазонов, example.com.
public class SecretMaskerTests
{
    private const string Hidden = SecretMasker.Hidden;

    [Theory]
    [InlineData("vless://11111111-2222-3333-4444-555555555555@203.0.113.24:443?security=reality&pbk=Abc#NL")]
    [InlineData("VMESS://eyJhZGQiOiIyMDMuMC4xMTMuMjQifQ==")]
    [InlineData("trojan://password@203.0.113.24:443")]
    [InlineData("ss://YWVzLTI1Ni1nY206cGFzcw@203.0.113.24:8388")]
    [InlineData("hysteria2://pass@203.0.113.24:443?sni=example.com")]
    [InlineData("hy2://pass@203.0.113.24:443")]
    [InlineData("tuic://uuid:pass@203.0.113.24:443")]
    [InlineData("wireguard://key@203.0.113.24:51820")]
    public void Server_link_is_hidden_after_scheme(string link)
    {
        var scheme = link[..link.IndexOf("://", StringComparison.Ordinal)];
        SecretMasker.Mask($"импорт {link} готов").ShouldBe($"импорт {scheme}://{Hidden} готов");
    }

    [Fact]
    public void Subscription_url_keeps_host_and_hides_path_and_query()
    {
        SecretMasker.Mask("загрузка https://example.com/sub/AbCdEf123?token=xyz завершена")
            .ShouldBe($"загрузка https://example.com/{Hidden} завершена");
    }

    [Fact]
    public void Url_without_path_is_left_as_is()
    {
        SecretMasker.Mask("проверка https://example.com").ShouldBe("проверка https://example.com");
    }

    [Theory]
    [InlineData("https_proxy=http://user:secret@127.0.0.1:2334", "https_proxy=http://" + Hidden + "@127.0.0.1:2334")]
    [InlineData("all_proxy=socks5h://user@proxy.example.com:1080", "all_proxy=socks5h://" + Hidden + "@proxy.example.com:1080")]
    [InlineData("прокси http://127.0.0.1:2334 без имени", "прокси http://127.0.0.1:2334 без имени")]
    public void Name_and_password_in_a_proxy_address_are_hidden(string text, string masked) => SecretMasker.Mask(text).ShouldBe(masked);

    [Fact]
    public void Uuid_is_hidden()
    {
        SecretMasker.Mask("id=11111111-2222-3333-4444-55555555abcd;").ShouldBe($"id={Hidden};");
    }

    [Fact]
    public void Base64_key_is_hidden()
    {
        // Похоже на ключ WireGuard: 44 знака base64 с «=» в конце. Слово «key» рядом не ставить —
        // на него срабатывает проверка секретов в CI (gitleaks).
        SecretMasker.Mask("peer aB3dE5fG7hI9jK1lM3nO5pQ7rS9tU1vW3xY5zA7bC9d= ok")
            .ShouldBe($"peer {Hidden} ok");
    }

    [Theory]
    [InlineData("/home/user/private-gateway-client/artifacts/bin/service")]
    [InlineData("служба запущена, ядро xray 25.10.15 готово")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Ordinary_text_paths_and_hashes_are_not_touched(string text)
    {
        SecretMasker.Mask(text).ShouldBe(text);
    }

    [Fact]
    public void Null_and_empty_give_empty_string()
    {
        SecretMasker.Mask(null).ShouldBe(string.Empty);
        SecretMasker.Mask(string.Empty).ShouldBe(string.Empty);
    }
}
