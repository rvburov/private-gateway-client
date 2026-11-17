namespace PrivateGateway.Client.Application.Import;

/// <summary>Адрес подписки для подписи: без токена доступа, который обычно в пути или в запросе.</summary>
public static class SubscriptionAddress
{
    public const string Hidden = "****";

    /// <summary>Сегмент пути длиннее — скорее токен, чем слово вроде «sub» или «clash».</summary>
    private const int WordLength = 12;

    /// <summary>
    /// Схема, сервер и первый сегмент пути, если он похож на слово; остальное — «****»:
    /// «https://example.com/sub/****». Имя и пароль перед сервером, остаток пути, запрос и якорь
    /// не показываются никогда.
    /// </summary>
    public static string Mask(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var head = url.Scheme + "://" + url.Host + (url.IsDefaultPort ? string.Empty : ":" + url.Port);
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var hidden = segments.Length > 1 || url.Query.Length > 1 || url.Fragment.Length > 1;
        if (segments.Length == 0)
        {
            return hidden ? head + "/" + Hidden : head;
        }

        return IsWord(segments[0])
            ? head + "/" + segments[0] + (hidden ? "/" + Hidden : string.Empty)
            : head + "/" + Hidden;
    }

    /// <summary>
    /// Тот же адрес подписки (решение 528): сравнивается целиком — с путем, токеном и запросом; регистр схемы и сервера,
    /// порт по умолчанию и последний «/» пути не важны. Другой токен — другая подписка (другое устройство).
    /// </summary>
    public static bool Same(Uri left, Uri right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return string.Equals(Canonical(left), Canonical(right), StringComparison.Ordinal);
    }

    private static string Canonical(Uri url) =>
        url.GetComponents(UriComponents.SchemeAndServer | UriComponents.UserInfo, UriFormat.UriEscaped).ToLowerInvariant()
        + url.AbsolutePath.TrimEnd('/') + url.Query + url.Fragment;

    private static bool IsWord(string segment) =>
        segment.Length <= WordLength && segment.All(c => char.IsAsciiLetter(c) || c is '-' or '_');
}
