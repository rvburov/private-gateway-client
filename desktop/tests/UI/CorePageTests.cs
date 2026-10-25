using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PrivateGateway.Client.Application.Settings;
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
        Text(window, "SecondCore").ShouldBe("не выбрано");
        Text(window, "LogChoiceText").ShouldBe("Предупреждения");
        Text(window, "StackChoiceText").ShouldBe("Системный");
        Text(window, "MtuChoiceText").ShouldBe("Авто");
        Text(window, "FingerprintChoiceText").ShouldBe("Из профиля");
        Text(window, "TimeoutChoiceText").ShouldBe("10 с");
        Text(window, "ReconnectChoiceText").ShouldBe("Экспоненц. до 60 с");
        Text(window, "ProxyPort").ShouldBe("10808");
        (page.StrictRoutes, page.Sniffing, page.Mux, page.TlsFragment, page.ProxyAllowLan).ShouldBe((true, true, false, false, false));
        Snapshot(window, "core");
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

    private (MainWindow Window, ShellViewModel Shell, CorePageViewModel Page) OpenCore()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Core);
        Dispatcher.UIThread.RunJobs();
        return (window, shell, shell.CurrentPage.ShouldBeOfType<CorePageViewModel>());
    }
}
