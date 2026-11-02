using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Какое уведомление дает смена состояния защиты (доска 22).</summary>
public class NotificationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static ConnectionStatus Status(ConnectionState state, ConnectionMode mode = ConnectionMode.Vpn) =>
        ConnectionStatus.Initial(Now) with { State = state, Mode = mode, ServerId = "srv-1", Protection = ProtectionChecks.For(state, mode) };

    private static readonly ConnectionStatus Connected = Status(ConnectionState.Connected);

    private static Notice? For(ConnectionStatus before, ConnectionStatus after) => NotificationPolicy.For(before, after, "Нидерланды 01", Now);

    [Fact]
    public void Lost_connection_says_what_happens_with_the_traffic()
    {
        var lost = For(Connected, Status(ConnectionState.Reconnecting)).ShouldNotBeNull();
        (lost.Kind, lost.TextKey, lost.Server, lost.IsImportant).ShouldBe((NoticeKind.ConnectionLost, "notify.lost.blocked", "Нидерланды 01", true));
        lost.Actions.ShouldBe([NoticeAction.ChangeServer, NoticeAction.Open]);

        var open = Status(ConnectionState.Reconnecting) with { Protection = ProtectionChecks.For(ConnectionState.Reconnecting) with { KillSwitch = CheckState.Off } };
        For(Connected, open)!.TextKey.ShouldBe("notify.lost.direct", "без блока при обрыве трафик идет напрямую");
        For(Status(ConnectionState.Connected, ConnectionMode.Proxy), Status(ConnectionState.Reconnecting, ConnectionMode.Proxy))!.TextKey
            .ShouldBe("notify.lost.proxy");

        For(Connected with { CaptivePauseUntil = Now.AddMinutes(5) }, Status(ConnectionState.Reconnecting)).ShouldBeNull("с паузы — не обрыв");
        For(Status(ConnectionState.Connecting), Status(ConnectionState.Reconnecting) with { Attempt = 1 })!.Kind
            .ShouldBe(NoticeKind.ConnectionFailed, "первая неудача имеет свое уведомление, а не молчит до первого успешного подключения");
    }

    [Fact]
    public void Initial_failure_is_reported_before_any_success_and_automatic_attempts_do_not_repeat_it()
    {
        var connecting = Status(ConnectionState.Connecting);
        var failed = Status(ConnectionState.Reconnecting) with { Attempt = 1 };

        var first = For(connecting, failed).ShouldNotBeNull();
        (first.Kind, first.TextKey, first.ServerId, first.IsImportant)
            .ShouldBe((NoticeKind.ConnectionFailed, "notify.connectFailed.blocked", "srv-1", true));
        first.Actions.ShouldBe([NoticeAction.ChangeServer, NoticeAction.Retry]);

        For(failed, failed with { Attempt = 2 }).ShouldBeNull("автоматическая попытка — не новая ошибка пользователя");
        For(connecting, failed with { Mode = ConnectionMode.Proxy })!.TextKey.ShouldBe("notify.connectFailed.proxy");
        For(connecting, failed with { Protection = ProtectionChecks.For(ConnectionState.Off) })!.TextKey.ShouldBe("notify.connectFailed.direct");
        For(connecting with { CaptivePauseUntil = Now.AddMinutes(5) }, failed).ShouldBeNull("намеренная пауза не является отказом первой попытки");
    }

    [Theory]
    [InlineData(ConnectionState.Error, CheckState.Off, "notify.connectRejected.direct")]
    [InlineData(ConnectionState.Blocked, CheckState.Ok, "notify.connectRejected.blocked")]
    [InlineData(ConnectionState.Error, CheckState.Warning, "notify.connectRejected.unverified")]
    public void Rejected_preflight_reports_the_observed_network_state(ConnectionState state, CheckState block, string key)
    {
        var failed = Status(state) with
        {
            ErrorCode = ErrorCodes.ServerNotResponding,
            Protection = ProtectionChecks.For(state) with { KillSwitch = block, AllTraffic = block == CheckState.Off ? CheckState.Off : CheckState.Stopped },
        };

        var notice = For(Status(ConnectionState.Connecting), failed).ShouldNotBeNull();

        (notice.Kind, notice.TextKey).ShouldBe((NoticeKind.ConnectionFailed, key));
        notice.Actions.ShouldBe([NoticeAction.ChangeServer, NoticeAction.Retry]);
    }

    [Fact]
    public void Opening_the_window_reports_existing_blocking_without_inventing_a_dead_server()
    {
        var saved = Status(ConnectionState.Blocked) with { ServerId = null };
        var notice = NotificationPolicy.Current(saved, null, Now).ShouldNotBeNull();
        (notice.Kind, notice.TextKey, notice.IsImportant).ShouldBe((NoticeKind.NetworkBlocked, "notify.blocked.saved", true));
        notice.Actions.ShouldBe([NoticeAction.ChooseServer, NoticeAction.Open]);

        NotificationPolicy.Current(saved with { StrictBlock = true }, null, Now)!.TextKey.ShouldBe("notify.blocked.strict");
        NotificationPolicy.Current(saved with { CaptivePauseUntil = Now.AddMinutes(5) }, null, Now).ShouldBeNull();
        NotificationPolicy.Current(Status(ConnectionState.Off), null, Now).ShouldBeNull();
        NotificationPolicy.Current(Connected, "Мой", Now).ShouldBeNull();
        NotificationPolicy.Current(Status(ConnectionState.Connecting), "Мой", Now).ShouldBeNull("проверка еще не закончилась — отказ не известен");

        var unverified = saved with { Protection = saved.Protection with { KillSwitch = CheckState.Warning } };
        NotificationPolicy.Current(unverified, null, Now)!.TextKey.ShouldBe("notify.blocked.unverified", "не обещать блокировку без подтверждения");
    }

    [Fact]
    public void Lost_and_failed_protection_do_not_claim_an_unverified_block_is_active()
    {
        var bad = Status(ConnectionState.Reconnecting) with { Protection = ProtectionChecks.For(ConnectionState.Reconnecting) with { KillSwitch = CheckState.Warning } };
        For(Connected, bad)!.TextKey.ShouldBe("notify.lost.unverified");

        For(bad, Status(ConnectionState.Error) with
        {
            ErrorCode = ErrorCodes.KillSwitchFailed,
            Protection = ProtectionChecks.For(ConnectionState.Error) with { KillSwitch = CheckState.Warning },
        })!.TextKey.ShouldBe("notify.failed.unverified");
    }

    [Fact]
    public void Attempts_right_after_a_network_change_suggest_a_wifi_sign_in_once()
    {
        var wifi = new NetworkInfo(NetworkKind.WiFi, null, IsOpen: true, IsMetered: false);
        var first = Status(ConnectionState.Reconnecting) with { Attempt = 1, Network = wifi, NetworkChangedAt = Now.AddSeconds(-30) };
        var second = first with { Attempt = 2 };

        var signIn = For(first, second).ShouldNotBeNull();
        (signIn.Kind, signIn.IsImportant).ShouldBe((NoticeKind.WifiSignIn, true));
        signIn.Actions.ShouldBe([NoticeAction.SignIn, NoticeAction.Later]);

        For(second, second with { Attempt = 3 }).ShouldBeNull("уже сказано");
        For(first, second with { NetworkChangedAt = Now.AddMinutes(-3) }).ShouldBeNull("сеть сменилась давно — не портал");
        For(first, second with { NetworkChangedAt = null }).ShouldBeNull("сеть не менялась");
        For(first, second with { Network = wifi with { Kind = NetworkKind.Mobile } }).ShouldBeNull("мобильная сеть входа не просит");
        For(first, second with { CaptivePauseUntil = Now.AddMinutes(5) }).ShouldBeNull("пауза уже идет");
    }

    [Fact]
    public void Server_down_and_failures_carry_the_reason()
    {
        var down = For(Status(ConnectionState.Reconnecting), Status(ConnectionState.Blocked)).ShouldNotBeNull();
        (down.Kind, down.TextKey).ShouldBe((NoticeKind.ServerDown, "notify.down.blocked"));
        down.Actions.ShouldBe([NoticeAction.Retry, NoticeAction.ChooseServer]);
        For(Status(ConnectionState.Blocked), Status(ConnectionState.Blocked)).ShouldBeNull("то же состояние — свежие цифры");

        var proxy = Status(ConnectionState.Error, ConnectionMode.Proxy) with { ErrorCode = ErrorCodes.ServerNotResponding };
        For(Status(ConnectionState.Reconnecting, ConnectionMode.Proxy), proxy)!.TextKey.ShouldBe("notify.down.proxy");

        var other = For(Status(ConnectionState.Connecting), Status(ConnectionState.Blocked) with { ErrorCode = ErrorCodes.OtherVpn }).ShouldNotBeNull();
        (other.Kind, other.TextKey, other.Code).ShouldBe((NoticeKind.ProtectionFailed, "notify.failed.blocked", ErrorCodes.OtherVpn));

        var core = Status(ConnectionState.Error) with { ErrorCode = ErrorCodes.CoreFailed, Protection = ProtectionChecks.For(ConnectionState.Off) };
        var failed = For(Status(ConnectionState.Connecting), core).ShouldNotBeNull();
        (failed.Kind, failed.TextKey, failed.Code, failed.IsImportant).ShouldBe((NoticeKind.ProtectionFailed, "notify.failed.direct", ErrorCodes.CoreFailed, true));
    }

    [Fact]
    public void Service_stop_counts_only_while_protection_was_on()
    {
        var gone = Status(ConnectionState.Error) with { ErrorCode = ErrorCodes.ServiceNotRunning };

        var stopped = For(Connected, gone).ShouldNotBeNull();
        (stopped.Kind, stopped.TextKey).ShouldBe((NoticeKind.ServiceStopped, "notify.service.text"));
        stopped.Actions.ShouldBe([NoticeAction.Restart]);
        For(Status(ConnectionState.Off), gone).ShouldBeNull("защиты не было — пропажу заметит следующее подключение");
    }

    [Fact]
    public void Connect_and_disconnect_are_ordinary()
    {
        var on = For(Status(ConnectionState.Connecting), Connected).ShouldNotBeNull();
        (on.Kind, on.TextKey, on.IsImportant, on.Actions.Count).ShouldBe((NoticeKind.Connected, "notify.on.text", false, 0));
        For(Status(ConnectionState.Reconnecting), Connected)!.TextKey.ShouldBe("notify.on.back");
        For(Status(ConnectionState.Connecting, ConnectionMode.Proxy), Status(ConnectionState.Connected, ConnectionMode.Proxy))!.TextKey.ShouldBe("notify.on.proxy");
        For(Connected, Connected with { Traffic = new TrafficStats(42, 1, 1) }).ShouldBeNull("свежие цифры — не событие");

        var off = For(Connected, Status(ConnectionState.Off)).ShouldNotBeNull();
        (off.Kind, off.TextKey).ShouldBe((NoticeKind.Disconnected, "notify.off.text"));
        off.Actions.ShouldBe([NoticeAction.Connect]);
        For(Connected, Status(ConnectionState.Blocked) with { StrictBlock = true })!.TextKey.ShouldBe("notify.off.strict");
    }

    [Fact]
    public void Protection_notices_share_one_tag_and_subscriptions_have_their_own()
    {
        new Notice(NoticeKind.ConnectionLost, "k", []).Tag.ShouldBe(Notice.ProtectionTag);
        new Notice(NoticeKind.Connected, "k", []).Tag.ShouldBe(Notice.ProtectionTag);
        new Notice(NoticeKind.ServerGone, "k", []) { Subscription = "Основная" }.Tag.ShouldBe("servers-Основная");
        new Notice(NoticeKind.ServersNotUpdated, "k", []) { Subscription = "Основная" }.IsImportant.ShouldBeFalse();
    }

    [Fact]
    public void Another_client_started_while_connected_is_told_once_in_general_words()
    {
        var other = Connected with
        {
            OtherClients = new OtherClientList([new OtherClient(OtherClientSign.Process, "sing-box")]),
        };

        var started = For(Connected, other).ShouldNotBeNull();
        (started.Kind, started.TextKey, started.IsImportant).ShouldBe((NoticeKind.OtherClient, "notify.other.text", true),
            "общими словами, без названия программы (решение 525)");
        started.Actions.ShouldBe([NoticeAction.Open]);
        For(other, other with { Traffic = new TrafficStats(40, 1, 1) }).ShouldBeNull("те же признаки — уведомление уже было");
        For(other, Connected).ShouldBeNull("клиент закрылся — сказать нечего");

        For(Status(ConnectionState.Connecting), other).ShouldNotBeNull().Kind.ShouldBe(NoticeKind.Connected, "подключились — об этом и речь");
    }
}
