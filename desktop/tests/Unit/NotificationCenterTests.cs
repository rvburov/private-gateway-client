using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;
using PrivateGateway.Client.Infrastructure.Storage;
using static PrivateGateway.Client.Tests.Unit.ConnectionControllerTests;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Правила показа уведомлений (доска 22): окно, уровень, повтор, служба, замена и уборка уведомления о защите.</summary>
public sealed class NotificationCenterTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeGateway _gateway = new(ConnectionState.Connected, "srv-1");
    private readonly FakeNotifier _notifier = new();
    private readonly FakeWindow _window = new();
    private readonly SettingsService _settings = new(new MemorySettings());
    private readonly SubscriptionUpdater _updater;
    private readonly NotificationCenter _center;
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-notice-" + Guid.NewGuid().ToString("N")));

    public NotificationCenterTests()
    {
        var catalog = new FakeCatalog(new ServerProfile("srv-1", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Tls,
            "203.0.113.10", 443, "tcp", null))
        { SelectedId = "srv-1" };
        var store = new SqliteServerCatalog(new SqliteDatabase(_paths), new FakeSecrets(), _time);
        _updater = new SubscriptionUpdater(store, new UnusedDownloader(), _gateway, _settings, _time, NullLogger<SubscriptionUpdater>.Instance);
        _center = new NotificationCenter(_gateway, catalog, _updater, _settings, _notifier, _window, new PlainTexts(), _time);

        // Без контекста окна центр работает сразу в потоке события.
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        _center.Start();
        SynchronizationContext.SetSynchronizationContext(context);
    }

    public void Dispose()
    {
        _center.Dispose();
        _updater.Dispose();
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    private void Move(ConnectionState state, string? code = null)
    {
        _gateway.Status = _gateway.Status with
        {
            State = state,
            ErrorCode = code,
            Since = _time.GetUtcNow(),
            Protection = ProtectionChecks.For(state),
        };
        _gateway.Announce();
    }

    [Fact]
    public void Drop_goes_to_the_os_with_sound_and_is_not_repeated_within_ten_minutes()
    {
        Move(ConnectionState.Reconnecting);

        _notifier.Shown.ShouldBe([(Notice.ProtectionTag, "notify.lost.title|Нидерланды 01", true)]);

        Move(ConnectionState.Connected);
        Move(ConnectionState.Reconnecting);
        _notifier.Shown.Count.ShouldBe(1, "повтор того же события — не чаще раза в 10 минут");

        _time.Advance(NotificationCenter.RepeatAfter);
        Move(ConnectionState.Connected);
        Move(ConnectionState.Reconnecting);
        _notifier.Shown.Count.ShouldBe(2);
    }

    [Fact]
    public void Active_window_gets_a_toast_instead_of_the_os()
    {
        _window.IsActive = _window.IsVisible = true;

        Move(ConnectionState.Reconnecting);

        _notifier.Shown.ShouldBeEmpty();
        _window.Toasts.ShouldBe([NoticeKind.ConnectionLost]);
    }

    [Fact]
    public void Level_decides_what_reaches_the_os()
    {
        Move(ConnectionState.Off);
        _notifier.Shown.ShouldBeEmpty("«Только важные» (по умолчанию): отключение — обычное");

        _settings.Update(s => s with { Notifications = NotificationLevel.All });
        Move(ConnectionState.Connecting);
        Move(ConnectionState.Connected);
        _notifier.Shown.ShouldBe([(Notice.ProtectionTag, "notify.on.title|Нидерланды 01", false)], "окно в трее — «Подключено» видно");

        _window.IsVisible = true;
        Move(ConnectionState.Off);
        _notifier.Shown.Count.ShouldBe(1, "окно на экране — «Отключено» и так видно");

        _settings.Update(s => s with { Notifications = NotificationLevel.Off });
        _window.IsVisible = false;
        Move(ConnectionState.Connected);
        Move(ConnectionState.Reconnecting);
        _notifier.Shown.Count.ShouldBe(1, "выключены — только значок в трее");
    }

    [Fact]
    public void Service_must_stay_gone_for_fifteen_seconds()
    {
        Move(ConnectionState.Error, ErrorCodes.ServiceNotRunning);
        _time.Advance(NotificationCenter.ServiceGrace - TimeSpan.FromSeconds(1));
        _notifier.Shown.ShouldBeEmpty("служба Windows перезапускается за 5 с");

        Move(ConnectionState.Connecting);
        Move(ConnectionState.Connected);
        _time.Advance(TimeSpan.FromMinutes(1));
        _notifier.Shown.ShouldBeEmpty("служба вернулась — молчать");

        Move(ConnectionState.Error, ErrorCodes.ServiceNotRunning);
        _time.Advance(NotificationCenter.ServiceGrace);
        _notifier.Shown.ShouldBe([(Notice.ProtectionTag, "notify.service.title|Нидерланды 01", true)]);
    }

    [Fact]
    public void Drop_that_passed_is_taken_away_from_the_os()
    {
        Move(ConnectionState.Reconnecting);
        Move(ConnectionState.Connected);

        _notifier.Closed.ShouldBe([Notice.ProtectionTag], "«Только важные»: «Подключено» не показано — «Соединение потеряно» убрано");

        Move(ConnectionState.Reconnecting);
        _notifier.Closed.Count.ShouldBe(1, "убирать уже нечего");
    }

    [Fact]
    public void Subscription_notices_follow_the_level_and_presses_come_back()
    {
        _center.Announce(new UpdateFailureNotice("Основная", DownloadFailure.Status, 403));
        _notifier.Shown.ShouldBeEmpty("обычное при «Только важные»");

        _settings.Update(s => s with { Notifications = NotificationLevel.All });
        _center.Announce(new SelectedServerNotice("Основная", "Германия 02", SelectedServerChange.Missing));
        _notifier.Shown.ShouldBe([("servers-Основная", "notify.gone.title|Германия 02", false)]);

        _window.IsActive = _window.IsVisible = true;
        _center.Announce(new UpdateFailureNotice("Основная", DownloadFailure.Status, 403));
        _window.Toasts.ShouldBe([NoticeKind.ServersNotUpdated], "окно активно — всплывашка в окне при любом уровне");

        NoticeReply? pressed = null;
        _center.Replied += (_, reply) => pressed = reply;
        _notifier.Press(new NoticeReply("servers-Основная", NoticeAction.ChooseOther));
        pressed.ShouldBe(new NoticeReply("servers-Основная", NoticeAction.ChooseOther));
    }

    [Fact]
    public void Update_is_an_ordinary_notice_of_its_own_with_the_version()
    {
        var offer = new UpdateOffer(UpdateState.Available, _time.GetUtcNow(), "0.3.0") { Installable = true };
        _center.Announce(offer);
        _notifier.Shown.ShouldBeEmpty("обычное при «Только важные»");

        _settings.Update(s => s with { Notifications = NotificationLevel.All });
        Move(ConnectionState.Reconnecting);
        _center.Announce(offer);
        _notifier.Shown[^1].ShouldBe((Notice.UpdateTag, "notify.update.text|0.3.0", false), "своя метка — уведомление о защите не заменяет");
        _notifier.Closed.ShouldBeEmpty();

        _center.Announce(offer with { Version = "0.3.1", Installable = false });
        _notifier.Shown.Count.ShouldBe(2, "повтор — не чаще раза в 10 минут");
        _time.Advance(NotificationCenter.RepeatAfter);
        _center.Announce(offer with { Version = "0.3.1", Installable = false });
        _notifier.Shown[^1].ShouldBe((Notice.UpdateTag, "notify.update.manual|0.3.1", false), "служба сама не ставит — обновляется вручную");
    }

    [Fact]
    public void Os_without_notifications_gets_nothing()
    {
        _notifier.IsSupported = false;

        Move(ConnectionState.Reconnecting);

        _notifier.Shown.ShouldBeEmpty();
    }

    private sealed class FakeNotifier : ISystemNotifier
    {
        public bool IsSupported { get; set; } = true;

        public List<(string Tag, string Text, bool Important)> Shown { get; } = [];

        public List<string> Closed { get; } = [];

        public event EventHandler<NoticeReply>? Replied;

        public void Press(NoticeReply reply) => Replied?.Invoke(this, reply);

        public Task ShowAsync(string tag, NoticeText text, bool important, CancellationToken cancellationToken = default)
        {
            Shown.Add((tag, text.Title + "|" + text.Body, important));
            return Task.CompletedTask;
        }

        public Task CloseAsync(string tag, CancellationToken cancellationToken = default)
        {
            Closed.Add(tag);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWindow : INoticeWindow
    {
        public bool IsActive { get; set; }

        public bool IsVisible { get; set; }

        public List<NoticeKind> Toasts { get; } = [];

        public void ShowInWindow(Notice notice) => Toasts.Add(notice.Kind);
    }

    /// <summary>Текст без локализации: заголовок — ключ события (у обновления — ключ текста), текст — сервер или версия.</summary>
    private sealed class PlainTexts : INoticeTexts
    {
        public NoticeText Text(Notice notice) => new(notice.Kind switch
        {
            NoticeKind.ConnectionLost => "notify.lost.title",
            NoticeKind.ServiceStopped => "notify.service.title",
            NoticeKind.Connected => "notify.on.title",
            NoticeKind.ServerGone => "notify.gone.title",
            NoticeKind.UpdateAvailable => notice.TextKey,
            _ => notice.Kind.ToString(),
        }, notice.Server ?? notice.Version ?? string.Empty, []);
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private AppSettings _value = AppSettings.Default;

        public AppSettings Load() => _value;

        public void Save(AppSettings settings) => _value = settings;
    }

    private sealed class UnusedDownloader : ISubscriptionDownloader
    {
        public Task<SubscriptionContent> DownloadAsync(Uri url, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Тест уведомлений не должен загружать подписки");
    }
}
