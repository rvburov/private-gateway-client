using System.Text.Json;

namespace PrivateGateway.Client.Core.Settings;

/// <summary>
/// Сервис для «Все адреса сервиса» (решение 741): <paramref name="Id"/> — категория geosite, <paramref name="Name"/> — название
/// (на всех языках одно), адреса — домены с поддоменами и точные имена.
/// </summary>
public sealed record ServiceSite(string Id, string Name, IReadOnlyList<string> Domains, IReadOnlyList<string> Full)
{
    /// <summary>Шаблон правила «geosite:id»: десктоп отдает ядру категорию geosite.dat.</summary>
    public string Pattern => SitePattern.GeositePrefix + Id;

    /// <summary>Входит ли имя в адреса сервиса: домен списка, его поддомен или точное имя.</summary>
    public bool Contains(string host) =>
        Full.Contains(host, StringComparer.Ordinal) || Domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
}

/// <summary>
/// Сервисы «Все адреса сервиса» (решение 741) — shared/specifications/routing/services.json, ресурс сборки: тот же список у
/// Android и iOS. Видео и картинки сайта идут с других адресов, и правило для одного домена сайт не открывает целиком.
/// </summary>
public static class ServiceSites
{
    public static IReadOnlyList<ServiceSite> All { get; } = Load();

    /// <summary>Сервис правила «geosite:id» из списка; иначе null.</summary>
    public static ServiceSite? OfPattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return pattern.StartsWith(SitePattern.GeositePrefix, StringComparison.Ordinal)
            ? All.FirstOrDefault(s => s.Id == pattern[SitePattern.GeositePrefix.Length..])
            : null;
    }

    /// <summary>Сервис, в адреса которого входит обычный домен («youtube.com», «m.youtube.com»); иначе null.</summary>
    public static ServiceSite? Find(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return SitePattern.IsPlainDomain(pattern) ? All.FirstOrDefault(s => s.Contains(pattern)) : null;
    }

    private static ServiceSite[] Load()
    {
        using var stream = typeof(ServiceSites).Assembly.GetManifestResourceStream("PrivateGateway.Routing.Services")
            ?? throw new InvalidOperationException("Нет списка сервисов в сборке");
        using var file = JsonDocument.Parse(stream);
        static string[] List(JsonElement service, string name) => [.. service.GetProperty(name).EnumerateArray().Select(e => e.GetString()!)];
        return [.. file.RootElement.GetProperty("services").EnumerateArray().Select(s => new ServiceSite(
            s.GetProperty("id").GetString()!, s.GetProperty("name").GetString()!, List(s, "domains"), List(s, "full")))];
    }
}
