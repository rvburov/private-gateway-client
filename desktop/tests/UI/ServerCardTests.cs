using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Карточка сервера (доска 11) и диалоги подтверждения (доска 18); снимки — для сверки с
/// docs/design/screens/11-server-card.png и 18-dialogs.png.
/// </summary>
public sealed class ServerCardTests : WindowHarness
{
    [AvaloniaFact]
    public async Task Card_of_the_server_in_use_matches_mockup()
    {
        var (window, shell, page) = OpenServers();
        await Connect(shell);

        var card = OpenCard(page, "Нидерланды 01");

        Find<Border>(window, "Sheet").IsVisible.ShouldBeTrue();
        Text(window, "CardName").ShouldBe("Нидерланды 01");
        card.Subtitle.ShouldBe("Амстердам · из подписки «Основная подписка»");
        card.IsFavorite.ShouldBeTrue();
        Text(window, "CardStatus").ShouldBe("Активен");
        card.LatencyText.ShouldBe("42 мс");
        card.Details.Select(d => d.Label + ": " + d.Value).ShouldBe(
        [
            "Адрес: 203.0.113.24:443",
            "Протокол: VLESS",
            "Транспорт: TCP",
            "Защита: REALITY",
            "Ядро: Xray-core",
        ]);
        Find<Border>(window, "InUse").IsVisible.ShouldBeTrue();
        Find<Button>(window, "ConnectButton").IsVisible.ShouldBeFalse();
        card.CanShare.ShouldBeFalse("у демо-сервера ключа нет — делиться нечем");
        card.ShareHint.ShouldBe("Поделиться можно сервером, добавленным ссылкой или подпиской: в ссылке есть ключ доступа.");
        Snapshot(window, "server-card");
    }

    [AvaloniaFact]
    public async Task Share_shows_a_readable_qr_code_and_copies_the_link()
    {
        var link = File.ReadAllText(Path.Combine(Repo.Root, "shared", "testdata", "links", "vless", "private-gateway.txt")).Trim();
        var (window, shell) = Open();
        _shell = shell;
        Service<IServerStore>().Add([LinkParser.Parse(link).Server!], null);
        shell.NavigateCommand.Execute(Pages.Servers);
        Dispatcher.UIThread.RunJobs();
        var card = OpenCard(shell.CurrentPage.ShouldBeOfType<ServersPageViewModel>(), "Ноутбук & «дом»");
        card.CanShare.ShouldBeTrue();
        card.ShareHint.ShouldBeNull();

        Press(window, "ShareButton");
        Dispatcher.UIThread.RunJobs();

        card.IsSharing.ShouldBeTrue();
        card.ShareLabel.ShouldBe("Скрыть QR");
        card.ShowsDetails.ShouldBeFalse("вместо сведений — QR-код");
        Find<Button>(window, "ShareButton").Classes.ShouldContain("on");
        // Код на экране — тот, что прочитает телефон: кадр окна распознается тем же QrCode, что импорт.
        QrCode.Decode(BitmapPixels.From(window.CaptureRenderedFrame()!)).ShouldBe(LinkWriter.Write(LinkParser.Parse(link).Server!));
        Snapshot(window, "server-card-share");

        Press(window, "CopyLink");
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
        Clipboard.Text.ShouldBe(LinkWriter.Write(LinkParser.Parse(link).Server!));
        shell.Toast.Text.ShouldBe("Ссылка скопирована");

        Press(window, "ShareButton");
        Dispatcher.UIThread.RunJobs();
        (card.IsSharing, card.QrModules, card.ShowsDetails).ShouldBe((false, (bool[,]?)null, true));
    }

    [AvaloniaFact]
    public void Share_without_a_key_in_the_store_says_so()
    {
        var (_, shell) = Open();
        _shell = shell;
        Service<IServerStore>().Add([LinkParser.Parse("trojan://pw-1@198.51.100.9:443#Ключ пропал").Server!], null);
        Secrets.Values.Clear();
        shell.NavigateCommand.Execute(Pages.Servers);
        Dispatcher.UIThread.RunJobs();
        var card = OpenCard(shell.CurrentPage.ShouldBeOfType<ServersPageViewModel>(), "Ключ пропал");

        card.ToggleShareCommand.Execute(null);

        card.IsSharing.ShouldBeFalse();
        shell.Toast.Text.ShouldBe("Ключа сервера нет в хранилище ОС — поделиться нечем");
    }

    [AvaloniaFact]
    public void Details_name_the_core_and_say_honestly_when_traffic_is_open()
    {
        var (_, _, page) = OpenServers();

        var se = OpenCard(page, "Швеция 01");
        se.Details.Single(d => d.Label == "Защита").Value.ShouldBe("шифрует протокол", "WireGuard шифрует сам");
        se.Details.Single(d => d.Label == "Ядро").Value.ShouldBe("sing-box");
        se.Details.Single(d => d.Label == "Транспорт").Value.ShouldBe("UDP");
        se.StatusText.ShouldBeNull("не выбран — без отметки");
    }

    [AvaloniaFact]
    public void Star_adds_the_server_to_favorites()
    {
        var (window, _, page) = OpenServers();
        var card = OpenCard(page, "Турция 01");
        card.IsFavorite.ShouldBeFalse();

        Press(window, "FavoriteButton");
        Dispatcher.UIThread.RunJobs();

        card.IsFavorite.ShouldBeTrue();
        card.FavoriteHint.ShouldBe("Убрать из избранного");
        Find<Button>(window, "FavoriteButton").Classes.ShouldContain("on");
        page.IsFavoritesTab = true;
        Dispatcher.UIThread.RunJobs();
        page.Groups.ShouldHaveSingleItem().Rows.Select(r => r.Name).ShouldContain("Турция 01");
    }

    [AvaloniaFact]
    public async Task Connect_from_card_closes_it_and_protects_through_that_server()
    {
        var (window, shell, page) = OpenServers();
        var card = OpenCard(page, "Германия 02");
        Find<Button>(window, "ConnectButton").IsVisible.ShouldBeTrue();

        var connect = card.ConnectCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.HasSheet.ShouldBeFalse();
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connecting);

        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
        shell.Protection.Status.ServerId.ShouldBe("demo-de-02");
        page.Groups[0].Rows.Single(r => r.IsSelected).Tag.ShouldBe("активен");
    }

    [AvaloniaFact]
    public void Rename_changes_the_name_in_card_and_list_and_rejects_empty()
    {
        var (window, _, page) = OpenServers();
        var card = OpenCard(page, "Польша 03");

        Press(window, "RenameButton");
        card.IsRenaming.ShouldBeTrue();
        card.NewName.ShouldBe("Польша 03");
        Find<TextBox>(window, "NewName").Text = "   ";
        Dispatcher.UIThread.RunJobs();
        card.SaveNameCommand.CanExecute(null).ShouldBeFalse();

        Find<TextBox>(window, "NewName").Text = "  Варшава, работа ";
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "server-card-rename");
        Press(window, "SaveName");
        Dispatcher.UIThread.RunJobs();

        card.IsRenaming.ShouldBeFalse();
        Text(window, "CardName").ShouldBe("Варшава, работа");
        page.Groups[0].Rows.Select(r => r.Name).ShouldContain("Варшава, работа");
    }

    [AvaloniaFact]
    public async Task Delete_asks_first_and_cancel_keeps_the_server()
    {
        var (window, shell, page) = OpenServers();
        var card = OpenCard(page, "США 01");

        var delete = card.DeleteCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Find<ContentControl>(window, "Dialog").IsVisible.ShouldBeTrue();
        Text(window, "DialogTitle").ShouldBe("Удалить сервер «США 01»?");
        Find<Button>(window, "DialogPrimary").Classes.ShouldContain("danger");
        Find<Button>(window, "DialogSecondary").IsFocused.ShouldBeTrue("фокус на «Отмена», а не на «Удалить»");
        Snapshot(window, "dialog-delete");

        // Escape закрывает диалог безопасным вариантом.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        await delete;
        Dispatcher.UIThread.RunJobs();

        shell.Overlay.HasDialog.ShouldBeFalse();
        shell.Overlay.HasSheet.ShouldBeTrue("карточка остается");
        page.AllTabText.ShouldBe("Все · 7");
    }

    [AvaloniaFact]
    public async Task Delete_confirmed_removes_the_server_and_reports_it()
    {
        var (window, shell, page) = OpenServers();
        var card = OpenCard(page, "США 01");

        var delete = card.DeleteCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Press(window, "DialogPrimary");
        await delete;
        Dispatcher.UIThread.RunJobs();

        shell.Overlay.HasSheet.ShouldBeFalse();
        shell.Toast.Text.ShouldBe("Сервер удален");
        page.AllTabText.ShouldBe("Все · 6");
        page.Groups[0].Rows.Select(r => r.Name).ShouldNotContain("США 01");
    }

    [AvaloniaFact]
    public async Task Deleting_the_server_in_use_turns_protection_off_and_leaves_choice_to_user()
    {
        var (window, shell, page) = OpenServers();
        await Connect(shell);
        var card = OpenCard(page, "Нидерланды 01");

        var delete = card.DeleteCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        shell.Overlay.Dialog!.Text.ShouldBe("Защита сейчас идет через этот сервер: VPN отключится, и другой сервер вы выберете сами.");
        Press(window, "DialogPrimary");
        await delete;
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Off);

        page.Groups[0].Rows.ShouldNotContain(r => r.IsSelected);
        shell.Protection.Label.ShouldBe("Нет сервера");
    }

    [AvaloniaFact]
    public void Scrim_and_navigation_close_the_card()
    {
        var (window, shell, page) = OpenServers();
        OpenCard(page, "Финляндия 01");

        Press(window, "Scrim");
        shell.Overlay.HasSheet.ShouldBeFalse();

        OpenCard(page, "Финляндия 01");
        shell.NavigateCommand.Execute(Pages.Home);
        shell.Overlay.HasSheet.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Shield_in_block_asks_before_exposing_the_real_ip()
    {
        var (window, shell, page) = OpenServers();
        var tr = page.Groups[0].Rows.Single(r => r.Name == "Турция 01");
        var card = OpenCard(page, tr.Name);
        var connect = card.ConnectCommand.ExecuteAsync(null);
        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        await connect;
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Blocked);

        // «Оставить защиту» — ничего не меняется.
        var keep = shell.Protection.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Text(window, "DialogTitle").ShouldBe("Снять защиту?");
        Find<Button>(window, "DialogPrimary").IsFocused.ShouldBeTrue("фокус на «Оставить защиту»");
        Find<Button>(window, "DialogSecondary").Classes.ShouldContain("danger");
        Snapshot(window, "dialog-unprotect");
        Press(window, "DialogPrimary");
        await keep;
        Dispatcher.UIThread.RunJobs();
        shell.Protection.Status.State.ShouldBe(ConnectionState.Blocked);

        // «Снять защиту» — отключение.
        var drop = shell.Protection.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Press(window, "DialogSecondary");
        await drop;
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Off);
        shell.Overlay.HasDialog.ShouldBeFalse();
    }

    private ShellViewModel? _shell;

    private (MainWindow Window, ShellViewModel Shell, ServersPageViewModel Page) OpenServers()
    {
        var (window, shell) = Open();
        _shell = shell;
        shell.NavigateCommand.Execute(Pages.Servers);
        Dispatcher.UIThread.RunJobs();
        return (window, shell, shell.CurrentPage.ShouldBeOfType<ServersPageViewModel>());
    }

    private ServerCardViewModel OpenCard(ServersPageViewModel page, string name)
    {
        var row = page.Groups.SelectMany(g => g.Rows).Single(r => r.Name == name);
        row.OpenCommand.Execute(row.Id);
        Dispatcher.UIThread.RunJobs();
        return _shell!.Overlay.Sheet.ShouldBeOfType<ServerCardViewModel>();
    }
}
