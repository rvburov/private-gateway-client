using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// «Профили и подписки» (доска 12) на своих серверах в базе; сеть и служба — подделки стенда.
/// Снимок — для сверки с docs/design/screens/12-subscriptions.png.
/// </summary>
public sealed class SubscriptionsPageTests : WindowHarness
{
    private static readonly Uri MainUrl = new("https://example.com/sub/fake-token-main");
    private static readonly Uri ReserveUrl = new("https://example.org/clash/fake-token-reserve");
    private static readonly Uri TestUrl = new("https://sub.example.net/test/fake-token-test");

    [AvaloniaFact]
    public void Groups_show_address_without_token_state_and_last_update()
    {
        var (window, shell, page) = OpenWithGroups();

        shell.IsIslandVisible.ShouldBeTrue("подстраница настроек — островок на месте");
        shell.IsSettings.ShouldBeTrue();
        Text(window, "SectionTitle").ShouldBe("ПОДПИСКИ · 4");
        page.Cards.Select(c => (c.Name, c.Address, c.HealthText, c.Meta)).ShouldBe(
        [
            ("Резервная", "https://example.org/clash/••••", "Работает", "1 сервер · обновлено вчера"),
            ("Основная", "https://example.com/sub/••••", "Работает", "2 сервера · обновлено в 15:42"),
            ("Тестовый стенд", "https://sub.example.net/test/••••", "Ошибка", "1 сервер · последняя попытка 2 ч назад"),
            ("Мои серверы", "добавлены вручную по ссылкам", "Вручную", "1 сервер"),
        ]);
        page.Cards[2].Error.ShouldBe("Сервер ответил 403 — доступ запрещен, проверьте ссылку. Оставлена прошлая версия списка — серверы работают.");
        page.Cards[3].CanUpdate.ShouldBeFalse();
        page.Cards[3].CanEdit.ShouldBeFalse("«Мои серверы» — не группа, переименовать нечего");
        Text(window, "RefreshText").ShouldBe("каждые 12 ч");
        Find<ToggleButton>(window, "ViaVpnToggle").IsChecked.ShouldBe(true);
        Text(window, "BlockNote").ShouldBe("Службы VPN еще нет — подписки не обновляются");
        Snapshot(window, "subscriptions");
    }

    [AvaloniaFact]
    public async Task Update_only_via_vpn_explains_why_nothing_was_downloaded()
    {
        var (_, shell, page) = OpenWithGroups();

        await page.Cards[1].UpdateCommand.ExecuteAsync(null);

        Downloads.Requested.ShouldBeEmpty("адрес подписки не ушел в сеть мимо VPN");
        shell.Toast.Text.ShouldStartWith("Службы VPN еще нет, а обновлять можно только через VPN");
    }

    [AvaloniaFact]
    public async Task Direct_update_replaces_the_list_and_reports_the_result()
    {
        var (_, shell, page) = OpenWithGroups();
        page.UpdateOnlyViaVpn = false;
        Downloads.Responses[MainUrl] = "trojan://pw-a@198.51.100.1:443#A\ntrojan://pw-b@198.51.100.2:443#B\ntrojan://pw-c@198.51.100.3:443#C";

        await page.Cards.Single(c => c.Name == "Основная").UpdateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Downloads.Requested.ShouldBe([MainUrl]);
        shell.Toast.Text.ShouldBe("«Основная» обновлена: новых 1, удалено 0");
        page.Cards.Single(c => c.Name == "Основная").Meta.ShouldBe("3 сервера · обновлено в 17:42");
        page.BlockNote.ShouldBeNull("настройка выключена — обновлять можно");
    }

    [AvaloniaFact]
    public async Task Failed_update_keeps_the_list_and_shows_why_in_the_card()
    {
        var (window, _, page) = OpenWithGroups();
        page.UpdateOnlyViaVpn = false;

        await page.Cards.Single(c => c.Name == "Основная").UpdateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var card = page.Cards.Single(c => c.Name == "Основная");
        card.HealthText.ShouldBe("Ошибка");
        card.Error.ShouldBe("Сервер ответил 404 — такой подписки нет, проверьте ссылку. Оставлена прошлая версия списка — серверы работают.");
        card.Meta.ShouldBe("2 сервера · последняя попытка только что");
        Service<IServerStore>().Servers.Count.ShouldBe(5);
        Snapshot(window, "subscriptions-error");
    }

    [AvaloniaFact]
    public void Refresh_interval_is_chosen_from_the_list_and_saved()
    {
        var (window, _, page) = OpenWithGroups();

        Press(window, "RefreshChoice");
        page.IsRefreshOpen.ShouldBeTrue();
        page.RefreshOptions.Select(o => o.Label).ShouldBe(["выключено", "каждые 6 ч", "каждые 12 ч", "каждые 24 ч"]);
        page.RefreshOptions.Single(o => o.IsCurrent).Label.ShouldBe("каждые 12 ч");
        page.RefreshOptions[3].ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        page.IsRefreshOpen.ShouldBeFalse();
        Text(window, "RefreshText").ShouldBe("каждые 24 ч");
        Service<SettingsService>().Current.SubscriptionRefresh.ShouldBe(SubscriptionRefresh.Every24Hours);
        page.RefreshOptions.Single(o => o.IsCurrent).Label.ShouldBe("каждые 24 ч");
    }

    [AvaloniaFact]
    public async Task Edit_renames_and_keeps_the_new_address_only_as_a_secret()
    {
        var (window, shell, page) = OpenWithGroups();
        page.Cards.Single(c => c.Name == "Основная").EditCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var edit = shell.Overlay.Sheet.ShouldBeOfType<SubscriptionEditViewModel>();
        Text(window, "EditTitle").ShouldBe("Изменить «Основная»");
        Find<TextBox>(window, "EditUrl").PlaceholderText.ShouldBe("https://example.com/sub/••••");

        edit.Name = "  Главная ";
        edit.Url = "ftp://example.com/sub";
        await edit.SaveCommand.ExecuteAsync(null);
        edit.Error.ShouldBe("Нужен адрес вида https://…");
        shell.Overlay.HasSheet.ShouldBeTrue();

        edit.Url = "https://example.com/sub/fake-token-new";
        await edit.SaveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        shell.Overlay.HasSheet.ShouldBeFalse();
        var card = page.Cards.Single(c => c.Name == "Главная");
        card.Address.ShouldBe("https://example.com/sub/••••");
        Secrets.Values.Values.ShouldContain("https://example.com/sub/fake-token-new");
        shell.Toast.Text.ShouldStartWith("Службы VPN еще нет", customMessage: "новый адрес сразу проверяется — но только через VPN");
    }

    [AvaloniaFact]
    public void Empty_name_cannot_be_saved()
    {
        var (_, shell, page) = OpenWithGroups();
        page.Cards[0].EditCommand.Execute(null);
        var edit = shell.Overlay.Sheet.ShouldBeOfType<SubscriptionEditViewModel>();

        edit.Name = "   ";

        edit.SaveCommand.CanExecute(null).ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Removing_asks_first_then_takes_the_group_with_its_servers()
    {
        var (_, shell, page) = OpenWithGroups();
        var store = Service<IServerStore>();
        var remove = page.Cards.Single(c => c.Name == "Основная").RemoveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var dialog = shell.Overlay.Dialog.ShouldNotBeNull();
        dialog.Title.ShouldBe("Удалить «Основная»?");
        dialog.Text.ShouldBe("Вместе с группой удалятся 2 сервера и их ключи.");
        dialog.IsDanger.ShouldBeTrue();

        dialog.PrimaryCommand.Execute(null);
        await remove;
        Dispatcher.UIThread.RunJobs();

        page.Cards.Select(c => c.Name).ShouldBe(["Резервная", "Тестовый стенд", "Мои серверы"]);
        store.Servers.Count.ShouldBe(3);
        Secrets.Values.Values.ShouldNotContain(MainUrl.ToString(), "адрес подписки удален из хранилища ОС");
        shell.Toast.Text.ShouldBe("«Основная» удалена");
    }

    [AvaloniaFact]
    public async Task Removing_the_group_protection_goes_through_disconnects_first()
    {
        var (_, shell, page) = OpenWithGroups();
        var store = Service<IServerStore>();
        store.Choose(store.Servers.First(s => s.Name == "R1").Id);
        // Пинга у своих серверов еще нет — имитация уходит в блокировку; защита все равно идет через R1.
        var toggle = shell.Protection.ToggleCommand.ExecuteAsync(null);
        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        await toggle;
        WaitFor(() => shell.Protection.Status.State != ConnectionState.Connecting);
        shell.Protection.Status.State.ShouldNotBe(ConnectionState.Off);
        var remove = page.Cards.Single(c => c.Name == "Резервная").RemoveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.Dialog!.Text.ShouldContain("VPN отключится");

        shell.Overlay.Dialog.PrimaryCommand.Execute(null);
        await remove;
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Off);

        store.Selected.ShouldBeNull("другой сервер выбирает пользователь");
    }

    [AvaloniaFact]
    public void Cancelled_removal_changes_nothing()
    {
        var (_, shell, page) = OpenWithGroups();
        _ = page.Cards[3].RemoveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        shell.Overlay.DismissCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        page.Cards.Count.ShouldBe(4);
    }

    [AvaloniaFact]
    public async Task Missing_selected_server_is_announced_and_marked_in_its_card()
    {
        var (window, shell, page) = OpenWithGroups();
        var store = Service<IServerStore>();
        store.Choose(store.Servers.First(s => s.Name == "A").Id);
        page.UpdateOnlyViaVpn = false;
        Downloads.Responses[MainUrl] = "trojan://pw-b@198.51.100.2:443#B";

        // Говорит центр уведомлений: окно активно — всплывашкой в окне.
        Service<NoticeWindow>().Attach(window, window.Show);
        window.Activate();
        Service<NoticeActions>().Start();

        await page.Cards.Single(c => c.Name == "Основная").UpdateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        shell.Toast.Text.ShouldBe("Выбранный сервер «A» пропал из подписки «Основная». Он останется, пока вы не выберете другой.");
        Find<TextBlock>(window, "ToastText").TextLayout.TextLines.Count.ShouldBeGreaterThan(1, "длинное сообщение переносится, а не уходит за край");
        Snapshot(window, "toast-long");
        store.Selected!.Name.ShouldBe("A");
        var card = new ServerCardViewModel(store.Selected.Id, Service<IServerCatalog>(), Service<IServerStore>(), Clipboard,
            Service<ProtectionViewModel>(), Service<ConnectionController>(), shell.Overlay, shell.Toast);
        card.Subtitle.ShouldBe("Пропал из подписки — остается, пока выбран");
    }

    [AvaloniaFact]
    public void Without_own_servers_the_page_offers_to_add_some()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Profiles);
        Dispatcher.UIThread.RunJobs();

        Text(window, "SectionTitle").ShouldBe("ПОДПИСКИ · 0");
        Find<TextBlock>(window, "Empty").IsVisible.ShouldBeTrue();

        Press(window, "AddButton");
        shell.CurrentKey.ShouldBe(Pages.Add);
        shell.CurrentPage.ShouldBeOfType<ImportPageViewModel>().BackCommand.Execute(null);
        shell.CurrentKey.ShouldBe(Pages.Profiles, "из «Добавить» — назад сюда же");
        Dispatcher.UIThread.RunJobs();
        Press(window, "BackButton");
        shell.CurrentKey.ShouldBe(Pages.Settings);
    }

    /// <summary>
    /// Группы как на доске 12: «Резервная» обновлена вчера, «Основная» — сегодня, «Тестовый стенд»
    /// два часа назад получил 403, и один сервер добавлен ссылкой.
    /// </summary>
    private (MainWindow Window, ShellViewModel Shell, SubscriptionsPageViewModel Page) OpenWithGroups()
    {
        var (window, shell) = Open();
        var store = Service<IServerStore>();
        store.Add(Parse("trojan://pw-r@198.51.100.21:443#R1"), "Резервная", ReserveUrl);
        Time.Advance(TimeSpan.FromDays(1));
        store.Add(Parse("trojan://pw-a@198.51.100.1:443#A", "trojan://pw-b@198.51.100.2:443#B"), "Основная", MainUrl);
        store.Add(Parse("trojan://pw-t@198.51.100.31:443#T"), "Тестовый стенд", TestUrl);
        store.MarkUpdateFailed(store.Subscriptions.Single(s => s.Name == "Тестовый стенд").Id, DownloadFailure.Status, 403);
        store.Add(Parse("trojan://pw-x@198.51.100.9:443#X"), null);
        Time.Advance(TimeSpan.FromHours(2));
        shell.NavigateCommand.Execute(Pages.Profiles);
        Dispatcher.UIThread.RunJobs();
        return (window, shell, shell.CurrentPage.ShouldBeOfType<SubscriptionsPageViewModel>());
    }

    private static List<ServerDefinition> Parse(params string[] links) =>
        [.. links.Select(l => LinkParser.Parse(l).Server.ShouldNotBeNull(l))];
}
