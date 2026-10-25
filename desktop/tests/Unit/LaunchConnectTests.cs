using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>«Подключаться при запуске»: окно открылось — защита через выбранный сервер, если ее еще нет.</summary>
public sealed class LaunchConnectTests
{
    private static readonly ServerProfile Nl =
        new("nl", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, "203.0.113.24", 443, "tcp", 42);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ConnectionState.Off)]
    [InlineData(ConnectionState.Blocked)]
    [InlineData(ConnectionState.Error)]
    public async Task Without_protection_connects_once_the_service_told_its_state(ConnectionState state)
    {
        var (launch, gateway, _) = Start(state);

        var run = launch.RunAsync(Ct);

        gateway.Calls.ShouldBeEmpty("состояние службы еще не пришло — она могла держать защиту без окна");

        gateway.Announce();
        await run;

        gateway.Calls.ShouldBe(["connect:nl"], "«Блокировка» после сбоя службы — подключение возвращает защиту");
    }

    [Theory]
    [InlineData(ConnectionState.Connected)]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Reconnecting)]
    public async Task Protection_kept_by_the_service_is_left_as_is(ConnectionState state)
    {
        var (launch, gateway, _) = Start(state);

        var run = launch.RunAsync(Ct);
        gateway.Announce();
        await run;

        gateway.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Silent_service_is_waited_for_a_moment_only()
    {
        var (launch, gateway, time) = Start(ConnectionState.Off);

        var run = launch.RunAsync(Ct);
        time.Advance(LaunchConnect.Wait);
        await run;

        gateway.Calls.ShouldBe(["connect:nl"]);
    }

    [Fact]
    public async Task Nothing_without_the_setting_the_first_run_or_a_server()
    {
        (await Calls(s => s with { ConnectOnLaunch = false })).ShouldBeEmpty("переключатель выключен");
        (await Calls(s => s with { OnboardingDone = false })).ShouldBeEmpty("первый запуск еще идет");
        (await Calls(s => s, selected: null)).ShouldBeEmpty("сервер не выбран");
    }

    private static async Task<List<string>> Calls(Func<AppSettings, AppSettings> change, string? selected = "nl")
    {
        var (launch, gateway, time) = Start(ConnectionState.Off, change, selected);
        var run = launch.RunAsync(Ct);
        gateway.Announce();
        time.Advance(LaunchConnect.Wait);
        await run;
        return gateway.Calls;
    }

    private static (LaunchConnect Launch, ConnectionControllerTests.FakeGateway Gateway, FakeTimeProvider Time) Start(ConnectionState state,
        Func<AppSettings, AppSettings>? change = null, string? selected = "nl")
    {
        var settings = new SettingsService(new MemorySettings());
        settings.Update(s => (change ?? (x => x))(s with { ConnectOnLaunch = true, OnboardingDone = true }));
        var gateway = new ConnectionControllerTests.FakeGateway(state, "nl");
        var time = new FakeTimeProvider();
        return (new LaunchConnect(settings, gateway, new ConnectionControllerTests.FakeCatalog(Nl) { SelectedId = selected }, time), gateway, time);
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private AppSettings _saved = AppSettings.Default;

        public AppSettings Load() => _saved;

        public void Save(AppSettings settings) => _saved = settings;
    }
}
