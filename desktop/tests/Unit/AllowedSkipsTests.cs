using System.Text.Json;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Список разрешенных пропусков CI (desktop/tests/allowed-skips.json, его читает scripts/test-report.cs): у каждой причины —
/// объяснение и ОС, и каждая встречается в коде тестов. Пропуск убрали или переписали — разрешение не остается висеть и не
/// пропускает чужой тест с похожей причиной.
/// </summary>
public class AllowedSkipsTests
{
    private static readonly string Root = FindRoot();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    [Fact]
    public void Every_allowed_skip_says_why_where_and_exists_in_the_tests()
    {
        var sources = string.Join('\n', Directory.GetFiles(Path.Combine(Root, "desktop", "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.EndsWith(nameof(AllowedSkipsTests) + ".cs", StringComparison.Ordinal))
            .Select(File.ReadAllText));

        var allowed = Load();

        allowed.ShouldNotBeEmpty();
        allowed.Select(a => a.Reason).ShouldBeUnique();
        foreach (var entry in allowed)
        {
            entry.Reason.ShouldNotBeNullOrWhiteSpace();
            entry.Why.ShouldNotBeNullOrWhiteSpace($"«{entry.Reason}»: почему пропуск законный");
            entry.Os.ShouldNotBeEmpty($"«{entry.Reason}»: на каких ОС");
            entry.Os.ShouldAllBe(os => os == "Linux" || os == "Windows" || os == "macOS", $"«{entry.Reason}»: ОС — Linux, Windows или macOS");
        }

        allowed.Select(a => a.Reason).Where(reason => !sources.Contains(reason, StringComparison.Ordinal))
            .ShouldBeEmpty("таких причин пропуска в тестах нет");
    }

    private static List<Allowance> Load() =>
        JsonSerializer.Deserialize<List<Allowance>>(File.ReadAllText(Path.Combine(Root, "desktop", "tests", "allowed-skips.json")), Json) ?? [];

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

    private sealed record Allowance(string Reason, string Why, string[] Os);
}
