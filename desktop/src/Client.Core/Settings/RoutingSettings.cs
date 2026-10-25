namespace PrivateGateway.Client.Core.Settings;

/// <summary>Что идет через VPN (доска 13).</summary>
public enum RoutingMode
{
    /// <summary>Весь трафик — через VPN; по умолчанию.</summary>
    AllTraffic,

    /// <summary>Все через VPN, кроме выбранных программ и правил для сайтов.</summary>
    Split,
}

/// <summary>Что делать с сайтом из правила.</summary>
public enum RuleAction
{
    Direct,
    Proxy,
    Block,
}

/// <summary>Программа мимо VPN: путь к исполняемому файлу, имя для подписи, включена ли.</summary>
public sealed record BypassApp(string Path, string Name, bool IsEnabled = true);

/// <summary>Правило для сайта: домен, «*.домен» или «geosite:категория».</summary>
public sealed record DomainRule(string Pattern, RuleAction Action);

/// <summary>
/// «Маршрутизация» (доска 13). По умолчанию весь трафик через VPN: раздельный режим — только
/// осознанным выбором (ROADMAP). Программы мимо VPN действуют только в раздельном режиме, правила
/// для сайтов — всегда.
/// </summary>
public sealed record RoutingSettings(RoutingMode Mode, IReadOnlyList<BypassApp> Apps, IReadOnlyList<DomainRule> Rules)
{
    public static RoutingSettings Default { get; } = new(RoutingMode.AllTraffic, [], []);

    /// <summary>Списки сравниваются по содержимому — иначе сохраненные настройки не равны прочитанным.</summary>
    public bool Equals(RoutingSettings? other) =>
        other is not null && Mode == other.Mode && Apps.SequenceEqual(other.Apps) && Rules.SequenceEqual(other.Rules);

    public override int GetHashCode() => HashCode.Combine(Mode, Apps.Count, Rules.Count);
}

/// <summary>Шаблон сайта для правила: проверка и приведение к виду для службы.</summary>
public static class SitePattern
{
    private const string Geosite = "geosite:";

    /// <summary>
    /// «example.com», «*.example.com» или «geosite:category» — строчными; null — не подходит. Адрес
    /// с «https://» и путем сокращается до домена: так его чаще всего и вставляют.
    /// </summary>
    public static string? Normalize(string? text)
    {
        var value = text?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value.StartsWith(Geosite, StringComparison.Ordinal))
        {
            var category = value[Geosite.Length..];
            return category.Length > 0 && category.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '!' or '@')
                ? value
                : null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            value = url.Host;
        }

        var wildcard = value.StartsWith("*.", StringComparison.Ordinal);
        var host = wildcard ? value[2..] : value;
        var labels = host.Split('.');
        // «*.local» — одна метка после звездочки допустима; без звездочки нужен домен с точкой.
        return labels.Length >= (wildcard ? 1 : 2)
            && labels.All(l => l.Length is > 0 and <= 63 && l[0] != '-' && l[^1] != '-' && l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            && !labels[^1].All(char.IsAsciiDigit)
            ? (wildcard ? "*." : string.Empty) + host
            : null;
    }
}
