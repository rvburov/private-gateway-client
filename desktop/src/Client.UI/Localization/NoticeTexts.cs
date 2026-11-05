using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.UI.Localization;

/// <summary>
/// Тексты уведомлений на языке интерфейса (доски 19 и 22): заголовок по событию (<c>{0}</c> — версия обновления), текст по
/// ключу — <c>{0}</c> сервер, <c>{1}</c> подписка, <c>{2}</c> причина: код ошибки словами или почему подписка не загрузилась,
/// <c>{3}</c> — другая программа VPN или прокси.
/// </summary>
public sealed class NoticeTexts : INoticeTexts
{
    public NoticeText Text(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var reason = notice.Code is { } code ? ErrorText(code)
            : notice.Failure is { } failure ? FailureText.Of(failure, notice.StatusCode)
            : string.Empty;
        var external = ExternalProxySettings.IsExternalId(notice.ServerId);
        var proxyFailure = external && notice.Kind is NoticeKind.ConnectionFailed or NoticeKind.ServerDown;
        return new NoticeText(
            Loc.Instance.Format(TitleKey(notice.Kind), notice.Version ?? string.Empty),
            proxyFailure ? Loc.Instance["externalProxy.failure"]
                : Loc.Instance.Format(notice.TextKey, external ? Loc.Instance["externalProxy.title"] : notice.Server ?? Loc.Instance["notify.server.none"],
                    notice.Subscription ?? string.Empty, reason),
            [.. notice.Actions.Select(a => new NoticeButton(a, Loc.Instance[external && a is NoticeAction.ChangeServer or NoticeAction.ChooseServer or NoticeAction.ChooseOther
                ? "externalProxy.configure" : "notify.action." + char.ToLowerInvariant(a.ToString()[0]) + a.ToString()[1..]]))]);
    }

    /// <summary>Код ошибки словами и сам код: «Адрес сервера не найден · PGC-NET-002».</summary>
    public static string ErrorText(string code) => Loc.Instance["error." + code] + " · " + code;

    /// <summary>Заголовок события.</summary>
    public static string TitleKey(NoticeKind kind) => kind switch
    {
        NoticeKind.ConnectionFailed => "notify.connectFailed.title",
        NoticeKind.NetworkBlocked => "notify.blocked.title",
        NoticeKind.NetworkUnavailable => "notify.offline.title",
        NoticeKind.ReconnectStoppedOffline => "notify.offline.stopped.title",
        NoticeKind.ConnectionLost => "notify.lost.title",
        NoticeKind.ServerDown => "notify.down.title",
        NoticeKind.WifiSignIn => "notify.signIn.title",
        NoticeKind.ServiceStopped => "notify.service.title",
        NoticeKind.ProtectionFailed => "notify.failed.title",
        NoticeKind.Connected => "notify.on.title",
        NoticeKind.Disconnected => "notify.off.title",
        NoticeKind.ServerGone => "notify.gone.title",
        NoticeKind.ServerChanged => "notify.changed.title",
        NoticeKind.UpdateAvailable => "notify.update.title",
        NoticeKind.OtherClient => "notify.other.title",
        _ => "notify.stale.title",
    };
}
