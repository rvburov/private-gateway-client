using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>Уведомления (доски 19 и 22) в окне: тексты на языке интерфейса, всплывашка в активном окне, кнопки уведомлений.</summary>
public sealed class NotificationTests : WindowHarness
{
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
