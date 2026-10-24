using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.Tests.UI;

public partial class LocalizationTests
{
    private static IReadOnlyDictionary<string, string> Ru => Loc.Instance.Dictionary("ru");

    private static IReadOnlyDictionary<string, string> En => Loc.Instance.Dictionary("en");

    [Fact]
    public void Both_languages_have_the_same_keys_and_no_empty_texts()
    {
        Ru.Keys.Order().ShouldBe(En.Keys.Order());
        Ru.Where(p => string.IsNullOrWhiteSpace(p.Value)).ShouldBeEmpty();
        En.Where(p => string.IsNullOrWhiteSpace(p.Value)).ShouldBeEmpty();
    }

    [Fact]
    public void Russian_texts_have_no_yo_letter()
    {
        Ru.Where(p => p.Value.Contains('ё', StringComparison.Ordinal) || p.Value.Contains('Ё', StringComparison.Ordinal))
            .Select(p => p.Key)
            .ShouldBeEmpty("AGENTS.md: буквы «е с точками» нет нигде в текстах");
    }

    [Fact]
    public void Every_key_used_in_markup_and_code_exists()
    {
        var used = Directory.EnumerateFiles(Repo.UiSource, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".axaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .SelectMany(f => MarkupKey().Matches(File.ReadAllText(f)).Concat(CodeKey().Matches(File.ReadAllText(f))))
            .Select(m => m.Groups["key"].Value)
            .Distinct()
            .ToArray();

        used.Length.ShouldBeGreaterThan(20);
        used.Where(k => !Ru.ContainsKey(k)).ShouldBeEmpty("ключи без текста в словаре");
    }

    [Fact]
    public void Every_state_has_title_subtitle_power_label_and_check_text()
    {
        foreach (var state in Enum.GetValues<ConnectionState>().Select(s => s.ToString().ToLowerInvariant()))
        {
            Ru.ShouldContainKey($"status.{state}.title");
            Ru.ShouldContainKey($"status.{state}.sub");
            Ru.ShouldContainKey($"power.label.{state}");
            if (state != "connected")
            {
                Ru.ShouldContainKey($"checked.{state}");
            }
        }

        foreach (var check in new[] { "all", "dns", "ipv6", "ks" })
        {
            foreach (var value in Enum.GetValues<CheckState>().Select(s => s.ToString().ToLowerInvariant()))
            {
                Ru.ShouldContainKey($"check.{check}.{value}");
            }
        }
    }

    [Theory]
    [InlineData(1, "Добавить 1 сервер")]
    [InlineData(2, "Добавить 2 сервера")]
    [InlineData(4, "Добавить 4 сервера")]
    [InlineData(5, "Добавить 5 серверов")]
    [InlineData(11, "Добавить 11 серверов")]
    [InlineData(12, "Добавить 12 серверов")]
    [InlineData(14, "Добавить 14 серверов")]
    [InlineData(21, "Добавить 21 сервер")]
    [InlineData(22, "Добавить 22 сервера")]
    [InlineData(111, "Добавить 111 серверов")]
    [InlineData(0, "Добавить 0 серверов")]
    public void Russian_plural_follows_the_last_digits(int count, string expected)
    {
        Loc.Instance.Plural("import.add", count).ShouldBe(expected);
    }

    // Смена языка доходит до окон из других тестов — только в потоке Avalonia.
    [AvaloniaFact]
    public void English_plural_has_one_and_other()
    {
        try
        {
            Loc.Instance.SetLanguage("en");
            Loc.Instance.Plural("import.add", 1).ShouldBe("Add 1 server");
            Loc.Instance.Plural("import.add", 2).ShouldBe("Add 2 servers");
            Loc.Instance.Plural("import.add", 21).ShouldBe("Add 21 servers");
        }
        finally
        {
            Loc.Instance.SetLanguage("ru");
        }
    }

    [Fact]
    public void Every_plural_key_has_all_three_forms()
    {
        var bases = Ru.Keys.Where(k => k.EndsWith(".one", StringComparison.Ordinal)).Select(k => k[..^4]).ToList();

        bases.ShouldNotBeEmpty();
        bases.ShouldAllBe(b => Ru.ContainsKey(b + ".few") && Ru.ContainsKey(b + ".many"));
    }

    // Ключ целиком; склейки вида "power.label." + состояние проверяет тест по состояниям.
    [GeneratedRegex(@"\{l:L (?<key>[a-zA-Z0-9.]*[a-zA-Z0-9])[,}]")]
    private static partial Regex MarkupKey();

    [GeneratedRegex(@"Loc\.Instance(?:\.Format\(|\[)""(?<key>[a-zA-Z0-9.]*[a-zA-Z0-9])""")]
    private static partial Regex CodeKey();
}
