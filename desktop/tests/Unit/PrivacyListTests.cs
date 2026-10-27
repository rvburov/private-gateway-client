using System.Text.RegularExpressions;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// PRIVACY.md — полный список того, куда программа обращается сама: каждый адрес из кода десктопа и Android (ссылки и узлы
/// STUN) там назван. Новый адрес в коде без строки в PRIVACY.md роняет тест, а не тихо расширяет то, что знает о
/// пользователе кто-то еще.
/// </summary>
public partial class PrivacyListTests
{
    // Не обращения программы: примеры и подсказки в тексте (домены и адреса документации), петля, DTD в заголовке plist macOS
    // (не загружается).
    private static readonly string[] NotContacted = ["example.com", "example.org", "localhost", "www.apple.com", "IP"];

    [Fact]
    public void Every_address_the_code_contacts_is_named_in_privacy_md()
    {
        var root = FindRoot();
        var privacy = File.ReadAllText(Path.Combine(root, "PRIVACY.md"));
        var hosts = Sources(root)
            .SelectMany(text => Url().Matches(text).Select(m => m.Groups["host"].Value).Concat(Stun().Matches(text).Select(m => m.Groups["host"].Value)))
            .Where(host => !NotContacted.Contains(host, StringComparer.OrdinalIgnoreCase) && !host.StartsWith("[2001:db8:", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        hosts.ShouldContain("cp.cloudflare.com", "сам поиск адресов работает");
        hosts.ShouldContain("stun.cloudflare.com");
        hosts.Where(host => !privacy.Contains(host, StringComparison.OrdinalIgnoreCase)).ShouldBeEmpty("адреса из кода, которых нет в PRIVACY.md");
    }

    private static IEnumerable<string> Sources(string root)
    {
        var folders = new[] { Path.Combine(root, "desktop", "src"), Path.Combine(root, "android", "core", "src", "main"), Path.Combine(root, "android", "app", "src", "main") };
        return folders.Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".kt", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}build{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText);
    }

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

    [GeneratedRegex(@"https?://(?<host>\[[0-9A-Fa-f:]+\]|[A-Za-z0-9.-]+)")]
    private static partial Regex Url();

    // Узел STUN: «DnsEndPoint Server = new("stun…", 3478)» или «new DnsEndPoint("stun…", 3478)».
    [GeneratedRegex(@"DnsEndPoint[^""\n]*?\(""(?<host>[A-Za-z0-9.-]+)""")]
    private static partial Regex Stun();
}
