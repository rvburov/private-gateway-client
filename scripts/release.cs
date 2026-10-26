// Выпуск на GitHub из сборок трех ОС (workflow release.yml) и поэтапная раскатка:
//
//   dotnet run scripts/release.cs assemble <версия> <папка сборок> <папка выпуска> [доля %]
//   dotnet run scripts/release.cs rollout <папка выпуска> <доля %>
//
// assemble: в папке сборок — по папке на ОС (артефакты release-<ОС> из publish.yml), у каждой свой SHA256SUMS. Каждый
// файл сверяется со своей суммой (порча при передаче между заданиями — ошибка), все файлы кладутся в папку выпуска с
// общим SHA256SUMS, рядом:
//   update.json — манифест обновления: версия, канал (beta — у версии с «-», иначе stable), доля раскатки в процентах
//     (поэтапное обновление: клиент берет выпуск, только если его номер из 0–99 меньше доли), файлы для каждой ОС с
//     SHA-256 и размером;
//   ../notes.md — заметки к выпуску из коммитов с прошлого тега: «Новое» (feat), «Исправлено» (fix), остальное
//     в «Сборка, проверки, документация»; и как сверить загрузку.
// rollout: меняет долю раскатки в update.json уже опубликованного выпуска и его строку в SHA256SUMS (workflow
// release-rollout.yml). notes.md пишется рядом с папкой выпуска — в файлы выпуска он не идет.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args is ["rollout", var published, var share])
{
    // update.json входит в SHA256SUMS: новая доля — новая сумма в той же строке.
    var manifestPath = Path.Combine(published, "update.json");
    var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
    manifest["rollout"] = Percent(share);
    File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    var sumsPath = Path.Combine(published, "SHA256SUMS");
    File.WriteAllLines(sumsPath, File.ReadAllLines(sumsPath).Select(l => l.EndsWith("  update.json", StringComparison.Ordinal) ? $"{Sha256(manifestPath)}  update.json" : l));
    Console.WriteLine($"{manifest["version"]}: раскатка {manifest["rollout"]} %");
    return 0;
}

if (args is not ["assemble", var version, var builds, var output, ..])
{
    Console.Error.WriteLine("Использование: dotnet run scripts/release.cs assemble <версия> <папка сборок> <папка выпуска> [доля %]\n"
        + "               dotnet run scripts/release.cs rollout <папка выпуска> <доля %>");
    return 2;
}

var beta = version.Contains('-', StringComparison.Ordinal);
var rollout = args.Length > 4 ? Percent(args[4]) : 100;
var release = Directory.CreateDirectory(output);
var files = new List<FileInfo>();
foreach (var folder in Directory.GetDirectories(builds).Order(StringComparer.Ordinal))
{
    var sums = Path.Combine(folder, "SHA256SUMS");
    if (!File.Exists(sums))
    {
        throw new InvalidOperationException($"В {folder} нет SHA256SUMS — это не сборка publish.yml");
    }

    var listed = File.ReadAllLines(sums).Where(l => l.Length > 0).Select(l => (Sum: l[..64], Name: l[66..])).ToList();
    foreach (var (sum, name) in listed)
    {
        var file = new FileInfo(Path.Combine(folder, name));
        if (!file.Exists || Sha256(file.FullName) != sum)
        {
            throw new InvalidOperationException($"{folder}/{name}: файла нет или сумма не та, что в его SHA256SUMS");
        }

        files.Add(file.CopyTo(Path.Combine(release.FullName, name), overwrite: true));
    }

    var unlisted = Directory.GetFiles(folder).Select(Path.GetFileName).Except(listed.Select(l => l.Name)).Where(n => n != "SHA256SUMS").ToList();
    if (unlisted.Count > 0)
    {
        throw new InvalidOperationException($"В {folder} файлы без суммы: {string.Join(", ", unlisted)}");
    }
}

// Что из выпуска для какой ОС: имя файла → ключ манифеста.
var kinds = new (string Key, string Suffix)[]
{
    ("win-x64-msi", $"-{version}-win-x64.msi"), ("win-x64-zip", $"-{version}-win-x64.zip"),
    ("linux-x64-deb", $"_{version.Replace('-', '~')}_amd64.deb"), ("linux-x64-rpm", ".x86_64.rpm"),
    ("linux-x64-tar", $"-{version}-linux-x64.tar.gz"), ("osx-arm64-tar", $"-{version}-osx-arm64.tar.gz"),
};
var assets = new JsonObject();
foreach (var (key, suffix) in kinds)
{
    if (files.FirstOrDefault(f => f.Name.EndsWith(suffix, StringComparison.Ordinal)) is { } file)
    {
        assets[key] = new JsonObject { ["file"] = file.Name, ["sha256"] = Sha256(file.FullName), ["size"] = file.Length };
    }
}

foreach (var required in new[] { "win-x64-msi", "linux-x64-deb", "osx-arm64-tar" })
{
    if (!assets.ContainsKey(required))
    {
        throw new InvalidOperationException($"В выпуске нет {required}: сборка одной из ОС не дошла до конца");
    }
}

var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "rvburov/private-gateway-client";
var update = new JsonObject
{
    ["version"] = version,
    ["channel"] = beta ? "beta" : "stable",
    ["rollout"] = rollout,
    ["published"] = Timestamp().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
    ["notes"] = $"https://github.com/{repository}/releases/tag/v{version}",
    ["assets"] = assets,
};
File.WriteAllText(Path.Combine(release.FullName, "update.json"), update.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

var all = release.GetFiles().Where(f => f.Name != "SHA256SUMS").OrderBy(f => f.Name, StringComparer.Ordinal);
File.WriteAllText(Path.Combine(release.FullName, "SHA256SUMS"), string.Join('\n', all.Select(f => $"{Sha256(f.FullName)}  {f.Name}")) + "\n");
File.WriteAllText(Path.Combine(release.Parent!.FullName, "notes.md"), Notes());
Console.WriteLine($"Выпуск {version} ({update["channel"]}, раскатка {rollout} %): {files.Count} файлов → {release.FullName}");
return 0;

// Заметки к выпуску: коммиты с прошлого тега по видам (тип(область): суть), затем как сверить загрузку.
string Notes()
{
    var previous = Git("describe", "--tags", "--abbrev=0", "HEAD^") is { Length: > 0 } tag ? tag.Trim() : null;
    var subjects = Git("log", "--no-merges", "--format=%s", previous is null ? "HEAD" : $"{previous}..HEAD")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    var lines = new List<string> { $"# Private Gateway {version}", string.Empty };
    if (beta)
    {
        lines.AddRange(["Бета-выпуск: приходит тем, у кого в «О программе» выбран канал «Бета».", string.Empty]);
    }

    lines.Add(previous is null ? $"Первый выпуск: изменения с начала разработки ({subjects.Count} коммитов)." : $"Изменения с {previous} ({subjects.Count} коммитов).");
    foreach (var (title, types) in new[] { ("Новое", new[] { "feat" }), ("Исправлено", new[] { "fix" }), ("Сборка, проверки, документация", new[] { "ci", "build", "test", "docs", "chore", "refactor", "perf" }) })
    {
        var group = subjects.Where(s => types.Contains(Type(s))).Select(s => "- " + s[(s.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim()).ToList();
        if (group.Count > 0)
        {
            lines.AddRange([string.Empty, $"## {title}", string.Empty, .. group]);
        }
    }

    var other = subjects.Where(s => Type(s) is not ("feat" or "fix" or "ci" or "build" or "test" or "docs" or "chore" or "refactor" or "perf")).ToList();
    if (other.Count > 0)
    {
        lines.AddRange([string.Empty, "## Прочее", string.Empty, .. other.Select(s => "- " + s)]);
    }

    lines.AddRange(
    [
        string.Empty, "## Как сверить загрузку", string.Empty,
        "Скачайте `SHA256SUMS` рядом с файлом и выполните `sha256sum -c SHA256SUMS --ignore-missing` (Linux, Git Bash на Windows) или "
            + "`shasum -a 256 -c SHA256SUMS --ignore-missing` (macOS). Состав каждого выпуска — `*.cdx.json` (SBOM), лицензии открытых "
            + "компонентов — `THIRD-PARTY-NOTICES.txt` в архиве и в папке программы.",
    ]);
    return string.Join('\n', lines) + "\n";

    // «feat(ui): …» → feat.
    static string Type(string subject) => subject.IndexOfAny(['(', ':']) is > 0 and var at ? subject[..at] : string.Empty;
}

static string Git(params string[] arguments)
{
    var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using var process = Process.Start(start)!;
    var text = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return process.ExitCode == 0 ? text : string.Empty;
}

static int Percent(string text) =>
    int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) && percent is >= 1 and <= 100
        ? percent
        : throw new ArgumentException($"Доля раскатки — целое от 1 до 100, а не «{text}»");

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

// Время выпуска: SOURCE_DATE_EPOCH (время коммита), иначе сейчас.
static DateTimeOffset Timestamp() =>
    long.TryParse(Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH"), out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.UtcNow;
