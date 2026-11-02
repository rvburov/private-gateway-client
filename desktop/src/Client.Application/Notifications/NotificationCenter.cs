using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;

namespace PrivateGateway.Client.Application.Notifications;

/// <summary>
/// Уведомления по правилам доски 22: окно открыто и активно — всплывашка в окне вместо уведомления ОС; одно уведомление на
/// событие, повтор не чаще <see cref="RepeatAfter"/>; уровень из настроек («Все», «Только важные», «Выключены») решает, что
/// уходит в ОС; «Подключено» и «Отключено» — только при окне, спрятанном в трей. «Служба остановлена» — только если служба
/// не вернулась за <see cref="ServiceGrace"/>: служба Windows перезапускается за 5 с. Уведомление ОС о защите одно: новое
/// заменяет прежнее, а беда, которая прошла, из центра уведомлений убирается. Работа идет в контексте окна (<see cref="Start"/>).
/// </summary>
public sealed class NotificationCenter : IDisposable
{
    /// <summary>Повтор того же события — не чаще.</summary>
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(10);

    /// <summary>Сколько служба может пропадать молча.</summary>
    public static readonly TimeSpan ServiceGrace = TimeSpan.FromSeconds(15);

    private readonly IGatewayClient _gateway;
    private readonly IServerCatalog _catalog;
    private readonly SubscriptionUpdater _updater;
    private readonly SettingsService _settings;
    private readonly ISystemNotifier _notifier;
    private readonly INoticeWindow _window;
    private readonly INoticeTexts _texts;
    private readonly TimeProvider _time;
    private readonly Dictionary<(NoticeKind Kind, string Subject), DateTimeOffset> _shown = [];
    private SynchronizationContext? _context;
    private ConnectionStatus? _last;
    private ITimer? _serviceWait;

    // Что из уведомлений о защите сейчас висит в центре уведомлений ОС.
    private NoticeKind? _protectionShown;

    public NotificationCenter(
        IGatewayClient gateway,
        IServerCatalog catalog,
        SubscriptionUpdater updater,
        SettingsService settings,
        ISystemNotifier notifier,
        INoticeWindow window,
        INoticeTexts texts,
        TimeProvider time)
    {
        _gateway = gateway;
        _catalog = catalog;
        _updater = updater;
        _settings = settings;
        _notifier = notifier;
        _window = window;
        _texts = texts;
        _time = time;
    }

    /// <summary>Нажали уведомление ОС или его кнопку — приходит в контексте окна.</summary>
    public event EventHandler<NoticeReply>? Replied;

    /// <summary>
    /// Слушать службу и подписки. Обычное первое состояние — точка отсчета; уже известный отказ или блокировку нужно
    /// показать сразу: ее переход мог произойти до открытия окна.
    /// </summary>
    public void Start()
    {
        if (_context is not null || _last is not null)
        {
            return;
        }

        _context = SynchronizationContext.Current;
        var initial = _gateway.Status;
        _last = initial;
        _gateway.StatusChanged += OnStatusChanged;
        _updater.SelectedServerChanged += OnSelectedServerChanged;
        _updater.ScheduledUpdateFailed += OnScheduledUpdateFailed;
        _notifier.Replied += OnReplied;
        Post(() =>
        {
            var current = _gateway.Status;
            // Автоподключение может уже прислать новые состояния, пока эта задача ждала UI. Их обработают свои события:
            // нельзя выдать их результат здесь, а затем еще раз после очистки истории на Connecting.
            if (current.State != initial.State || current.ServerId != initial.ServerId || current.Since != initial.Since
                || current.Mode != initial.Mode || current.ErrorCode != initial.ErrorCode || current.StrictBlock != initial.StrictBlock
                || current.CaptivePauseUntil != initial.CaptivePauseUntil || current.NetworkUnavailable != initial.NetworkUnavailable)
            {
                return;
            }

            if (NotificationPolicy.Current(current, ServerName(current), _time.GetUtcNow()) is { } notice)
            {
                Deliver(notice);
            }
        });
    }

    public void Dispose()
    {
        _gateway.StatusChanged -= OnStatusChanged;
        _updater.SelectedServerChanged -= OnSelectedServerChanged;
        _updater.ScheduledUpdateFailed -= OnScheduledUpdateFailed;
        _notifier.Replied -= OnReplied;
        _serviceWait?.Dispose();
    }

    private void OnStatusChanged(object? sender, ConnectionStatus status) => Post(() => OnStatus(status));

    private void OnStatus(ConnectionStatus status)
    {
        var before = _last ?? status;
        _last = status;
        if (before.NetworkUnavailable && (!status.NetworkUnavailable || status.State == ConnectionState.Off))
        {
            // Следующий отдельный обрыв сети должен быть заметен и раньше десяти минут; прежнее уведомление об offline
            // после восстановления уже не описывает текущую сеть. Ошибка остановленных попыток остается в состоянии.
            foreach (var key in _shown.Keys.Where(k => k.Kind is NoticeKind.NetworkUnavailable or NoticeKind.ReconnectStoppedOffline).ToArray())
            {
                _shown.Remove(key);
            }

            if (_protectionShown is NoticeKind.NetworkUnavailable or NoticeKind.ReconnectStoppedOffline)
            {
                _protectionShown = null;
                _ = _notifier.CloseAsync(Notice.ProtectionTag);
            }
        }

        if (status.State == ConnectionState.Connecting
            && (before.State != ConnectionState.Connecting || before.ServerId != status.ServerId || before.Since != status.Since))
        {
            // Явная новая попытка или смена сервера: прежний отказ не должен скрывать результат еще на десять минут.
            // Автоматические повторы остаются в Reconnecting и историю не сбрасывают.
            foreach (var key in _shown.Keys.Where(k => k.Kind is not (NoticeKind.ServerGone or NoticeKind.ServerChanged
                or NoticeKind.ServersNotUpdated or NoticeKind.UpdateAvailable)).ToArray())
            {
                _shown.Remove(key);
            }
        }
        if (status.ErrorCode != ErrorCodes.ServiceNotRunning)
        {
            _serviceWait?.Dispose();
            _serviceWait = null;
        }

        var server = ServerName(status);
        if (NotificationPolicy.For(before, status, server, _time.GetUtcNow()) is not { } notice)
        {
            return;
        }

        if (notice.Kind == NoticeKind.ServiceStopped)
        {
            _serviceWait?.Dispose();
            _serviceWait = _time.CreateTimer(_ => Post(() => ServiceStillGone(notice)), null, ServiceGrace, Timeout.InfiniteTimeSpan);
            return;
        }

        Deliver(notice);
    }

    private string? ServerName(ConnectionStatus status) =>
        _catalog.Servers.FirstOrDefault(s => s.Id == status.ServerId)?.Name ?? _catalog.Selected?.Name;

    private void ServiceStillGone(Notice notice)
    {
        if (_serviceWait is null || _gateway.Status is not { State: ConnectionState.Error, ErrorCode: ErrorCodes.ServiceNotRunning })
        {
            return;
        }

        _serviceWait.Dispose();
        _serviceWait = null;
        Deliver(notice);
    }

    /// <summary>Выбранный сервер пропал из подписки или изменился.</summary>
    public void Announce(SelectedServerNotice change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Post(() => Deliver(
        change.Change == SelectedServerChange.Changed
            ? new Notice(NoticeKind.ServerChanged, "notify.changed.text", [NoticeAction.ChooseServer, NoticeAction.Later])
            {
                Server = change.ServerName, Subscription = change.SubscriptionName,
            }
            : new Notice(NoticeKind.ServerGone, "notify.gone.text", [NoticeAction.ChooseOther, NoticeAction.Later])
            {
                Server = change.ServerName, Subscription = change.SubscriptionName,
            }));
    }

    /// <summary>Подписка не обновилась по расписанию.</summary>
    public void Announce(UpdateFailureNotice failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        Post(() => Deliver(new Notice(NoticeKind.ServersNotUpdated, "notify.stale.text", [NoticeAction.OpenSubscriptions])
        {
            Subscription = failure.SubscriptionName, Failure = failure.Failure, StatusCode = failure.StatusCode,
        }));
    }

    /// <summary>
    /// Нашлась новая версия: служба ставит сама — «загрузится в фоне», нет — «обновляется вручную»; кнопки «Обновить» и
    /// «Позже» (доска 22). Обычное: в ОС — только при уровне «Все».
    /// </summary>
    public void Announce(UpdateOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        Post(() => Deliver(new Notice(NoticeKind.UpdateAvailable, offer.Installable ? "notify.update.text" : "notify.update.manual",
            [NoticeAction.Update, NoticeAction.Later])
        {
            Version = offer.Version,
        }));
    }

    private void OnSelectedServerChanged(object? sender, SelectedServerNotice change) => Announce(change);

    private void OnScheduledUpdateFailed(object? sender, UpdateFailureNotice failure) => Announce(failure);

    private void OnReplied(object? sender, NoticeReply reply) => Post(() =>
    {
        if (reply.Tag == Notice.ProtectionTag)
        {
            _protectionShown = null;
        }

        Replied?.Invoke(this, reply);
    });

    private void Deliver(Notice notice)
    {
        var now = _time.GetUtcNow();
        var key = (notice.Kind, notice.Subscription ?? notice.ServerId ?? string.Empty);
        var state = notice.Kind is NoticeKind.Connected or NoticeKind.Disconnected;
        if (_shown.TryGetValue(key, out var at) && now - at < RepeatAfter)
        {
            Withdraw(notice);
            return;
        }

        if (!state && _window.IsActive)
        {
            _shown[key] = now;
            _window.ShowInWindow(notice);
            Withdraw(notice);
            return;
        }

        if ((state && _window.IsVisible) || !Allowed(notice))
        {
            Withdraw(notice);
            return;
        }

        _shown[key] = now;
        if (notice.Tag == Notice.ProtectionTag)
        {
            _protectionShown = notice.Kind;
        }

        _ = _notifier.ShowAsync(notice.Tag, _texts.Text(notice), notice.IsImportant);
    }

    /// <summary>
    /// Уведомление о защите не показано, а в центре уведомлений ОС висит прежнее о другом — убрать его: «Соединение потеряно»
    /// не должно висеть, когда защита вернулась, а «Подключено» — когда связь снова пропала.
    /// </summary>
    private void Withdraw(Notice notice)
    {
        if (notice.Tag == Notice.ProtectionTag && _protectionShown is { } shown && shown != notice.Kind)
        {
            _protectionShown = null;
            _ = _notifier.CloseAsync(Notice.ProtectionTag);
        }
    }

    private bool Allowed(Notice notice) => _notifier.IsSupported && _settings.Current.Notifications switch
    {
        NotificationLevel.All => true,
        NotificationLevel.ImportantOnly => notice.IsImportant,
        _ => false,
    };

    private void Post(Action action)
    {
        if (_context is { } context)
        {
            context.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }
}
