using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PrivateGateway.Client.Application.Notifications;

namespace PrivateGateway.Client.Infrastructure.Notifications;

/// <summary>
/// macOS: уведомления центра UserNotifications (UNUserNotificationCenter) — их показывает только программа из пакета .app,
/// поэтому вне пакета (запуск из папки сборки) уведомлений нет. Разрешение спрашивается один раз при запуске; отказ —
/// уведомления молча не показываются, о беде говорит значок в трее. Метка — идентификатор запроса: новое уведомление с той
/// же меткой заменяет прежнее. Кнопки — действия категории (у одинакового набора кнопок одна категория). Важное — со звуком
/// и уровнем «активное», остальное — «пассивное»: только в Центре уведомлений, без звука.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacNotifier : ISystemNotifier
{
    // UNAuthorizationOptionSound | UNAuthorizationOptionAlert.
    private const nint AlertAndSound = (1 << 1) | (1 << 2);

    // UNNotificationPresentationOptionSound | List | Banner: программа впереди, а окно не активно — уведомление все равно видно.
    private const nint PresentAll = (1 << 1) | (1 << 3) | (1 << 4);

    // UNNotificationActionOptionForeground: кнопка выводит программу вперед.
    private const nint Foreground = 1 << 2;

    private static readonly Lock Gate = new();
    private static MacNotifier? _current;
    private static nint _ignoreAuthorization;
    private static nint _settingsBlock;
    private static TaskCompletionSource<string?>? _settings;

    private readonly nint _center;
    private readonly Lock _categoriesGate = new();
    private readonly Dictionary<string, nint> _categories = new(StringComparer.Ordinal);

    private MacNotifier(nint center) => _center = center;

    public bool IsSupported => true;

    public event EventHandler<NoticeReply>? Replied;

    /// <summary>
    /// Центр уведомлений программы из пакета .app: делегат для нажатий и запрос разрешения. Не macOS или не из пакета — null
    /// (вне пакета центр уведомлений бросил бы исключение Objective-C и завершил процесс).
    /// </summary>
    public static MacNotifier? ForThisApp()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        lock (Gate)
        {
            if (_current is { } existing)
            {
                return existing;
            }

            ObjC.Load("Foundation");
            ObjC.Load("UserNotifications");
            using var pool = ObjC.Pool();
            var bundle = ObjC.Send(ObjC.Class("NSBundle"), "mainBundle");
            if (ObjC.ToString(ObjC.Send(bundle, "bundleIdentifier")) is null
                || ObjC.ToString(ObjC.Send(bundle, "bundlePath")) is not { } path || !path.EndsWith(".app", StringComparison.Ordinal))
            {
                return null;
            }

            var center = ObjC.Send(ObjC.Class("UNUserNotificationCenter"), "currentNotificationCenter");
            var type = ObjC.DefineClass("PGNoticeDelegate", "UNUserNotificationCenterDelegate",
                ("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidReceive, "v@:@@@?"),
                ("userNotificationCenter:willPresentNotification:withCompletionHandler:", (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&WillPresent, "v@:@@@?"));

            // Делегат центр держит слабой ссылкой: объект живет до конца процесса.
            ObjC.Send(center, "setDelegate:", ObjC.Send(ObjC.Send(type, "alloc"), "init"));
            _ignoreAuthorization = ObjC.GlobalBlock((nint)(delegate* unmanaged<nint, byte, nint, void>)&Ignore);
            ObjC.Send(center, "requestAuthorizationWithOptions:completionHandler:", AlertAndSound, _ignoreAuthorization);
            return _current = new MacNotifier(center);
        }
    }

    /// <summary>
    /// Проверка из пакета (<c>--check-notifications</c>, CI): спросить центр о разрешении. Ответ — «разрешено», «запрещено» и
    /// т. п.; вне пакета — null; центр молчит дольше <paramref name="timeout"/> — <see cref="TimeoutException"/>.
    /// </summary>
    public static Task<string?> CheckAsync(TimeSpan timeout)
    {
        if (ForThisApp() is not { } notifier)
        {
            return Task.FromResult<string?>(null);
        }

        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        nint block;
        lock (Gate)
        {
            _settings = answer;
            block = _settingsBlock = _settingsBlock is 0 ? ObjC.GlobalBlock((nint)(delegate* unmanaged<nint, nint, void>)&Settings) : _settingsBlock;
        }

        using (ObjC.Pool())
        {
            ObjC.Send(notifier._center, "getNotificationSettingsWithCompletionHandler:", block);
        }

        return answer.Task.WaitAsync(timeout);
    }

    public Task ShowAsync(string tag, NoticeText text, bool important, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(text);
        using var pool = ObjC.Pool();
        var content = ObjC.New("UNMutableNotificationContent");
        try
        {
            ObjC.Send(content, "setTitle:", ObjC.String(text.Title));
            ObjC.Send(content, "setBody:", ObjC.String(text.Body));
            if (Category(text.Buttons) is { } category)
            {
                ObjC.Send(content, "setCategoryIdentifier:", ObjC.String(category));
            }

            if (important)
            {
                ObjC.Send(content, "setSound:", ObjC.Send(ObjC.Class("UNNotificationSound"), "defaultSound"));
            }

            ObjC.Send(content, "setInterruptionLevel:", MacNotices.InterruptionLevel(important));
            var request = ObjC.Send(ObjC.Class("UNNotificationRequest"), "requestWithIdentifier:content:trigger:", ObjC.String(tag), content, 0);
            ObjC.Send(_center, "addNotificationRequest:withCompletionHandler:", request, 0);
        }
        finally
        {
            ObjC.Release(content);
        }

        return Task.CompletedTask;
    }

    public Task CloseAsync(string tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        using var pool = ObjC.Pool();
        var identifiers = ObjC.Array(ObjC.String(tag));
        ObjC.Send(_center, "removeDeliveredNotificationsWithIdentifiers:", identifiers);
        ObjC.Send(_center, "removePendingNotificationRequestsWithIdentifiers:", identifiers);
        return Task.CompletedTask;
    }

    /// <summary>Категория набора кнопок: новая — регистрируется в центре вместе с прежними (центр заменяет весь набор).</summary>
    private string? Category(IReadOnlyList<NoticeButton> buttons)
    {
        if (MacNotices.Category(buttons) is not { } name)
        {
            return null;
        }

        lock (_categoriesGate)
        {
            if (_categories.ContainsKey(name))
            {
                return name;
            }

            var actions = buttons.Select(b => ObjC.Send(ObjC.Class("UNNotificationAction"), "actionWithIdentifier:title:options:",
                ObjC.String(MacNotices.Action(b.Action)), ObjC.String(b.Label), Foreground)).ToArray();
            var category = ObjC.Send(ObjC.Class("UNNotificationCategory"), "categoryWithIdentifier:actions:intentIdentifiers:options:",
                ObjC.String(name), ObjC.Array(actions), ObjC.Array(), 0);

            // Категории живут столько же, сколько программа.
            _categories[name] = ObjC.Send(category, "retain");
            var all = ObjC.Send(ObjC.Class("NSSet"), "setWithArray:", ObjC.Array([.. _categories.Values]));
            ObjC.Send(_center, "setNotificationCategories:", all);
            return name;
        }
    }

    private void OnReply(NoticeReply reply) => Replied?.Invoke(this, reply);

    // Исключения из обработчиков, которые вызывает Objective-C, дальше не идут: процесс бы завершился.
    [UnmanagedCallersOnly]
    private static void DidReceive(nint self, nint selector, nint center, nint response, nint completion)
    {
        try
        {
            using var pool = ObjC.Pool();
            var request = ObjC.Send(ObjC.Send(response, "notification"), "request");
            var reply = MacNotices.Reply(ObjC.ToString(ObjC.Send(request, "identifier")), ObjC.ToString(ObjC.Send(response, "actionIdentifier")));
            if (reply is not null)
            {
                Volatile.Read(ref _current)?.OnReply(reply);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Нажатие потерялось — окно откроют из трея.
        }
        finally
        {
            ObjC.Invoke(completion);
        }
    }

    [UnmanagedCallersOnly]
    private static void WillPresent(nint self, nint selector, nint center, nint notification, nint completion) => ObjC.Invoke(completion, PresentAll);

    [UnmanagedCallersOnly]
    private static void Ignore(nint block, byte granted, nint error)
    {
    }

    [UnmanagedCallersOnly]
    private static void Settings(nint block, nint settings)
    {
        string? status;
        try
        {
            // UNAuthorizationStatus: 0 — не спрашивали, 1 — запрещено, 2 — разрешено, 3 — тихо, 4 — на время.
            status = ObjC.Send(settings, "authorizationStatus") switch
            {
                0 => "не спрашивали",
                1 => "запрещено",
                2 => "разрешено",
                3 => "тихо",
                4 => "на время",
                var other => other.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            status = null;
        }

        TaskCompletionSource<string?>? answer;
        lock (Gate)
        {
            (answer, _settings) = (_settings, null);
        }

        answer?.TrySetResult(status);
    }
}
