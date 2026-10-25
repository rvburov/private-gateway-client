using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Application.Import;

/// <summary>
/// Общее для конфигов sing-box и Clash: поля узлов дерева и сборка сервера через те же части
/// ссылки и проверки, что у ссылок (<see cref="LinkParser.FromParts"/>), — одинаковый сервер и
/// одинаковые коды ошибок, откуда бы он ни пришел.
/// </summary>
internal static class ConfigImport
{
    /// <summary>Весь текст не разобран: одна строка с ошибкой <see cref="ImportErrors.BadEncoding"/>.</summary>
    public static ImportBatch Broken(string format, int line) =>
        new(format, [new ImportLine(Math.Max(1, line), new LinkResult(null, ImportErrors.BadEncoding))]);

    public static LinkResult Fail(string error) => new(null, error);

    /// <summary>Параметры, как в ссылке: регистр ключей не важен.</summary>
    public static Dictionary<string, string> Query() => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Добавить параметр; пустое значение не пишется — как отсутствующее в ссылке.</summary>
    public static void Put(Dictionary<string, string> query, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            query[key] = value;
        }
    }

    /// <summary>Сервер из полей: секреты, адрес, порт, параметры и название — как части ссылки.</summary>
    public static LinkResult Server(
        ProxyProtocol protocol, IEnumerable<string?> secrets, string? host, string? port, Dictionary<string, string> query, string? name) =>
        LinkParser.FromParts(protocol, LinkParser.Parts(secrets, host, port, query, name));

    /// <summary>Строковое поле; число и логическое значение — их текст.</summary>
    public static string? Text(JsonNode? node, string key) => node is JsonObject o ? Scalar(o[key]) : null;

    private static string? Scalar(JsonNode? node) =>
        node is JsonValue value
            ? value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>(),
                JsonValueKind.Number => value.ToJsonString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;

    /// <summary>Логическое поле: true в JSON, «true», «yes», «on» или «1» в YAML.</summary>
    public static bool Flag(JsonNode? node, string key) =>
        Text(node, key)?.ToLowerInvariant() is "true" or "yes" or "on" or "1";

    public static JsonObject? Object(JsonNode? node, string key) => node is JsonObject o ? o[key] as JsonObject : null;

    /// <summary>Список строк: массив или одна строка.</summary>
    public static IReadOnlyList<string> List(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonArray array
            ? [.. array.Select(Scalar).OfType<string>()]
            : Text(node, key) is { Length: > 0 } one ? [one] : [];

    /// <summary>Список через запятую, как в параметрах ссылки; пустой — null.</summary>
    public static string? Joined(JsonNode? node, string key) => List(node, key) is { Count: > 0 } list ? string.Join(',', list) : null;

    /// <summary>Заголовок HTTP из словаря заголовков (значение — строка или список); регистр имени не важен.</summary>
    public static string? Header(JsonObject? headers, string name)
    {
        foreach (var (key, value) in headers ?? [])
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return Scalar(value is JsonArray many ? many.FirstOrDefault() : value);
            }
        }

        return null;
    }
}
