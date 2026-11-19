using System.Net;
using System.Net.Sockets;

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

/// <summary>
/// Правило для сайта: домен, «*.домен», «geosite:категория», адрес или подсеть. <paramref name="Exact"/> — «Только этот
/// адрес» (решение 739): домен без поддоменов; у зоны, списка и адреса не действует.
/// </summary>
public sealed record DomainRule(string Pattern, RuleAction Action, bool Exact = false)
{
    /// <summary>Действует ли «Только этот адрес»: только у обычного домена.</summary>
    public bool IsExact => Exact && SitePattern.IsPlainDomain(Pattern);
}

/// <summary>
/// «Маршрутизация» (доска 13). По умолчанию весь трафик через VPN: раздельный режим — только
/// осознанным выбором (ROADMAP). Программы мимо VPN действуют только в раздельном режиме, правила
/// для сайтов — всегда. <paramref name="BlockOthers"/> — «Остальные сайты: Блокировать» (решение 740): открываются только
/// сайты с правилом «Через VPN» или «Напрямую» и «Пропускается всегда» (<see cref="AlwaysAllowed"/>).
/// </summary>
public sealed record RoutingSettings(RoutingMode Mode, IReadOnlyList<BypassApp> Apps, IReadOnlyList<DomainRule> Rules, bool BlockOthers = false)
{
    public static RoutingSettings Default { get; } = new(RoutingMode.AllTraffic, [], []);

    /// <summary>Идут ли сейчас программы мимо VPN: только в раздельном режиме и только включенные.</summary>
    public bool HasBypassApps => Mode == RoutingMode.Split && Apps.Any(a => a.IsEnabled);

    /// <summary>Идут ли сайты мимо VPN: правила «Напрямую» действуют только в раздельном режиме.</summary>
    public bool HasBypassSites => Mode == RoutingMode.Split && Rules.Any(r => r.Action == RuleAction.Direct);

    /// <summary>
    /// Правила в порядке проверки ядром — точнее раньше (решение 739): «только этот адрес», затем домены от длинного к
    /// короткому (music.youtube.com раньше youtube.com), готовые списки geosite, адреса и подсети от узкой к широкой.
    /// Равные — в порядке списка. Так правило для части сайта не теряется за правилом для всего сайта.
    /// </summary>
    public static IReadOnlyList<DomainRule> Ordered(IEnumerable<DomainRule> rules) =>
        [.. rules.Select((rule, index) => (Rule: rule, Index: index))
            .OrderBy(p => SitePattern.Rank(p.Rule).Group)
            .ThenBy(p => SitePattern.Rank(p.Rule).Detail)
            .ThenBy(p => p.Index)
            .Select(p => p.Rule)];

    /// <summary>Списки сравниваются по содержимому — иначе сохраненные настройки не равны прочитанным.</summary>
    public bool Equals(RoutingSettings? other) =>
        other is not null && Mode == other.Mode && Apps.SequenceEqual(other.Apps) && Rules.SequenceEqual(other.Rules) && BlockOthers == other.BlockOthers;

    public override int GetHashCode() => HashCode.Combine(Mode, Apps.Count, Rules.Count, BlockOthers);
}

/// <summary>Шаблон сайта для правила: проверка и приведение к виду для службы.</summary>
public static class SitePattern
{
    private const string Geosite = GeositePrefix;

    /// <summary>Начало шаблона готового списка: «geosite:категория».</summary>
    public const string GeositePrefix = "geosite:";

    /// <summary>
    /// «example.com», «*.example.com» или «geosite:category» — строчными; адрес «203.0.113.5» или
    /// подсеть «198.51.100.0/24», «2001:db8::/32» — подсеть приводится к адресу сети. null — не
    /// подходит. Адрес с «https://» и путем сокращается до домена: так его чаще всего и вставляют.
    /// </summary>
    public static string? Normalize(string? text)
    {
        var value = text?.Trim().ToLowerInvariant() ?? string.Empty;
        if (Address(value) is { } address)
        {
            return address;
        }

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

    /// <summary>Шаблон — адрес или подсеть, а не имя: правило сравнивает IP соединения.</summary>
    public static bool IsAddress(string pattern) => Address(pattern) is not null;

    /// <summary>Обычный домен («example.com»): не зона «*.», не список geosite и не адрес — у него есть «Только этот адрес».</summary>
    public static bool IsPlainDomain(string pattern) =>
        !pattern.StartsWith("*.", StringComparison.Ordinal) && !pattern.StartsWith(Geosite, StringComparison.Ordinal) && !IsAddress(pattern);

    /// <summary>
    /// Место правила при проверке (меньше — раньше): группа (0 — только этот адрес, 1 — домен или зона, 2 — geosite,
    /// 3 — адрес или подсеть) и порядок внутри нее (больше меток домена или длиннее префикс подсети — раньше).
    /// </summary>
    public static (int Group, int Detail) Rank(DomainRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var pattern = rule.Pattern;
        if (pattern.StartsWith(Geosite, StringComparison.Ordinal))
        {
            return (2, 0);
        }

        if (IsAddress(pattern))
        {
            var slash = pattern.IndexOf('/', StringComparison.Ordinal);
            var prefix = slash < 0 ? (pattern.Contains(':', StringComparison.Ordinal) ? 128 : 32)
                : int.Parse(pattern[(slash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            return (3, -prefix);
        }

        var labels = (pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern[2..] : pattern).Split('.').Length;
        return (rule.IsExact ? 0 : 1, -labels);
    }

    /// <summary>
    /// Адрес или подсеть в каноническом виде; иначе null. IPv4 — только четыре числа через точку
    /// («1.2» адресом не считается). Подсеть /0 — весь интернет: это режим, а не правило.
    /// </summary>
    private static string? Address(string value)
    {
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var text = slash < 0 ? value : value[..slash];
        var v4 = text.Split('.') is { Length: 4 } parts && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit));
        if (!(v4 || text.Contains(':', StringComparison.Ordinal)) || !IPAddress.TryParse(text, out var address)
            || address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) || text.Contains('%', StringComparison.Ordinal))
        {
            return null;
        }

        if (slash < 0)
        {
            return address.ToString();
        }

        var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (!int.TryParse(value[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix)
            || prefix is < 1 || prefix > bits)
        {
            return null;
        }

        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }

        return $"{new IPAddress(bytes)}/{prefix}";
    }
}
