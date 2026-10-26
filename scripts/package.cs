// Упаковка сборки выпуска: SBOM (CycloneDX 1.6), архив и контрольные суммы. Вызывает scripts/publish.sh, вручную —
//   dotnet run scripts/package.cs <папка сборки> <папка выпуска> <версия> <rid>
//
// Папка сборки — окно и служба (dotnet publish, самодостаточные) и ядра в cores/xray, cores/sing-box. Имя выпуска —
// PrivateGateway-<версия>-<rid>. В папку выпуска пишутся:
//   <имя>.zip (Windows) или <имя>.tar.gz (Linux, macOS; права файлов сохраняются) — папка сборки целиком;
//   <имя>.cdx.json — SBOM: пакеты NuGet обоих приложений (из *.deps.json, с SHA-512 и лицензией из .nuspec кеша
//     пакетов), среда .NET, ядра и геобазы с SHA-256 файлов; он же лежит в архиве как sbom.cdx.json;
//   SHA256SUMS — суммы всех файлов папки выпуска (формат sha256sum -c).
//
// Неполный SBOM не пишется: пакет без лицензии, ядро без версии или файла — ошибка, а не пропуск.

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

var exe = windows ? ".exe" : string.Empty;
var xray = Version("xray");
var singBox = Version("sing-box");
Core("Xray-core", xray, $"pkg:github/XTLS/Xray-core@{xray}", "MPL-2.0", Path.Combine("cores", "xray", "xray" + exe));
Core("sing-box", singBox, $"pkg:github/SagerNet/sing-box@{singBox}", "GPL-3.0-or-later", Path.Combine("cores", "sing-box", "sing-box" + exe));

// Геобазы из архива выпуска Xray: GeoIP — данные IPinfo Lite / GeoLite2 (CC BY-SA 4.0), GeoSite — v2fly/domain-list-community (MIT).
Data("geoip.dat", xray, "CC-BY-SA-4.0", Path.Combine("cores", "xray", "geoip.dat"));
Data("geosite.dat", xray, "MIT", Path.Combine("cores", "xray", "geosite.dat"));

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

void Core(string coreName, string coreVersion, string purl, string license, string path) => components[purl] = new JsonObject
{
    ["type"] = "application",
    ["bom-ref"] = purl,
    ["name"] = coreName,
    ["version"] = coreVersion,
    ["purl"] = purl,
    ["licenses"] = new JsonArray(new JsonObject { ["expression"] = license }),
    ["hashes"] = new JsonArray(Hash("SHA-256", Sha256(Existing(path)))),
};

void Data(string file, string coreVersion, string license, string path) => components["data:" + file] = new JsonObject
{
    ["type"] = "data",
    ["bom-ref"] = "data:" + file,
    ["name"] = file,
    ["version"] = coreVersion,
    ["licenses"] = new JsonArray(new JsonObject { ["expression"] = license }),
    ["hashes"] = new JsonArray(Hash("SHA-256", Sha256(Existing(path)))),
};

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
