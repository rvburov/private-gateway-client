using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Application.Updates;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>Уведомления (доски 19 и 22) в окне: тексты на языке интерфейса, всплывашка в активном окне, кнопки уведомлений.</summary>
public sealed class NotificationTests : WindowHarness
{
    [AvaloniaFact]
    public async Task First_connection_failure_is_shown_and_remains_actionable_after_the_toast()
    {
        var service = default(ScriptedService);
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Outcome = connecting => connecting.MoveTo(ConnectionState.Reconnecting, Time.GetUtcNow()),
        });
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);
        Service<NoticeWindow>().Attach(window, window.Show);
        Service<SettingsService>().Update(s => s with { Notifications = NotificationLevel.Off });
        Service<NoticeActions>().Start();
        window.Activate();

        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Reconnecting);
        Dispatcher.UIThread.RunJobs();
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();

        Notifier.Shown.ShouldBeEmpty("ошибка при активном окне показывается внутри даже при выключенных уведомлениях ОС");
        shell.Toast.Text.ShouldStartWith("Не удалось подключиться · Не удалось подтвердить связь через «Мой».");
        shell.Toast.Text.ShouldContain("Интернет заблокирован");
        var message = shell.Toast.Text;
        main.HasConnectionProblem.ShouldBeTrue("до этой неудачи ни одного успешного подключения не было");
        main.ConnectionProblemAction.ShouldBe("Сменить сервер");

        shell.Toast.IsVisible = false;
        service!.Push(s => s with { Attempt = s.Attempt + 1 });
        Dispatcher.UIThread.RunJobs();
        Find<Button>(window, "ConnectionProblemBanner").IsVisible.ShouldBeTrue("причина остается после короткой всплывашки");
        Text(window, "ConnectionProblemText").ShouldNotBeNull().ShouldContain("Интернет заблокирован");
        shell.Toast.IsVisible.ShouldBeFalse("следующая автоматическая попытка не показывает ту же всплывашку снова");
        shell.Toast.Text.ShouldBe(message);

        Loc.Instance.SetLanguage("en");
        main.ConnectionProblemText.ShouldContain("Internet is blocked");
        main.ConnectionProblemAction.ShouldBe("Change server");
        Press(window, "ConnectionProblemBanner");
        shell.CurrentKey.ShouldBe(Pages.Servers);
        service.Status.State.ShouldBe(ConnectionState.Reconnecting, "переход к списку не выбирает другой сервер автоматически и не открывает сеть");
    }

    [AvaloniaFact]
    public async Task Preflight_and_its_rejection_do_not_claim_the_open_network_is_blocked()
    {
        var service = default(ScriptedService);
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Outcome = connecting => connecting with { Protection = ProtectionChecks.For(ConnectionState.Off) },
        });
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);
        Service<NoticeWindow>().Attach(window, window.Show);
        Service<NoticeActions>().Start();
        window.Activate();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();

        main.StatusTitle.ShouldBe("Проверяем сервер…");
        main.StatusSubtitle.ShouldContain("Пока трафик идет напрямую");
        main.HasConnectionProblem.ShouldBeFalse("пока проверка не завершилась, отказ не известен");
        shell.Toast.IsVisible.ShouldBeFalse();

        service!.Push(s => s.MoveTo(ConnectionState.Error, Time.GetUtcNow()) with
        {
            ErrorCode = ErrorCodes.ServerNotResponding,
            Protection = ProtectionChecks.For(ConnectionState.Off),
        });
        Dispatcher.UIThread.RunJobs();

        shell.Toast.Text.ShouldContain("Проверка подключения через «Мой» не прошла");
        shell.Toast.Text.ShouldContain("VPN не включен, трафик идет напрямую");
        shell.Toast.Text.ShouldNotContain("Интернет заблокирован");
        main.ConnectionProblemText.ShouldContain("VPN не включен, трафик идет напрямую");
        main.ConnectionProblemAction.ShouldBe("Сменить сервер");
        main.HasConnectionProblem.ShouldBeTrue();

        service.Push(s => s.MoveTo(ConnectionState.Connecting, Time.GetUtcNow()).MoveTo(ConnectionState.Connected, Time.GetUtcNow()));
        Dispatcher.UIThread.RunJobs();
        main.HasConnectionProblem.ShouldBeFalse("успешное новое подключение убирает причину прошлого отказа");
    }

    [AvaloniaFact]
    public async Task Hidden_window_gets_os_notices_and_their_buttons_open_the_right_screen()
    {
        var service = default(ScriptedService);
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);
        var shown = 0;
        Service<NoticeWindow>().Attach(window, () =>
        {
            shown++;
            window.Show();
        });
        Service<SettingsService>().Update(s => s with { Notifications = NotificationLevel.All });
        Service<NoticeActions>().Start();
        window.Hide();

        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var on = Notifier.Shown.ShouldHaveSingleItem("окно в трее — «Подключено» уведомлением");
        (on.Title, on.Body, on.Buttons.Count).ShouldBe(("Подключено", "Защита включена через «Мой».", 0));

        service!.Push(s => s.MoveTo(ConnectionState.Reconnecting, Time.GetUtcNow()));
        Dispatcher.UIThread.RunJobs();
        var lost = Notifier.Shown[^1];
        (lost.Title, lost.Body).ShouldBe(("Соединение потеряно", "Интернет заблокирован, пока VPN не вернется. Переподключаюсь к «Мой»…"));
        lost.Buttons.Select(b => b.Label).ShouldBe(["Сменить сервер", "Открыть"]);

        Notifier.Press(new NoticeReply(Notice.ProtectionTag, NoticeAction.ChangeServer));
        Dispatcher.UIThread.RunJobs();
        (shown, shell.CurrentKey).ShouldBe((1, Pages.Servers), "«Сменить сервер» — окно на экране серверов");

        window.Hide();
        Loc.Instance.SetLanguage("en");
        service.Push(s => s.MoveTo(ConnectionState.Blocked, Time.GetUtcNow()));
        Dispatcher.UIThread.RunJobs();
        var down = Notifier.Shown[^1];
        (down.Title, down.Body).ShouldBe(("Server is not responding", "“Мой” is unreachable. Internet is blocked — no other server is chosen."));
        down.Buttons.Select(b => b.Label).ShouldBe(["Retry", "Choose server"]);
    }

    [AvaloniaFact]
    public void Update_found_on_schedule_is_told_and_its_buttons_postpone_or_start_it_on_about()
    {
        var service = default(ScriptedService);
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        service!.Update = new UpdateOffer(UpdateState.Available, Time.GetUtcNow(), "0.2.0") { Installable = true };
        service.Download = [new UpdateProgress(100, 100, Ready: true)];
        Service<NoticeWindow>().Attach(window, window.Show);
        Service<SettingsService>().Update(s => s with { Notifications = NotificationLevel.All });
        Service<NoticeActions>().Start();
        var updates = Service<UpdateWatcher>();
        updates.Start();
        window.Hide();

        Time.Advance(UpdateWatcher.FirstCheck);
        WaitFor(() => Notifier.Shown.Count > 0);

        var notice = Notifier.Shown.ShouldHaveSingleItem();
        (notice.Title, notice.Body).ShouldBe(("Доступно обновление 0.2.0",
            "Нажмите «Обновить» — загрузка пойдет в фоне, VPN продолжит работать; установится при перезапуске клиента."));
        notice.Buttons.Select(b => b.Label).ShouldBe(["Обновить", "Позже"]);

        Notifier.Press(new NoticeReply(Notice.UpdateTag, NoticeAction.Later));
        Dispatcher.UIThread.RunJobs();
        (updates.Postponed, window.IsVisible).ShouldBe(("0.2.0", false), "«Позже» сворачивает карточку этой версии, окно не открывает");

        Notifier.Press(new NoticeReply(Notice.UpdateTag, NoticeAction.Update));
        WaitFor(() => updates.Step == UpdateStep.Ready);
        (window.IsVisible, shell.CurrentKey, updates.Postponed).ShouldBe((true, Pages.About, null), "«Обновить» — окно на «О программе», загрузка идет");
    }

    [AvaloniaFact]
    public async Task Active_window_shows_the_notice_inside()
    {
        var service = default(ScriptedService);
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-a@198.51.100.1:443#Мой").Server!], null);
        Service<NoticeWindow>().Attach(window, window.Show);
        Service<NoticeActions>().Start();
        window.Activate();
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        service!.Push(s => s.MoveTo(ConnectionState.Reconnecting, Time.GetUtcNow()));
        Dispatcher.UIThread.RunJobs();

        service.Push(s => s.MoveTo(ConnectionState.Blocked, Time.GetUtcNow()) with { ErrorCode = ErrorCodes.ServerNotFound });
        Dispatcher.UIThread.RunJobs();

        Notifier.Shown.ShouldBeEmpty("окно активно — уведомление ОС не нужно");
        shell.Toast.Text.ShouldBe("Защита не работает · Адрес сервера не найден · PGC-NET-002. Интернет заблокирован.");
    }
}
