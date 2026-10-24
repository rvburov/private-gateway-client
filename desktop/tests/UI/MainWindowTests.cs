using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

// Одна коллекция на сборку: словарь языка и окна — общие, тесты идут по одному.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Главный экран и островок. Снимки каждого состояния — для сверки с макетом
/// (docs/design/screens/01-main.png).
/// </summary>
public sealed class MainWindowTests : WindowHarness
{
    [AvaloniaFact]
    public void Starts_off_with_real_ip_visible_to_sites()
    {
        var (window, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;

        Text(window, "StatusTitle").ShouldBe("IP открыт");
        main.WebIp.ShouldBe("198.51.100.7");
        main.RealIpState.ShouldBe("виден сайтам");
        main.ServerTitle.ShouldBe("NL · Нидерланды 01");
        main.ServerMeta.ShouldBe("VLESS · REALITY · порт 443");
        Text(window, "StateLabel").ShouldBe("Выключено");
        Snapshot(window, "main-off");
    }

    [AvaloniaFact]
    public void Shield_connects_through_connecting_to_connected()
    {
        var (window, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;

        Press(window, "Power");
        Text(window, "StatusTitle").ShouldBe("Подключение…");
        Text(window, "StateLabel").ShouldBe("Подключение");
        shell.Protection.IsBusy.ShouldBeTrue();
        shell.Protection.ToggleCommand.CanExecute(null).ShouldBeTrue("подключение можно прервать кнопкой");
        main.WebIp.ShouldBe("—");
        Snapshot(window, "main-connecting");

        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);

        Text(window, "StatusTitle").ShouldBe("IP скрыт");
        Text(window, "StateLabel").ShouldBe("Защищено");
        main.StatusSubtitle.ShouldBe("Трафик зашифрован");
        main.WebIp.ShouldBe("203.0.113.24");
        main.WebIpMeta.ShouldBe("Амстердам · дата-центр · AS64500");
        main.RealIpState.ShouldBe("скрыт");
        main.Chips.Select(c => c.Value).ShouldBe(["через туннель", "запросы в туннеле", "нет обхода", "Kill Switch вкл"]);
        main.PingText.ShouldBe("42");

        Time.Advance(new TimeSpan(2, 14, 9));
        main.TimerText.ShouldBe("02:14:09");
        Snapshot(window, "main-connected");

        Press(window, "Power");
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Off);
        Text(window, "StatusTitle").ShouldBe("IP открыт");
    }

    [AvaloniaFact]
    public async Task Copy_puts_exit_ip_into_clipboard_and_shows_toast()
    {
        var (window, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;
        await Connect(shell);

        await main.CopyIpCommand.ExecuteAsync(null);

        Clipboard.Text.ShouldBe("203.0.113.24");
        shell.Toast.IsVisible.ShouldBeTrue();
        shell.Toast.Text.ShouldBe("IP скопирован");
        Snapshot(window, "main-toast");
    }

    [AvaloniaFact]
    public async Task Hidden_ip_is_masked_and_cannot_be_copied()
    {
        var (_, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;
        await Connect(shell);

        main.ToggleIpMaskCommand.Execute(null);

        main.WebIp.ShouldBe("•••.•.•••.••");
        main.CanCopyIp.ShouldBeFalse();
    }

    [AvaloniaFact]
    public async Task Choosing_another_server_while_protected_reconnects_to_it()
    {
        var (_, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;
        await Connect(shell);

        var quick = main.QuickServers;
        quick.Count.ShouldBe(3);
        quick[0].IsCurrent.ShouldBeTrue();
        quick[1].Name.ShouldBe("Финляндия 01");
        // Подключение к новому серверу ждет задержку имитации — часы в тесте двигаем сами.
        var choose = quick[1].ChooseCommand.ExecuteAsync("demo-fi-01");
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connecting);
        main.ServerTitle.ShouldBe("FI · Финляндия 01");

        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        await choose;
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
        shell.Protection.Status.ServerId.ShouldBe("demo-fi-01");
    }

    [AvaloniaFact]
    public void Chip_press_opens_its_explanation_and_closes_the_other()
    {
        var (window, shell) = Open();
        var main = (MainPageViewModel)shell.CurrentPage!;

        main.Chips[1].ToggleCommand.Execute(null);
        main.SelectedChip!.Id.ShouldBe("dns");
        main.Chips[3].ToggleCommand.Execute(null);

        main.Chips.Count(c => c.IsSelected).ShouldBe(1);
        main.SelectedChip!.Id.ShouldBe("ks");
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "main-chip-info");
    }

    [AvaloniaFact]
    public void Island_navigates_between_pages_and_marks_active_item()
    {
        var (window, shell) = Open();

        shell.NavigateCommand.Execute(Pages.Servers);
        Dispatcher.UIThread.RunJobs();

        shell.IsServers.ShouldBeTrue();
        shell.CurrentPage.ShouldBeOfType<ServersPageViewModel>();
        Buttons(window).Single(b => b.Classes.Contains("active")).CommandParameter.ShouldBe("servers");

        shell.NavigateCommand.Execute(Pages.Home);
        shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();
    }

    [AvaloniaFact]
    public void Language_switch_updates_labels_at_once()
    {
        var (window, _) = Open();

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();

        Text(window, "StatusTitle").ShouldBe("IP exposed");
        Text(window, "StateLabel").ShouldBe("Off");
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
        texts.ShouldContain("Settings");
        texts.ShouldContain("SITES SEE YOUR IP");
        texts.ShouldContain("demo");
        texts.ShouldNotContain("Настройки");
        Snapshot(window, "main-off-en");
    }
}
