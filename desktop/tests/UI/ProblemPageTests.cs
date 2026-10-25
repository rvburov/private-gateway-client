using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Ошибки службы (доска 16): «Служба не запущена» и «Нужны права администратора», выход через режим
/// прокси со службой пользователя. Служба — подделка за настоящим <see cref="GatewayHost"/>; снимки —
/// для сверки с docs/design/screens/16-errors.png.
/// </summary>
public sealed class ProblemPageTests : WindowHarness
{
    private static readonly ServiceAddress Installed = new("installed", System: true);
    private static readonly ServiceAddress User = new("user", System: false);

    private readonly HashSet<ServiceAddress> _answering = [];
    private readonly FakeLauncher _launcher;
    private ScriptedService? _service;

    public ProblemPageTests() => _launcher = new FakeLauncher(() => _answering.Add(User));

    [AvaloniaFact]
    public async Task Connect_without_a_service_opens_the_service_screen()
    {
        var (window, shell) = OpenWithServer();

        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentKey == Pages.Problem);

        var page = shell.CurrentPage.ShouldBeOfType<ProblemPageViewModel>();
        Text(window, "ProblemTitle").ShouldBe("Служба не запущена");
        page.Facts.Select(f => $"{f.Label}: {f.Value}").ShouldBe(
            ["Служба Private Gateway: не найдена", "Сетевой адаптер (TUN): нет", "Правила брандмауэра: нет"]);
        Find<Button>(window, "Cancel").IsVisible.ShouldBeFalse();
        Find<Button>(window, "Details").Content.ShouldBe("Подробнее в журнале · код PGC-SVC-001");
        shell.IsIslandVisible.ShouldBeTrue("окно работает — уйти можно на любой экран");
        _launcher.Started.ShouldBe(0, "режим VPN — служба пользователя не поможет");
        Snapshot(window, "problem-service");

        shell.NavigateCommand.Execute(Pages.Home);
        Dispatcher.UIThread.RunJobs();
        shell.CurrentPage.ShouldBeOfType<MainPageViewModel>().StatusSubtitle
            .ShouldBe("PGC-SVC-001 · защиты нет, трафик идет напрямую", "без службы ничего не блокируется");

        // Каждое нажатие — новая ошибка и снова экран; то же состояние, присланное повторно, экран не открывает.
        Time.Advance(TimeSpan.FromSeconds(1));
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentKey == Pages.Problem);
    }

    [AvaloniaFact]
    public async Task Proxy_mode_starts_the_user_service_and_connects()
    {
        var (window, shell) = OpenWithServer();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentKey == Pages.Problem);

        Press(window, "UseProxy");
        WaitFor(() => shell.CurrentKey == Pages.Home);

        var gateway = Service<IGatewayClient>();
        (gateway.Status.State, gateway.Status.Mode, gateway.Service).ShouldBe((ConnectionState.Connected, ConnectionMode.Proxy, ServiceKind.User));
        Service<SettingsService>().Current.Mode.ShouldBe(ConnectionMode.Proxy, "выбор запомнен: дальше — сразу режим прокси");
        _launcher.Started.ShouldBe(1);
    }

    [AvaloniaFact]
    public async Task User_service_that_does_not_start_keeps_the_screen_and_says_so()
    {
        _launcher.Fails = true;
        var (window, shell) = OpenWithServer();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentKey == Pages.Problem);

        Press(window, "UseProxy");
        WaitFor(() => shell.Toast.IsVisible);

        shell.Toast.Text.ShouldBe("Служба пользователя не запустилась — подробности в журнале");
        shell.CurrentKey.ShouldBe(Pages.Problem);
        Service<IGatewayClient>().Journal.ShouldContain(e => e.Message.Contains("файла службы нет", StringComparison.Ordinal));
        Press(window, "Details");
        Dispatcher.UIThread.RunJobs();
        shell.CurrentKey.ShouldBe(Pages.Check);
    }

    [AvaloniaFact]
    public async Task Vpn_through_the_user_service_asks_for_administrator_rights()
    {
        _answering.Add(User);
        var (window, shell) = OpenWithServer();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentKey == Pages.Problem);

        var page = shell.CurrentPage.ShouldBeOfType<ProblemPageViewModel>();
        Text(window, "ProblemTitle").ShouldBe("Нужны права администратора");
        page.Facts.Select(f => $"{f.Label}: {f.Value}").ShouldBe(
            ["Служба Private Gateway: без прав", "Режим VPN: недоступен", "Режим прокси: доступен"]);
        Find<Button>(window, "Details").Content.ShouldBe("Подробнее в журнале · код PGC-SVC-004");
        Snapshot(window, "problem-admin");

        Press(window, "Cancel");
        Dispatcher.UIThread.RunJobs();
        shell.CurrentKey.ShouldBe(Pages.Home);
        _service!.Resend();
        Dispatcher.UIThread.RunJobs();
        shell.CurrentKey.ShouldBe(Pages.Home, "то же состояние, присланное еще раз, экран не открывает");

        Time.Advance(TimeSpan.FromSeconds(1));
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentKey == Pages.Problem);
        Press(window, "UseProxy");
        WaitFor(() => shell.CurrentKey == Pages.Home);
        Service<IGatewayClient>().Status.State.ShouldBe(ConnectionState.Connected);
        _launcher.Started.ShouldBe(0, "служба пользователя уже работает");
    }

    [AvaloniaFact]
    public async Task Screen_follows_the_newest_service_error()
    {
        var (window, shell) = OpenWithServer();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentKey == Pages.Problem);
        Text(window, "ProblemTitle").ShouldBe("Служба не запущена");

        // Пользователь запустил службу пользователя сам, а режим остался VPN.
        _answering.Add(User);
        Time.Advance(TimeSpan.FromSeconds(1));
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => Text(window, "ProblemTitle") == "Нужны права администратора");

        Find<Button>(window, "Cancel").IsVisible.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Proxy_without_servers_goes_to_adding_one()
    {
        var (window, shell) = Open(gateway: sp => new GatewayHost(new ServiceLocator([Installed, User], _answering.Contains, _launcher, Time),
            _ => new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.User),
            () => sp.GetRequiredService<SettingsService>().Current, Time));
        shell.NavigateCommand.Execute(Pages.Problem);
        Dispatcher.UIThread.RunJobs();

        Press(window, "UseProxy");
        Dispatcher.UIThread.RunJobs();

        shell.CurrentKey.ShouldBe(Pages.Add, "подключаться не к чему — сначала добавить сервер");
        _launcher.Started.ShouldBe(0);
    }

    private (MainWindow Window, ShellViewModel Shell) OpenWithServer()
    {
        var (window, shell) = Open(gateway: sp =>
        {
            AppSettings Settings() => sp.GetRequiredService<SettingsService>().Current;
            // Служба пользователя, как настоящая: прокси подключает сразу, VPN — отказ «нужны права».
            return new GatewayHost(new ServiceLocator([Installed, User], _answering.Contains, _launcher, Time),
                _ => _service = new ScriptedService(Time, Settings, ServiceKind.User)
                {
                    Outcome = connecting => connecting.Mode == ConnectionMode.Vpn
                        ? (connecting with { ErrorCode = ErrorCodes.AdminRequired }).MoveTo(ConnectionState.Error, Time.GetUtcNow()) with
                        {
                            Protection = ProtectionChecks.For(ConnectionState.Off),
                        }
                        : connecting.MoveTo(ConnectionState.Connected, Time.GetUtcNow()),
                },
                Settings, Time);
        });
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);
        Dispatcher.UIThread.RunJobs();
        return (window, shell);
    }

    private sealed class FakeLauncher(Action started) : IUserServiceLauncher
    {
        public int Started { get; private set; }

        public bool Fails { get; set; }

        public bool Start()
        {
            if (Fails)
            {
                return false;
            }

            Started++;
            started();
            return true;
        }
    }
}
