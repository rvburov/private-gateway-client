// «Все адреса сервиса» в листе правила (решение 741): список сервисов и их доменов —
//   shared/specifications/routing/services.json — один для десктопа, Android и iOS.
//     Десктоп берет из него только название и домены для подсказки, правило уходит ядру как «geosite:id» (geosite.dat);
//     Android и iOS базы geosite не возят — их ядро получает домены сервиса из этого же списка.
//   → ios/Core/Sources/PrivateGatewayCore/Settings/ServiceSites.swift — тот же список кодом: пакет Swift не берет файлы вне
//     своей папки. Десктоп встраивает JSON ресурсом, Android кладет его ресурсом модуля core.
//
// Запуск из корня репозитория (файловое приложение .NET 10, отдельного проекта нет):
//   dotnet run scripts/gen-services.cs                    # только ServiceSites.swift из services.json
//   dotnet run scripts/gen-services.cs -- <папка data>    # сначала домены из v2fly/domain-list-community (коммит —
//                                                          # shared/cores/geosite.version, его же берет scripts/get-geosite.sh)
//
// Из файла сервиса берутся домены и «full:» с вложенными «include:»; «regexp:» и «keyword:» ядро телефона без базы не
// проверит, записи «@ads» и «@cn» — не адреса самого сервиса. Домен внутри другого домена списка не повторяется.
// Руками services.json и ServiceSites.swift не правятся: тесты платформ сверяют их между собой и с geosite.version.

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var root = FindRoot();
var path = Path.Combine(root, "shared", "specifications", "routing", "services.json");
var file = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
var services = file["services"]!.AsArray();

if (args.Length > 0)
{
    var data = args[0];
    foreach (var service in services)
    {
        var (domains, full) = Read(data, service!["id"]!.GetValue<string>(), []);
        var unique = domains.Distinct().ToList();
        unique = [.. unique.Where(d => !unique.Any(other => other != d && d.EndsWith("." + other, StringComparison.Ordinal))).Order(StringComparer.Ordinal)];
        service["domains"] = new JsonArray([.. unique.Select(d => JsonValue.Create(d))]);
        service["full"] = new JsonArray([.. full.Distinct().Where(f => !unique.Any(d => f == d || f.EndsWith("." + d, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal).Select(f => JsonValue.Create(f))]);
    }

    file["commit"] = File.ReadAllText(Path.Combine(root, "shared", "cores", "geosite.version")).Trim();
    var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    File.WriteAllText(path, file.ToJsonString(options).Replace("\r\n", "\n") + "\n");
}

var swift = new StringBuilder();
swift.Append("// Сгенерировано scripts/gen-services.cs из shared/specifications/routing/services.json; руками не править.\n\n");
swift.Append("/// Сервисы для «Все адреса сервиса» (решение 741): название, домены с поддоменами и точные имена. Тот же список, что\n");
swift.Append("/// services.json: тест сверяет.\n");
swift.Append("public enum ServiceSites {\n");
swift.Append("    public static let all: [ServiceSite] = [\n");
foreach (var service in services)
{
    string Text(string key) => service![key]!.GetValue<string>();
    string List(string key) => string.Join(", ", service![key]!.AsArray().Select(d => "\"" + d!.GetValue<string>() + "\""));
    swift.Append(CultureInfo.InvariantCulture, $"        ServiceSite(id: \"{Text("id")}\", name: \"{Text("name")}\",\n");
    swift.Append(CultureInfo.InvariantCulture, $"                    domains: [{List("domains")}],\n");
    swift.Append(CultureInfo.InvariantCulture, $"                    full: [{List("full")}]),\n");
}

swift.Append("    ]\n}\n");
var output = Path.Combine(root, "ios", "Core", "Sources", "PrivateGatewayCore", "Settings", "ServiceSites.swift");
File.WriteAllText(output, swift.ToString());
Console.WriteLine($"{services.Count} сервисов → {Path.GetRelativePath(root, output)}");

// Домены сервиса из файла data/<имя>: домены и «full:» с вложенными «include:», без «regexp:», «keyword:», «@ads» и «@cn».
static (List<string> Domains, List<string> Full) Read(string data, string name, HashSet<string> seen)
{
    var domains = new List<string>();
    var full = new List<string>();
    if (!seen.Add(name))
    {
        return (domains, full);
    }

    foreach (var raw in File.ReadLines(Path.Combine(data, name)))
    {
        var line = raw.Split('#')[0].Trim();
        if (line.Length == 0)
        {
            continue;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var value = parts[0];
        var attributes = parts.Skip(1).ToList();
        if (attributes.Contains("@ads") || attributes.Contains("@cn"))
        {
            continue;
        }

        if (value.StartsWith("include:", StringComparison.Ordinal))
        {
            var (d, f) = Read(data, value["include:".Length..], seen);
            domains.AddRange(d);
            full.AddRange(f);
            continue;
        }

        if (value.StartsWith("regexp:", StringComparison.Ordinal) || value.StartsWith("keyword:", StringComparison.Ordinal))
        {
            continue;
        }

        var exact = value.StartsWith("full:", StringComparison.Ordinal);
        value = (exact ? value["full:".Length..] : value.StartsWith("domain:", StringComparison.Ordinal) ? value["domain:".Length..] : value).ToLowerInvariant();
        if (value.Contains('.') && value.Split('.').All(label => Regex.IsMatch(label, "^[a-z0-9-]+$")))
        {
            (exact ? full : domains).Add(value);
        }
    }

    return (domains, full);
}

static string FindRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "PrivateGateway.slnx")))
        {
            return dir.FullName;
        }
    }

    throw new InvalidOperationException("Запускать из репозитория: PrivateGateway.slnx не найден");
}
