using PrivateGateway.Client.Application.Notifications;

namespace PrivateGateway.Client.UI.Localization;

/// <summary>
/// Тексты уведомлений на языке интерфейса (доски 19 и 22): заголовок по событию, текст по ключу —
/// <c>{0}</c> сервер, <c>{1}</c> подписка, <c>{2}</c> причина: код ошибки словами или почему подписка не загрузилась.
/// </summary>
public sealed class NoticeTexts : INoticeTexts
{
    public NoticeText Text(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var reason = notice.Code is { } code ? ErrorText(code)
            : notice.Failure is { } failure ? FailureText.Of(failure, notice.StatusCode)
            : string.Empty;
        return new NoticeText(
            Loc.Instance[TitleKey(notice.Kind)],
            Loc.Instance.Format(notice.TextKey, notice.Server ?? Loc.Instance["notify.server.none"], notice.Subscription ?? string.Empty, reason),
            [.. notice.Actions.Select(a => new NoticeButton(a, Loc.Instance["notify.action." + char.ToLowerInvariant(a.ToString()[0]) + a.ToString()[1..]]))]);
    }

    /// <summary>Код ошибки словами и сам код: «Адрес сервера не найден · PGC-NET-002».</summary>
    public static string ErrorText(string code) => Loc.Instance["error." + code] + " · " + code;

    /// <summary>Заголовок события.</summary>
    public static string TitleKey(NoticeKind kind) => kind switch
    {
        NoticeKind.ConnectionLost => "notify.lost.title",
        NoticeKind.ServerDown => "notify.down.title",
        NoticeKind.WifiSignIn => "notify.signIn.title",
        NoticeKind.ServiceStopped => "notify.service.title",
        NoticeKind.ProtectionFailed => "notify.failed.title",
        NoticeKind.Connected => "notify.on.title",
        NoticeKind.Disconnected => "notify.off.title",
        NoticeKind.ServerGone => "notify.gone.title",
        NoticeKind.ServerChanged => "notify.changed.title",
        _ => "notify.stale.title",
    };
}
