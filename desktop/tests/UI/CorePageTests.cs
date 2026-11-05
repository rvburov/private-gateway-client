using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>«Подключение и ядро» (доска 6); снимок — для сверки с docs/design/screens/06-advanced.png.</summary>
public sealed class CorePageTests : WindowHarness
{
    [AvaloniaFact]
    public void Defaults_match_the_board()
    {
        var (window, shell, page) = OpenCore();

        shell.IsSettings.ShouldBeTrue();
        Text(window, "XrayVersion").ShouldBe("не установлено");
        Text(window, "SecondCore").ShouldBe("не установлено", "имитация службы ядер не сообщает");
        Text(window, "LogChoiceText").ShouldBe("Предупреждения");
        Text(window, "StackChoiceText").ShouldBe("Системный");
        Text(window, "MtuChoiceText").ShouldBe("Авто");
        Text(window, "FingerprintChoiceText").ShouldBe("Из профиля");
        Text(window, "TimeoutChoiceText").ShouldBe("10 с");
        Text(window, "ReconnectChoiceText").ShouldBe("Экспоненц. до 60 с");
        Text(window, "ProxyPort").ShouldBe("10808");
        (page.StrictRoutes, page.Sniffing, page.Mux, page.TlsFragment, page.ProxyAllowLan).ShouldBe((true, true, false, false, false));
        Find<ToggleButton>(window, "SystemProxyToggle").IsChecked.ShouldBe(false, "совместный путь VPN + прокси включается только явно");
        Snapshot(window, "core");
    }

    [AvaloniaFact]
    public void System_proxy_status_displays_confirmed_application_and_failure_independently_of_the_toggle()
    {
        var (window, shell, _) = OpenCore();
        var controller = new ProxyStatusStub();
        var page = new CorePageViewModel(Service<SettingsService>(), Service<IGatewayClient>(), Service<OverlayViewModel>(), shell, controller);
        page.SystemProxy = true;
        window.Content = new CorePage { DataContext = page };
        Dispatcher.UIThread.RunJobs();
        Text(window, "SystemProxyStatus").ShouldBe("Включится после готовности локального прокси");

        controller.Report(new UserProxyStatus(UserProxyState.Applied, 2080));
        Dispatcher.UIThread.RunJobs();
        Text(window, "SystemProxyStatus").ShouldBe("Применен: 127.0.0.1:2080", "показывается фактический порт, а не желаемые 10808");

        controller.Report(new UserProxyStatus(UserProxyState.Error));
        Dispatcher.UIThread.RunJobs();
        page.SystemProxy.ShouldBeTrue("намерение пользователя сохранено, несмотря на ошибку применения");
        Text(window, "SystemProxyStatus").ShouldBe("Не удалось изменить настройки системного прокси");
    }

    private sealed class ProxyStatusStub : IUserProxyController
    {
        public UserProxyStatus Status { get; private set; } = new(UserProxyState.Waiting);
        public event EventHandler<UserProxyStatus>? Changed;
        public void Start() { }
        public void Shutdown() { }
        public void Report(UserProxyStatus status)
        {
            Status = status;
            Changed?.Invoke(this, status);
        }
    }

    [AvaloniaFact]
    public void System_proxy_preferences_are_separate_for_vpn_and_proxy_modes()
    {
        var (_, _, page) = OpenCore();
        var settings = Service<SettingsService>();
        page.SystemProxy.ShouldBeFalse();
        page.SystemProxy = true;
        settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        page.SystemProxy.ShouldBeTrue("старое предпочтение proxy-only сохранено");
        page.SystemProxy = false;
        settings.Update(s => s with { Mode = ConnectionMode.Vpn });
        page.SystemProxy.ShouldBeTrue("VPN имеет отдельный явно выбранный флаг");
        settings.Current.Connection.SystemProxy.ShouldBeFalse();
        settings.Current.Connection.VpnSystemProxy.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Choices_from_the_lists_are_saved_at_once()
    {
        var (window, _, page) = OpenCore();

        page.StackOptions.Select(o => o.Label).ShouldBe(["Системный", "gVisor", "Смешанный"]);
        page.FingerprintOptions.Select(o => o.Label).ShouldBe(["Из профиля", "chrome", "firefox", "safari", "random"]);
        page.TimeoutOptions.Select(o => o.Label).ShouldBe(["10 с", "5 с", "20 с", "30 с"]);
        page.LogOptions[2].ChooseCommand.Execute(null);
        Press(window, "StackChoice");
        page.IsStackOpen.ShouldBeTrue();
        page.StackOptions[1].ChooseCommand.Execute(null);
        page.IsStackOpen.ShouldBeFalse("выбор закрывает список");
        page.FingerprintOptions[1].ChooseCommand.Execute(null);
        page.TimeoutOptions[2].ChooseCommand.Execute(null);
        page.ReconnectOptions[2].ChooseCommand.Execute(null);
        page.Mux = true;
        page.TlsFragment = true;
        page.StrictRoutes = false;
        Find<ToggleButton>(window, "SystemProxyToggle").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Text(window, "StackChoiceText").ShouldBe("gVisor");
        Text(window, "ReconnectChoiceText").ShouldBe("Не повторять");
        Service<SettingsService>().Current.Connection.ShouldBe(ConnectionSettings.Default with
        {
            CoreLog = CoreLogLevel.Debug,
            Stack = TunEngine.Gvisor,
            Fingerprint = TlsFingerprint.Chrome,
            ConnectTimeoutSeconds = 20,
            Reconnect = ReconnectPolicy.Never,
            Mux = true,
            TlsFragment = true,
            StrictRoutes = false,
            VpnSystemProxy = true,
        });
    }

    [AvaloniaFact]
    public void Mtu_is_chosen_from_the_list_or_typed_within_limits()
    {
        var (window, shell, page) = OpenCore();

        Press(window, "MtuChoice");
        page.IsMtuOpen.ShouldBeTrue();
        page.MtuOptions.Select(o => o.Label).ShouldBe(["Авто", "1500", "1420", "1280", "Свое…"]);
        page.MtuOptions[2].ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        page.IsMtuOpen.ShouldBeFalse();
        Text(window, "MtuChoiceText").ShouldBe("1420");

        page.MtuOptions[4].ChooseCommand.Execute(null);
        var sheet = shell.Overlay.Sheet.ShouldBeOfType<MtuViewModel>();
        sheet.Value.ShouldBe("1420");
        sheet.Value = "1000";
        sheet.SaveCommand.Execute(null);
        sheet.Error.ShouldBe("Нужно число от 1280 до 1500");
        sheet.Value = "1600";
        sheet.SaveCommand.Execute(null);
        shell.Overlay.HasSheet.ShouldBeTrue("больше 1500 — тоже нельзя");
        sheet.Value = " 1380 ";
        sheet.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        shell.Overlay.HasSheet.ShouldBeFalse();
        Text(window, "MtuChoiceText").ShouldBe("1380");
        page.MtuOptions.Single(o => o.IsCurrent).Label.ShouldBe("Свое…");
        Service<SettingsService>().Current.Connection.Mtu.ShouldBe(1380);
    }

    [AvaloniaFact]
    public void Opening_the_proxy_to_the_local_network_asks_first()
    {
        var (_, shell, page) = OpenCore();

        page.ProxyAllowLan = true;
        Dispatcher.UIThread.RunJobs();
        var dialog = shell.Overlay.Dialog.ShouldNotBeNull();
        dialog.Title.ShouldBe("Открыть прокси для локальной сети?");
        dialog.PrimaryText.ShouldBe("Не открывать");
        shell.Overlay.DismissCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        page.ProxyAllowLan.ShouldBeFalse("Escape — безопасный вариант");

        page.ProxyAllowLan = true;
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.Dialog!.SecondaryCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        page.ProxyAllowLan.ShouldBeTrue();

        page.ProxyAllowLan = false;
        shell.Overlay.HasDialog.ShouldBeFalse("закрыть можно без вопроса");
        page.ProxyAllowLan.ShouldBeFalse();
    }

    [AvaloniaFact]
    public void Core_versions_come_from_the_service()
    {
        var (window, shell) = Open(gateway: sp => new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Details = new ServiceDetails(AppInfo.Version, ContractMapping.ApiVersion, [new CoreDetails("xray", "25.8.3", true), new CoreDetails("sing-box", "1.14.3", true)]),
        });

        shell.NavigateCommand.Execute(Pages.Core);
        Dispatcher.UIThread.RunJobs();

        Text(window, "XrayVersion").ShouldBe("25.8.3");
        Text(window, "SecondCore").ShouldBe("sing-box 1.14.3", "Hysteria2 и TUIC ведет ретранслятор sing-box за Xray");
    }

    [AvaloniaFact]
    public void Scrolling_the_page_closes_an_open_drop_down()
    {
        // Просьба пользователя 2026-11-05: открытый список «Отпечаток TLS» ехал вместе со страницей поверх нижнего меню.
        var (window, _, page) = OpenCore();
        var scroll = window.GetVisualDescendants().OfType<CorePage>().Single().GetVisualDescendants().OfType<ScrollViewer>().First();

        var popup = window.GetVisualDescendants().OfType<Popup>().Single(p => p.PlacementTarget?.Name == "FingerprintChoice");

        page.ToggleFingerprintCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        popup.IsOpen.ShouldBeTrue();

        scroll.Offset = new Vector(0, scroll.Offset.Y + 40);
        Dispatcher.UIThread.RunJobs();

        page.IsFingerprintOpen.ShouldBeFalse("прокрутка страницы закрывает список, как системные списки");
        popup.IsOpen.ShouldBeFalse();
    }

    private (MainWindow Window, ShellViewModel Shell, CorePageViewModel Page) OpenCore()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Core);
        Dispatcher.UIThread.RunJobs();
        return (window, shell, shell.CurrentPage.ShouldBeOfType<CorePageViewModel>());
    }
}
