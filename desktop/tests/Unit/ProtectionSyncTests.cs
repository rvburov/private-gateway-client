using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Настройки на ходу: изменения, пока защита включена, — одно переподключение к тому же серверу.</summary>
public sealed class ProtectionSyncTests
{
    private static readonly ServerProfile Nl =
        new("nl", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, "203.0.113.24", 443, "tcp", 42);

    [Fact]
    public void Changes_while_protected_reconnect_once_after_a_quiet_moment()
    {
        var (settings, gateway, time) = Start(ConnectionState.Connected);

        settings.Update(s => s with { Protection = s.Protection with { AllowLan = true } });
        time.Advance(ProtectionSync.Quiet / 2);
        settings.Update(s => s with { KillSwitch = false });
        time.Advance(ProtectionSync.Quiet / 2);

        gateway.Calls.ShouldBeEmpty("изменения идут подряд — ждать тишины");

        time.Advance(ProtectionSync.Quiet);

        gateway.Calls.ShouldBe(["connect:nl"], "два изменения — одно переподключение к тому же серверу");
    }

    [Theory]
    [InlineData(ConnectionState.Off)]
    [InlineData(ConnectionState.Error)]
    public void Without_protection_settings_wait_for_the_next_connection(ConnectionState state)
    {
        var (settings, gateway, time) = Start(state);

        settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        time.Advance(ProtectionSync.Quiet * 2);

        gateway.Calls.ShouldBeEmpty("защита не включена — подключаться без просьбы нельзя");
    }

    [Fact]
    public void Pause_for_wifi_is_not_ended_by_a_setting()
    {
        var (settings, gateway, time) = Start(ConnectionState.Connected, pausedUntil: DateTimeOffset.UnixEpoch.AddMinutes(5));

        settings.Update(s => s with { Connection = s.Connection with { Mtu = 1400 } });
        time.Advance(ProtectionSync.Quiet * 2);

        gateway.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Strict_mode_block_opens_the_network_when_strict_mode_stops_and_never_reconnects()
    {
        var (settings, gateway, time) = Start(ConnectionState.Blocked, strict: true,
            change: s => s with { Protection = s.Protection with { StrictMode = true } });

        settings.Update(s => s with { Protection = s.Protection with { AllowLan = true } });
        time.Advance(ProtectionSync.Quiet * 2);

        gateway.Calls.ShouldBeEmpty("защиту выключили вручную — подключаться без просьбы нельзя, сеть закрыта по-прежнему");

        settings.Update(s => s with { Protection = s.Protection with { StrictMode = false } });
        time.Advance(ProtectionSync.Quiet * 2);

        gateway.Calls.ShouldBe(["disconnect"], "строгий режим выключен — сеть открывается");
    }

    [Fact]
    public void Strict_mode_turned_off_while_the_service_was_away_opens_the_block_it_sends_later_once()
    {
        var (settings, gateway, time) = Start(ConnectionState.Error, change: s => s with { Protection = s.Protection with { StrictMode = true } });

        // Службы нет («Ошибка»): строгий режим выключили, а открыть еще нечего.
        settings.Update(s => s with { Protection = s.Protection with { StrictMode = false } });
        time.Advance(ProtectionSync.Quiet * 2);
        gateway.Calls.ShouldBeEmpty();

        // Служба вернулась и помнит строгий режим.
        gateway.Status = gateway.Status with { State = ConnectionState.Blocked, StrictBlock = true };
        gateway.Announce();
        gateway.Announce();

        gateway.Calls.ShouldBe(["disconnect"], "одна устаревшая «Блокировка» — одно отключение");
    }

    [Fact]
    public void Strict_block_this_window_did_not_turn_off_is_left_closed()
    {
        // Другой пользователь машины или сброшенные настройки окна: строгий режим в окне выключен, а выключало его не оно.
        var (_, gateway, _) = Start(ConnectionState.Blocked, strict: true);

        gateway.Announce();

        gateway.Calls.ShouldBeEmpty("открыть сеть без нажатия можно, только если строгий режим выключили в этом окне");
    }

    [Fact]
    public void Strict_block_of_the_strict_mode_that_is_on_stays_closed_also_on_a_pause()
    {
        var (_, gateway, _) = Start(ConnectionState.Blocked, pausedUntil: DateTimeOffset.UnixEpoch.AddMinutes(5), strict: true,
            change: s => s with { Protection = s.Protection with { StrictMode = true } });

        gateway.Announce();

        gateway.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Settings_the_service_does_not_get_do_not_reconnect()
    {
        var (settings, gateway, time) = Start(ConnectionState.Connected);

        settings.Update(s => s with { Language = "en", MinimizeToTray = false });
        time.Advance(ProtectionSync.Quiet * 2);

        gateway.Calls.ShouldBeEmpty();
        var d = AppSettings.Default;
        new[]
        {
            d with { Mode = ConnectionMode.Proxy },
            d with { KillSwitch = false },
            d with { Protection = d.Protection with { BlockIpv6 = false } },
            d with { Connection = d.Connection with { Mtu = 1400 } },
            d with { Routing = d.Routing with { Mode = RoutingMode.Split } },
        }.ShouldAllBe(changed => ProtectionSync.Affects(d, changed), "все это служба получает в подключении");
    }

    private static (SettingsService Settings, ConnectionControllerTests.FakeGateway Gateway, FakeTimeProvider Time) Start(
        ConnectionState state, DateTimeOffset? pausedUntil = null, bool strict = false, Func<AppSettings, AppSettings>? change = null)
    {
        var settings = new SettingsService(new MemorySettings());
        if (change is not null)
        {
            settings.Update(change);
        }

        var gateway = new ConnectionControllerTests.FakeGateway(state, "nl", pausedUntil, strict);
        var time = new FakeTimeProvider();
        new ProtectionSync(settings, gateway, new ConnectionControllerTests.FakeCatalog(Nl) { SelectedId = "nl" }, time).Start();
        return (settings, gateway, time);
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private AppSettings _saved = AppSettings.Default;

        public AppSettings Load() => _saved;

        public void Save(AppSettings settings) => _saved = settings;
    }
}
