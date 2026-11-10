// Выпуск на GitHub из сборок трех ОС (workflow release.yml) и поэтапная раскатка:
//
//   dotnet run scripts/release.cs assemble <версия> <папка сборок> <папка выпуска> [доля %]
//   dotnet run scripts/release.cs rollout <папка выпуска> <доля %>
//   dotnet run scripts/release.cs keygen <файл закрытого ключа вне репозитория>
//
// assemble: в папке сборок — по папке на ОС (артефакты release-<ОС> из publish.yml), у каждой свой SHA256SUMS. Каждый
// файл сверяется со своей суммой (порча при передаче между заданиями — ошибка), все файлы кладутся в папку выпуска с
// общим SHA256SUMS, рядом:
//   update.json — манифест обновления: версия, канал (beta — у версии с «-», иначе stable), доля раскатки в процентах
//     (поэтапное обновление: клиент берет выпуск, только если его номер из 0–99 меньше доли), главное из заметок, файлы
//     для каждой ОС с SHA-256 и размером;
//   update.json.sig — подпись манифеста (ECDSA P-256, SHA-256, DER в base64) ключом из переменной UPDATE_SIGNING_KEY
//     (секрет GitHub, PEM PKCS#8). Подпись сразу сверяется с открытыми ключами shared/update/keys/*.pem — с ними
//     служба проверяет обновление. Ключа нет: пока открытых ключей в репозитории нет — выпуск без подписи (обновление
//     из него служба не поставит), а если они уже есть — ошибка: такой выпуск не примет ни один клиент;
//   highlights манифеста — главное из секции версии в CHANGELOG.md (нет секции — ошибка); текст страницы выпуска
//     собирает из той же секции scripts/release-notes.sh.
// rollout: меняет долю раскатки в update.json уже опубликованного выпуска, подписывает его заново (подписанный без ключа
// не меняется) и обновляет строки в SHA256SUMS (workflow release-rollout.yml).
// keygen: новый ключ подписи обновлений — закрытый в указанный файл (только вне репозитория, права 600), открытый — в
// shared/update/keys/<год-месяц>.pem; что делать дальше, печатает сам (docs/architecture/release.md).

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (args is ["keygen", var privatePath])
{
    return KeyGen(Path.GetFullPath(privatePath));
}

if (args is ["rollout", var published, var share])
{
    // update.json и его подпись входят в SHA256SUMS: новая доля — новые суммы в тех же строках.
    var manifestPath = Path.Combine(published, "update.json");
    var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
    manifest["rollout"] = Percent(share, stop: true);
    var signed = File.Exists(manifestPath + ".sig");
    if (signed && SigningKey() is null)
    {
        throw new InvalidOperationException("Манифест подписан, а ключа подписи (UPDATE_SIGNING_KEY) нет: без новой подписи служба его не примет");
    }

    File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    if (signed)
    {
        Sign(manifestPath, SigningKey()!);
    }

    var sumsPath = Path.Combine(published, "SHA256SUMS");
    File.WriteAllLines(sumsPath, File.ReadAllLines(sumsPath).Select(l => l[66..] is "update.json" or "update.json.sig" ? $"{Sha256(Path.Combine(published, l[66..]))}  {l[66..]}" : l));
    Console.WriteLine($"{manifest["version"]}: раскатка {manifest["rollout"]} %{(signed ? ", подпись обновлена" : string.Empty)}");
    return 0;
}

if (args is not ["assemble", var version, var builds, var output, ..])
{
    Console.Error.WriteLine("Использование: dotnet run scripts/release.cs assemble <версия> <папка сборок> <папка выпуска> [доля %]\n"
        + "               dotnet run scripts/release.cs rollout <папка выпуска> <доля %>\n"
        + "               dotnet run scripts/release.cs keygen <файл закрытого ключа вне репозитория>");
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
    ("osx-arm64-dmg", $"-{version}-osx-arm64.dmg"),
};
var assets = new JsonObject();
foreach (var (key, suffix) in kinds)
{
    if (files.FirstOrDefault(f => f.Name.EndsWith(suffix, StringComparison.Ordinal)) is { } file)
    {
        assets[key] = new JsonObject { ["file"] = file.Name, ["sha256"] = Sha256(file.FullName), ["size"] = file.Length };
    }
}

foreach (var required in new[] { "win-x64-msi", "linux-x64-deb", "osx-arm64-dmg" })
{
    if (!assets.ContainsKey(required))
    {
        throw new InvalidOperationException($"В выпуске нет {required}: сборка одной из ОС не дошла до конца");
    }
}

var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") ?? "rvburov/private-gateway-client";
var news = News(version);
var update = new JsonObject
{
    ["version"] = version,
    ["channel"] = beta ? "beta" : "stable",
    ["rollout"] = rollout,
    ["published"] = Timestamp().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
    ["notes"] = $"https://github.com/{repository}/releases/tag/v{version}",
    ["highlights"] = new JsonArray([.. Highlights(news).Select(h => JsonValue.Create(h))]),
    ["assets"] = assets,
};
var updatePath = Path.Combine(release.FullName, "update.json");
File.WriteAllText(updatePath, update.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
if (SigningKey() is { } signingKey)
{
    Sign(updatePath, signingKey);
}
else if (PublicKeys().Count > 0)
{
    throw new InvalidOperationException("В shared/update/keys есть открытые ключи, а ключа подписи (UPDATE_SIGNING_KEY) нет: неподписанный выпуск не примет ни один клиент");
}
else
{
    Console.Error.WriteLine("Манифест без подписи: ключа подписи обновлений еще нет (docs/architecture/release.md, «Подпись обновлений»), обновление из этого выпуска служба не поставит");
}

var all = release.GetFiles().Where(f => f.Name != "SHA256SUMS").OrderBy(f => f.Name, StringComparer.Ordinal);
File.WriteAllText(Path.Combine(release.FullName, "SHA256SUMS"), string.Join('\n', all.Select(f => $"{Sha256(f.FullName)}  {f.Name}")) + "\n");
Console.WriteLine($"Выпуск {version} ({update["channel"]}, раскатка {rollout} %{(File.Exists(updatePath + ".sig") ? ", подписан" : string.Empty)}): {files.Count} файлов → {release.FullName}");
return 0;

// Главное для карточки обновления — из секции «## [1.2.3]» в CHANGELOG.md (как текст страницы выпуска в
// scripts/release-notes.sh): «### Добавлено» и пункты «- …» с продолжением строк; одинаковые заголовки — одной группой.
// Секции нет или в ней нет пунктов — ошибка: выпуск без описания для людей не собирается, тем коммитов в нем нет (решение
// пользователя 2026-11-10).
static List<(string Title, List<string> Items)> News(string version)
{
    var path = Environment.GetEnvironmentVariable("RELEASE_CHANGELOG") is { Length: > 0 } custom ? custom : "CHANGELOG.md";
    var all = File.Exists(path) ? File.ReadAllLines(path) : [];
    var start = Array.FindIndex(all, l => l.StartsWith($"## [{version}]", StringComparison.Ordinal));
    var groups = new List<(string Title, List<string> Items)>();
    List<string>? items = null;
    foreach (var raw in start < 0 ? [] : all.Skip(start + 1))
    {
        if (raw.StartsWith("## ", StringComparison.Ordinal))
        {
            break;
        }

        if (raw.StartsWith("### ", StringComparison.Ordinal))
        {
            var title = raw[4..].Split(" — ")[0].Trim();
            items = groups.FirstOrDefault(g => g.Title == title).Items;
            if (items is null)
            {
                items = [];
                groups.Add((title, items));
            }
        }
        else if (items is not null && raw.StartsWith("- ", StringComparison.Ordinal))
        {
            items.Add(raw[2..].Trim());
        }
        else if (items is { Count: > 0 } && raw.StartsWith("  ", StringComparison.Ordinal) && raw.Trim().Length > 0)
        {
            items[^1] += " " + raw.Trim();
        }
    }

    groups.RemoveAll(g => g.Items.Count == 0);
    return groups.Count > 0
        ? groups
        : throw new InvalidOperationException($"В {path} нет секции «## [{version}]» с пунктами — опишите версию до выпуска");
}

// Главное для карточки «Доступно обновление»: до трех строк из «Что нового» по порядку групп — первое предложение пункта,
// без разметки Markdown.
static List<string> Highlights(List<(string Title, List<string> Items)> news) =>
    [.. news.SelectMany(g => g.Items).Take(3).Select(i => Regex.Replace(i.Split(". ")[0].TrimEnd('.'), @"[`*]", string.Empty) + ".")];

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

// Доля раскатки: 1–100; rollout принимает и 0 — остановить раскатку опубликованного выпуска (инцидент, docs/operations/support.md).
static int Percent(string text, bool stop = false) =>
    int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var percent) && percent >= (stop ? 0 : 1) && percent <= 100
        ? percent
        : throw new ArgumentException($"Доля раскатки — целое от {(stop ? 0 : 1)} до 100, а не «{text}»");

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

// Закрытый ключ подписи обновлений: PEM из UPDATE_SIGNING_KEY (секрет GitHub) или null.
static string? SigningKey() => Environment.GetEnvironmentVariable("UPDATE_SIGNING_KEY") is { Length: > 0 } pem ? pem : null;

// Открытые ключи, с которыми служба сверяет манифест: shared/update/keys/*.pem (UPDATE_KEYS — другая папка, для проверок).
static List<string> PublicKeys()
{
    var folder = Environment.GetEnvironmentVariable("UPDATE_KEYS") is { Length: > 0 } dir ? dir : Path.Combine("shared", "update", "keys");
    return Directory.Exists(folder) ? [.. Directory.GetFiles(folder, "*.pem").Order(StringComparer.Ordinal).Select(File.ReadAllText)] : [];
}

// Подпись манифеста: ECDSA P-256 над точными байтами файла, SHA-256, DER в base64 — update.json.sig рядом. Подпись,
// которую не проверить ни одним открытым ключом из репозитория, — ошибка: секрет и ключи в репозитории разошлись.
static void Sign(string manifestPath, string privatePem)
{
    using var key = ECDsa.Create();
    key.ImportFromPem(privatePem);
    if (key.KeySize != 256)
    {
        throw new InvalidOperationException("Ключ подписи обновлений — ECDSA P-256");
    }

    var data = File.ReadAllBytes(manifestPath);
    var signature = key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    var verified = PublicKeys().Any(pem =>
    {
        using var publicKey = ECDsa.Create();
        publicKey.ImportFromPem(pem);
        return publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    });
    if (!verified)
    {
        throw new InvalidOperationException("Подпись не проверяется ни одним ключом из shared/update/keys: открытый ключ этого секрета не в репозитории");
    }

    File.WriteAllText(manifestPath + ".sig", Convert.ToBase64String(signature) + "\n");
}

// Новый ключ подписи обновлений. Закрытый — только вне репозитория: попади он в рабочую папку, его легко закоммитить.
static int KeyGen(string privatePath)
{
    var root = Git("rev-parse", "--show-toplevel").Trim();
    if (root.Length > 0 && Path.GetFullPath(privatePath).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"Закрытый ключ — только вне репозитория ({root}): укажите файл, например, в домашней папке");
        return 2;
    }

    if (File.Exists(privatePath))
    {
        Console.Error.WriteLine($"{privatePath} уже есть — старый ключ не перезаписывается");
        return 2;
    }

    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem() + "\n");
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    var keys = Directory.CreateDirectory(Environment.GetEnvironmentVariable("UPDATE_KEYS") is { Length: > 0 } dir ? dir : Path.Combine(root.Length > 0 ? root : ".", "shared", "update", "keys")).FullName;
    var publicPath = Path.Combine(keys, DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".pem");
    if (File.Exists(publicPath))
    {
        publicPath = Path.Combine(keys, DateTime.UtcNow.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture) + ".pem");
    }

    File.WriteAllText(publicPath, key.ExportSubjectPublicKeyInfoPem() + "\n");
    Console.WriteLine(new StringBuilder()
        .AppendLine(CultureInfo.InvariantCulture, $"Закрытый ключ: {privatePath}")
        .AppendLine(CultureInfo.InvariantCulture, $"Открытый ключ: {publicPath}")
        .AppendLine()
        .AppendLine("Дальше:")
        .AppendLine("1. GitHub → репозиторий → Settings → Secrets and variables → Actions → New repository secret:")
        .AppendLine("   Name — UPDATE_SIGNING_KEY, Secret — все содержимое файла закрытого ключа, Add secret.")
        .AppendLine("2. Закрытый ключ сохранить в менеджере паролей и удалить файл. В чат, Git и письма его не отправлять.")
        .AppendLine("3. Открытый ключ закоммитить: выпуски, собранные с ним, служба примет.")
        .ToString());
    return 0;
}

// Время выпуска: SOURCE_DATE_EPOCH (время коммита), иначе сейчас.
static DateTimeOffset Timestamp() =>
    long.TryParse(Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH"), out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.UtcNow;
