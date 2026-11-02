using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>Подтвержденное отсутствие сети отличается от ошибки сервера и от неизвестной сети старой службы.</summary>
public sealed class OfflineNetworkTests : WindowHarness
{
    [AvaloniaTheory]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Connected)]
    [InlineData(ConnectionState.Reconnecting)]
    [InlineData(ConnectionState.Blocked)]
    [InlineData(ConnectionState.Error)]
    public async Task Confirmed_offline_is_visible_without_a_spinner_or_a_server_change(ConnectionState state)
    {
        ScriptedService? service = null;
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        var machine = service.ShouldNotBeNull();
        machine.Push(s => s with
        {
            State = state,
            NetworkUnavailable = true,
            Network = NetworkInfo.Unknown,
            Attempt = 101,
            Protection = state == ConnectionState.Connecting ? ProtectionChecks.For(ConnectionState.Off) : ProtectionChecks.For(state),
            Exit = s.Exit with { Ip = "203.0.113.24" },
            Traffic = new TrafficStats(42, 10, 5),
        });
        Dispatcher.UIThread.RunJobs();
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();

        main.StatusTitle.ShouldBe("Нет сети");
        shell.Protection.Label.ShouldBe("Нет сети");
        shell.Protection.Mark.ShouldBe("Icon.Mark.Alert");
        shell.Protection.IsBusy.ShouldBeFalse("ожидание появления физической сети — не бесконечная попытка соединения");
        shell.Protection.Tone.ShouldNotBe(Tone.Connected);
        main.NetworkText.ShouldBe("Нет сети");
        main.HasTimer.ShouldBeFalse();
        main.WebIp.ShouldBe("—");
        main.PingText.ShouldBe("—");
        main.IsPingGood.ShouldBeFalse();
        main.ConnectionProblemAction.ShouldBe("Проверить сеть");
        main.ConnectionProblemPage.ShouldBe(Pages.Check);
        Find<Button>(window, "ConnectionProblemBanner").IsVisible.ShouldBeTrue();
        main.CheckActionText.ShouldBe("Проверить сеть");
        if (state == ConnectionState.Reconnecting)
        {
            main.StatusSubtitle.ShouldContain("продолжится автоматически");
        }
        else
        {
            main.StatusSubtitle.Contains("автоматически", StringComparison.Ordinal)
                .ShouldBeFalse("такое обещание допустимо только для активного Reconnecting");
        }

        await main.CheckActionCommand.ExecuteAsync(null);
        shell.CurrentKey.ShouldBe(Pages.Check);
        machine.Status.State.ShouldBe(state);
        machine.Pauses.ShouldBe(0, "кнопка не открывает сеть и не начинает паузу входа в Wi-Fi");
    }

    [AvaloniaFact]
    public void Unknown_network_is_not_offline_and_restored_waiting_resumes_its_normal_presentation()
    {
        ScriptedService? service = null;
        var (_, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        var machine = service.ShouldNotBeNull();
        machine.Push(s => s with
        {
            State = ConnectionState.Reconnecting,
            Network = NetworkInfo.Unknown,
            NetworkUnavailable = false,
            Protection = ProtectionChecks.For(ConnectionState.Reconnecting),
        });
        Dispatcher.UIThread.RunJobs();
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();

        main.NetworkText.ShouldBe("Сеть не определена");
        main.StatusTitle.ShouldBe("Переподключение…");
        shell.Protection.IsBusy.ShouldBeTrue("старый IPC не имеет нового bool, и отсутствие типа сети не останавливает попытки");

        machine.Push(s => s with { NetworkUnavailable = true });
        Dispatcher.UIThread.RunJobs();
        main.StatusTitle.ShouldBe("Нет сети");
        shell.Protection.IsBusy.ShouldBeFalse();

        machine.Push(s => s with { NetworkUnavailable = false, Network = new NetworkInfo(NetworkKind.WiFi, null, false, false) });
        Dispatcher.UIThread.RunJobs();
        main.StatusTitle.ShouldBe("Переподключение…");
        shell.Protection.IsBusy.ShouldBeTrue();
        main.ConnectionProblemTitle.ShouldNotBe("Нет сети");
        main.ConnectionProblemAction.ShouldBe("Сменить сервер");
    }

    [AvaloniaTheory]
    [InlineData(ConnectionState.Blocked)]
    [InlineData(ConnectionState.Error)]
    public async Task Expired_offline_attempts_keep_manual_retry_instructions_after_network_observation_changes(ConnectionState state)
    {
        ScriptedService? service = null;
        var (_, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        var machine = service.ShouldNotBeNull();
        machine.Push(s => s with
        {
            State = state,
            NetworkUnavailable = true,
            ErrorCode = ErrorCodes.NetworkUnavailable,
            Protection = ProtectionChecks.For(state),
        });
        Dispatcher.UIThread.RunJobs();
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();
        main.StatusSubtitle.ShouldContain("вручную");
        main.ConnectionProblemText.ShouldContain("повторите подключение вручную");
        main.ConnectionProblemText.ShouldNotContain("автоматически");

        machine.Push(s => s with { NetworkUnavailable = false, Network = NetworkInfo.Unknown });
        Dispatcher.UIThread.RunJobs();
        main.StatusTitle.ShouldNotBe("Нет сети", "прошлая причина не заменяет новое наблюдение сети");
        main.NetworkText.ShouldBe("Сеть не определена");
        main.ConnectionProblemText.ShouldContain("сеть была недоступна");
        main.ConnectionProblemText.ShouldNotContain("Сетевого подключения нет");
        main.ConnectionProblemText.ShouldNotContain("автоматически");
        main.ConnectionProblemAction.ShouldBe("Проверить сеть");
        main.CheckActionText.ShouldBe("Проверить сеть", "историческая ошибка сети не требует перезапуска исправной службы");
        shell.Protection.IsBusy.ShouldBeFalse();

        await main.CheckActionCommand.ExecuteAsync(null);
        shell.CurrentKey.ShouldBe(Pages.Check);
        machine.Status.State.ShouldBe(state, "проверка не запускает заново исчерпанную попытку");
    }

    [AvaloniaFact]
    public async Task Offline_os_notice_checks_the_network_instead_of_changing_server_or_removing_the_block()
    {
        ScriptedService? service = null;
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        var machine = service.ShouldNotBeNull();
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);
        Service<NoticeWindow>().Attach(window, window.Show);
        Service<NoticeActions>().Start();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var selected = machine.Status.ServerId;
        window.Hide();
        machine.Push(s => s with { NetworkUnavailable = true, Network = NetworkInfo.Unknown });
        Dispatcher.UIThread.RunJobs();

        var notice = Notifier.Shown.ShouldHaveSingleItem();
        notice.Title.ShouldBe("Нет сети");
        notice.Body.ShouldContain("Блок при обрыве удерживает");
        notice.Buttons.Select(b => b.Label).ShouldBe(["Проверить сеть"]);
        Notifier.Press(new NoticeReply(Notice.ProtectionTag, NoticeAction.CheckNetwork));
        Dispatcher.UIThread.RunJobs();

        window.IsVisible.ShouldBeTrue();
        shell.CurrentKey.ShouldBe(Pages.Check);
        machine.Status.ServerId.ShouldBe(selected);
        machine.Status.Protection.KillSwitch.ShouldBe(CheckState.Ok);
        machine.Pauses.ShouldBe(0);
    }

    [AvaloniaFact]
    public void Offline_does_not_hide_a_new_firewall_failure_or_a_missing_service()
    {
        ScriptedService? service = null;
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        var machine = service.ShouldNotBeNull();
        machine.Push(s => s with
        {
            State = ConnectionState.Reconnecting,
            NetworkUnavailable = true,
            ErrorCode = ErrorCodes.KillSwitchFailed,
            Protection = ProtectionChecks.For(ConnectionState.Reconnecting) with { KillSwitch = CheckState.Warning, AllTraffic = CheckState.Warning },
        });
        Dispatcher.UIThread.RunJobs();
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();
        main.ConnectionProblemText.ShouldContain(ErrorCodes.KillSwitchFailed);
        main.ConnectionProblemText.ShouldContain("блокировки не подтверждено");
        main.ConnectionProblemText.ShouldNotContain("удерживает интернет закрытым");
        main.ConnectionProblemAction.ShouldBe("Проверить сеть");

        machine.Push(s => s with { State = ConnectionState.Error, ErrorCode = ErrorCodes.ServiceNotRunning });
        Dispatcher.UIThread.RunJobs();
        main.StatusTitle.ShouldNotBe("Нет сети");
        main.NetworkText.ShouldBe(Loc.Instance["network.unknown"]);
        main.CheckActionText.ShouldBe(Loc.Instance["action.restartService"]);
        main.HasConnectionProblem.ShouldBeFalse();
        shell.CurrentKey.ShouldBe(Pages.Problem, "потеря службы имеет свой экран, а не остается за старым offline-баннером");
        shell.CurrentPage.ShouldBeOfType<ProblemPageViewModel>();
        Text(window, "ProblemTitle").ShouldBe("Служба не запущена");
    }
}
