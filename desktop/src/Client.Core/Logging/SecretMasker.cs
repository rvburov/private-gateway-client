using System.Text.RegularExpressions;

namespace PrivateGateway.Client.Core.Logging;

/// <summary>
/// Скрывает секреты в строке перед записью в журнал и в отчет проверки: ссылки на серверы,
/// пути и параметры адресов подписок, UUID, ключи. Журнал пользователь может отправить в
/// отчете, поэтому в нем не должно быть ничего, по чему можно подключиться к его серверу.
/// </summary>
public static partial class SecretMasker
{
    /// <summary>Чем заменяется скрытая часть.</summary>
    public const string Hidden = "[скрыто]";

    /// <summary>Строка без секретов. Порядок правил важен: сначала ссылки целиком.</summary>
    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = ProxyLink().Replace(text, m => m.Groups["scheme"].Value + "://" + Hidden);
        result = UserInfo().Replace(result, m => m.Groups["scheme"].Value + Hidden + "@");
        result = HttpUrl().Replace(result, m =>
            m.Groups["rest"].Length > 1 ? m.Groups["origin"].Value + "/" + Hidden : m.Value);
        result = Uuid().Replace(result, Hidden);
        result = LongKey().Replace(result, m => LooksLikeKey(m.Value) ? Hidden : m.Value);
        return result;
    }

    // Случайный ключ почти всегда содержит цифру, строчную и заглавную букву; путь к файлу
    // начинается с «/» или состоит из слов — его не трогаем.
    private static bool LooksLikeKey(string value) =>
        value[0] != '/'
        && value.Count(c => c == '/') <= 4
        && value.Any(char.IsAsciiDigit)
        && value.Any(char.IsAsciiLetterUpper)
        && value.Any(char.IsAsciiLetterLower);

    // Ссылки серверов: все после схемы — учетные данные и параметры.
    [GeneratedRegex(@"\b(?<scheme>vless|vmess|trojan|ss|ssr|hysteria2|hy2|tuic|wireguard|wg)://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProxyLink();

    // Адрес подписки: путь и параметры часто и есть токен доступа, хост оставляем.
    // Имя и пароль в адресе прокси или сайта: «http://имя:пароль@узел» — сам узел остается.
    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.-]*://)[^\s/@""'<>]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserInfo();

    [GeneratedRegex(@"(?<origin>\bhttps?://[^\s/""'<>?#]+)(?<rest>[/?#][^\s""'<>]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrl();

    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Uuid();

    // Кандидаты в ключи REALITY и WireGuard — base64 длиной от 32 знаков; решает LooksLikeKey.
    [GeneratedRegex(@"(?<![A-Za-z0-9+/_-])[A-Za-z0-9+/_-]{32,}={0,2}(?![A-Za-z0-9+/_=-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex LongKey();
}
