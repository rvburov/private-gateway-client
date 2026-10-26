// Отчет о прогоне тестов: сколько прошло, упало и пропущено — и каждый пропуск с причиной.
//
// Запуск из корня репозитория (файловое приложение .NET 10, отдельного проекта нет):
//   dotnet test ... --results-directory artifacts/test-results --report-ctrf
//   dotnet run scripts/test-report.cs artifacts/test-results
//
// Читает все отчеты CTRF (*.ctrf) из папки — по одному на проект и запуск. Один тест в нескольких запусках (на Linux
// тесты сетевых пространств пропускаются без root и идут вторым запуском от root) — один итог: упал хоть раз — упал,
// иначе прошел хоть раз — прошел, иначе пропущен.
//
// Пропуск без разрешения — ошибка (код 1): разрешенные причины для ОС, на которой идет отчет, — в
// desktop/tests/allowed-skips.json, у каждой сказано почему. Так тест, который тихо перестал идти в CI (нет ядра, нет
// прав, шаг выпал из workflow), роняет проверку, а не прячется в счетчике skipped.
//
// Итог пишется в вывод и, если задана GITHUB_STEP_SUMMARY, в сводку задания GitHub Actions.

using System.Text.Json;

if (args.Length != 1)
{
    Console.Error.WriteLine("Использование: dotnet run scripts/test-report.cs <папка с отчетами CTRF>");
    return 2;
}

var os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
var allowed = Allowed(Path.Combine(FindRoot(), "desktop", "tests", "allowed-skips.json"), os);
var files = Directory.Exists(args[0]) ? Directory.GetFiles(args[0], "*.ctrf", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList() : [];
if (files.Count == 0)
{
    Console.Error.WriteLine($"В {args[0]} нет отчетов CTRF: тесты не запускались с --report-ctrf или шаг выпал.");
    return 1;
}

// Тест → итог по всем запускам.
var tests = new Dictionary<string, Result>(StringComparer.Ordinal);
foreach (var file in files)
{
    using var json = JsonDocument.Parse(File.ReadAllBytes(file));
    var results = json.RootElement.GetProperty("results");
    var projects = results.GetProperty("extra").GetProperty("suites").EnumerateArray()
        .ToDictionary(s => s.GetProperty("id").GetString()!, s => Path.GetFileNameWithoutExtension(s.GetProperty("filePath").GetString()!));
    foreach (var test in results.GetProperty("tests").EnumerateArray())
    {
        var name = test.GetProperty("name").GetString()!;
        var status = test.GetProperty("status").GetString() switch
        {
            "passed" => Status.Passed,
            "skipped" => Status.Skipped,
            _ => Status.Failed,
        };
        var reason = test.TryGetProperty("extra", out var extra) && extra.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;
        var project = projects.GetValueOrDefault(test.GetProperty("suite").GetString() ?? string.Empty, "?");
        tests[name] = tests.TryGetValue(name, out var before) && before.Status > status ? before : new Result(project, status, reason);
    }
}

var report = new List<string> { $"## Тесты — {os}", string.Empty, "| Проект | Всего | Прошло | Упало | Пропущено |", "| --- | ---: | ---: | ---: | ---: |" };
foreach (var project in tests.Values.GroupBy(t => t.Project).OrderBy(g => g.Key, StringComparer.Ordinal))
{
    report.Add($"| {project.Key.Replace("PrivateGateway.Client.Tests.", string.Empty, StringComparison.Ordinal)} | {project.Count()} "
        + $"| {project.Count(t => t.Status == Status.Passed)} | {project.Count(t => t.Status == Status.Failed)} | {project.Count(t => t.Status == Status.Skipped)} |");
}

report.Add($"| **Итого** | **{tests.Count}** | **{tests.Values.Count(t => t.Status == Status.Passed)}** "
    + $"| **{tests.Values.Count(t => t.Status == Status.Failed)}** | **{tests.Values.Count(t => t.Status == Status.Skipped)}** |");
report.Add(string.Empty);

var failed = tests.Where(t => t.Value.Status == Status.Failed).Select(t => t.Key).Order(StringComparer.Ordinal).ToList();
if (failed.Count > 0)
{
    report.Add($"### Упало — {failed.Count}");
    report.Add(string.Empty);
    report.AddRange(failed.Select(name => $"- `{name}`"));
    report.Add(string.Empty);
}

var forbidden = 0;
var skipped = tests.Where(t => t.Value.Status == Status.Skipped).GroupBy(t => t.Value.Reason).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
if (skipped.Count > 0)
{
    report.Add($"### Пропущено — {skipped.Sum(g => g.Count())}, по причинам");
    report.Add(string.Empty);
    foreach (var group in skipped)
    {
        var why = allowed.FirstOrDefault(a => group.Key.StartsWith(a.Reason, StringComparison.Ordinal)).Why;
        forbidden += why is null ? group.Count() : 0;
        report.Add($"- **{(group.Key.Length > 0 ? group.Key : "без причины")}** — {group.Count()}: "
            + (why is null ? $"**не разрешено на {os}** (desktop/tests/allowed-skips.json)" : why));
        report.AddRange(group.Select(t => t.Key).Order(StringComparer.Ordinal).Select(name => $"  - `{name}`"));
    }

    report.Add(string.Empty);
}

if (forbidden > 0)
{
    report.Add($"**Пропусков без разрешения — {forbidden}:** тест должен идти в CI на {os}. Вернуть ему условия (ядро, права, шаг "
        + "workflow) или, если пропуск законный, записать причину с объяснением в desktop/tests/allowed-skips.json.");
    report.Add(string.Empty);
}

var text = string.Join('\n', report);
Console.WriteLine(text);
if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
{
    File.AppendAllText(summary, text + "\n");
}

return forbidden > 0 || failed.Count > 0 ? 1 : 0;

// Разрешенные пропуски для ОС: причина (начало текста) и почему.
static List<(string Reason, string Why)> Allowed(string path, string os)
{
    using var json = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    return [.. json.RootElement.EnumerateArray()
        .Where(a => a.GetProperty("os").EnumerateArray().Any(o => o.GetString() == os))
        .Select(a => (a.GetProperty("reason").GetString()!, a.GetProperty("why").GetString()!))];
}

static string FindRoot()
{
    for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PrivateGateway.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new InvalidOperationException("Не найден корень репозитория (PrivateGateway.slnx): запускать из него.");
}

// Порядок важен: при слиянии запусков побеждает больший.
internal enum Status
{
    Skipped,
    Passed,
    Failed,
}

internal sealed record Result(string Project, Status Status, string Reason);

