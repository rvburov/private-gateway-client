// Упаковка сборки выпуска: SBOM (CycloneDX 1.6), THIRD-PARTY-NOTICES, архив и контрольные суммы. Вызывает
// scripts/publish.sh, вручную —
//   dotnet run scripts/package.cs <папка сборки> <папка выпуска> <версия> <rid>
//
// Папка сборки — окно и служба (dotnet publish, самодостаточные) и ядра в cores/xray, cores/sing-box. Имя выпуска —
// PrivateGateway-<версия>-<rid>. В папку выпуска пишутся:
//   <имя>.zip (Windows) или <имя>.tar.gz (Linux, macOS; права файлов сохраняются) — папка сборки целиком;
//   <имя>.cdx.json — SBOM: пакеты NuGet обоих приложений (из *.deps.json, с SHA-512 и лицензией из .nuspec кеша
//     пакетов), среда .NET, ядра и геобазы с SHA-256 файлов; он же лежит в архиве как sbom.cdx.json;
//   SHA256SUMS — суммы всех файлов папки выпуска (формат sha256sum -c).
// В папку сборки (и в архив) — еще THIRD-PARTY-NOTICES.txt: компоненты из shared/licenses/components.json (тот же
// список, что на экране «О программе») с версиями, авторством, ссылками на исходники, полные тексты их лицензий из
// shared/licenses/texts и уведомления, которые пакеты NuGet приносят сами (THIRD-PARTY-NOTICES в пакете).
//
// Неполное не пишется: пакет без лицензии в .nuspec или без строки в components.json, ядро без версии или файла,
// лицензия без текста — ошибка, а не пропуск.

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

if (args.Length != 4)
{
    Console.Error.WriteLine("Использование: dotnet run scripts/package.cs <папка сборки> <папка выпуска> <версия> <rid>");
    return 2;
}

var root = FindRoot();
var build = new DirectoryInfo(args[0]);
var release = Directory.CreateDirectory(args[1]);
var (version, rid) = (args[2], args[3]);
var name = $"PrivateGateway-{version}-{rid}";
var windows = rid.StartsWith("win-", StringComparison.Ordinal);
var packages = PackagesFolder();

var components = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
foreach (var deps in build.GetFiles("*.deps.json"))
{
    using var json = JsonDocument.Parse(File.ReadAllBytes(deps.FullName));
    foreach (var library in json.RootElement.GetProperty("libraries").EnumerateObject())
    {
        var type = library.Value.GetProperty("type").GetString();
        if (type is not ("package" or "runtimepack"))
        {
            continue;
        }

        // «Avalonia/12.1.4»; среда .NET — «runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/10.0.13».
        var (id, packageVersion) = (library.Name.Split('/')[0], library.Name.Split('/')[1]);
        id = id.StartsWith("runtimepack.", StringComparison.Ordinal) ? id["runtimepack.".Length..] : id;
        var purl = $"pkg:nuget/{id}@{packageVersion}";
        if (components.ContainsKey(purl))
        {
            continue;
        }

        var component = new JsonObject
        {
            ["type"] = type == "runtimepack" ? "framework" : "library",
            ["bom-ref"] = purl,
            ["name"] = id,
            ["version"] = packageVersion,
            ["purl"] = purl,
            ["licenses"] = License(id, packageVersion),
        };
        if (library.Value.TryGetProperty("sha512", out var sha512) && sha512.GetString() is { Length: > 7 } hash)
        {
            component["hashes"] = new JsonArray(Hash("SHA-512", Convert.ToHexStringLower(Convert.FromBase64String(hash["sha512-".Length..]))));
        }

        components[purl] = component;
    }
}

if (components.Count == 0)
{
    throw new InvalidOperationException($"В {build.FullName} нет *.deps.json: это не папка dotnet publish.");
}

// Открытые компоненты — тот же список, что на экране «О программе»: каждый пакет выпуска должен в нем быть.
var open = OpenComponents(Path.Combine(root, "shared", "licenses", "components.json"));
var nuget = components.Values.Select(c => (Id: (string)c["name"]!, Version: (string)c["version"]!)).ToList();
var uncovered = nuget.Where(p => !open.Any(o => o.Covers(p.Id))).Select(p => p.Id).ToList();
if (uncovered.Count > 0)
{
    throw new InvalidOperationException("Пакеты выпуска без строки в shared/licenses/components.json: " + string.Join(", ", uncovered));
}

// Ядра и геобазы (из архива выпуска Xray, рядом с ним): версия — из shared/cores, лицензия — из списка.
var exe = windows ? ".exe" : string.Empty;
foreach (var core in open.Where(o => o.Core is not null))
{
    var purl = $"pkg:github/{core.Url["https://github.com/".Length..]}@{Version(core.Core!)}";
    components[purl] = new JsonObject
    {
        ["type"] = "application",
        ["bom-ref"] = purl,
        ["name"] = core.Name,
        ["version"] = Version(core.Core!),
        ["purl"] = purl,
        ["licenses"] = new JsonArray(new JsonObject { ["expression"] = core.License }),
        ["hashes"] = new JsonArray(Hash("SHA-256", Sha256(Existing(Path.Combine("cores", core.Core!, core.Core + exe))))),
    };
}

foreach (var data in open.Where(o => o.Data is not null))
{
    components["data:" + data.Data] = new JsonObject
    {
        ["type"] = "data",
        ["bom-ref"] = "data:" + data.Data,
        ["name"] = data.Data,
        ["version"] = Version("xray"),
        ["licenses"] = new JsonArray(new JsonObject { ["expression"] = data.License }),
        ["hashes"] = new JsonArray(Hash("SHA-256", Sha256(Existing(Path.Combine("cores", "xray", data.Data!))))),
    };
}

File.WriteAllText(Path.Combine(build.FullName, "THIRD-PARTY-NOTICES.txt"), Notices());

var sbom = new JsonObject
{
    ["bomFormat"] = "CycloneDX",
    ["specVersion"] = "1.6",
    ["serialNumber"] = $"urn:uuid:{Guid.NewGuid()}",
    ["version"] = 1,
    ["metadata"] = new JsonObject
    {
        ["timestamp"] = Timestamp().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        ["tools"] = new JsonObject { ["components"] = new JsonArray(new JsonObject { ["type"] = "application", ["name"] = "scripts/package.cs" }) },
        ["component"] = new JsonObject
        {
            ["type"] = "application",
            ["bom-ref"] = "private-gateway-client",
            ["name"] = "Private Gateway Client",
            ["version"] = version,
        },
    },
    ["components"] = new JsonArray([.. components.Values]),
};

var text = sbom.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
File.WriteAllText(Path.Combine(build.FullName, "sbom.cdx.json"), text);
File.WriteAllText(Path.Combine(release.FullName, name + ".cdx.json"), text);

var archive = Path.Combine(release.FullName, name + (windows ? ".zip" : ".tar.gz"));
File.Delete(archive);
if (windows)
{
    ZipFile.CreateFromDirectory(build.FullName, archive, CompressionLevel.Optimal, includeBaseDirectory: true);
}
else
{
    using var file = File.Create(archive);
    using var gzip = new GZipStream(file, CompressionLevel.Optimal);
    TarFile.CreateFromDirectory(build.FullName, gzip, includeBaseDirectory: true);
}

var sums = release.GetFiles().Where(f => f.Name != "SHA256SUMS").OrderBy(f => f.Name, StringComparer.Ordinal)
    .Select(f => $"{Sha256(f.FullName)}  {f.Name}");
File.WriteAllText(Path.Combine(release.FullName, "SHA256SUMS"), string.Join('\n', sums) + "\n");

Console.WriteLine($"{name}: {components.Count} компонентов в SBOM → {release.FullName}");
foreach (var line in File.ReadLines(Path.Combine(release.FullName, "SHA256SUMS")))
{
    Console.WriteLine("  " + line);
}

return 0;

// Лицензия пакета из его .nuspec в кеше NuGet: выражение SPDX или, у старых пакетов, ссылка.
JsonArray License(string id, string packageVersion)
{
    var nuspec = Path.Combine(packages, id.ToLowerInvariant(), packageVersion.ToLowerInvariant(), id.ToLowerInvariant() + ".nuspec");
    if (!File.Exists(nuspec))
    {
        throw new InvalidOperationException($"Нет {nuspec}: пакеты не восстановлены этим кешем NuGet.");
    }

    var metadata = XDocument.Load(nuspec).Root!.Elements().First(e => e.Name.LocalName == "metadata").Elements().ToList();
    if (metadata.FirstOrDefault(e => e.Name.LocalName == "license" && (string?)e.Attribute("type") == "expression")?.Value is { Length: > 0 } expression)
    {
        return new JsonArray(new JsonObject { ["expression"] = expression });
    }

    if (metadata.FirstOrDefault(e => e.Name.LocalName == "licenseUrl")?.Value is { Length: > 0 } url)
    {
        return new JsonArray(new JsonObject { ["license"] = new JsonObject { ["name"] = "см. ссылку", ["url"] = url } });
    }

    throw new InvalidOperationException($"У пакета {id} {packageVersion} в .nuspec нет лицензии — записать ее вручную нельзя, только разобраться.");
}

string Existing(string relative)
{
    var path = Path.Combine(build.FullName, relative);
    return File.Exists(path) ? path : throw new InvalidOperationException($"В сборке нет {relative}: ядра не положены в cores/.");
}

string Version(string core) => File.ReadAllText(Path.Combine(root, "shared", "cores", core + ".version")).Trim();

static JsonObject Hash(string algorithm, string content) => new() { ["alg"] = algorithm, ["content"] = content };

static string Sha256(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

// Время сборки: SOURCE_DATE_EPOCH (время коммита — для повторимой сборки), иначе сейчас.
static DateTimeOffset Timestamp() =>
    long.TryParse(Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH"), out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.UtcNow;

// Кеш пакетов NuGet, из которого собиралась сборка (NuGet.config кладет его в packages/packages, у рабочей папки — в основной).
static string PackagesFolder()
{
    using var process = Process.Start(new ProcessStartInfo("dotnet", "nuget locals global-packages --list") { RedirectStandardOutput = true })!;
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    var at = output.IndexOf(':', StringComparison.Ordinal);
    return process.ExitCode == 0 && at > 0 ? output[(at + 1)..].Trim() : throw new InvalidOperationException("dotnet nuget locals не назвал кеш пакетов");
}

// THIRD-PARTY-NOTICES.txt: компоненты выпуска, тексты их лицензий, уведомления пакетов.
string Notices()
{
    const string Rule = "================================================================================";
    var lines = new List<string>
    {
        $"Private Gateway Client {version} ({rid})",
        "Открытые компоненты программы и их лицензии / Open source components and their licenses",
    };
    var licenses = new List<string>();
    foreach (var component in open)
    {
        var shipped = nuget.Where(p => component.Covers(p.Id)).Select(p => $"{p.Id} {p.Version}").ToList();
        var componentVersion = component.Core is { } core ? Version(core)
            : component.Data is not null ? Version("xray")
            : string.Join(", ", nuget.Where(p => component.Covers(p.Id)).Select(p => p.Version).Distinct());
        if (component.Packages.Count > 0 && shipped.Count == 0)
        {
            // На этой ОС пакетов компонента нет (например, только для другой платформы) — в уведомления он не идет.
            Console.Error.WriteLine($"Внимание: в выпуске {rid} нет пакетов компонента «{component.Name}»");
            continue;
        }

        lines.AddRange([string.Empty, Rule, $"{component.Name} {componentVersion}".TrimEnd(), $"Лицензия / License: {component.License}", component.Copyright, component.Url]);
        if (component.Source is { } source)
        {
            lines.Add("Исходный код / Source code: " + source.Replace("{version}", componentVersion, StringComparison.Ordinal));
        }

        if (component.Notice is { } notice)
        {
            lines.Add(notice);
        }

        if (shipped.Count > 0)
        {
            lines.Add("Пакеты / Packages: " + string.Join(", ", shipped));
        }

        licenses.AddRange(LicenseIds(component.License).Where(id => !licenses.Contains(id)));
    }

    // Один текст на несколько идентификаторов (GPL-3.0-only и -or-later) — один раз.
    lines.AddRange([string.Empty, Rule, "Тексты лицензий / License texts"]);
    foreach (var group in licenses.GroupBy(id => id.Replace("-only", string.Empty, StringComparison.Ordinal).Replace("-or-later", string.Empty, StringComparison.Ordinal)))
    {
        var path = Path.Combine(root, "shared", "licenses", "texts", group.Key + ".txt");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Нет полного текста лицензии {group.Key}: {path}");
        }

        lines.AddRange([string.Empty, "---------------------------------------- " + string.Join(", ", group), File.ReadAllText(path).ReplaceLineEndings("\n").TrimEnd()]);
    }

    // Одно и то же уведомление (у пакетов .NET оно общее) — один раз, со списком пакетов, которые его принесли.
    lines.AddRange([string.Empty, Rule, "Уведомления, которые пакеты приносят сами / Notices shipped with packages"]);
    var shippedNotices = nuget.OrderBy(p => p.Id, StringComparer.Ordinal)
        .SelectMany(p => Directory.GetFiles(Path.Combine(packages, p.Id.ToLowerInvariant(), p.Version.ToLowerInvariant()))
            .Where(f => Path.GetFileName(f).Replace("-", string.Empty, StringComparison.Ordinal).StartsWith("thirdpartynotices", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Package: $"{p.Id} {p.Version}", Text: File.ReadAllText(f).ReplaceLineEndings("\n").TrimEnd())))
        .GroupBy(n => n.Text, StringComparer.Ordinal);
    foreach (var notice in shippedNotices)
    {
        lines.AddRange([string.Empty, "---------------------------------------- " + string.Join(", ", notice.Select(n => n.Package)), notice.Key]);
    }

    return string.Join('\n', lines) + "\n";
}

// Выражение SPDX → идентификаторы: «MIT OR Apache-2.0» → MIT, Apache-2.0.
static IEnumerable<string> LicenseIds(string expression) =>
    expression.Replace("(", " ", StringComparison.Ordinal).Replace(")", " ", StringComparison.Ordinal)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t is not ("OR" or "AND" or "WITH"));

// shared/licenses/components.json — разбор вручную: сериализатор по отражению в файловом приложении не годится (AOT).
static List<OpenComponent> OpenComponents(string path)
{
    using var json = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    return [.. json.RootElement.EnumerateArray().Select(c => new OpenComponent(
        c.GetProperty("name").GetString()!, c.GetProperty("license").GetString()!, c.GetProperty("copyright").GetString()!, c.GetProperty("url").GetString()!,
        c.TryGetProperty("packages", out var list) ? [.. list.EnumerateArray().Select(p => p.GetString()!)] : [],
        Optional(c, "core"), Optional(c, "data"), Optional(c, "source"), Optional(c, "notice")))];

    static string? Optional(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.GetString() : null;
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

// Строка shared/licenses/components.json.
internal sealed record OpenComponent(
    string Name, string License, string Copyright, string Url, IReadOnlyList<string> Packages, string? Core, string? Data, string? Source, string? Notice)
{
    // «*» в конце — любой хвост имени пакета.
    public bool Covers(string package) => Packages.Any(p => p.EndsWith('*')
        ? package.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
        : string.Equals(p, package, StringComparison.OrdinalIgnoreCase));
}
