namespace PrivateGateway.Client.Application.Import;

/// <summary>Строка разобранного текста: номер (с 1), сама строка и результат разбора.</summary>
public sealed record ImportLine(int Number, LinkResult Result);

/// <summary>Разобранный текст: формат («links», «base64») и строки по порядку.</summary>
public sealed record ImportBatch(string Format, IReadOnlyList<ImportLine> Lines)
{
    public int Found => Lines.Count;

    public int Failed => Lines.Count(l => !l.Result.IsOk);
}

/// <summary>
/// Текст из буфера, файла или подписки: ссылки по одной в строке или такой же список в base64
/// (обычная подписка). Пустые строки и строки-комментарии («#», «//») пропускаются.
/// </summary>
public static class ImportParser
{
    public const string FormatLinks = "links";
    public const string FormatBase64 = "base64";

    public static ImportBatch Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
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
}
