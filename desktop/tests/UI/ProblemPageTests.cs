using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Ошибки службы (доска 16): «Служба не запущена», «Нужны права администратора» и «Работает другой
/// VPN»; выход через установку службы, режим прокси со службой пользователя, проверку заново. Служба — подделка за настоящим <see cref="GatewayHost"/>; снимки —
/// для сверки с docs/design/screens/16-errors.png.
/// </summary>
public sealed class ProblemPageTests : WindowHarness
{
    private static readonly ServiceAddress Installed = new("installed", System: true);
    private static readonly ServiceAddress User = new("user", System: false);

    private readonly HashSet<ServiceAddress> _answering = [];
    private readonly FakeLauncher _launcher;
    private ScriptedService? _service;
    private ScriptedService? _installed;

    // Установленная служба находит чужой VPN, пока его не выключат или не выберут «Продолжить».
    private bool _otherVpn;
    private OtherClientList? _others;

    public ProblemPageTests() => _launcher = new FakeLauncher(() => _answering.Add(User));

    [AvaloniaFact]
    public async Task Denied_background_service_shows_settings_instead_of_install_and_recovers_without_vpn()
    {
        BackgroundAccess.IsSupported = true;
        BackgroundAccess.Value = ServiceAccess.RequiresApproval;
        Installer.IsAvailable = true;
        var (window, shell) = OpenWithServer();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentPage is ProblemPageViewModel { NeedsApproval: true, IsCheckingAccess: false });
        var page = shell.CurrentPage.ShouldBeOfType<ProblemPageViewModel>();

        Text(window, "ProblemTitle").ShouldBe("Разрешите фоновую работу службы");
        Text(window, "ProblemText").ShouldNotBeNull().ShouldContain("PrivateGatewayService");
        Find<Button>(window, "Install").IsVisible.ShouldBeFalse();
        page.InstallCommand.CanExecute(null).ShouldBeFalse();
        Press(window, "OpenServiceSettings");
        BackgroundAccess.Opened.ShouldBe(1);
        await page.CheckServiceCommand.ExecuteAsync(null);
        shell.CurrentKey.ShouldBe(Pages.Problem, "без разрешения экран остается");
        Installer.Installs.ShouldBe(0);
        Snapshot(window, "problem-background-macos");

        BackgroundAccess.Value = ServiceAccess.Installed;
        await page.CheckServiceCommand.ExecuteAsync(null);
        shell.CurrentKey.ShouldBe(Pages.Problem, "разрешение еще не доказывает связь со службой");
        Text(window, "ProblemText").ShouldNotBeNull().ShouldContain("пока не отвечает");
        _answering.Add(Installed);
        await page.CheckServiceCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentKey == Pages.Home);
        _installed.ShouldNotBeNull().Status.State.ShouldBe(ConnectionState.Off, "проверка связи не включает VPN");
        shell.Navigate(Pages.Problem);
        _installed.Resend();
        Dispatcher.UIThread.RunJobs();
        shell.CurrentKey.ShouldBe(Pages.Problem, "начальный Off без ответа не означает восстановление");
        _installed.HasReceivedStatus = true;
        _installed.Resend();
        WaitFor(() => shell.CurrentKey == Pages.Home);
        Installer.Installs.ShouldBe(0);
        _launcher.Started.ShouldBe(0);
    }

    [AvaloniaFact]
    public async Task User_proxy_service_does_not_offer_reinstall_when_system_background_service_is_denied()
    {
        _answering.Add(User);
        BackgroundAccess.IsSupported = true;
        BackgroundAccess.Value = ServiceAccess.RequiresApproval;
        Installer.IsAvailable = true;
        var (window, shell) = OpenWithServer();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentPage is ProblemPageViewModel { NeedsApproval: true, IsCheckingAccess: false });
        var page = shell.CurrentPage.ShouldBeOfType<ProblemPageViewModel>();
        page.Code.ShouldBe(ErrorCodes.AdminRequired);
        Text(window, "ProblemTitle").ShouldBe("Разрешите фоновую работу службы");
        Find<Button>(window, "Install").IsVisible.ShouldBeFalse();
        Find<Button>(window, "OpenServiceSettings").IsVisible.ShouldBeTrue();
        Find<Button>(window, "Cancel").IsVisible.ShouldBeFalse();
        page.InstallCommand.CanExecute(null).ShouldBeFalse();
        await page.CheckServiceCommand.ExecuteAsync(null);
        Installer.Installs.ShouldBe(0);
        shell.CurrentKey.ShouldBe(Pages.Problem);
        BackgroundAccess.Value = ServiceAccess.Installed;
        _answering.Add(Installed);
        await page.CheckServiceCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentKey == Pages.Home);
        _installed.ShouldNotBeNull().Status.State.ShouldBe(ConnectionState.Off);
    }

    [AvaloniaFact]
    public async Task Unknown_service_permission_is_not_reported_as_denied_or_missing()
    {
        BackgroundAccess.IsSupported = true;
        BackgroundAccess.Value = ServiceAccess.Unknown;
        Installer.IsAvailable = true;
        var (window, shell) = OpenWithServer();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentPage is ProblemPageViewModel { IsCheckingAccess: false });
        var page = shell.CurrentPage.ShouldBeOfType<ProblemPageViewModel>();
        page.NeedsApproval.ShouldBeFalse();
        page.CanInstall.ShouldBeFalse();
        page.Facts[0].Value.ShouldBe("не определено");
        Text(window, "ProblemText").ShouldNotBeNull().ShouldContain("Не удалось определить");

        BackgroundAccess.Value = ServiceAccess.NotInstalled;
        await page.CheckServiceCommand.ExecuteAsync(null);
        Find<Button>(window, "Install").IsVisible.ShouldBeTrue();
        Text(window, "ProblemTitle").ShouldBe("Служба не установлена");
    }

    [AvaloniaFact]
    public async Task Settings_open_failure_gives_the_manual_path()
    {
        BackgroundAccess.IsSupported = true;
        BackgroundAccess.Value = ServiceAccess.RequiresApproval;
        BackgroundAccess.OpenResult = false;
        var (window, shell) = OpenWithServer();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentPage is ProblemPageViewModel { NeedsApproval: true, IsCheckingAccess: false });
        Press(window, "OpenServiceSettings");
        shell.Toast.Text.ShouldContain("Объекты входа и расширения");
        Installer.Installs.ShouldBe(0);
    }

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
    public async Task Install_service_then_connect_through_it_in_vpn_mode()
    {
        Installer.IsAvailable = true;
        Installer.OnInstalled = () => _answering.Add(Installed);
        var (window, shell) = OpenWithServer();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentKey == Pages.Problem);

        Find<Button>(window, "Install").Content.ShouldBe("Установить службу");
        Find<Button>(window, "UseProxy").Classes.ShouldContain("secondary", "по макету главная — установка, режим прокси — вторая");
        Snapshot(window, "problem-install");
        Press(window, "Install");
        WaitFor(() => shell.CurrentKey == Pages.Home);

        var gateway = Service<IGatewayClient>();
        (gateway.Service, gateway.Status.State, gateway.Status.Mode).ShouldBe((ServiceKind.Installed, ConnectionState.Connected, ConnectionMode.Vpn));
        shell.Toast.Text.ShouldBe("Служба установлена");
        Installer.Installs.ShouldBe(1);
    }

    [AvaloniaTheory]
    [InlineData(ServiceInstallResult.Cancelled, null, "Установка отменена: права администратора не даны")]
    [InlineData(ServiceInstallResult.Failed, null, "Служба не установлена: установка не назвала причину — попробуйте еще раз")]
    [InlineData(ServiceInstallResult.Failed, "служба работает от имени системы, а ей не виден .NET 10",
        "Служба не установлена: служба работает от имени системы, а ей не виден .NET 10")]
    public async Task Refused_or_failed_install_keeps_the_screen_and_says_why(ServiceInstallResult result, string? reason, string toast)
    {
        Installer.IsAvailable = true;
        Installer.Result = result;
        Installer.LastError = reason;
        var (window, shell) = OpenWithServer();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentKey == Pages.Problem);

        Press(window, "Install");
        WaitFor(() => shell.Toast.IsVisible);

        shell.Toast.Text.ShouldBe(toast);
        shell.CurrentKey.ShouldBe(Pages.Problem);
        Service<IGatewayClient>().Service.ShouldBe(ServiceKind.None);
    }

    [AvaloniaFact]
    public async Task Rights_screen_asks_again_and_moves_to_the_installed_service()
    {
        Installer.IsAvailable = true;
        Installer.OnInstalled = () => _answering.Add(Installed);
        _answering.Add(User);
        var (window, shell) = OpenWithServer();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentKey == Pages.Problem);

        (Find<Button>(window, "Install").Content, Find<Button>(window, "Cancel").IsVisible, Find<Button>(window, "UseProxy").IsVisible)
            .ShouldBe(("Запросить еще раз", true, false), "по макету: «Запросить еще раз» и «Отмена»");
        Press(window, "Install");
        WaitFor(() => shell.CurrentKey == Pages.Home);

        var gateway = Service<IGatewayClient>();
        (gateway.Service, gateway.Status.State).ShouldBe((ServiceKind.Installed, ConnectionState.Connected), "окно ушло со службы пользователя на установленную");
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
    public async Task Other_vpn_screen_shows_the_signs_checks_again_and_goes_on_only_with_the_setting()
    {
        Installer.IsAvailable = true;
        _answering.Add(Installed);
        _otherVpn = true;
        SystemProxy.Proxy = new UserProxy("127.0.0.1:7890");
        var (window, shell) = OpenWithServer();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.CurrentKey == Pages.Problem);

        // Служба старее окна — находок не присылает: экран по макету.
        var page = shell.CurrentPage.ShouldBeOfType<ProblemPageViewModel>();
        Text(window, "ProblemTitle").ShouldBe("Работает другой VPN или прокси-клиент");
        WaitFor(() => page.Facts.Count == 3);
        page.Facts.Select(f => $"{f.Label}: {f.Value}").ShouldBe(["Туннель другого клиента: ведет весь трафик", "Системный прокси: 127.0.0.1:7890", "Маршрут по умолчанию: занят"],
            "системный прокси другой программы — строка по макету; читает его окно: служба настроек пользователя не видит");
        Find<Button>(window, "Retry").Content.ShouldBe("Я закрыл его — проверить снова");
        window.GetVisualDescendants().OfType<Button>().ShouldNotContain(b => b.Name == "Continue",
            "«Продолжить (не рекомендуется)» нет: рядом с другим VPN защита не включается (решение 507)");
        (Find<Button>(window, "Install").IsVisible, Find<Button>(window, "UseProxy").IsVisible, Find<Button>(window, "Cancel").IsVisible)
            .ShouldBe((false, false, false), "ни установка, ни режим прокси другой VPN не уберут");
        Find<Button>(window, "Details").Content.ShouldBe("Подробнее в журнале · код PGC-NET-010");

        Press(window, "Retry");
        WaitFor(() => shell.Toast.IsVisible);

        shell.Toast.Text.ShouldBe("Другой VPN или прокси-клиент все еще работает — закройте его: значок в трее → «Выход»");
        shell.CurrentKey.ShouldBe(Pages.Problem);

        // Служба прислала признаки: строки — признаками, без названий программ и процессов (решение 525); два ядра — одна строка;
        // вход прокси служба нашла сама — строки окна нет.
        _others = new OtherClientList([new OtherClient(OtherClientSign.Process, "sing-box"), new OtherClient(OtherClientSign.Process, "xray"),
            new OtherClient(OtherClientSign.SystemProxy, "127.0.0.1:7890")]);
        Time.Advance(TimeSpan.FromSeconds(1));
        Press(window, "Retry");
        WaitFor(() => page.Facts.Any(f => f.Label == "Ядро прокси"));

        Text(window, "ProblemTitle").ShouldBe("Работает другой VPN или прокси-клиент");
        Text(window, "ProblemText").ShouldStartWith("Другой VPN или прокси-клиент уже ведет трафик этого компьютера. Закройте его полностью — значок в трее → «Выход»");
        page.Facts.Select(f => $"{f.Label}: {f.Value}").ShouldBe(["Ядро прокси: работает", "Системный прокси: 127.0.0.1:7890"]);
        page.Facts.ShouldAllBe(f => !f.Value.Contains("xray", StringComparison.OrdinalIgnoreCase) && !f.Value.Contains("sing-box", StringComparison.Ordinal));
        shell.Toast.Text.ShouldBe("Другой VPN или прокси-клиент все еще работает — закройте его: значок в трее → «Выход»");
        Snapshot(window, "problem-other-vpn");

        _otherVpn = false;
        Press(window, "Retry");
        WaitFor(() => shell.CurrentKey == Pages.Home);

        Service<IGatewayClient>().Status.State.ShouldBe(ConnectionState.Connected);

        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        _otherVpn = true;
        Service<SettingsService>().Update(s => s with { Protection = s.Protection with { AllowOtherVpn = true } });
        Time.Advance(TimeSpan.FromSeconds(1));
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        (shell.CurrentKey, Service<IGatewayClient>().Status.State).ShouldBe((Pages.Home, ConnectionState.Connected),
            "«Разрешить работу рядом с другим VPN» — подключение идет");
    }

    [AvaloniaFact]
    public void The_setting_to_run_alongside_another_vpn_is_off_by_default_and_saved()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Protection);
        Dispatcher.UIThread.RunJobs();

        var toggle = Find<ToggleButton>(window, "OtherVpnToggle");
        toggle.IsChecked.ShouldBe(false, "по умолчанию рядом с другим VPN защита не включается");
        toggle.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Service<SettingsService>().Current.Protection.AllowOtherVpn.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Proxy_without_a_configured_endpoint_goes_to_settings()
    {
        var (window, shell) = Open(gateway: sp => new GatewayHost(new ServiceLocator([Installed, User], _answering.Contains, _launcher, Time),
            _ => new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.User),
            () => sp.GetRequiredService<SettingsService>().Current, Time));
        shell.NavigateCommand.Execute(Pages.Problem);
        Dispatcher.UIThread.RunJobs();

        Press(window, "UseProxy");
        Dispatcher.UIThread.RunJobs();

        shell.CurrentKey.ShouldBe(Pages.Settings, "внешний прокси настраивается отдельно от VPN-каталога");
        _launcher.Started.ShouldBe(0);
    }

    private (MainWindow Window, ShellViewModel Shell) OpenWithServer()
    {
        var (window, shell) = Open(gateway: sp =>
        {
            AppSettings Settings() => sp.GetRequiredService<SettingsService>().Current;
            // Служба пользователя, как настоящая: прокси подключает сразу, VPN — отказ «нужны права»; установленная подключает все.
            return new GatewayHost(new ServiceLocator([Installed, User], _answering.Contains, _launcher, Time),
                address => address.System
                    ? _installed = new ScriptedService(Time, Settings, ServiceKind.Installed)
                    {
                        Details = new ServiceDetails("0.1.0", "1", []),
                        Outcome = connecting => _otherVpn && !Settings().Protection.AllowOtherVpn
                            ? (connecting with { ErrorCode = ErrorCodes.OtherVpn, OtherClients = _others }).MoveTo(ConnectionState.Error, Time.GetUtcNow()) with
                            {
                                Protection = ProtectionChecks.For(ConnectionState.Off),
                            }
                            : connecting.MoveTo(ConnectionState.Connected, Time.GetUtcNow()),
                    }
                    : _service = new ScriptedService(Time, Settings, ServiceKind.User)
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
        // Обе независимые цели стенда готовы: переключение режима не подставляет VPN-сервер в прокси.
        Service<IExternalProxyStore>().Save(new ExternalProxySettings(Host: "proxy.example.com", Port: 8080), string.Empty, string.Empty);
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
