using System.Text.RegularExpressions;

namespace PrivateGateway.Client.Application.Import;

/// <summary>Строка разобранного текста: номер (с 1), сама строка и результат разбора.</summary>
public sealed record ImportLine(int Number, LinkResult Result);

/// <summary>Разобранный текст: формат (<see cref="ImportParser.Formats"/>) и строки по порядку.</summary>
public sealed record ImportBatch(string Format, IReadOnlyList<ImportLine> Lines)
{
    public int Found => Lines.Count;

    public int Failed => Lines.Count(l => !l.Result.IsOk);
}

/// <summary>
/// Текст из буфера, файла или подписки: ссылки по одной в строке, такой же список в base64
/// (обычная подписка), конфиг sing-box (JSON) или Clash/Mihomo (YAML со списком proxies). В списке
/// ссылок пустые строки и строки-комментарии («#», «//») пропускаются.
/// </summary>
public static partial class ImportParser
{
    public const string FormatLinks = "links";
    public const string FormatBase64 = "base64";
    public const string FormatSingBox = "singbox";
    public const string FormatClash = "clash";

    /// <summary>Все форматы — для подписей на экране импорта.</summary>
    public static IReadOnlyList<string> Formats { get; } = [FormatLinks, FormatBase64, FormatSingBox, FormatClash];

    public static ImportBatch Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.TrimStart('\uFEFF');
        if (text.TrimStart() is ['{' or '[', ..])
        {
            return SingBoxImport.Parse(text);
        }

        if (ClashProxies().IsMatch(text))
        {
            return ClashImport.Parse(text);
        }

        var format = FormatLinks;
        if (!text.Contains("://", StringComparison.Ordinal)
            && LinkParser.DecodeBase64(text) is { } decoded
            && decoded.Contains("://", StringComparison.Ordinal))
        {
            format = FormatBase64;
            text = decoded;
        }

        var lines = new List<ImportLine>();
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add(new ImportLine(number, LinkParser.Parse(line)));
        }

        return new ImportBatch(format, lines);
    }

    /// <summary>Конфиг Clash: строка «proxies:» с начала строки.</summary>
    [GeneratedRegex(@"^proxies[ \t]*:", RegexOptions.Multiline)]
    private static partial Regex ClashProxies();
}
