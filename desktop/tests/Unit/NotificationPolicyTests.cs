using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Core;

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
        For(Status(ConnectionState.Connecting), Status(ConnectionState.Reconnecting) with { Attempt = 1 }).ShouldBeNull("еще не было защиты — не обрыв");
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
}
