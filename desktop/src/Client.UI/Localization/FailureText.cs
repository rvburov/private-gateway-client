using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.UI.Localization;

/// <summary>
/// Почему подписка не загрузилась — словами на языке интерфейса, со строчной буквы: «сервер ответил
/// 404 — такой подписки нет, проверьте ссылку». Адреса подписки здесь нет никогда.
/// </summary>
public static class FailureText
{
    public static string Of(DownloadFailure failure, int? statusCode) => (failure, statusCode) switch
    {
        (DownloadFailure.Status, 404) => Loc.Instance["import.download.not_found"],
        (DownloadFailure.Status, 401 or 403) => Loc.Instance.Format("import.download.forbidden", statusCode),
        (DownloadFailure.Status, _) => Loc.Instance.Format("import.download.status", statusCode),
        (DownloadFailure.TooLarge, _) => Loc.Instance["import.download.too_large"],
        (DownloadFailure.Timeout, _) => Loc.Instance["import.download.timeout"],
        (DownloadFailure.NameResolution, _) => Loc.Instance["import.download.dns"],
        (DownloadFailure.Connection, _) => Loc.Instance["import.download.connection"],
        (DownloadFailure.Tls, _) => Loc.Instance["import.download.tls"],
        (DownloadFailure.NoServers, _) => Loc.Instance["import.download.no_servers"],
        (DownloadFailure.NoAddress, _) => Loc.Instance["import.download.no_address"],
        (DownloadFailure.UnsafeDns, _) => Loc.Instance["import.download.unsafe_dns"],
        _ => Loc.Instance["import.download.network"],
    };

    /// <summary>То же с заглавной буквы — для начала предложения.</summary>
    public static string Sentence(DownloadFailure failure, int? statusCode)
    {
        var text = Of(failure, statusCode);
        return text.Length == 0 ? text : char.ToUpper(text[0], Loc.Instance.Culture) + text[1..];
    }
}
