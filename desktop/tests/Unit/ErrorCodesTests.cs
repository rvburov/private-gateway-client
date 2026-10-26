using System.Reflection;
using System.Text.RegularExpressions;
using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Каталог кодов ошибок: константы <see cref="ErrorCodes"/> и строки docs/architecture/errors.md — одно и то же.</summary>
public class ErrorCodesTests
{
    [Fact]
    public void Every_code_is_described_in_the_catalog_and_numbers_do_not_repeat()
    {
        var codes = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        var catalog = File.ReadAllText(Path.Combine(FindRoot(), "docs", "architecture", "errors.md"));
        var described = Regex.Matches(catalog, @"^\| `(PGC-[A-Z]+-\d{3})` \|", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();

        codes.ShouldBeUnique("номер кода не переиспользуется");
        codes.ShouldAllBe(code => Regex.IsMatch(code, @"^PGC-(SVC|NET)-\d{3}$"));
        described.ShouldBe(codes, ignoreOrder: true, "каждая константа — строка каталога, и каждая строка — константа");
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Every_code_has_words_in_both_languages(string language)
    {
        var texts = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRoot(), "shared", "specifications", "localization", language + ".json")))
            .RootElement;
        var missing = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!)
            .Where(code => !texts.TryGetProperty("error." + code, out var text) || string.IsNullOrWhiteSpace(text.GetString()));

        missing.ShouldBeEmpty("код в уведомлении — словами, а не только номером");
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
}
